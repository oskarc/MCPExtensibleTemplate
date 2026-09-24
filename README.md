# MCP Server Template

A production-ready [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server built with .NET 10 and the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk). Ships with three working providers — SMHI weather forecasts, historical observations, and JSONPlaceholder (fake REST API for testing) — that serve as working examples you can replace with your own API integrations.

## Features

- **Dual transport** — stdio for IDE/local use (Development only), stateless streamable HTTP for hosted multi-client deployments
- **Bearer-token identity** — OAuth 2.1 resource server; each provider answers to exactly one configured identity provider
- **A policy per provider** — every tool, resource and prompt declares its scope, risk class and limits; anything undeclared refuses startup, and any request kind the frame does not govern is refused
- **Per-caller rate limiting** — sliding windows per caller and per caller per tool, held in Redis so they apply across instances
- **Risk gates** — write tools need a recent token; irreversible tools need a single-use confirmation tied to the exact arguments
- **Per-client (IP) rate limiting** — HTTP-level protection via ASP.NET Core middleware
- **HTTP resilience** — retry with exponential backoff + circuit breaker on upstream calls
- **Structured logging** — Serilog to stderr + rolling files with correlation IDs per tool call
- **Input validation** — coordinate bounds, parameter allowlists, period allowlists, HTTPS-only base URLs
- **Response guardrails** — byte-level size limits, schema validation on upstream data
- **Kestrel hardening** — request body size cap, connection limits, header timeouts
- **CORS control** — deny-all by default, configurable allowed origins
- **Environment configs** — Development (verbose, every provider, stdio allowed) and Production (warnings, Redis and a declared proxy required); Production is the default
- **Tested end to end** — xUnit: unit tests, the shipped HTTP server composed in-process with real tokens, the real program as a child process, and Redis in a container via Testcontainers

## Project Structure

```
McpServerTemplate/
├── Program.cs                        # Entry point — transport, DI, middleware
├── Infrastructure/
│   ├── Frame/                        # The policy frame: policies, request gate, limits, confirmation, startup checks
│   ├── Identity/                     # Bearer-token identity, one scheme per identity provider
│   ├── ToolCallLoggingFilter.cs      # Correlation IDs, timing, arg sanitization
│   └── HealthEndpoints.cs            # /healthz and /readyz
├── Providers/
│   ├── BuiltInProviders.cs           # The provider modules this server ships with
│   ├── JsonPlaceholder/              # Fake REST API provider (testing/demo)
│   │   ├── JsonPlaceholderModule.cs          # Name, policy, types, settings
│   │   ├── JsonPlaceholderApiClient.cs       # Typed HTTP client
│   │   ├── JsonPlaceholderConfig.cs          # Strongly-typed config
│   │   ├── JsonPlaceholderFormatters.cs      # LLM-optimized output
│   │   ├── JsonPlaceholderServiceRegistration.cs # DI registration
│   │   ├── JsonPlaceholderTools.cs           # MCP tools (get_blog_post, create_blog_post, etc.)
│   │   └── Models/                           # DTOs (Post, Comment, Todo)
│   ├── Smhi/                         # Weather forecast provider (example)
│   │   ├── SmhiModule.cs             # Name, policy, types, settings
│   │   ├── SmhiApiClient.cs          # Typed HTTP client with resilience
│   │   ├── SmhiConfig.cs             # Strongly-typed config
│   │   ├── SmhiFormatters.cs         # LLM-optimized output formatting
│   │   ├── SmhiPrompts.cs            # MCP prompt templates
│   │   ├── SmhiResources.cs          # MCP resources (symbol codes, coverage)
│   │   ├── SmhiServiceRegistration.cs# DI registration
│   │   ├── SmhiTools.cs              # MCP tools (get_forecast, get_current_weather, etc.)
│   │   └── Models/                   # API response DTOs
│   └── SmhiObs/                      # Historical observations provider (example)
│       ├── SmhiObsApiClient.cs
│       ├── SmhiObsTools.cs           # get_recent_temperature, get_temperature_history, etc.
│       └── ...
├── appsettings.json                  # Base configuration
├── appsettings.Development.json      # Debug logging, every provider, the stdio principal
├── appsettings.Production.json       # Warning level, the SMHI providers only
└── docs/                             # Comprehensive documentation
    ├── README.md                     # Documentation index and navigation
    ├── 01-ARCHITECTURE.md            # Technical deep dive & design patterns
    ├── 02-ARCHITECTURE-FLOWCHARTS.md # 10 visual flowcharts (Mermaid diagrams)
    ├── 03-TESTING-STRATEGY.md        # Testing approach & examples
    ├── 04-CONFIGURATION.md           # Complete configuration reference
    ├── 05-USAGE-GUIDE-BEGINNERS.md   # Beginner-friendly guide with examples
    ├── 06-SECURITY-ROADMAP.md        # Security hardening roadmap (strict form)
    ├── 07-SECURITY-FLOWCHARTS.md     # 17 diagrams of the hardened design
    └── artifacts/                    # Interactive field guide and HTML roadmap
```

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/), to run the tests (they start Redis in a container)

