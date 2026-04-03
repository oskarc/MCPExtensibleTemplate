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
Claude: (uses GetCurrentWeather tool)
Claude: "It's 15°C and cloudy."
✅ Works!
```

**Examples of tools**:
- `GetBlogPost` - retrieve a blog post
- `CreateBlogPost` - write a new post
- `GetUserTodos` - view tasks
- `CreateUserTodo` - add a task

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
- How fast can my AI call tools? (10 times per minute? 100?)
- What's my API key? (security password)
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

Works across the internet. Requires authentication.
Needs rate limiting to prevent abuse.
```

---

## Getting Started: Step by Step

### Step 1: Install Prerequisites

You need **one thing**: .NET 8 SDK

Download from: https://dotnet.microsoft.com/download/dotnet/8.0

Verify it's installed:
```bash
dotnet --version
# Output: 8.0.x (or higher)
```

### Step 2: Open the Project

```bash
cd d:\projects\mcp\McpServerTemplate
```

### Step 3: Run the Server (Stdio Mode - Local)

```bash
dotnet run
```

**What happens**:
- The server starts
- It listens on stdin/stdout
- Clients (like VS Code or Claude Desktop) can now connect
- You should see log output in your terminal indicating the server has started

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
         "args": ["run", "--project", "d:/projects/mcp/McpServerTemplate"]
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

1. Claude sees you have a `GetBlogPost` tool available
2. Claude calls: `GetBlogPost(postId=1)`
3. MCP Server receives this request
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
Claude: Calls GetBlogPost(99999)
MCP Server: "Post not found"
Claude: "I couldn't find post #99999. Valid posts are 1-100."
```

### Error 2: Rate Limit Exceeded

```
You: "Create 50 new posts" (calling CreateBlogPost 50 times in 1 minute)
Config says: Max 10 calls per minute
MCP Server: "🛑 Rate limit exceeded!"
Claude: "I've been rate-limited. Please wait a minute before trying again."
```

### Error 3: External API is Down

```
Claude: Calls GetBlogPost(1)
MCP Server: Makes HTTP request to external API
External API: (no response - server is down)
MCP Server: Retries (up to 3 times) with backoff
MCP Server: "😞 External service is unavailable"
Claude: "The blog service is currently unavailable."
```

---

## Configuration for Beginners

### Scenario 1: Running Locally (Development)

This is what you have right now. No configuration needed!

**Command**:
```bash
dotnet run
```

**What's happening**:
- Transport: stdio (local only)
- Rate limit: 10 calls per tool per minute (base config)
- Logs: Information level (base config)

> **Tip**: To get relaxed limits (30 calls/min) and Debug logging, set the environment first:
> ```powershell
> $env:ASPNETCORE_ENVIRONMENT = "Development"
> dotnet run
> ```
> Without this, `appsettings.Development.json` is **not** loaded.

---

### Scenario 2: Running on the Network (Testing)

Want to test from another computer? Switch to HTTP mode.

**Step 1**: Create file `appsettings.Development.json` (already exists, but let's check it):

```json
{
  "Transport": "http",
  "HttpTransport": {
    "Port": 3001,
    "BindAddress": "0.0.0.0"
  },
  "Authentication": {
    "ApiKey": "test-key-12345"
  }
}
```

**Step 2**: Run the server

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

Or on bash/zsh:

```bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet run
```

**Step 3**: Test from another computer

```bash
# From another computer on the same network
curl -H "X-Api-Key: test-key-12345" \
  http://<your-computer-ip>:3001/mcp
