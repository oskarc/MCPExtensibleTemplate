# MCP Server Architecture

## Overview

The MCP Server Template is a **Model Context Protocol (MCP) server** built on .NET 10 and the official C# MCP SDK. It bridges AI assistants and external APIs, and it is built around one idea: **every provider works inside a declared policy that the server enforces**. A provider cannot reach a caller, and a caller cannot reach a provider, except through that policy.

## Core Concept

Think of the MCP server as a **translator between AI assistants and external APIs, with a gate in the middle**:

```
AI Assistant (Claude, VS Code, etc.)
         ↓
    MCP Protocol  (bearer token over HTTP, or a local Development principal over stdio)
         ↓
   The frame  ← checks every request against the provider's policy
         ↓
   Providers  ← your integrations
         ↓
    Internet APIs (Weather, Todo Services, etc.)
```

The server receives requests from AI assistants, checks them against the policy of the provider they are for, translates them into API calls, and sends formatted results back.

---

## Layered Architecture

The server is organized in **4 key layers**:

### 1. **Transport & Identity Layer** (Entry Point)
- **Purpose**: How the server communicates with MCP clients, and who they are
- **Options**:
  - **stdio**: Development only. A plain host with no web server; the caller is the *Development principal* declared under `Development:DevPrincipal`, and it passes through exactly the same checks as an HTTP caller.
  - **HTTP**: A hosted server. Stateless streamable HTTP at the server root (`/`), so any instance can serve any request.
- **Responsibilities (HTTP)**, in this order:
  1. Forwarded headers — only from declared proxies
  2. HSTS and HTTPS redirection
  3. Host allowlist — stops DNS rebinding
  4. CORS — deny-all by default
  5. Per-IP rate limiter — before any token is examined
  6. Health endpoints — `/healthz` and `/readyz`, no credential needed
  7. Origin guard — refuses cross-origin browser requests
  8. Authentication — a bearer token from a configured identity provider

**Files**: `Program.cs`, `Infrastructure/HttpServerComposition.cs`, `Infrastructure/Identity/`

**Identity, in short**: each configured identity provider gets its own JWT bearer scheme. A routing scheme reads the token's issuer and hands it to that provider's scheme, which validates signature, issuer, audience, lifetime and algorithm. Tokens must carry `sub`, `jti`, `client_id` (or `azp`) and `iat`. The validated principal is normalized to one shape — which identity provider issued it, its scopes, and the `{idp}:{sub}` key limits are kept under. The server publishes its OAuth protected-resource metadata at `/.well-known/oauth-protected-resource`, so a client that is refused learns where to get a token.

---

### 2. **The Frame** (Policy & Enforcement)
- **Purpose**: Enforces each provider's declared policy on every request
- **At startup** it refuses to start rather than start open. It checks:
  - every setting in the sections it governs is one it reads — a typo or retired key is named, with the nearest real key
  - every provider named in `Providers:Enabled` exists
  - no provider, while registering its services, removed or replaced anything, or registered anything that belongs to the frame, the MCP SDK, the identity layer or the host pipeline
  - every served tool, resource and prompt has a policy, and every policy names something served
  - every scope is one the provider's identity provider can issue
  - every outbound host is an exact domain name, and the provider's configured `BaseUrl` is one of them
  - the request checks actually installed are the frame's own, and the request-kind gate is first
- **On every request**, in this order:
  1. **Request kind** — only the kinds the frame governs go further (listing and using tools, resources, templates, prompts and completions; initialize, discovery, ping, cancellation). Anything else is refused.
  2. **Signed in** — the request carries a principal the server can attribute
  3. **Known item** — the tool, resource or prompt exists
  4. **Right identity provider** — its provider is bound to the caller's identity provider
  5. **Scope** — the caller holds the scope its policy requires
  6. **Risk gate** — a *Write* tool needs a token issued in the last 5 minutes; an *Irreversible* tool needs one issued in the last 60 seconds and a single-use confirmation tied to its exact arguments
  7. **Limits** — per caller, and per caller per tool, in sliding one-minute windows held in Redis so they apply on every instance
  8. **Argument shape** — no argument the tool does not declare, values that fit its schema, strings within the policy's length, numbers that are numbers
  9. **Output cap** — an answer larger than the policy allows is withheld, not cut short
