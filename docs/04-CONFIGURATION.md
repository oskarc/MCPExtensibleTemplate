# Configuration Guide

## Overview

The MCP Server Template uses a hierarchical configuration system where settings can come from multiple sources, with clear precedence rules. This guide explains every setting the server reads and how to set them.

One rule shapes everything below: **in the sections the frame governs — `Authentication`, `Providers`, `Limits`, `Confirmation`, `Development`, `HttpTransport` and `Kestrel` — a key the server does not read stops it from starting.** A misspelled key is otherwise ignored silently, and an operator believes it is in force. The startup message names the key and the nearest real one. `Kestrel` holds no key the server reads: the server configures its own listener from `HttpTransport`, so any `Kestrel` key stops it, with the reason.

---

## Configuration Hierarchy (Top Priority First)

1. **Command-line arguments** (highest priority), for example `--Transport http`
2. **Environment Variables**
3. **User secrets** (Development only — `dotnet user-secrets`)
4. **McpServerTemplate.settings.{Environment}.json** and **McpServerTemplate.settings.json** — read beside `appsettings` by the .NET 10 host; the template ships neither
5. **appsettings.{Environment}.json** (Development, Production, Staging, etc.)
6. **appsettings.json** (base/default)
7. **Defaults in code** (lowest priority)

**Example**: If a setting exists in all of them, the command-line argument wins.

**Settings are read once, at startup, where they are checked.** No settings file is watched: editing one while the server runs changes nothing until the server restarts, and the restart checks the change like any other setting — a key it does not read stops it from starting. A running server's behaviour cannot be changed by a file it was not started with. No launch can switch this back on: every settings file is read once whatever the command line or the environment says. A request to read them again stops the server at startup, naming where it came from, since the server would otherwise ignore it: `hostBuilder:reloadConfigOnChange=true` on the command line or as `DOTNET_hostBuilder__reloadConfigOnChange` in the environment, under either transport, and as `ASPNETCORE_hostBuilder__reloadConfigOnChange` under HTTP. The stdio host does not read `ASPNETCORE_` variables, so there that one has no effect.

**Which settings sources the server accepts.** Every source its settings come from must be one that cannot read them again: settings in memory, environment variables, the command line, a settings file that is not watched, or a chained configuration made only of those. Any other kind of source stops the server at startup, naming it — a key-per-file source (`AddKeyPerFile`, the usual way mounted Docker or Kubernetes secrets are read) included, even with `reloadOnChange: false`, because the check goes by kind: a source cannot be asked whether it will read again. To bring mounted or vault secrets in today, pass them as environment variables (Kubernetes `secretKeyRef` or `envFrom`, Docker `--env-file` or Compose `env_file`), or read them in code at startup into an in-memory source (`AddInMemoryCollection`) before the server is composed. Teaching the check a new kind of source is a code change to `SettingsReadOnce`, with a test proving that kind cannot read its settings again.

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
  - `http`: A hosted server using stateless streamable HTTP. The MCP endpoint is `/mcp`, the path `Authentication:Resource` must name.

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
  - `"0.0.0.0"`: Accessible from any address — only behind a proxy and firewall, and only with `HttpTransport:AllowedHosts` set: on any bind that is not loopback the server refuses to start without it
  - It must be one of three forms, exactly: an IP address Kestrel reads as one (IPv4, dotted or in a short form such as `127.1`; IPv6, bare or bracketed, such as `::1` or `[::1]`, with a zone such as `%2` only on a link-local address); `localhost`, in any case; or an explicit all-interfaces spelling: `0.0.0.0`, `::`, `[::]`, `*` or `+`. Anything else stops the server, saying what Kestrel would do with it: a path or a scheme (`127.0.0.1/x`, `http://127.0.0.1`), a Unix socket or a named pipe (`unix:/...`, `pipe:/...`) would stop Kestrel; a host name (`myhost.example`), or text Kestrel does not read as an address (`[127.0.0.1]`, whitespace around an address), would have it listen on every interface; a name under `.localhost` it reads as `localhost`; and a zone on any other address (`::1%1`) it ignores on Linux and cannot bind on Windows. A loopback address (`::1`, `[::1]`, `0::1`, `127.1`, `127.0.0.2`) or `localhost` needs no allowed hosts; any other accepted form does
  - Write the address alone: the port is `HttpTransport:Port`. The server refuses to start on an empty bind address, or one of whitespace alone (an unset variable in a compose file leaves it empty: set `HttpTransport__BindAddress` to the address), on a bind address that carries a port (`[::1]:9999`, `127.0.0.1:9999`, `localhost:9999`), which it would otherwise ignore, and on an IPv4-mapped address, bracketed or not (`::ffff:127.0.0.1`, `[::ffff:127.0.0.1]`), which Kestrel cannot listen on: write the IPv4 address itself (`127.0.0.1`)