```

---

### Scenario 3: Production Deployment (Secure, Internet-Facing)

**Create** `appsettings.Production.json`:

```json
{
  "Transport": "http",
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

**Set environment variables** (securely, not in code):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Transport = "http"
$env:HttpTransport__Port = "3001"
$env:Authentication__ApiKey = [Convert]::ToHexString((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
$env:HttpTransport__BindAddress = "0.0.0.0"
```

Or on bash/zsh:

```bash
export ASPNETCORE_ENVIRONMENT=Production
export Transport=http
export HttpTransport__Port=3001
export Authentication__ApiKey=$(openssl rand -hex 32)  # Very long random key
export HttpTransport__BindAddress=0.0.0.0
```

**Why these changes**?
- Stricter rate limit (prevents abuse)
- Minimal logging (faster, less disk space)
- Long random API key (more secure)
- Bound to all IPs (accessible from internet)

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

**Step 3**: Register in Program.cs

```csharp
builder.Services.AddGithubProvider(builder.Configuration);
```

**Step 4**: Add config to appsettings.json

```json
{
  "Providers": {
    "Github": {
      "BaseUrl": "https://api.github.com",
      "UserAgent": "MyMcpServer/1.0"
    }
  }
}
```

**Step 5**: Your tools are now available!

Claude can call `GetRepository`, `CreateIssue`, etc.

---

## Debugging: Tools for Understanding What's Happening

### 1. Check the Logs

When the server starts, tool registrations and requests are logged to the `logs/` folder and to stderr. Check the log files for details on which tools were discovered and any errors.

### 2. View Request Logs

The server logs every tool call with a correlation ID:

```
[12:34:57 INF] ToolCallLoggingFilter: [abc123] Calling tool GetBlogPost with arguments: postId
[12:34:57 INF] ToolCallLoggingFilter: [abc123] Tool GetBlogPost completed in 234ms
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
# Test that server is running
curl -H "X-Api-Key: test-key-12345" http://localhost:3001/mcp

# (Exact endpoint depends on MCP client implementation)
```

---

## FAQ (Frequently Asked Questions)

**Q: Can I use this without an external API?**

A: Yes! The server is just a framework. You can create mock providers that return fake data.

---

**Q: What happens if the external API goes down?**

A: The server has built-in retry logic (up to 3 attempts). If the API is still down, it returns a friendly error to Claude.

---

**Q: How do I keep this running 24/7?**

A: Deploy it to the cloud (Azure, AWS, Heroku) or use a service manager like systemd (Linux) or forever (Node.js).

---

**Q: Is my data secure?**

A: Yes! The server validates all inputs, requires API keys (in HTTP mode), rate-limits requests, and uses HTTPS-only connections to external APIs.

---

**Q: Can multiple people use this at the same time?**

A: Yes! In HTTP mode, multiple AI assistants can connect simultaneously. The server handles up to 100 concurrent connections by default.

---

**Q: How do I modify how tools work?**

A: Edit the tool in `Providers/YourProvider/YourProviderTools.cs`. The MCP server auto-discovers changes when you restart.

---

## Next Steps

1. **Get it running**: `dotnet run` in stdio mode
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
1. Make sure the server is running: `dotnet run`
2. Restart your Claude Desktop or VS Code
3. Check logs for errors: in PowerShell run `$env:Serilog__MinimumLevel__Default = "Debug"`, then run again

### Pitfall 2: "Rate limit keeps hitting me"

**Cause**: You're calling tools too fast (~10+ times per minute)

**Solution**: Increase the rate limit for development:
```powershell
$env:RateLimit__MaxCallsPerToolPerMinute = "100"
```

```bash
export RateLimit__MaxCallsPerToolPerMinute=100
```

Or just wait a minute before retrying.

### Pitfall 3: "Getting weird JSON errors"

**Cause**: External API response changed format

**Solution**: Check the API documentation, update your `Models/*.cs` files to match the new response format

### Pitfall 4: "Server won't start"

**Cause**: Configuration error or port already in use

**Solution**:
1. Check PORT: Is 3001 already in use? In PowerShell use `$env:HttpTransport__Port = "3002"`
2. Check CONFIG: Review `appsettings.json` for syntax errors
3. Check LOGS: Run with all logs enabled to see the real error

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