- Each refusal names its rule (for example `insufficient_scope`, `idp-binding`, `extraneous-argument`) and is logged as a security event.

**Files**: `Infrastructure/Frame/` — `Policy.cs`, `IProviderModule.cs`, `GovernedServer.cs`, `RequestGate.cs`, `PolicyRegistry.cs`, `ArgumentGuard.cs`, `Confirmation.cs`, `LimitStore.cs`, `SettingsAllowlist.cs`, `FrameIntegrity.cs`

---

### 3. **MCP Protocol Layer** (Tools, Resources, Prompts)
- **Purpose**: Exposes the tools, resources and prompts MCP clients can list and use
- **What it does**:
  1. Registers the tool, resource and prompt types each enabled provider module names — nothing is found by scanning the assembly
  2. Builds a catalog of the tools, their descriptions and parameters
  3. Routes incoming calls to the right method, after the frame has admitted them

**How it works**:
```csharp
// A static class the module names in its ToolTypes
[McpServerToolType]
public static class WeatherTools
{
    [McpServerTool]
    public static async Task<string> GetForecast(ApiClient client, double latitude, double longitude)
    {
        // Implementation
    }
}
// ↑ Served as "get_forecast" only because a module lists WeatherTools and its policy names get_forecast
```

**File**: `Infrastructure/Frame/GovernedServer.cs`

---

### 4. **Provider Layer** (API Integration Logic)
- **Purpose**: Contains all integration-specific logic for connecting to external APIs
- **Pluggable Design**: Each provider is self-contained, and declares what it may do

```
Providers/
├── BuiltInProviders.cs                    # The modules this server ships with
├── JsonPlaceholder/
│   ├── JsonPlaceholderModule.cs           # Name, policy, types, settings
│   ├── JsonPlaceholderConfig.cs           # Configuration (base URL, etc.)
│   ├── JsonPlaceholderApiClient.cs        # Typed HTTP client
│   ├── JsonPlaceholderTools.cs            # MCP tools
│   ├── JsonPlaceholderFormatters.cs       # Output formatting
│   ├── JsonPlaceholderServiceRegistration.cs  # DI wiring, called by the module
│   └── Models/                            # Data models (Post, Comment, Todo)
├── Smhi/                                  # Weather provider
│   ├── SmhiModule.cs
│   ├── SmhiApiClient.cs
│   ├── SmhiTools.cs
│   ├── SmhiFormatters.cs
│   ├── SmhiResources.cs                   # MCP resources
│   ├── SmhiPrompts.cs                     # MCP prompts
│   └── Models/
└── SmhiObs/                               # Weather observations provider
    └── ...
```

Each provider contains:
1. **Module class**: The provider's name, its **policy** (scope, risk class and limits for every tool; a scope for every resource and prompt; the hosts it may call), the types that declare its primitives, and the settings it reads
2. **Config class**: Holds settings (API base URL, user agent, etc.)
3. **ApiClient class**: Typed HTTP client with resilience (retry, circuit breaker)
4. **Tools class**: A static class of MCP tools (resources and prompts likewise)
5. **Formatters class**: Transforms raw API responses into human-friendly text
6. **Models**: Data transfer objects (DTOs) for type safety
7. **ServiceRegistration class**: Wires DI, called from the module's `Register`

---

## Data Flow: A Complete Request

Here's what happens when an AI assistant uses an MCP tool (the demo provider is enabled in Development only):

