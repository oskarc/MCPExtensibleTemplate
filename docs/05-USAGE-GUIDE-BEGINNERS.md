# Usage Guide for Beginners

Welcome! This guide explains what the MCP Server is, how to use it, and how it works **with zero preexisting knowledge assumed**.

---

## What is This?

Think of the MCP Server as a **bridge between AI and the internet**.

```
Your AI Assistant (Claude, ChatGPT, VS Code Copilot)
            ↓
        MCP Server ← YOU
            ↓
    External APIs (Weather Service, Todo App, Blog Platform)
```

The server lets your AI assistant do things it normally can't:
- 📖 Read blog posts from a website
- ✍️ Create new posts
- 💬 Add comments
- ✅ Manage todo lists
- 🌤 Get weather forecasts

---

## Key Concepts (Simple Explanations)

### 1. What is a "Tool"?

A **tool** is something your AI assistant can use, like a function.

Without MCP: Claude can't fetch data from the internet
```
You: "What's the weather in Stockholm?"
Claude: "I don't have access to real-time weather data."
❌ Dead end
```

With MCP: Claude can fetch real data
```
You: "What's the weather in Stockholm?"
Claude: (uses get_current_weather tool)
Claude: "It's 15°C and cloudy."
✅ Works!
```

**Examples of tools**:
- `get_blog_post` - retrieve a blog post
- `create_blog_post` - write a new post
- `get_user_todos` - view tasks
- `create_user_todo` - add a task

---

### 2. What is a "Provider"?

A **provider** is a connection to an external API (website/service).

Think of it like a store clerk who knows how to get information from a specific store.

```
Providers:
├── JsonPlaceholder Provider
│   └── Knows how to talk to https://jsonplaceholder.typicode.com
│       (Fake blog/todos API)
├── Weather Provider (SMHI)
│   └── Knows how to talk to https://opendata-download-metfcst.smhi.se
│       (Swedish weather forecasts)
└── Your Custom Provider
    └── Knows how to talk to YOUR API
        (Whatever you want!)
```

Each provider contains:
- **Tools**: What your AI assistant can do with this service
- **Configuration**: How to connect to the API

---

### 3. What is "Configuration"?

**Configuration** = settings that change how the server behaves.

**Examples**:
- Which port to listen on? (3001, 8080, 9000?)
- How many requests may one caller make per minute? (120? 60?)
- Which identity provider signs the tokens callers present?
- Which providers does this server offer?
- Should I show verbose logs or minimal logs?

You don't hardcode these; you configure them so you can change them later without modifying code.

---

### 4. What are "Stdio" vs "HTTP"?

Two ways the server can communicate:

**Stdio** (local development):
```
Your Computer
    ↓ (stdin/stdout)
MCP Server
    ↓ (stdin/stdout)
AI Assistant (VS Code, Claude Desktop)
    
Only works on your computer. Fast. Simple.
```

**HTTP** (hosted/network):
```
Your Computer / Network
    ↓ (HTTP requests)
MCP Server (listening on port 3001)
    ↓ (HTTP requests)
Multiple Clients (Claude, VS Code, Mobile App, etc.)

Works across the internet. Every caller presents a sign-in
token from an identity provider, and each caller's requests
are limited.
```

---

## Getting Started: Step by Step

### Step 1: Install Prerequisites

You need **the .NET 10 SDK** — and, to run the tests, **Docker**.

Download from: https://dotnet.microsoft.com/download/dotnet/10.0

Verify it's installed:
```bash
dotnet --version
# Output: 10.0.x
```

### Step 2: Open the Project

```bash
cd McpServerTemplate
```

### Step 3: Run the Server (Stdio Mode - Local)

Local (stdio) mode runs only in **Development**. With no environment set, the server runs as Production and refuses to start in stdio mode, so set it first:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

