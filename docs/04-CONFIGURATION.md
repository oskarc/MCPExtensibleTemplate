# Configuration Guide

## Overview

The MCP Server Template uses a hierarchical configuration system where settings can come from multiple sources, with clear precedence rules. This guide explains every setting the server reads and how to set them.

One rule shapes everything below: **in the sections the frame governs — `Authentication`, `Providers`, `Limits`, `Confirmation`, `Development` — a key the server does not read stops it from starting.** A misspelled key is otherwise ignored silently, and an operator believes it is in force. The startup message names the key and the nearest real one.

---

## Configuration Hierarchy (Top Priority First)

1. **Command-line arguments** (highest priority), for example `--Transport http`
2. **Environment Variables**
3. **User secrets** (Development only — `dotnet user-secrets`)
4. **appsettings.{Environment}.json** (Development, Production, Staging, etc.)
5. **appsettings.json** (base/default)
6. **Defaults in code** (lowest priority)

**Example**: If a setting exists in all of them, the command-line argument wins.

```
Command line: --MyValue cli
Environment Variable: MyValue=env
    appsettings.Production.json: MyValue=prod
    appsettings.json: MyValue=default

→ Result: cli
```

**Arrays merge by index, not as a whole.** If the base file lists three providers under `Providers:Enabled` and an environment file lists two, the result keeps the base file's third entry. That is why `Providers:Enabled` is set only in the environment files, never in `appsettings.json`.

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
  "Serilog": { "...": "console to stderr, rolling files under logs/" },
  "Providers": {
    "Smhi": {
      "BaseUrl": "https://opendata-download-metfcst.smhi.se",
      "UserAgent": "McpServerTemplate/1.0",
      "IdentityProvider": "corp"
    },
    "SmhiObs": {
      "BaseUrl": "https://opendata-download-metobs.smhi.se",
      "UserAgent": "McpServerTemplate/1.0",
      "IdentityProvider": "corp"
    },
    "JsonPlaceholder": {
      "BaseUrl": "https://jsonplaceholder.typicode.com",
      "UserAgent": "McpServerTemplate/1.0",
      "IdentityProvider": "corp"
    }
  },
  "Limits": {
    "PerPrincipalPerMinute": 120
  }
}
```

### 2. appsettings.Development.json

Applied **only** when the environment is `Development`.

Used for **local development**: verbose logging, every provider enabled, and the principal a stdio run acts as.

```json
{
  "Transport": "stdio",
  "Providers": {
    "Enabled": [ "Smhi", "SmhiObs", "JsonPlaceholder" ]
  },
  "Development": {
    "DevPrincipal": {
      "IdentityProvider": "corp",
      "Subject": "dev-user",
      "ClientId": "dev-ide",
      "Scopes": [ "weather:read", "observations:read", "demo:read", "demo:write" ]
    }
  }
}
```

### 3. appsettings.Production.json

Applied **only** when the environment is `Production` — which is also what the server runs as when no environment is set.

Used for **hosted deployments**: minimal logging, and only the SMHI providers. The demo provider is not enabled in Production.

```json
{
  "Providers": {
    "Enabled": [ "Smhi", "SmhiObs" ]
  },
  "Serilog": {
    "MinimumLevel": { "Default": "Warning" }
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
  - `stdio`: stdin/stdout for a local IDE. **Development only** — anywhere else the server refuses to start. Runs as the Development principal (below), through the same checks as HTTP.
  - `http`: A hosted server using stateless streamable HTTP. The MCP endpoint is the server root, `/`.

---

### HTTP Transport Settings

#### `HttpTransport:Port`
- **Type**: `int` (1–65535)
- **Default**: `3001`
- **Applies**: Only when `Transport=http`

```powershell
# Set via environment variable in PowerShell
$env:HttpTransport__Port = "8080"
```

```bash
# Set via environment variable in bash/zsh
export HttpTransport__Port=8080
```

#### `HttpTransport:BindAddress`
- **Type**: `string`
- **Default**: `"localhost"`
- **Description**: The address to bind to
  - `"localhost"` or `"127.0.0.1"`: Only accessible from this machine
  - `"0.0.0.0"`: Accessible from any address — only behind a proxy and firewall

#### `HttpTransport:AllowedHosts`
- **Type**: `string[]`
- **Default**: `localhost`, `127.0.0.1`, `[::1]` when bound to loopback; otherwise the bind address
- **Description**: The `Host` header values this server answers for. Stops DNS rebinding. Set it to your public host name.

#### `HttpTransport:AllowedOrigins`
- **Type**: `string[]`
- **Default**: `[]` (deny all)
- **Description**: Origins a browser-based client may call from. Empty denies every cross-origin request, and a request carrying any other `Origin` is refused before its token is read.

#### `HttpTransport:KnownProxies` and `HttpTransport:KnownNetworks`
- **Type**: `string[]` — addresses, and networks in CIDR form (`10.0.0.0/8`)
- **Default**: none (only loopback is trusted for forwarded headers)
- **Description**: The proxies allowed to set forwarded headers. **In Production the server refuses to start unless one is set**: it must sit behind a proxy it trusts explicitly, which terminates TLS.

---

### Authentication Settings

Required for `Transport=http`. Callers present a bearer token issued by one of these identity providers.

#### `Authentication:Resource`
- **Type**: `string` — an absolute `https` URI
- **Description**: This server's identity as an OAuth resource. Every identity provider must issue tokens whose audience is exactly this value. Published in the protected-resource metadata at `/.well-known/oauth-protected-resource`.

#### `Authentication:IdentityProviders:{name}:*`

One section per identity provider, under a name of your choosing (the examples use `corp`):

| Key | Required | Description |
|-----|----------|-------------|
| `Authentication:IdentityProviders:{name}:Authority` | Yes | Absolute `https` URI; discovery and signing keys are fetched from it |
| `Authentication:IdentityProviders:{name}:Issuer` | Yes | The exact `iss` value tokens must carry |
| `Authentication:IdentityProviders:{name}:Algorithms` | Yes | One or more of `RS256`, `PS256`, `ES256` |
| `Authentication:IdentityProviders:{name}:ScopeCatalog` | Yes | Every scope this identity provider may assert; no wildcards |
| `Authentication:IdentityProviders:{name}:ScopeClaim` | No | The claim carrying scopes: `scope` (default; Keycloak, Auth0) or `scp` (Entra ID) |
| `Authentication:IdentityProviders:{name}:ClientIdClaim` | No | `client_id` (default) or `azp` |

Tokens must also carry `sub`, `jti`, `client_id` (or `azp`) and `iat`, and are refused over 8 KB.

#### `Authentication:AdminIdentityProvider`
- **Type**: `string`
- **Default**: not set
- **Description**: The one identity provider whose `mcp:admin` scope is honoured. When set, it must name a configured identity provider whose catalog contains `mcp:admin`. (The administrative plane itself arrives in a later phase.)

```bash
# An identity provider named corp, from environment variables
export Authentication__Resource=https://mcp.example.com/
export Authentication__IdentityProviders__corp__Authority=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__Issuer=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__Algorithms__0=RS256
export Authentication__IdentityProviders__corp__ScopeCatalog__0=weather:read
export Authentication__IdentityProviders__corp__ScopeCatalog__1=observations:read
```

---

### Provider Settings

#### `Providers:Enabled`
- **Type**: `string[]`
- **Default**: every built-in provider in Development; **required** everywhere else
- **Description**: The providers this deployment serves. A name that is not a provider this server has stops it from starting.

#### `Providers:{Name}:IdentityProvider`
- **Type**: `string`
- **Required**: Yes, for every enabled provider
- **Description**: The one identity provider this provider answers to. A caller from any other identity provider cannot see or use its tools, resources or prompts. Two providers may share an identity provider; no provider may have none.

#### `Providers:{Name}:BaseUrl`
- **Type**: `string` — an absolute `https` URL
- **Description**: The upstream the provider calls. Its host must be one of the hosts the provider's policy allows, or the server refuses to start.

#### `Providers:{Name}:UserAgent`
- **Type**: `string`
- **Description**: The `User-Agent` sent upstream. A setting the built-in providers declare; a provider of your own reads whatever settings its module declares.

```json
{
  "Providers": {
    "Smhi": {
      "BaseUrl": "https://opendata-download-metfcst.smhi.se",
      "UserAgent": "McpServerTemplate/1.0",
      "IdentityProvider": "corp"
    }
  }
}
```

Tools' scopes, risk classes, per-tool limits and allowed hosts are **not** configuration: they are the provider's policy, declared in its module in code.

---

### Limits Settings

#### `Limits:Redis`
- **Type**: `string` — a StackExchange.Redis connection string, for example `redis.internal:6379,password=...`
- **Default**: not set; **required outside Development**
- **Description**: Where per-caller limits and used confirmations are kept, so they hold on every instance. Without it, Development keeps them in memory; any other environment refuses to start. If Redis becomes unreachable while running, requests are refused, not let through unlimited.

#### `Limits:PerPrincipalPerMinute`
- **Type**: `int` (1–1,000,000)
- **Default**: `120`
- **Description**: How many requests one caller — one identity provider plus subject — may make per minute, across all instances. Each tool also has its own per-caller limit in its provider's policy.

**What happens when a limit is exceeded** (the caller receives an MCP error):
```
excess_rate_limit_exceeded (rule: caller-rate). You have made 120 requests in the last minute, which is your limit. Wait and try again.
```

Separately, HTTP requests are limited to 60 per minute per client address before any token is examined; over that, the server answers `429 Too Many Requests`.

---

### Confirmation Settings

#### `Confirmation:Key`
- **Type**: `string` — base64, at least 32 bytes decoded
- **Required**: when any enabled provider has a tool whose policy marks it *Irreversible*
- **Description**: Signs the confirmations irreversible tools require. Generate one with `openssl rand -base64 32` and keep it in a secret store.

---

### Development Settings

#### `Development:DevPrincipal:*`

The principal a stdio run acts as. Development only.

| Key | Default | Description |
|-----|---------|-------------|
| `Development:DevPrincipal:IdentityProvider` | `development` | The identity provider it claims; must match the providers' bindings to reach them |
| `Development:DevPrincipal:Subject` | `dev-user` | Its subject |
| `Development:DevPrincipal:ClientId` | `dev-ide` | Its client |
| `Development:DevPrincipal:Scopes` | none | Its scopes; with identity providers configured, each must be in the catalog |

With no identity providers configured, a stdio run synthesizes one per name the providers are bound to, able to issue exactly the scopes their policies require.

---

### Logging Settings (Serilog)

#### `Serilog:MinimumLevel:Default`
- **Type**: `string`
- **Options**: `"Verbose"`, `"Debug"`, `"Information"`, `"Warning"`, `"Error"`, `"Fatal"`
- **Default**: `"Information"`

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
- **Description**: Override log levels for specific namespaces. Production keeps `McpServerTemplate` at `Information`, so security events and the startup record of the installed frame are written.

#### `Serilog:WriteTo`
- **Type**: `array`
- **Description**: Where logs are written. The console sink writes to **stderr**, so stdout stays clean for the MCP protocol. A file path containing `..` is refused at startup.

---

### Retired Settings

<!-- retired -->
These are refused at startup, with the reason:

| Setting | Retired | Instead |
|---------|---------|---------|
| `Authentication:ApiKey` | Phase 1 | Callers present a bearer token from a configured identity provider |
| `RateLimit:MaxCallsPerToolPerMinute` | Phase 2 | `Limits:PerPrincipalPerMinute`, and each tool's own limit in its provider's policy |
<!-- /retired -->

---

## Environment Variables

Use environment variables to override any JSON configuration.

### Naming Convention

JSON nested keys → environment variables using `__` (double underscore); array entries use their index.

```json
// appsettings.json
{
  "Transport": "stdio",
  "HttpTransport": { "Port": 3001 },
  "Providers": { "Enabled": [ "Smhi" ] }
}

// Equivalent environment variables:
$env:Transport = "http"
$env:HttpTransport__Port = "8080"
$env:Providers__Enabled__0 = "Smhi"
```

### Setting Environment Variables

**Linux/Mac**:
```bash
export Transport=http
export Limits__Redis=localhost:6379
export Limits__PerPrincipalPerMinute=60

dotnet run
```

**Windows PowerShell**:
```powershell
$env:Transport = "http"
$env:Limits__Redis = "localhost:6379"
$env:Limits__PerPrincipalPerMinute = "60"

dotnet run
```

**Docker Compose**:
```yaml
services:
  redis:
    image: redis:7.4-alpine
  mcp-server:
    image: mcp-server:latest
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Transport=http
      - HttpTransport__BindAddress=0.0.0.0
      - HttpTransport__KnownNetworks__0=172.16.0.0/12
      - Limits__Redis=redis:6379
      - Authentication__Resource=https://mcp.example.com/
      - Authentication__IdentityProviders__corp__Authority=https://login.example.com/realms/corp
      - Authentication__IdentityProviders__corp__Issuer=https://login.example.com/realms/corp
      - Authentication__IdentityProviders__corp__Algorithms__0=RS256
      - Authentication__IdentityProviders__corp__ScopeCatalog__0=weather:read
      - Authentication__IdentityProviders__corp__ScopeCatalog__1=observations:read
    ports:
      - "3001:3001"
```

---

## Environment Selection

The server selects configuration from `ASPNETCORE_ENVIRONMENT`, or `DOTNET_ENVIRONMENT` when that is not set.

### Default Behavior

1. If neither is set → **Production**
2. `Development` → loads `appsettings.Development.json`; stdio allowed; limits may be in memory
3. `Production` → loads `appsettings.Production.json`; stdio refused; Redis, `Providers:Enabled` and a declared proxy required

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

---

## Common Configuration Scenarios

### Scenario 1: Local Development (stdio)

**Goal**: Fast iteration, verbose logging, every provider

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

Or on bash/zsh:

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run
```

**Effective config**:
- Transport: stdio, as the Development principal (`corp:dev-user`, all four scopes)
- Providers: all three
- Limits: in memory, 120 requests per minute per caller
- Log level: Debug

---

### Scenario 2: Local Testing (HTTP Mode)

**Goal**: Test HTTP mode locally. You need an identity provider that issues tokens — a local Keycloak works (`docker run -p 8080:8080 quay.io/keycloak/keycloak start-dev`), though its authority must be reachable over `https`.

```bash
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

**Then test**:
```bash
curl http://localhost:3001/healthz                     # alive, no credential needed
curl -X POST http://localhost:3001/ \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Without a token the server answers `401` with a `WWW-Authenticate` header naming its resource metadata, which tells a client where to get one.

---

### Scenario 3: Production Deployment

**Goal**: Secure, monitored, multi-instance

```bash
export ASPNETCORE_ENVIRONMENT=Production
export Transport=http
export HttpTransport__BindAddress=0.0.0.0
export HttpTransport__AllowedHosts__0=mcp.example.com
export HttpTransport__KnownNetworks__0=10.0.0.0/8      # the proxy that terminates TLS
export Limits__Redis=redis.internal:6379
export Authentication__Resource=https://mcp.example.com/
export Authentication__IdentityProviders__corp__Authority=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__Issuer=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__Algorithms__0=RS256
export Authentication__IdentityProviders__corp__ScopeCatalog__0=weather:read
export Authentication__IdentityProviders__corp__ScopeCatalog__1=observations:read

dotnet run
```

**Effective config**:
- Transport: HTTP behind a declared proxy, TLS at the proxy
- Providers: Smhi and SmhiObs (from appsettings.Production.json)
- Limits: in Redis, 120 per caller per minute, plus each tool's own limit
- Log level: Warning, with the server's own security events at Information
- Every request: a bearer token from `corp`

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

**Result**: the provider calls the development API in Development — **provided its policy allows both hosts**. A `BaseUrl` whose host the policy does not list stops the server from starting, so `MyApiModule` must declare `api.example.com` and `dev-api.example.com`.

---

## Validation & Security

Every one of these stops the server at startup, with a message saying what to fix, and exit code 78:

| Check | Why |
|-------|-----|
| A governed key the server does not read, or a retired one | A setting that is ignored answers a question falsely |
| `Transport=stdio` outside Development | stdio authenticates nobody |
| HTTP with no identity provider, or with a non-`https` authority or resource | The server could verify no token, or would fetch keys over plaintext |
| Production without a declared proxy | Bearer tokens over plaintext can be read and replayed |
| `Providers:Enabled` missing outside Development, or naming an unknown provider | A deployment says which providers it serves |
| A provider with no `IdentityProvider`, or one not configured | A provider with no trust domain would be reachable from all of them |
| A scope a provider's identity provider cannot issue | The tool would be unreachable, and nobody would know |
| A `BaseUrl` that is not `https`, or whose host the policy does not allow | Keeps upstream calls to the declared hosts |
| `Limits:Redis` missing outside Development | Limits must hold on every instance |
| An irreversible tool without `Confirmation:Key` | It could never run safely |

---

## Troubleshooting

### "'…' is not a setting this server reads. Did you mean '…'?"

**Cause**: A key in `Authentication`, `Providers`, `Limits`, `Confirmation` or `Development` is misspelled, or belongs to a provider this server does not have.

**Fix**: Use the key the message suggests, or remove it.

---

### "'…' is retired"

**Cause**: A setting that no longer does anything — see [Retired Settings](#retired-settings).

**Fix**: Remove it; the message says what replaced it.

---

### "Providers:Enabled is required outside Development"

**Fix**: Name the providers this deployment serves, in `appsettings.{Environment}.json` or `Providers__Enabled__0`, `Providers__Enabled__1`, …

---

### "Limits:Redis is required outside Development"

**Fix**: Point `Limits:Redis` at a Redis every instance can reach.

---

### "Transport 'stdio' is permitted only in Development"

**Cause**: No environment was set, so the server ran as Production.

**Fix**: Set `ASPNETCORE_ENVIRONMENT=Development` for a local stdio run.

---

### Rate Limit Exceeded

**Cause**: `caller-rate` — the caller's requests per minute; `tool-rate` — the caller's calls to one tool per minute, from its policy; `429` — the per-address HTTP limit.

**Fix**: Wait a minute, or raise `Limits:PerPrincipalPerMinute`. A tool's own limit is in its provider's policy.

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

- ✅ Hierarchy with clear precedence (command line > env vars > user secrets > env-specific files > base)
- ✅ Environment-specific configs (Development vs Production), Production by default
- ✅ Refusal over silence: a key the server would ignore, or a deployment it cannot secure, stops it from starting
- ✅ Flexibility (JSON files or environment variables)

Use this for:
- **Development**: stdio as the Development principal, every provider, limits in memory
- **Testing**: HTTP against a real identity provider
- **Production**: identity providers, Redis, a declared proxy, the providers you serve