```
1. MCP Client (e.g., Claude Desktop)
   ↓
2. HTTP request with a bearer token, or a stdio message
   "Please call get_blog_post with postId=1"
   ↓
3. Transport & Identity Layer
   Middleware in order; the token is validated and normalized
   ↓
4. The Frame
   - The request kind is governed
   - get_blog_post exists, belongs to JsonPlaceholder, which answers to the caller's identity provider
   - The caller holds demo:read
   - Read tool: no freshness or confirmation needed
   - The caller is within their limits (checked in Redis)
   - The arguments match the tool's schema
   ↓
5. MCP Protocol Layer
   Routes to JsonPlaceholderTools.GetBlogPost
   ↓
6. Provider Layer: JsonPlaceholder
   a) JsonPlaceholderTools.GetBlogPost() is called
   b) Calls JsonPlaceholderApiClient.GetPostAsync(1)
   c) HTTP GET to https://jsonplaceholder.typicode.com/posts/1
   d) Response: { "userId": 1, "id": 1, "title": "...", "body": "..." }
   e) Format response with JsonPlaceholderFormatters.FormatPost()
   ↓
7. The Frame
   The answer is within the tool's output cap; timing and result are logged
   ↓
8. Transport Layer
   Return formatted response
   ↓
9. MCP Client
   Display result to user
```

---

## Dependency Injection (DI) Flow

The server uses **Microsoft.Extensions.DependencyInjection**, composed in one place — `GovernedServer.AddGovernedMcpServer` — which the HTTP server, the stdio mode and the tests all call:

```csharp
// Program.cs (HTTP)
HttpServerComposition.AddHttpServer(builder, BuiltInProviders.Create());

// Inside the frame, for each enabled module — watched, then the frame registers itself last:
module.Register(services, configuration);

// JsonPlaceholderModule.Register:
public void Register(IServiceCollection services, IConfiguration configuration) =>
    services.AddJsonPlaceholderProvider(configuration, Policy.Egress);

// Inside JsonPlaceholderServiceRegistration.cs:
public static IServiceCollection AddJsonPlaceholderProvider(
    this IServiceCollection services,
    IConfiguration configuration,
    EgressPolicy egress)
{
    // 1. Bind config from Providers:JsonPlaceholder
    var section = configuration.GetSection("Providers:JsonPlaceholder");
    services.Configure<JsonPlaceholderConfig>(section);

    var config = section.Get<JsonPlaceholderConfig>()
        ?? throw new ConfigurationException(
            "Missing configuration section 'Providers:JsonPlaceholder' in appsettings.json.");

    // 2. Register the typed HttpClient, with the timeouts from the provider's policy
    services.AddHttpClient<JsonPlaceholderApiClient>(client =>
    {
        client.BaseAddress = new Uri(config.BaseUrl);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
    });

    return services;
}
```

**Result**: When `JsonPlaceholderTools.GetBlogPost()` runs:
```csharp
public static async Task<string> GetBlogPost(
    JsonPlaceholderApiClient client,  // ← Injected by the DI container
    int postId,
    CancellationToken cancellationToken = default)
{
    // client is ready to use; no manual instantiation needed
}
```

---

## Configuration Layers

The system supports multiple configuration sources in order of precedence:

1. **Environment Variables** (highest priority)
   ```powershell
   $env:Transport = "http"
   $env:Limits__Redis = "localhost:6379"
   ```

   Or on bash/zsh:
   ```bash
   export Transport=http
   export Limits__Redis=localhost:6379
   ```

2. **appsettings.{Environment}.json**
   ```text
   ASPNETCORE_ENVIRONMENT=Development  -> loads appsettings.Development.json
   ASPNETCORE_ENVIRONMENT=Production   -> loads appsettings.Production.json
   ```
   With neither variable set, the server runs as **Production**.

3. **appsettings.json** (base/default)

In the sections the frame governs — `Authentication`, `Providers`, `Limits`, `Confirmation`, `Development` — a key the server does not read stops it from starting. See [04-CONFIGURATION.md](04-CONFIGURATION.md).

---

## Security Model

The server implements **defense in depth**:

