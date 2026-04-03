# Configuration Guide

## Overview

The MCP Server Template uses a hierarchical configuration system where settings can come from multiple sources, with clear precedence rules. This guide explains all configuration options and how to set them up.

---

## Configuration Hierarchy (Top Priority First)

1. **Environment Variables** (highest priority)
2. **appsettings.{Environment}.json** (Development, Production, Staging, etc.)
3. **appsettings.json** (base/default)
4. **Hard-coded defaults in code** (lowest priority)

**Example**: If a setting exists in all three, the environment variable wins.

```
Environment Variable: MyValue=env
    appsettings.Production.json: MyValue=prod
    appsettings.json: MyValue=default
    
→ Result: env
```

---

## Configuration Files Explained

### 1. appsettings.json (Base Configuration)

This is the **default configuration** that applies to all environments.

```json
{
  "Transport": "stdio",
  "HttpTransport": {
    "Port": 3001,
    "BindAddress": "localhost",
    "AllowedOrigins": []
  },
  "Authentication": {
    "ApiKey": ""
  },
  "RateLimit": {
    "MaxCallsPerToolPerMinute": 10
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "standardErrorFromLevel": "Verbose",
          "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "logs/mcp-server-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 14
        }
      }
    ]
  },
  "Providers": {
    "JsonPlaceholder": {
      "BaseUrl": "https://jsonplaceholder.typicode.com",
      "UserAgent": "McpServerTemplate/1.0"
    },
    "Smhi": {
      "BaseUrl": "https://opendata-download-metfcst.smhi.se",
      "UserAgent": "McpServerTemplate/1.0"
    }
  }
}
```

### 2. appsettings.Development.json

Applied **only** when `ASPNETCORE_ENVIRONMENT=Development`.

Used for **local development** with relaxed limits and verbose logging.

```json
{
  "RateLimit": {
    "MaxCallsPerToolPerMinute": 30
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  }
}
```

### 3. appsettings.Production.json

Applied **only** when `ASPNETCORE_ENVIRONMENT=Production`.

Used for **hosted deployments** with strict limits and minimal logging.

```json
{
  "RateLimit": {
    "MaxCallsPerToolPerMinute": 10
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning"
    }
  }
}
```

---

## Configuration Keys Reference

### Transport Settings

#### `Transport`
- **Type**: `string`
- **Options**: `"stdio"`, `"http"`
- **Default**: `"stdio"`
- **Description**: How the server communicates with clients
  - `stdio`: Communicates via stdin/stdout (local development, IDE integration)
  - `http`: Runs as a web server with HTTP + SSE (hosted multi-client deployments)

**Example**:
```json
{
  "Transport": "http"
}
```

---

### HTTP Transport Settings

#### `HttpTransport:Port`
- **Type**: `int`
- **Default**: `3001`
- **Applies**: Only when `Transport=http`
- **Description**: The port the HTTP server listens on

```powershell
# Set via environment variable in PowerShell
$env:HttpTransport__Port = "8080"
```

```bash
# Set via environment variable in bash/zsh
export HttpTransport__Port=8080

# Set via appsettings.json
{
  "HttpTransport": {
    "Port": 8080
  }
}
```

#### `HttpTransport:BindAddress`
- **Type**: `string`
- **Default**: `"localhost"`
- **Applies**: Only when `Transport=http`
- **Description**: The IP address to bind to
  - `"localhost"` or `"127.0.0.1"`: Only accessible from this machine
  - `"0.0.0.0"`: Accessible from any IP (use only behind a firewall!)

```json
{
  "HttpTransport": {
    "BindAddress": "0.0.0.0"
  }
}
```

#### `HttpTransport:AllowedOrigins`
- **Type**: `string[]` (array of URLs)
- **Default**: `[]` (deny all)
- **Applies**: Only when `Transport=http`
- **Description**: CORS allowed origins (for browser-based clients)
  - Empty = deny all cross-origin requests (safest default)
  - List origins like `"https://example.com"`

```json
{
  "HttpTransport": {
    "AllowedOrigins": [
      "https://example.com",
      "https://app.example.com"
    ]
  }
}
```