Or on bash/zsh:

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run
```

**What happens**:
- The server starts
- It listens on stdin/stdout
- Clients (like VS Code or Claude Desktop) can now connect
- You act as the *Development principal* (`dev-user`), which holds every scope the built-in tools need
- You should see log output in your terminal, including a `Frame installed:` line listing every tool and the scope it requires

> **Note**: In stdio mode, the server communicates over stdin/stdout. Log messages are written to stderr and to the `logs/` folder.

---

### Step 4: Connect Your AI Assistant

**Option A: VS Code IDE (Local)**

1. Install VS Code Copilot extension
2. In VS Code settings, configure an MCP server pointing to this running process
3. Now Copilot can use your tools

**Option B: Claude Desktop**

1. Edit `%APPDATA%\Claude\claude_desktop_config.json` (Windows) or `~/Library/Application Support/Claude/claude_desktop_config.json` (Mac)
2. Add:
   ```json
   {
     "mcpServers": {
       "my-mcp": {
         "command": "dotnet",
         "args": ["run", "--project", "/absolute/path/to/McpServerTemplate"],
         "env": { "ASPNETCORE_ENVIRONMENT": "Development" }
       }
     }
   }
   ```
3. Restart Claude Desktop
4. Claude can now use your tools

---

## Using a Tool: Example

### Example: Get a Blog Post

**Your prompt to Claude**:
```
"Show me blog post #1"
```

**What happens behind the scenes**:

1. Claude sees you have a `get_blog_post` tool available
2. Claude calls: `get_blog_post(postId=1)`
3. MCP Server receives this request, and **checks it** — the tool exists, you hold its scope (`demo:read`), you're within your limits, and `postId` is a number
4. **Tool Execution**:
   - Tool calls JsonPlaceholderApiClient
   - ApiClient makes HTTP request to https://jsonplaceholder.typicode.com/posts/1
   - Response: `{ "userId": 1, "id": 1, "title": "Post title", "body": "Post content" }`
5. **Formatting**:
   - Formatter transforms raw JSON into readable text:
     ```
     📝 Post #1
     Author: User 1
     Title: Post title
     
     Post content
     ```
6. Claude receives the formatted text
7. Claude displays it to you

**Result**:
```
Claude: "Here's blog post #1:

📝 Post #1
Author: User 1
Title: sunt aut facere repellat provident occaecati excepturi optio reprehenderit
...
```

---

## Error Handling: What Goes Wrong?

### Error 1: Invalid Input

```
You: "Show me blog post #99999"
Claude: Calls get_blog_post(99999)
MCP Server: "Post ID 99999 is out of range. JSONPlaceholder serves posts 1-100; ask for an id in that range."
Claude: "I couldn't find post #99999. Valid posts are 1-100."
```

### Error 2: Rate Limit Exceeded

```
You: "Create 50 new posts" (calling create_blog_post 50 times in 1 minute)
Its policy says: 10 calls per caller per minute
MCP Server: "excess_rate_limit_exceeded (rule: tool-rate). You have called 'create_blog_post' 10 times in the last minute, which is its limit. Wait and try again."
Claude: "I've been rate-limited. Please wait a minute before trying again."
```

### Error 3: A Tool You May Not Use

```
Claude: Calls get_forecast(...) with a token that only holds observations:read
MCP Server: "authz_fail (rule: insufficient_scope). The tool 'get_forecast' requires the scope 'weather:read', which your token does not carry. Request a token with that scope and call again."
Claude: "I'm not permitted to use the forecast tool with the current sign-in."
```

### Error 4: External API is Down

```
Claude: Calls get_blog_post(1)
MCP Server: Makes HTTP request to external API
External API: (no response - server is down)
MCP Server: Tries up to 3 times with backoff, within the provider's time limits
MCP Server: "😞 External service is unavailable"
Claude: "The blog service is currently unavailable."
```

---

## Configuration for Beginners

### Scenario 1: Running Locally (Development)

This is Step 3 above.

**Command**:
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

**What's happening**:
- Transport: stdio (local only), as the Development principal
- Providers: all three, including the demo blog/todo provider
- Limits: 120 requests per minute for you, plus each tool's own limit; kept in memory
- Logs: Debug level

> **Tip**: Without `ASPNETCORE_ENVIRONMENT=Development` the server runs as Production, where stdio is refused and `appsettings.Development.json` is **not** loaded.

---

### Scenario 2: Running on the Network (Testing)

Want to test from another computer? Switch to HTTP mode. HTTP mode needs one more thing than stdio: an **identity provider** — a sign-in service such as Keycloak, Entra ID or Auth0 — that issues the tokens callers present. Nobody can call the server without one.

**Step 1**: Tell the server about your identity provider and switch to HTTP. The exact settings are in [Configuration Guide → Scenario 2](04-CONFIGURATION.md#scenario-2-local-testing-http-mode); in short:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export Transport=http
export HttpTransport__BindAddress=0.0.0.0
export Authentication__Resource=https://<your-computer-name>:3001/
export Authentication__IdentityProviders__corp__Authority=https://<your-identity-provider>
export Authentication__IdentityProviders__corp__Issuer=https://<your-identity-provider>
export Authentication__IdentityProviders__corp__Algorithms__0=RS256
export Authentication__IdentityProviders__corp__ScopeCatalog__0=demo:read
```

