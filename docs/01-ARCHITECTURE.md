# MCP Server Architecture

## Overview

The MCP Server Template is a **Model Context Protocol (MCP) server** built on .NET 8 that bridges AI assistants and external APIs. It follows a layered, pluggable architecture designed for security, scalability, and maintainability.

## Core Concept

Think of the MCP server as a **translator between AI assistants and external APIs**:

```
AI Assistant (Claude, VS Code, etc.)
         ↓
    MCP Protocol
         ↓
   MCP Server ← Your Application
         ↓
    Internet APIs (Weather, Todo Services, etc.)
```

The server receives requests from AI assistants, translates them into API calls, processes responses, and sends formatted results back to the AI.

---

## Layered Architecture

The server is organized in **4 key layers**:

### 1. **Transport Layer** (Entry Point)
- **Purpose**: Handles how the server communicates with MCP clients
- **Options**:
  - **stdio** (default): Runs locally; uses stdin/stdout for communication (good for development, IDE integration)
  - **HTTP**: Runs as a web server; clients connect via HTTP requests (good for hosted deployments)
- **Responsibilities**:
  - Accept incoming requests
  - Return responses
  - Handle authentication (API keys for HTTP mode)
  - Apply rate limiting (prevent abuse)

**File**: `Program.cs` (lines 75-135)

---

### 2. **Middleware & Filters Layer** (Security & Observability)
- **Purpose**: Intercepts all requests and responses to apply cross-cutting concerns
- **Components**:
  - **ApiKeyMiddleware**: Validates `X-Api-Key` header (HTTP mode only)
  - **ToolCallLoggingFilter**: Logs every tool call, response time, parameters (sanitized)
  - **ToolCallThrottleFilter**: Rate-limits per-tool calls (prevents agentic loops)
  - **HealthProbe**: Checks upstream API connectivity at startup

**Files**:
- `Infrastructure/ApiKeyMiddleware.cs`
- `Infrastructure/ToolCallLoggingFilter.cs`
- `Infrastructure/ToolCallThrottleFilter.cs`
- `Infrastructure/HealthProbe.cs`

---

### 3. **MCP Protocol Layer** (Tool/Resource Discovery)
- **Purpose**: Exposes tools, resources, and prompts that MCP clients can discover and invoke
- **What it does**:
  1. Scans the entire application for classes marked `[McpServerToolType]`
  2. Automatically registers all methods marked `[McpServerTool]` as available tools
  3. Builds a catalog of what tools exist, their descriptions, and parameters
  4. Routes incoming tool calls to the appropriate handler

**How it works**:
```csharp
// Example: This is automatically discovered
[McpServerToolType]
public static class WeatherTools
{
    [McpServerTool]
    public static async Task<string> GetForecast(ApiClient client, double latitude, double longitude)
    {
        // Implementation
    }
}
// ↑ This method is now available as an MCP tool
```

**File**: `Program.cs` (lines 53-56) - `WithToolsFromAssembly()`, `WithResourcesFromAssembly()`, etc.

---

### 4. **Provider Layer** (API Integration Logic)
- **Purpose**: Contains all integration-specific logic for connecting to external APIs
- **Pluggable Design**: Each provider is self-contained and can be swapped out

```
Providers/
├── JsonPlaceholder/
│   ├── JsonPlaceholderConfig.cs          # Configuration (base URL, etc.)
│   ├── JsonPlaceholderApiClient.cs       # Typed HTTP client
│   ├── JsonPlaceholderTools.cs           # MCP tools
│   ├── JsonPlaceholderFormatters.cs      # Output formatting
│   ├── JsonPlaceholderServiceRegistration.cs  # DI wiring
│   └── Models/                            # Data models (Post, Comment, Todo)
├── Smhi/                                  # Weather provider
│   ├── SmhiApiClient.cs
│   ├── SmhiTools.cs
│   ├── SmhiFormatters.cs
│   ├── SmhiResources.cs                  # MCP resources
│   ├── SmhiPrompts.cs                    # MCP prompts
│   └── Models/
└── SmhiObs/                               # Weather observations provider
    └── ...
```

Each provider contains:
1. **Config class**: Holds settings (API base URL, credentials, etc.)
2. **ApiClient class**: Typed HTTP client with resilience (retry, circuit breaker)
3. **Tools class**: MCP tools that users call
4. **Formatters class**: Transforms raw API responses into human-friendly text
5. **Models**: Data transfer objects (DTOs) for type safety
6. **ServiceRegistration class**: Wires DI so the provider is available at runtime

---

## Data Flow: A Complete Request

Here's what happens when an AI assistant uses an MCP tool:

```
1. MCP Client (e.g., Claude Desktop)
   ↓
2. HTTP Request or stdio message
   "Please call GetBlogPost with postId=1"
   ↓
3. Transport Layer
   Route to MCP protocol handler
   ↓
4. Middleware Layer
   - Check API key (if HTTP)
   - Log the request
   - Check rate limit
   ↓
5. MCP Protocol Layer
   Identify that "GetBlogPost" tool exists in JsonPlaceholderTools
   ↓
6. Provider Layer: JsonPlaceholder
   a) JsonPlaceholderTools.GetBlogPost() is called
   b) Calls JsonPlaceholderApiClient.GetPostAsync(1)
   c) HTTP GET to https://jsonplaceholder.typicode.com/posts/1
   d) Response: { "userId": 1, "id": 1, "title": "...", "body": "..." }
   e) Format response with JsonPlaceholderFormatters.FormatPost()
   ↓
7. Middleware Layer
   Log completion, timing
   ↓
8. Transport Layer
   Return formatted response
   ↓
9. MCP Client
   Display result to user
```

---

## Dependency Injection (DI) Flow

The server uses **Microsoft.Extensions.DependencyInjection** for wiring:

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Register all providers
builder.Services.AddJsonPlaceholderProvider(builder.Configuration);
builder.Services.AddSmhiProvider(builder.Configuration);
builder.Services.AddSmhiObsProvider(builder.Configuration);

// Inside JsonPlaceholderServiceRegistration.cs:
public static IServiceCollection AddJsonPlaceholderProvider(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // 1. Bind config from appsettings.json
    var section = configuration.GetSection("Providers:JsonPlaceholder");
    services.Configure<JsonPlaceholderConfig>(section);

    var config = section.Get<JsonPlaceholderConfig>()
        ?? throw new InvalidOperationException(
            "Missing configuration section 'Providers:JsonPlaceholder' in appsettings.json.");

    // 2. Register typed HttpClient
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
    JsonPlaceholderApiClient client,  // ← Auto-injected by DI container
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
   $env:Authentication__ApiKey = "secret"
   ```

   Or on bash/zsh:
   ```bash
   export Transport=http
   export Authentication__ApiKey=secret
   ```

2. **appsettings.{Environment}.json**
   ```text
   ASPNETCORE_ENVIRONMENT=Development  -> loads appsettings.Development.json
   ASPNETCORE_ENVIRONMENT=Production   -> loads appsettings.Production.json
   ```

3. **appsettings.json** (base/default)

---

## Security Model

The server implements **defense in depth**:

| Layer | Mechanism | Purpose |
|-------|-----------|---------|
| **Transport** | HTTP TLS/SSL (configured by reverse proxy) | Encrypt data in transit |
| **Authentication** | API Key validation (HTTP mode) | Verify caller identity |
| **Rate Limiting** | Per-tool sliding window throttle | Prevent abuse loops |
| **Input Validation** | Parameter bounds checks | Reject invalid requests early |
| **HTTP Resilience** | Retry + circuit breaker on upstream calls | Handle failures gracefully |
| **Logging & Monitoring** | Correlation IDs, request tracking | Detect suspicious patterns |

---

## Extension Points

### Adding a New Provider

To add a new API provider (e.g., GitHub API):

1. Create `Providers/Github/` folder
2. Create core files:
   - `GithubConfig.cs` (configuration class)
   - `GithubApiClient.cs` (HTTP client with methods)
   - `GithubTools.cs` (MCP tools marked with `[McpServerTool]`)
   - `GithubFormatters.cs` (output formatting)
   - `GithubServiceRegistration.cs` (DI setup)
   - `Models/` folder (DTOs)
3. Call `builder.Services.AddGithubProvider(builder.Configuration)` in `Program.cs`
4. Update `appsettings.json` with Github config section
5. Done! Tools are auto-discovered and available

### Adding Logging/Monitoring

Modify `Program.cs` to add custom filters or middleware:

```csharp
filters.AddCallToolFilter(/* your custom filter */);
app.UseMiddleware<YourCustomMiddleware>();
```

### Transport Flexibility

Switch from stdio to HTTP (or vice versa) by changing one config setting:
```json
{
  "Transport": "stdio"  // or "http"
}
```

---

## Performance Considerations

1. **HttpClientFactory**: Reuses connections and handles DNS resolution efficiently
2. **Async/Await**: All I/O operations are non-blocking
3. **Rate Limiting**: Sliding window prevents agentic loops
4. **Resilience**: Retry + circuit breaker prevents cascading failures
5. **Structured Logging**: Async file writes minimize blocking

---

## Summary

The MCP Server Template architecture is built on these principles:

- **Layered Design**: Each layer has a specific responsibility
- **Pluggable Providers**: Easy to add/remove API integrations
- **Security by Default**: Authentication, validation, rate limits pre-configured
- **Observable**: Detailed logging, correlation IDs for debugging
- **Resilient**: Retry logic, circuit breakers, graceful error handling
- **Non-blocking**: Async/await throughout for performance

This structure allows you to focus on implementing your provider's business logic while the framework handles cross-cutting concerns.