---

### Authentication Settings

#### `Authentication:ApiKey`
- **Type**: `string`
- **Default**: `""` (empty — **required** when using HTTP transport)
- **Applies**: Only when `Transport=http`
- **Description**: Secret key used to authenticate HTTP requests
  - The middleware **throws at startup** if this is empty when HTTP transport is enabled
  - Clients must send `X-Api-Key: {this-key}` header with every request
  - Use a strong random string (minimum 32 characters recommended)
  - Never commit real keys to git; use environment variables instead

```powershell
# Generate a secure key in PowerShell
[Convert]::ToHexString((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

```bash
# Generate a secure key in bash/zsh
openssl rand -hex 32

# Result: a3f5b9c2d1e4f8a6b2c5d9e3f1a4b7c0d2e5f8a1b4c7d0e3f6a9b2c5d8e1f4

# Set via environment variable in bash/zsh (never in code!)
export Authentication__ApiKey=a3f5b9c2d1e4f8a6b2c5d9e3f1a4b7c0d2e5f8a1b4c7d0e3f6a9b2c5d8e1f4
```

```powershell
# Set via environment variable in PowerShell (never in code!)
$env:Authentication__ApiKey = "a3f5b9c2d1e4f8a6b2c5d9e3f1a4b7c0d2e5f8a1b4c7d0e3f6a9b2c5d8e1f4"
```

---

### Rate Limiting Settings

#### `RateLimit:MaxCallsPerToolPerMinute`
- **Type**: `int`
- **Default**: `10`
- **Description**: Maximum number of times a single tool can be called per minute
  - Prevents agentic loops and runaway AI processes
  - Applied per tool (not globally)
  - Examples:
    - `10`: Moderate (good for production)
    - `100`: Relaxed (good for development)
    - `1000`: Very relaxed (good for manual testing)

```json
{
  "RateLimit": {
    "MaxCallsPerToolPerMinute": 20
  }
}
```

**What happens when limit is exceeded**:
```
✗ 429 Too Many Requests
Tool 'GetBlogPost' exceeded rate limit: 10 calls per minute
```

---

### Logging Settings (Serilog)

#### `Serilog:MinimumLevel:Default`
- **Type**: `string`
- **Options**: `"Verbose"`, `"Debug"`, `"Information"`, `"Warning"`, `"Error"`, `"Fatal"`
- **Default**: `"Information"`
- **Description**: Global log level (what messages are recorded)
  - `Verbose`: Most detailed (includes framework chatter)
  - `Debug`: Detailed (good for development)
  - `Information`: Normal (good for production)
  - `Warning`: Only problems
  - `Error`: Only errors
  - `Fatal`: Only fatal crashes

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  }
}
```

#### `Serilog:MinimumLevel:Override`
- **Type**: `object`
- **Description**: Override log levels for specific namespaces
  - Useful to suppress verbose logs from framework libraries

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft": "Warning",      // Suppress ASP.NET Core debug logs
        "System": "Warning",         // Suppress System namespace logs
        "System.Net": "Information"  // But keep System.Net at Information
      }
    }
  }
}
```

#### `Serilog:WriteTo`
- **Type**: `array`
- **Description**: Where logs are written (console, files, etc.)

**Console output**:
```json
{
  "Name": "Console",
  "Args": {
    "standardErrorFromLevel": "Verbose",
    "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}"
  }
}
```

**File output**:
```json
{
  "Name": "File",
  "Args": {
    "path": "logs/mcp-server-.log",
    "rollingInterval": "Day",
    "retainedFileCountLimit": 14,
    "fileSizeLimitBytes": 10485760
  }
}
```

---

### Provider Settings

Each provider has its own configuration section.

#### Example: JsonPlaceholder Provider

```json
{
  "Providers": {
    "JsonPlaceholder": {
      "BaseUrl": "https://jsonplaceholder.typicode.com",
      "UserAgent": "McpServerTemplate/1.0"
    }
  }
}
```

- **BaseUrl**: URL of the external API
  - Must be HTTPS (validated for security)
  - Prevents SSRF attacks
- **UserAgent**: HTTP User-Agent header
  - Identifies your server to the external API

#### Example: Weather (SMHI) Provider

```json
{
  "Providers": {
    "Smhi": {
      "BaseUrl": "https://opendata-download-metfcst.smhi.se",
      "UserAgent": "McpServerTemplate/1.0"
    }
  }
}
```

---

## Environment Variables

Use environment variables to override any JSON configuration (highest priority).

### Naming Convention

JSON nested keys → environment variables using `__` (double underscore)

**Examples**:

```json
// appsettings.json
{
  "Transport": "stdio",
  "HttpTransport": {
    "Port": 3001
  },
  "Providers": {
    "JsonPlaceholder": {
      "BaseUrl": "https://..."
    }
  }
}