| Layer | Mechanism | Purpose |
|-------|-----------|---------|
| **Transport** | TLS at the proxy; HSTS; Production refuses to start without a declared trusted proxy | Encrypt data in transit |
| **Authentication** | Bearer tokens from configured identity providers, one scheme each | Verify caller identity |
| **Trust domains** | Each provider answers to exactly one identity provider | Contain what each identity provider can reach |
| **Authorization** | A scope per tool, resource and prompt, from the provider's policy | Least privilege |
| **Request kinds** | Only governed request kinds are accepted | Nothing new is reachable until someone governs it |
| **Risk gates** | Fresh tokens for writes; single-use, argument-bound confirmation for irreversible actions | Bound what a stolen or stale token can do |
| **Rate Limiting** | Per IP before authentication; per caller and per caller per tool in Redis | Prevent abuse and agentic loops, across instances |
| **Input Validation** | Argument shape against the tool's schema, plus each tool's own checks | Reject invalid requests early |
| **Output caps** | Per-tool answer size from the policy | Bound what reaches the model |
| **Frame integrity** | Providers cannot remove or add to the frame's checks | The policy cannot be switched off by a provider |
| **HTTP Resilience** | Retry + circuit breaker on upstream calls | Handle failures gracefully |
| **Logging & Monitoring** | A named security event per refusal; correlation IDs | Detect suspicious patterns |

---

## Extension Points

### Adding a New Provider

To add a new API provider (e.g., GitHub API):

1. Create `Providers/Github/` folder
2. Create core files:
   - `GithubConfig.cs` (configuration class)
   - `GithubApiClient.cs` (HTTP client with methods)
   - `GithubTools.cs` (a **static** class of MCP tools marked with `[McpServerTool]`)
   - `GithubFormatters.cs` (output formatting)
   - `GithubServiceRegistration.cs` (DI setup)
   - `GithubModule.cs` — an `IProviderModule` declaring the policy: a scope, risk class and per-caller limit for every tool, a scope for every resource and prompt, the hosts it may call, and any settings of its own
   - `Models/` folder (DTOs)
3. List the module in `Providers/BuiltInProviders.cs`
4. Add a `Providers:Github` section (`BaseUrl`, `UserAgent`, `IdentityProvider`) and add `Github` to `Providers:Enabled`
5. Start the server. If anything is undeclared, mis-scoped or points at a host the policy does not allow, it refuses to start and says what to fix.

### Adding Logging/Monitoring

The request checks are the frame's. A provider cannot add its own — the frame refuses to start with a check it did not install. To add a cross-cutting check, add it to the frame in `Infrastructure/Frame/GovernedServer.cs`, registered through the frame's manifest so the startup comparison recognizes it. Middleware that runs before the MCP endpoint belongs in `Infrastructure/HttpServerComposition.cs`.

### Transport Flexibility

Switch from stdio to HTTP (or vice versa) by changing one config setting:
```json
{
  "Transport": "stdio"  // or "http"
}
```
stdio is refused outside Development. HTTP needs at least one identity provider configured, and outside Development, Redis.

---

## Performance Considerations

1. **HttpClientFactory**: Reuses connections and handles DNS resolution efficiently
2. **Async/Await**: All I/O operations are non-blocking
3. **Limits in Redis**: One atomic script per window per request — a caller's whole-server window and, for a tool call, the tool's own window
4. **Policies frozen at startup**: The frame's lookups are frozen dictionaries, built once
5. **Resilience**: Retry + circuit breaker prevents cascading failures
6. **Structured Logging**: Async file writes minimize blocking

---

## Summary

The MCP Server Template architecture is built on these principles:

- **Layered Design**: Each layer has a specific responsibility
- **Governed Providers**: Each provider declares what it may do; the frame enforces it and cannot be removed by a provider
- **Refuse, don't guess**: A misconfiguration stops startup; an ungoverned request kind is refused; an unreachable limit store refuses rather than permits
- **Observable**: A named security event for every refusal, correlation IDs for every call
- **Resilient**: Retry logic, circuit breakers, graceful error handling
- **Non-blocking**: Async/await throughout for performance

This structure lets you focus on your provider's business logic while the frame handles who may reach it and how.
