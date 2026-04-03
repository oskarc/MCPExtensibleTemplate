# MCP Server Template

A production-ready [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server built with .NET 8 and the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk). Ships with three working providers — SMHI weather forecasts, historical observations, and JSONPlaceholder (fake REST API for testing) — that serve as working examples you can replace with your own API integrations.

## Features

- **Dual transport** — stdio for IDE/local use, HTTP with SSE for hosted multi-client deployments
- **API key authentication** — constant-time validated `X-Api-Key` header (HTTP mode)
- **Per-tool rate limiting** — sliding window throttle to catch agentic loops before they spiral
- **Per-client (IP) rate limiting** — HTTP-level protection via ASP.NET Core middleware
- **HTTP resilience** — retry with exponential backoff + circuit breaker on upstream calls
- **Structured logging** — Serilog to stderr + rolling files with correlation IDs per tool call
- **Input validation** — coordinate bounds, parameter allowlists, period allowlists, HTTPS-only base URLs
- **Response guardrails** — byte-level size limits, schema validation on upstream data
- **Kestrel hardening** — request body size cap, connection limits, header timeouts
- **CORS control** — deny-all by default, configurable allowed origins
- **Environment configs** — Development (verbose, relaxed limits) and Production (warnings, strict)
- **35 unit tests** — xUnit, covering formatters, coordinate validation, error mapping

## Project Structure

```
McpServerTemplate/
├── Program.cs                        # Entry point — transport, DI, middleware
├── Infrastructure/
│   ├── ApiKeyMiddleware.cs           # X-Api-Key authentication (HTTP mode)
│   ├── ToolCallLoggingFilter.cs      # Correlation IDs, timing, arg sanitization
│   ├── ToolCallThrottleFilter.cs     # Per-tool sliding window rate limiter
│   └── HealthProbe.cs               # Startup upstream connectivity check
├── Providers/
│   ├── JsonPlaceholder/              # Fake REST API provider (testing/demo)
│   │   ├── JsonPlaceholderApiClient.cs       # Typed HTTP client
│   │   ├── JsonPlaceholderConfig.cs          # Strongly-typed config
│   │   ├── JsonPlaceholderFormatters.cs      # LLM-optimized output
│   │   ├── JsonPlaceholderServiceRegistration.cs # DI registration
│   │   ├── JsonPlaceholderTools.cs           # MCP tools (Get/CreateBlogPost, etc.)
│   │   └── Models/                           # DTOs (Post, Comment, Todo)
│   ├── Smhi/                         # Weather forecast provider (example)
│   │   ├── SmhiApiClient.cs          # Typed HTTP client with resilience
│   │   ├── SmhiConfig.cs             # Strongly-typed config
│   │   ├── SmhiFormatters.cs         # LLM-optimized output formatting
│   │   ├── SmhiPrompts.cs            # MCP prompt templates
│   │   ├── SmhiResources.cs          # MCP resources (symbol codes, coverage)
│   │   ├── SmhiServiceRegistration.cs# DI registration
│   │   ├── SmhiTools.cs              # MCP tools (GetForecast, GetCurrentWeather, etc.)
│   │   └── Models/                   # API response DTOs
│   └── SmhiObs/                      # Historical observations provider (example)
│       ├── SmhiObsApiClient.cs
│       ├── SmhiObsTools.cs           # GetRecentTemperature, GetTemperatureHistory, etc.
│       └── ...
├── appsettings.json                  # Base configuration
├── appsettings.Development.json      # Debug logging, relaxed rate limits
├── appsettings.Production.json       # Warning level, strict limits
└── docs/                             # Comprehensive documentation
    ├── README.md                     # Documentation index and navigation
    ├── 01-ARCHITECTURE.md            # Technical deep dive & design patterns
    ├── 02-ARCHITECTURE-FLOWCHARTS.md # 10 visual flowcharts (Mermaid diagrams)
    ├── 03-TESTING-STRATEGY.md        # Testing approach & examples
    ├── 04-CONFIGURATION.md           # Complete configuration reference
    └── 05-USAGE-GUIDE-BEGINNERS.md   # Beginner-friendly guide with examples
```

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later

### Build

```bash
cd McpServerTemplate
dotnet build
```

### Run in stdio mode (IDE / local)

```bash
cd McpServerTemplate
dotnet run
```