**Step 2**: Run the server

```bash
dotnet run
```

**Step 3**: Test from another computer, with a token from your identity provider

```bash
# From another computer on the same network
curl http://<your-computer-ip>:3001/healthz          # "alive" — no token needed
curl -X POST http://<your-computer-ip>:3001/ \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Without a token you get `401`, with a header telling your client where to sign in.

---

### Scenario 3: Production Deployment (Secure, Internet-Facing)

`appsettings.Production.json` already exists: it enables only the weather providers and keeps logging minimal. Production also **requires**, and refuses to start without:

- **An identity provider** — `Authentication:IdentityProviders:…`, as in Scenario 2
- **Redis** — `Limits:Redis`, so each caller's limits hold on every copy of the server you run
- **A trusted proxy in front** — `HttpTransport:KnownProxies` or `HttpTransport:KnownNetworks`, the proxy that handles HTTPS

The full example is in [Configuration Guide → Scenario 3](04-CONFIGURATION.md#scenario-3-production-deployment).

**Why these requirements**?
- Tokens instead of shared passwords: the server knows *who* is calling, and each provider answers only to its own identity provider
- Redis: a caller cannot escape their limit by spreading requests across servers
- A proxy: tokens never travel over plain HTTP
- Refusing to start: a server that cannot be secured never comes up half-secured

---

## Adding Your Own API

### Example: Connect to GitHub API

**Step 1**: Create folder structure

```
Providers/
└── Github/
    ├── GithubConfig.cs
    ├── GithubApiClient.cs
    ├── GithubTools.cs
    ├── GithubFormatters.cs
    ├── GithubServiceRegistration.cs
    └── Models/
        └── Repository.cs
```

**Step 2**: Implement each file (I can help with this!)

**Step 3**: Write `GithubModule.cs` — the provider's **policy**: for each tool, the scope a caller needs, whether it reads, writes or does something irreversible, and how often one caller may use it; and the hosts it may call (`api.github.com`). Then list the module in `Providers/BuiltInProviders.cs`.

**Step 4**: Add config to appsettings.json, and enable it

```json
{
  "Providers": {
    "Enabled": [ "Smhi", "SmhiObs", "JsonPlaceholder", "Github" ],
    "Github": {
      "BaseUrl": "https://api.github.com",
      "UserAgent": "MyMcpServer/1.0",
      "IdentityProvider": "corp"
    }
  }
}
```

**Step 5**: Start the server. If a tool has no policy, or a scope your identity provider cannot issue, the server refuses to start and tells you which. Once it starts, your tools are available to callers who hold their scopes.

Claude can call your new tools, for example `fetch_repository` or `open_issue`.

---

## Debugging: Tools for Understanding What's Happening

### 1. Check the Logs

When the server starts, tool registrations and requests are logged to the `logs/` folder and to stderr. Check the log files for details on which tools were discovered and any errors.

### 2. View Request Logs

The server logs every tool call with a correlation ID:

```
[12:34:57 INF] ToolCallLoggingFilter: [abc123] Calling tool get_blog_post with arguments: postId
[12:34:57 INF] ToolCallLoggingFilter: [abc123] Tool get_blog_post completed in 234ms
```

### 3. Enable More Detailed Logging

```powershell
$env:Serilog__MinimumLevel__Default = "Debug"
dotnet run
```

Or on bash/zsh:

```bash
export Serilog__MinimumLevel__Default=Debug
dotnet run
```

Now you'll see:
- HTTP requests being made
- JSON responses
- Deserialization details
- Everything!

### 4. Test a Tool Manually

Use curl to test:

```bash
# Test that the server is running (HTTP mode)
curl http://localhost:3001/healthz

# Call the MCP endpoint — the server root — with a token
curl -X POST http://localhost:3001/ -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

---

## FAQ (Frequently Asked Questions)

**Q: Can I use this without an external API?**

A: Yes! The server is just a framework. You can create mock providers that return fake data.

---

**Q: What happens if the external API goes down?**

A: The server tries up to 3 times, within the time limits in the provider's policy. If the API is still down, it returns a friendly error to Claude.

---

**Q: How do I keep this running 24/7?**

A: Deploy it to the cloud (Azure, AWS, Heroku) or use a service manager like systemd (Linux) or forever (Node.js).

---

**Q: Is my data secure?**

A: The server checks every request: in HTTP mode the caller must present a valid token from a configured identity provider, hold the scope the tool requires, stay within their limits, and send arguments of the right shape. Providers may only call the HTTPS hosts their policy allows.