#### `HttpTransport:AllowedHosts`
- **Type**: `string[]`
- **Default**: `localhost`, `127.0.0.1`, `[::1]` when bound to `localhost`, `127.0.0.1` or `::1`, however written (`0::1`, `[::1]`, `127.1`); that address alone, in its standard form, when bound to any other IPv4 loopback address (`127.2` gives `127.0.0.2`: Kestrel binds that address alone, and it is the `Host` a request reaching it carries); an IPv4-mapped address, `::ffff:127.0.0.1` or `[::ffff:127.0.0.1]`, is refused as a bind address before any default is made. The default is held to the rules below like an entry written out; **required** on any other bind address
- **Description**: The `Host` header values this server answers for. Stops DNS rebinding. Set it to the host names clients use, for example `mcp.example.com`, each one name written exactly as the host filter matches it: ASCII (an internationalised name in its punycode form, which begins `xn--`), with no `*` anywhere and no trailing dot. The server refuses to start on any other entry — `*.example.com` admits every name under `example.com`, `*.com` nearly any host, and a non-ASCII entry can fold into `*` as the filter converts it — and on `0.0.0.0`, `[::]` or `::`: the first two switch host filtering off entirely, and the last is the IPv6 any-address. Nor does it start on an entry no request can match, which would leave it answering `400` to every request meant for that name without saying why: an empty entry (an unset variable leaves one, as `HttpTransport__AllowedHosts__0=${MCP_HOST}` does in a compose file when `MCP_HOST` is not set), whitespace around a name, a port (`mcp.example.com:443`; the filter compares a request's host without its port), an IPv6 address without its brackets (`::1`; write `[::1]`), or anything else Kestrel never lets a request carry as its `Host`, such as a path, a user name or two names in one entry.

#### `Kestrel`
- **Description**: Not read. The server configures its listener itself, from `HttpTransport:BindAddress` and `HttpTransport:Port`; Kestrel's own endpoints would bind around them and around the host allowlist chosen for that address. Any `Kestrel` key stops the server from starting.

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

**Data protection is in memory only.** Nothing in the server protects data with it, so it keeps no key material on disk and loads no key ring from the user's profile: an in-memory, ephemeral provider, and no key ring loaded at startup.

Required for `Transport=http`. Callers present a bearer token issued by one of these identity providers.

#### `Authentication:Resource`
- **Type**: `string` — an absolute `https` URI whose path is `/mcp`, for example `https://mcp.example.com/mcp`
- **Description**: This server's identity as an OAuth resource, and the URL a client connects to: MCP answers at `/mcp`, and the server refuses to start when the resource's path (a trailing slash aside) is anything else. Every identity provider must issue tokens whose audience is exactly this value. Published in the protected-resource metadata at `/.well-known/oauth-protected-resource/mcp` — RFC 9728's location for the resource, and the URL the `401` challenge names.
- **Written exactly**: it is published as written, and every token's audience must equal it character for character, so the server holds the string, not what it parses to. A query, a fragment, user information, a backslash, percent-encoding or a dot-segment (`/./`, `/../`) stops the server from starting, as does a path of `/mcp` with more than one trailing slash.

#### `Authentication:IdentityProviders:{name}:*`

One section per identity provider, under a name of your choosing (the examples use `corp`):

| Key | Required | Description |
|-----|----------|-------------|
| `Authentication:IdentityProviders:{name}:Authority` | Yes | Absolute `https` URI with no query, fragment or user information; discovery and signing keys are fetched from it |
| `Authentication:IdentityProviders:{name}:Issuer` | Yes | Absolute `https` URI with no query, fragment or user information; the exact `iss` value tokens must carry, and the authorization server the protected-resource metadata sends clients to (which may differ from the authority, for example by a trailing slash) |
| `Authentication:IdentityProviders:{name}:Algorithms` | Yes | One or more of `RS256`, `PS256`, `ES256` |
| `Authentication:IdentityProviders:{name}:ScopeCatalog` | Yes | Every scope this identity provider may assert; no wildcards |
| `Authentication:IdentityProviders:{name}:ScopeClaim` | No | The claim carrying scopes: `scope` (default; Keycloak, Auth0) or `scp` (Entra ID) |
| `Authentication:IdentityProviders:{name}:ClientIdClaim` | No | The claim naming the calling client, which every token must carry. It must be exactly one of four, the one your identity provider puts the client in: `azp` (Keycloak, Entra ID v2), `cid` (Okta), `appid` (Entra ID v1) or `client_id` (RFC 9068's JWT access token profile; the default). Anything else is refused at startup |

Tokens must also carry `sub`, `jti`, the claim `ClientIdClaim` names, and `iat`, and are refused over 8 KB. The default is `client_id`; Keycloak and Entra ID v2 name the client in `azp`, Okta in `cid` and Entra ID v1 in `appid`, so set `ClientIdClaim` for those, as the Keycloak examples below do — a token without the claim it names is refused.

#### `Authentication:AdminIdentityProvider`
- **Type**: `string`
- **Default**: not set
- **Description**: The one identity provider whose `mcp:admin` scope is honoured. When set, it must name a configured identity provider whose catalog contains `mcp:admin`. (The administrative plane itself arrives in a later phase.)

```bash
# An identity provider named corp (a Keycloak realm), from environment variables
export Authentication__Resource=https://mcp.example.com/mcp
export Authentication__IdentityProviders__corp__Authority=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__Issuer=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__ClientIdClaim=azp        # Keycloak puts the client in azp
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
- **Description**: Where logs are written. The console sink writes to **stderr**, so stdout stays clean for the MCP protocol. Every `File` sink, at any index (and in a sub-logger), is checked at startup where it will write: its path with any `%VARIABLE%` expanded, as Serilog's reader expands it, and resolved against the working directory, as the sink resolves it. A path that contains `..` once expanded, a directory the process cannot write, or an existing file it cannot append to — the file the sink opens first: for a rolling sink the current period's file, named as Serilog names it (`logs/mcp-server-20260927.log`, or its highest `_NNN`) — stops the server, naming that file; the check leaves nothing behind. A sink that fails later (a full disk, a removed directory, a file it rolls to later, a sink name Serilog does not know) is reported by Serilog's self-log, which also goes to stderr.

---

### Retired Settings

<!-- retired -->
These are refused at startup, with the reason:

| Setting | Retired | Instead |
|---------|---------|---------|
| `Authentication:ApiKey` | Phase 1 | Callers present a bearer token from a configured identity provider |
| `RateLimit:MaxCallsPerToolPerMinute` | Phase 2 | `Limits:PerPrincipalPerMinute`, and each tool's own limit in its provider's policy |
| `HttpTransport:Certificate:Path`, `HttpTransport:Certificate:Subject` | Phase 2 | The server does not terminate TLS itself: terminate it at a proxy named in `HttpTransport:KnownProxies` or `:KnownNetworks` |
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
      # The host name clients reach this server by. Compose stops, saying so, when MCP_HOST is unset or
      # empty; without the :? an empty entry reaches the server, which refuses to start on it.
      - HttpTransport__AllowedHosts__0=${MCP_HOST:?set MCP_HOST to the host name clients reach this server by, such as mcp.example.com}
      - HttpTransport__KnownNetworks__0=172.16.0.0/12
      - Limits__Redis=redis:6379
      - Authentication__Resource=https://${MCP_HOST}/mcp
      - Authentication__IdentityProviders__corp__Authority=https://login.example.com/realms/corp
      - Authentication__IdentityProviders__corp__Issuer=https://login.example.com/realms/corp
      - Authentication__IdentityProviders__corp__ClientIdClaim=azp
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
export Authentication__Resource=https://localhost:3001/mcp
export Authentication__IdentityProviders__corp__Authority=https://your-idp/realms/corp
export Authentication__IdentityProviders__corp__Issuer=https://your-idp/realms/corp
export Authentication__IdentityProviders__corp__ClientIdClaim=azp        # Keycloak puts the client in azp
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
curl -X POST http://localhost:3001/mcp \
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
export Authentication__Resource=https://mcp.example.com/mcp
export Authentication__IdentityProviders__corp__Authority=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__Issuer=https://login.example.com/realms/corp
export Authentication__IdentityProviders__corp__ClientIdClaim=azp        # Keycloak puts the client in azp
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
| HTTP with no identity provider, or with a non-`https` authority, issuer or resource | The server could verify no token, would fetch keys over plaintext, or would send clients to a plaintext issuer |
| An authority or issuer with a query, a fragment or user information | An issuer identifier has neither (RFC 8414), and is published as written; discovery is fetched from paths appended to the authority |
| A resource whose path is not `/mcp`, or that is not written as clients connect to it (a query, fragment, user information, backslash, percent-encoding or dot-segment) | MCP answers at `/mcp`; the resource is published as written, and every audience must equal it exactly |
| A `ClientIdClaim` other than `azp`, `cid`, `appid` or `client_id`, or equal to the provider's `ScopeClaim` | A claim every token carries, or one that means something else, would switch the client requirement off |
| A `File` log sink whose path contains `..` once expanded, or that cannot write where it resolves | A sink that cannot write writes nothing and says nothing |
| A bind address that is not loopback with no `HttpTransport:AllowedHosts`, or an allowed host, written out or given by a loopback bind address, that is not one name written as the filter matches it (any `*`, a non-ASCII character, a trailing dot), is `0.0.0.0`, `[::]` or `::`, or is one no request can match (empty, whitespace around it, a port, an IPv6 address without brackets, anything Kestrel never lets a request carry as its `Host`) | Host filtering would be off or wider than written, and DNS rebinding could make a browser a client; or the server would answer `400` to every request meant for that name, and not say why |
| A bind address that is none of its three accepted forms (a path, a scheme, a Unix socket or named pipe, a host name, text Kestrel does not read as an address, a zone on an address that is not link-local), is empty or whitespace alone, carries a port (`[::1]:9999`, `127.0.0.1:9999`), or is IPv4-mapped (`::ffff:127.0.0.1`, bracketed or not) | Kestrel would not start on it, would listen on every interface, or would not bind it as written; an empty one names nothing to listen on; the server listens on `HttpTransport:Port` and would ignore the port; Kestrel cannot listen on an IPv4-mapped address |
| Any `Kestrel` key | Kestrel's endpoints would bind around `HttpTransport:BindAddress` and the host allowlist chosen for it |
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

**Cause**: A key in `Authentication`, `Providers`, `Limits`, `Confirmation`, `Development` or `HttpTransport` is misspelled, or belongs to a provider this server does not have.

**Fix**: Use the key the message suggests, or remove it.

---

### "'Kestrel…' is not a setting this server honours"

**Cause**: A key under `Kestrel`, which would configure the listener around the server's own checks.

**Fix**: Remove it. Set `HttpTransport:BindAddress` and `HttpTransport:Port`; terminate TLS at the proxy named in `HttpTransport:KnownProxies`.

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