// Equivalent environment variables:
$env:Transport = "http"
$env:HttpTransport__Port = "8080"
$env:Providers__JsonPlaceholder__BaseUrl = "https://custom-api.com"
```

### Setting Environment Variables

**Linux/Mac**:
```bash
export Transport=http
export Authentication__ApiKey=your-secret-key
export RateLimit__MaxCallsPerToolPerMinute=20

dotnet run
```

**Windows PowerShell**:
```powershell
$env:Transport = "http"
$env:Authentication__ApiKey = "your-secret-key"
$env:RateLimit__MaxCallsPerToolPerMinute = "20"

dotnet run
```

**Docker**:
```dockerfile
ENV Transport=http
ENV Authentication__ApiKey=your-secret-key
ENV RateLimit__MaxCallsPerToolPerMinute=20
```

**Docker Compose**:
```yaml
services:
  mcp-server:
    image: mcp-server:latest
    environment:
      - Transport=http
      - Authentication__ApiKey=your-secret-key
      - RateLimit__MaxCallsPerToolPerMinute=20
    ports:
      - "3001:3001"
```

---

## Environment Selection

The server automatically selects configuration based on the `ASPNETCORE_ENVIRONMENT` variable.

### Default Behavior

1. If `ASPNETCORE_ENVIRONMENT` is not set → treated as `"Production"`
2. If `ASPNETCORE_ENVIRONMENT=Development` → load `appsettings.Development.json`
3. If `ASPNETCORE_ENVIRONMENT=Production` → load `appsettings.Production.json`

### Setting the Environment

**Linux/Mac**:
```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run
```

**Windows PowerShell**:
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

**appsettings.json** (base):
```json
{
  "RateLimit": { "MaxCallsPerToolPerMinute": 10 }
}
```

**appsettings.Development.json** (override):
```json
{
  "RateLimit": { "MaxCallsPerToolPerMinute": 30 }
}
```

**Result** (when `ASPNETCORE_ENVIRONMENT=Development`):
```
MaxCallsPerToolPerMinute = 30  (from Development file, overrides base)
```

---

## Common Configuration Scenarios

### Scenario 1: Local Development

**Goal**: Fast iteration, relaxed limits, verbose logging

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Transport = "stdio"

dotnet run
```

Or on bash/zsh:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export Transport=stdio

dotnet run
```

**Effective config**:
- Transport: stdio
- Max calls per tool per minute: 30
- Log level: Debug
- Server listens on stdin/stdout

---

### Scenario 2: Local Testing (HTTP Mode)

**Goal**: Test HTTP mode locally

```powershell
$env:Transport = "http"
$env:HttpTransport__Port = "3001"
$env:HttpTransport__BindAddress = "localhost"
$env:Authentication__ApiKey = "test-key-12345"

dotnet run
```

Or on bash/zsh:

```bash
export Transport=http
export HttpTransport__Port=3001
export HttpTransport__BindAddress=localhost
export Authentication__ApiKey=test-key-12345