The server communicates over stdin/stdout using the MCP protocol. Connect it from any MCP-compatible client (VS Code, Claude Desktop, etc.).

### Run in HTTP mode (hosted)

```bash
cd McpServerTemplate
dotnet run -- --Transport http --Authentication:ApiKey "your-secret-key"
```

Or via environment variables:

```powershell
$env:Transport = "http"
$env:Authentication__ApiKey = "your-secret-key"
dotnet run
```

Or on bash/zsh:

```bash
export Transport=http
export Authentication__ApiKey=your-secret-key
dotnet run
```

The server starts on `http://localhost:3001`. All requests require the `X-Api-Key` header:

```bash
curl -H "X-Api-Key: your-secret-key" http://localhost:3001/mcp
```

### Run tests

```bash
cd McpServerTemplate.Tests
dotnet test
```

## Client Configuration

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "weather": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/McpServerTemplate"]
    }
  }
}
```

Or connect to a running HTTP instance:

```json
{
  "mcpServers": {
    "weather": {
      "url": "http://localhost:3001/mcp",
      "headers": {
        "X-Api-Key": "your-secret-key"
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
      "args": ["run", "--project", "/absolute/path/to/McpServerTemplate"]
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
      "args": ["run", "--project", "/absolute/path/to/McpServerTemplate"]
    }
  }
}
```

## Configuration

All settings live in `appsettings.json` and can be overridden via environment variables or command-line args.

| Setting | Default | Description |
|---------|---------|-------------|
| `Transport` | `stdio` | `stdio` or `http` |
| `HttpTransport:Port` | `3001` | HTTP listen port |
| `HttpTransport:BindAddress` | `localhost` | Bind address (`localhost`, `0.0.0.0`, etc.) |
| `HttpTransport:AllowedOrigins` | `[]` | CORS allowed origins (empty = deny all) |
| `Authentication:ApiKey` | `""` | Required API key for HTTP mode |
| `RateLimit:MaxCallsPerToolPerMinute` | `10` | Per-tool rate limit (agentic loop protection) |
| `Providers:JsonPlaceholder:BaseUrl` | `https://jsonplaceholder.typicode.com` | Fake REST API (must be HTTPS) |
| `Providers:Smhi:BaseUrl` | SMHI API URL | Must be absolute HTTPS |
| `Providers:SmhiObs:BaseUrl` | SMHI Obs API URL | Must be absolute HTTPS |

### Environment-specific overrides

- **Development** (`ASPNETCORE_ENVIRONMENT=Development`) — Debug logging, 30 calls/min rate limit, `-dev` user agent
- **Production** (`ASPNETCORE_ENVIRONMENT=Production`) — Warning level, 10 calls/min, 30-day log retention

## Documentation

Comprehensive documentation is available in the [docs/](docs/) folder:

- **[docs/README.md](docs/README.md)** — Navigation guide, learning paths by role
- **[docs/01-ARCHITECTURE.md](docs/01-ARCHITECTURE.md)** — Technical deep dive: 4-layer architecture, data flow, DI patterns
- **[docs/02-ARCHITECTURE-FLOWCHARTS.md](docs/02-ARCHITECTURE-FLOWCHARTS.md)** — 10 visual flowcharts (Mermaid diagrams)
- **[docs/03-TESTING-STRATEGY.md](docs/03-TESTING-STRATEGY.md)** — Testing approach, xUnit examples, best practices
- **[docs/04-CONFIGURATION.md](docs/04-CONFIGURATION.md)** — Complete config reference, environment selection, scenarios
- **[docs/05-USAGE-GUIDE-BEGINNERS.md](docs/05-USAGE-GUIDE-BEGINNERS.md)** — Newbie-friendly guide with step-by-step examples

**New to the project?** Start with [docs/05-USAGE-GUIDE-BEGINNERS.md](docs/05-USAGE-GUIDE-BEGINNERS.md).

**Building features?** Read [docs/01-ARCHITECTURE.md](docs/01-ARCHITECTURE.md).

## MCP Tools

### JSONPlaceholder (Fake REST API - Testing/Demo)

| Tool | Description |
|------|-------------|
| `GetBlogPost` | Retrieve a blog post (ID 1-100) |
| `CreateBlogPost` | Create a new blog post (demonstrates POST) |
| `GetPostComments` | View comments on a post |
| `AddPostComment` | Add a comment to a post (demonstrates POST) |
| `GetUserTodos` | View a user's todo list |
| `CreateUserTodo` | Create a new todo item (demonstrates POST) |

### SMHI Forecast

| Tool | Description |
|------|-------------|
| `GetForecast` | Multi-day weather forecast for coordinates in Northern Europe |
| `GetCurrentWeather` | Current conditions snapshot (temperature, wind, humidity, etc.) |
| `GetForecastModelInfo` | When the forecast model was last updated |

### SMHI Observations

| Tool | Description |
|------|-------------|
| `GetRecentTemperature` | Last 24h of actual temperature readings from nearest station |
| `GetTemperatureHistory` | ~4 months of daily temperature summaries |
| `GetPrecipitationHistory` | ~4 months of daily precipitation totals |
| `GetMonthlyClimate` | Historical monthly climate comparison across years |

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

Every MCP tool call passes through the infrastructure filters in order:

1. **LoggingFilter** — assigns a crypto-random correlation ID, logs arguments (names only at Info level), starts a timer
2. **ThrottleFilter** — checks per-tool sliding window rate limit; rejects immediately if exceeded
3. **Tool execution** — the provider's tool method runs, calling the upstream API through the resilient HTTP client
4. **LoggingFilter** — logs duration and result status with the same correlation ID

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
   - `YourApiTools.cs` — `[McpServerToolType]` class with tool methods
   - `YourApiServiceRegistration.cs` — `AddYourApiProvider()` extension method
   - Optionally: formatters, resources, prompts, models
3. Register in `Program.cs`:
   ```csharp
   builder.Services.AddYourApiProvider(builder.Configuration);
   ```
4. Add config in `appsettings.json`:
   ```json
   "Providers": {
     "YourApi": {
       "BaseUrl": "https://api.example.com",
       "UserAgent": "McpServerTemplate/1.0"
     }
   }
   ```

Tools, resources, and prompts are auto-discovered via `WithToolsFromAssembly()` — no additional wiring needed.

## Security

- **Authentication** — API key required for all HTTP requests (constant-time comparison)
- **Input validation** — coordinates, parameter IDs, and period values are validated against allowlists
- **Response limits** — upstream responses are byte-counted (not Content-Length) and capped
- **HTTPS enforcement** — provider base URLs must be absolute HTTPS at startup
- **Kestrel hardening** — 1 MB request body limit, 100 max connections, 30 s header timeout
- **Rate limiting** — per-tool (agentic loop protection) + per-IP (HTTP abuse protection)
- **Log safety** — argument values logged at Debug only; log paths validated against traversal
- **CORS** — deny-all by default when in HTTP mode
- **Binding** — defaults to `localhost`, not `0.0.0.0`

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Server exits immediately in stdio mode | This is normal if no client is connected. Use a MCP client (Claude Desktop, VS Code) to connect. |
| `401 Unauthorized` in HTTP mode | Ensure the `X-Api-Key` header matches the configured `Authentication:ApiKey` value. |
| `429 Too Many Requests` on tool calls | Rate limit hit. Default is 10 calls/tool/min. Increase `RateLimit:MaxCallsPerToolPerMinute` or wait. |
| Upstream API errors / timeouts | Check your network connection. The circuit breaker will auto-recover after 15 s. See logs for details. |
| `HTTPS required` startup error | Provider `BaseUrl` values in config must use `https://`. HTTP is not allowed for security. |
| No tools showing up in client | Ensure the project builds successfully. Tool classes need the `[McpServerToolType]` attribute. |

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) | 1.2.0 | Official MCP SDK |
| [ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) | 1.2.0 | HTTP/SSE transport |
| [Microsoft.Extensions.Http.Resilience](https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience) | 10.1.0 | Retry + circuit breaker |
| [Microsoft.Extensions.Caching.Memory](https://www.nuget.org/packages/Microsoft.Extensions.Caching.Memory) | 10.0.5 | Station metadata caching |
| [Serilog.AspNetCore](https://www.nuget.org/packages/Serilog.AspNetCore) | 9.0.0 | Structured logging |
| [Serilog.Sinks.File](https://www.nuget.org/packages/Serilog.Sinks.File) | 6.0.0 | Rolling file log sink |

## License

This project is licensed under the [MIT License](LICENSE).