### Build

```bash
cd McpServerTemplate
dotnet build
```

### Run in stdio mode (IDE / local)

stdio runs only in Development; with no environment set the server runs as Production and refuses it.

```bash
cd McpServerTemplate
export ASPNETCORE_ENVIRONMENT=Development    # PowerShell: $env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

The server communicates over stdin/stdout using the MCP protocol, as the Development principal declared under `Development:DevPrincipal`, through the same checks an HTTP caller meets. Connect it from any MCP-compatible client (VS Code, Claude Desktop, etc.).

### Run in HTTP mode (hosted)

HTTP mode needs an identity provider that issues the bearer tokens callers present:

```bash
cd McpServerTemplate
export ASPNETCORE_ENVIRONMENT=Development
export Transport=http
export Authentication__Resource=https://localhost:3001/
export Authentication__IdentityProviders__corp__Authority=https://your-idp/realms/corp
export Authentication__IdentityProviders__corp__Issuer=https://your-idp/realms/corp
export Authentication__IdentityProviders__corp__Algorithms__0=RS256
export Authentication__IdentityProviders__corp__ScopeCatalog__0=weather:read
export Authentication__IdentityProviders__corp__ScopeCatalog__1=observations:read
export Authentication__IdentityProviders__corp__ScopeCatalog__2=demo:read
export Authentication__IdentityProviders__corp__ScopeCatalog__3=demo:write
dotnet run
```

The MCP endpoint is the server root, `http://localhost:3001/`. Every request carries a token; without one the server answers `401`, naming its resource metadata so a client knows where to sign in:

```bash
curl http://localhost:3001/healthz
curl -X POST http://localhost:3001/ -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Outside Development it also needs `Limits:Redis` and a declared proxy. See [docs/04-CONFIGURATION.md](docs/04-CONFIGURATION.md).

### Run tests

With Docker running:

```bash
dotnet test McpServerTemplate.sln
```

## Client Configuration

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "weather": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/McpServerTemplate"],
      "env": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

Or connect to a running HTTP instance — clients that support MCP authorization discover the identity provider from the server's `401`; otherwise pass a token:

```json
{
  "mcpServers": {
    "weather": {
      "url": "http://localhost:3001/",
      "headers": {
        "Authorization": "Bearer <token>"
      }
    }
  }
}
```

### VS Code (Claude Code / Copilot)

Add to your `.vscode/mcp.json`:

```json
{
  "servers": {
    "weather": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/McpServerTemplate"],
      "env": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

### Cursor

Add to your Cursor MCP settings:

```json
{
  "mcpServers": {
    "weather": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/McpServerTemplate"],
      "env": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  }
}
```

## Configuration

All settings live in `appsettings.json` and can be overridden via environment variables or command-line args. The full reference is [docs/04-CONFIGURATION.md](docs/04-CONFIGURATION.md).

| Setting | Default | Description |
|---------|---------|-------------|
| `Transport` | `stdio` | `stdio` or `http` |
| `HttpTransport:Port` | `3001` | HTTP listen port |
| `HttpTransport:BindAddress` | `localhost` | Bind address (`localhost`, `0.0.0.0`, etc.) |
| `HttpTransport:AllowedOrigins` | `[]` | CORS allowed origins (empty = deny all) |
| `Authentication:IdentityProviders:{name}:*` | — | Authority, issuer, algorithms and scope catalog per identity provider (HTTP) |
| `Providers:Enabled` | — | The providers this deployment serves. Required outside Development |
| `Providers:{Name}:IdentityProvider` | — | The one identity provider a provider answers to. Required |
| `Limits:Redis` | — | Redis connection string for per-caller limits and used confirmations. Required outside Development |
| `Limits:PerPrincipalPerMinute` | `120` | Requests per caller per minute, across all instances. Each tool's own limit is in its provider's policy |
| `Confirmation:Key` | — | Base64 key (32+ bytes) signing confirmations. Required if any irreversible tool is enabled |
| `Providers:JsonPlaceholder:BaseUrl` | `https://jsonplaceholder.typicode.com` | Fake REST API (must be HTTPS) |
| `Providers:Smhi:BaseUrl` | SMHI API URL | Must be absolute HTTPS |
| `Providers:SmhiObs:BaseUrl` | SMHI Obs API URL | Must be absolute HTTPS |

### Environment-specific overrides

- **Development** (`ASPNETCORE_ENVIRONMENT=Development`) — Debug logging, all three providers, in-memory limits allowed, `-dev` user agent
- **Production** (`ASPNETCORE_ENVIRONMENT=Production`) — Warning level, the SMHI providers only (the demo provider is not enabled), Redis required, 30-day log retention

A misspelled or retired key in the `Authentication`, `Providers`, `Limits`, `Confirmation` or `Development` sections stops the server from starting and names the nearest real key: a setting the server would silently ignore is refused rather than trusted.

## Documentation

Comprehensive documentation is available in the [docs/](docs/) folder:

- **[docs/README.md](docs/README.md)** — Navigation guide, learning paths by role
- **[docs/01-ARCHITECTURE.md](docs/01-ARCHITECTURE.md)** — Technical deep dive: 4-layer architecture, data flow, DI patterns
- **[docs/02-ARCHITECTURE-FLOWCHARTS.md](docs/02-ARCHITECTURE-FLOWCHARTS.md)** — 10 visual flowcharts (Mermaid diagrams)
- **[docs/03-TESTING-STRATEGY.md](docs/03-TESTING-STRATEGY.md)** — Testing approach, xUnit examples, best practices
- **[docs/04-CONFIGURATION.md](docs/04-CONFIGURATION.md)** — Complete config reference, environment selection, scenarios
- **[docs/05-USAGE-GUIDE-BEGINNERS.md](docs/05-USAGE-GUIDE-BEGINNERS.md)** — Newbie-friendly guide with step-by-step examples
- **[docs/06-SECURITY-ROADMAP.md](docs/06-SECURITY-ROADMAP.md)** — Security hardening roadmap: requirements, target architecture, phased plan, compliance matrix
- **[docs/07-SECURITY-FLOWCHARTS.md](docs/07-SECURITY-FLOWCHARTS.md)** — 17 diagrams of the hardened design: trust boundaries, refusal paths, revocation, audit chain
- **[docs/artifacts/security-guide/index.html](docs/artifacts/security-guide/index.html)** — MCP Hardening Field Guide: 17 interactive chapters explaining each flow and phase (open in a browser)

**New to the project?** Start with [docs/05-USAGE-GUIDE-BEGINNERS.md](docs/05-USAGE-GUIDE-BEGINNERS.md).

**Building features?** Read [docs/01-ARCHITECTURE.md](docs/01-ARCHITECTURE.md).

## MCP Tools

These are the names the server exposes over MCP; a client calls them exactly as written.
Send `tools/list` to a running server to confirm the set.

### JSONPlaceholder (Fake REST API - Testing/Demo)

| Tool | Description |
|------|-------------|
| `get_blog_post` | Retrieve a blog post (ID 1-100) |
| `create_blog_post` | Create a new blog post (demonstrates POST) |
| `get_post_comments` | View comments on a post |
| `add_post_comment` | Add a comment to a post (demonstrates POST) |
| `get_user_todos` | View a user's todo list |
| `create_user_todo` | Create a new todo item (demonstrates POST) |

### SMHI Forecast

| Tool | Description |
|------|-------------|
| `get_forecast` | Multi-day weather forecast for coordinates in Northern Europe |
| `get_current_weather` | Current conditions snapshot (temperature, wind, humidity, etc.) |
| `get_forecast_model_info` | When the forecast model was last updated |

### SMHI Observations

| Tool | Description |
|------|-------------|
| `get_recent_temperature` | Last 24h of actual temperature readings from nearest station |
| `get_temperature_history` | ~4 months of daily temperature summaries |
| `get_precipitation_history` | ~4 months of daily precipitation totals |
| `get_monthly_climate` | One month of the year across every archived year, from SMHI's corrected archive |

### MCP Resources

| URI | Description |
|-----|-------------|
| `smhi://weather-symbols` | Weather symbol code reference (1-27) |
| `smhi://coverage-area` | Geographic coverage boundaries and example coordinates |

### MCP Prompts

| Prompt | Description |
|--------|-------------|
| `ForecastBriefing` | Structured weather briefing template with current conditions + outlook |

## Architecture

### Resilience Pipeline

Upstream HTTP calls go through a resilience pipeline powered by `Microsoft.Extensions.Http.Resilience`:

1. **Retry** — 3 attempts with exponential backoff (500 ms initial delay) for transient failures
2. **Circuit breaker** — opens after 5 failures in 30 s, half-opens after 15 s recovery
3. **Timeout** — 15 s per request, 30 s total including retries

### Tool Call Lifecycle

Every MCP request passes the frame's checks in order:

1. **Request kind** — only the request kinds the frame governs go further; anything else is refused
2. **LoggingFilter** — assigns a crypto-random correlation ID, logs arguments (names only at Info level), starts a timer
3. **Request gate** — signed in → known item → right identity provider → scope → risk gate → per-caller limits → argument shape; each refusal names its rule and is logged as a security event
4. **Tool execution** — the provider's tool method runs, calling the upstream API through the resilient HTTP client
5. **Output cap** — an answer larger than the tool's policy allows is withheld, not cut short

### Output Engineering

Tool responses are formatted for LLM consumption, not raw JSON:

- All values are labelled with units (e.g. `Temperature: 12.3 C`)
- Large datasets are summarized (48-entry forecast cap, 50k observation limit)
- Missing data is handled gracefully with explicit notes
- Recovery hints are included in error messages

## Creating Your Own Provider

See [docs/01-ARCHITECTURE.md#4-provider-layer-api-integration-logic](docs/01-ARCHITECTURE.md#4-provider-layer-api-integration-logic) and [docs/02-ARCHITECTURE-FLOWCHARTS.md#10-adding-a-new-provider-step-by-step](docs/02-ARCHITECTURE-FLOWCHARTS.md#10-adding-a-new-provider-step-by-step) for detailed guidance.

Quick steps:

1. Create a folder under `Providers/YourApi/`
2. Add these files following the JsonPlaceholder or SMHI pattern:
   - `YourApiConfig.cs` — strongly-typed config record
   - `YourApiClient.cs` — typed HTTP client with input validation
   - `YourApiTools.cs` — a **static** `[McpServerToolType]` class with tool methods (resources and prompts likewise)
   - `YourApiServiceRegistration.cs` — `AddYourApiProvider()` extension method
   - `YourApiModule.cs` — an `IProviderModule`: the provider's name, its types, and its **policy** — a scope, risk class and per-caller limit for every tool, a scope for every resource and prompt, and the hosts it may call
3. List the module in `Providers/BuiltInProviders.cs`
4. Add config, and name the provider in `Providers:Enabled`:
   ```json
   "Providers": {
     "Enabled": [ "Smhi", "SmhiObs", "YourApi" ],
     "YourApi": {
       "BaseUrl": "https://api.example.com",
       "UserAgent": "McpServerTemplate/1.0",
       "IdentityProvider": "corp"
     }
   }
   ```

Nothing is found by scanning the assembly: a class no module names is not served. A tool without a policy, a policy without a tool, a scope the provider's identity provider cannot issue, or a `BaseUrl` outside the policy's hosts stops the server from starting, with a message saying what to fix.

## Security

- **Authentication** — a bearer token from a configured identity provider on every HTTP request; the caller's trust domain decides which providers it can reach
- **Policy frame** — every request is checked against its provider's declared policy; a provider cannot remove or add to the frame's checks
- **Input validation** — coordinates, parameter IDs, and period values are validated against allowlists
- **Response limits** — every tool's answer is capped by its policy; the SMHI clients also byte-count upstream responses (enforcing each provider's declared upstream cap for every provider is the next phase's work)
- **HTTPS enforcement** — provider base URLs must be absolute HTTPS at startup
- **Kestrel hardening** — 1 MB request body limit, 100 max connections, 30 s header timeout
- **Rate limiting** — per caller and per caller per tool, across instances (agentic loop protection) + per-IP (HTTP abuse protection)
- **Log safety** — argument values logged at Debug only; log paths validated against traversal
- **CORS** — deny-all by default when in HTTP mode
- **Binding** — defaults to `localhost`, not `0.0.0.0`

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Server exits with code 78 | A configuration problem; the last log line names it. In stdio mode the usual cause is a missing `ASPNETCORE_ENVIRONMENT=Development`. A misspelled setting is named with the nearest real key. |
| `401 Unauthorized` in HTTP mode | Present a bearer token from a configured identity provider, issued for exactly the audience in `Authentication:Resource`. |
| `429 Too Many Requests` | The per-address HTTP limit (60 per minute). |
| `excess_rate_limit_exceeded` from a tool | Your per-caller limit (`caller-rate`, from `Limits:PerPrincipalPerMinute`) or the tool's own (`tool-rate`, in its provider's policy). Wait a minute. |
| Upstream API errors / timeouts | Check your network connection. The circuit breaker will auto-recover after 15 s. See logs for details. |
| `HTTPS required` startup error | Provider `BaseUrl` values in config must use `https://`. HTTP is not allowed for security. |
| No tools showing up in client | A caller sees only the tools whose scope it holds, from providers bound to its identity provider. Check the token's scopes (or `Development:DevPrincipal:Scopes`) and `Providers:Enabled`. |

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) | 2.2.0 | Official MCP SDK |
| [ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) | 2.2.0 | Streamable HTTP transport, protected-resource metadata |
| [Microsoft.AspNetCore.Authentication.JwtBearer](https://www.nuget.org/packages/Microsoft.AspNetCore.Authentication.JwtBearer) | 10.0.11 | Bearer-token validation |
| [JsonSchema.Net](https://www.nuget.org/packages/JsonSchema.Net) | 9.4.0 | Tool arguments checked against their schema |
| [StackExchange.Redis](https://www.nuget.org/packages/StackExchange.Redis) | 3.3.1 | Per-caller limits and used confirmations, across instances |
| [Microsoft.Extensions.Http.Resilience](https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience) | 10.1.0 | Retry + circuit breaker |
| [Serilog.AspNetCore](https://www.nuget.org/packages/Serilog.AspNetCore) | 9.0.0 | Structured logging |
| [Serilog.Sinks.File](https://www.nuget.org/packages/Serilog.Sinks.File) | 6.0.0 | Rolling file log sink |

## License

This project is licensed under the [MIT License](LICENSE).