dotnet run
```

**Then test**:
```bash
curl -H "X-Api-Key: test-key-12345" http://localhost:3001/mcp
```

---

### Scenario 3: Production Deployment

**Goal**: Secure, monitored, strict limits

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Transport = "http"
$env:HttpTransport__Port = "3001"
$env:HttpTransport__BindAddress = "0.0.0.0"
$env:Authentication__ApiKey = [Convert]::ToHexString((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
$env:RateLimit__MaxCallsPerToolPerMinute = "10"

dotnet run
```

Or on bash/zsh:

```bash
export ASPNETCORE_ENVIRONMENT=Production
export Transport=http
export HttpTransport__Port=3001
export HttpTransport__BindAddress=0.0.0.0
export Authentication__ApiKey=$(openssl rand -hex 32)
export RateLimit__MaxCallsPerToolPerMinute=10

dotnet run
```

**Effective config**:
- Transport: HTTP (open to network)
- Rate limit: 10 calls per tool per minute (strict)
- Log level: Warning (minimal)
- API key required for all requests
- Logs written to file with rolling interval (14 day retention)

---

### Scenario 4: Using Different APIs per Environment

**appsettings.json** (base):
```json
{
  "Providers": {
    "MyApi": {
      "BaseUrl": "https://api.example.com"
    }
  }
}
```

**appsettings.Development.json** (dev):
```json
{
  "Providers": {
    "MyApi": {
      "BaseUrl": "https://dev-api.example.com"
    }
  }
}
```

**Result**: Auto-switches API based on environment

---

## Validation & Security

### HTTPS-Only Validation

All provider `BaseUrl` values must be HTTPS:

```csharp
// In JsonPlaceholderServiceRegistration.cs
if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
    baseUri.Scheme != "https")
{
    throw new InvalidOperationException(
        $"BaseUrl must be HTTPS, got: '{config.BaseUrl}'");
}
```

**Why**: Prevents accidental use of unencrypted HTTP in production (SSRF vulnerability)

### API Key Validation

The API key must be present when running in HTTP mode. If the key is empty or missing, the middleware **throws at startup**:

```csharp
// In ApiKeyMiddleware.cs
if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException(
        "Authentication:ApiKey must be configured when using HTTP transport.");
}
```

**Why**: Prevents unauthorized access to your MCP tools

---

## Troubleshooting

### "Configuration section 'Providers:JsonPlaceholder' not found"

**Cause**: The JSON section is missing from `appsettings.json`

**Fix**: Add the provider section:
```json
{
  "Providers": {
    "JsonPlaceholder": {
      "BaseUrl": "https://jsonplaceholder.typicode.com"
    }
  }
}
```

---

### "API must be HTTPS"

**Cause**: Provider URL is `http://` instead of `https://`

**Fix**: Use HTTPS:
```json
{
  "Providers": {
    "MyApi": {
      "BaseUrl": "https://my-api.com"  // ← https not http
    }
  }
}
```

---

### Rate Limit Exceeded

**Cause**: Tool called too many times in one minute

**Current limit**: Check `RateLimit:MaxCallsPerToolPerMinute`

**Fix**: Either wait 1 minute, or increase the limit:
```powershell
$env:RateLimit__MaxCallsPerToolPerMinute = "100"
```

```bash
export RateLimit__MaxCallsPerToolPerMinute=100
```

---

### Logs Not Appearing

**Cause**: Log level too high (filtering out messages)

**Check**: Is `Serilog:MinimumLevel:Default` set to `"Warning"` or higher?

**Fix**: Lower to `"Debug"` or `"Information"`:
```powershell
$env:Serilog__MinimumLevel__Default = "Debug"
```

```bash
export Serilog__MinimumLevel__Default=Debug
```

---

## Summary

The configuration system provides:

- ✅ Hierarchy with clear precedence (env vars > env-specific files > base)
- ✅ Environment-specific configs (Development vs Production)
- ✅ Security validation (HTTPS-only, API keys, etc.)
- ✅ Flexibility (JSON files or environment variables)
- ✅ Easy debugging (override any setting without code changes)

Use this for:
- **Development**: Relax limits, verbose logging
- **Testing**: Use test API endpoints, high rate limits
- **Production**: Strict limits, minimal logging, HTTPS enforced