---

**Q: Can multiple people use this at the same time?**

A: Yes! In HTTP mode, multiple AI assistants can connect simultaneously. The server handles up to 100 concurrent connections by default.

---

**Q: How do I modify how tools work?**

A: Edit the tool in `Providers/YourProvider/YourProviderTools.cs` and restart. A new tool also needs an entry in the provider's policy, or the server refuses to start and names it.

---

## Next Steps

1. **Get it running**: `dotnet run` in stdio mode, with `ASPNETCORE_ENVIRONMENT=Development`
2. **Connect an AI**: Set up VS Code Copilot or Claude Desktop
3. **Test a tool**: Ask Claude to get a blog post
4. **Create your own provider**: Replace JsonPlaceholder with your API
5. **Secure it**: Deploy in HTTP mode with proper authentication
6. **Share it**: Give other people access (if you want)

---

## Common Pitfalls & Solutions

### Pitfall 1: "Tools not showing up in Claude"

**Cause**: Claude isn't discovering the tools

**Solution**: 
1. Make sure the server is running as Development: `ASPNETCORE_ENVIRONMENT=Development`, then `dotnet run`
2. Check the Development principal holds the tool's scope: `Development:DevPrincipal:Scopes`
3. Restart your Claude Desktop or VS Code
4. Check logs for errors: in PowerShell run `$env:Serilog__MinimumLevel__Default = "Debug"`, then run again

### Pitfall 2: "Rate limit keeps hitting me"

**Cause**: You're calling faster than your limit — `caller-rate` for all your requests, `tool-rate` for one tool

**Solution**: Raise your overall limit for development:
```powershell
$env:Limits__PerPrincipalPerMinute = "600"
```

```bash
export Limits__PerPrincipalPerMinute=600
```

A single tool's limit is in its provider's policy. Or just wait a minute before retrying.

### Pitfall 3: "Getting weird JSON errors"

**Cause**: External API response changed format

**Solution**: Check the API documentation, update your `Models/*.cs` files to match the new response format

### Pitfall 4: "Server won't start"

**Cause**: Configuration error or port already in use

**Solution**:
1. Read the last line it printed: a configuration problem exits with code 78 and says exactly which setting to fix — including a misspelled one, with the nearest real name
2. Check PORT: Is 3001 already in use? In PowerShell use `$env:HttpTransport__Port = "3002"`
3. Check CONFIG: Review `appsettings.json` for syntax errors

---

## Summary

- ✅ MCP Server = bridge between AI and APIs
- ✅ Tools = what your AI can do
- ✅ Providers = connections to external APIs
- ✅ Configuration = settings (no hardcoding)
- ✅ Stdio mode = local development
- ✅ HTTP mode = multi-user/network deployment
- ✅ Easy to extend = add your own providers

You're ready to build! 🚀

For detailed technical info, see:
- [Architecture](01-ARCHITECTURE.md)
- [Flowcharts](02-ARCHITECTURE-FLOWCHARTS.md)
- [Testing](03-TESTING-STRATEGY.md)
- [Configuration Reference](04-CONFIGURATION.md)

---

## Tool reference

These are the names the server exposes over MCP. A client calls them exactly as written here.

| Tool | What it does | Scope a caller needs |
|---|---|---|
| `get_blog_post` | Retrieve a blog post by id | `demo:read` |
| `create_blog_post` | Create a blog post | `demo:write` |
| `get_post_comments` | List the comments on a post | `demo:read` |
| `add_post_comment` | Add a comment to a post | `demo:write` |
| `get_user_todos` | List a user's todo items | `demo:read` |
| `create_user_todo` | Create a todo item for a user | `demo:write` |
| `get_current_weather` | Current conditions at a coordinate in Sweden | `weather:read` |
| `get_forecast` | Forecast for a coordinate in Sweden | `weather:read` |
| `get_forecast_model_info` | Metadata about the forecast model | `weather:read` |
| `get_recent_temperature` | Recent temperature readings from the nearest station | `observations:read` |
| `get_temperature_history` | Daily temperature summary, last ~4 months | `observations:read` |
| `get_precipitation_history` | Daily precipitation totals, last ~4 months | `observations:read` |
| `get_monthly_climate` | One month of the year across all archived years | `observations:read` |

The demo tools (the first six) are enabled in Development only. The `demo:write` tools also need a token issued within the last 5 minutes.

Verify this list against a running server at any time:

```bash
# after initialize, send:
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
```
