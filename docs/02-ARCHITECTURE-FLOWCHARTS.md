# Architecture Flowcharts

This document contains flowcharts describing the MCP server structure and request flow, using a generic provider as an example.

## 1. High-Level System Architecture

```mermaid
graph TB
    subgraph Client["MCP Clients"]
        A1["Claude Desktop"]
        A2["VS Code IDE"]
        A3["Custom Client"]
    end

    subgraph Transport["Transport Layer"]
        T1["stdio<br/>(stdin/stdout)"]
        T2["HTTP Server<br/>(port 3001)"]
    end

    subgraph Middleware["Security & Observability Layer"]
        M1["ApiKeyMiddleware<br/>(authentication)"]
        M2["ToolCallLoggingFilter<br/>(logging)"]
        M3["ToolCallThrottleFilter<br/>(rate limiting)"]
    end

    subgraph MCP["MCP Protocol Layer<br/>(auto-discovery)"]
        MP["Tool Registry<br/>(GetBlogPost, CreatePost, etc.)"]
    end

    subgraph Providers["Provider Layer (Pluggable)"]
        P1["Provider A: JsonPlaceholder"]
        P2["Provider B: Weather"]
        P3["Provider C: Your API"]
    end

    subgraph External["External APIs"]
        E1["https://jsonplaceholder.typicode.com"]
        E2["https://weather-api.service"]
        E3["Your API Server"]
    end

    Client -->|MCP Protocol| Transport
    Transport -->|Routes to| Middleware
    Middleware -->|Invoke Tool| MCP
    MCP -->|Dispatch| Providers
    Providers -->|HTTP Request| External
    External -->|Response| Providers
    Providers -->|Formatted Result| MCP
    MCP -->|Tool Result| Middleware
    Middleware -->|Response| Transport
    Transport -->|Return to| Client

    style Client fill:#e1f5ff
    style Transport fill:#fff3e0
    style Middleware fill:#f3e5f5
    style MCP fill:#e8f5e9
    style Providers fill:#fce4ec
    style External fill:#f1f8e9
```

---

## 2. Request Lifecycle: Complete Flow

```mermaid
sequenceDiagram
    participant Client as MCP Client
    participant Transport as Transport Layer<br/>(stdio/HTTP)
    participant Auth as ApiKeyMiddleware
    participant Logger as ToolCallLogging
    participant RateLimit as RateLimit Filter
    participant MCP as MCP Protocol
    participant Tool as GenericProvider<br/>Tools
    participant ApiClient as GenericProvider<br/>ApiClient
    participant API as External API
    participant Formatter as GenericProvider<br/>Formatter

    Client->>Transport: "Call GetData with param=123"
    Transport->>Auth: Pass request
    Auth->>Auth: Validate API Key (if HTTP)
    Auth-->>Transport: ✓ Authenticated
    Transport->>Logger: Log incoming request
    Logger->>RateLimit: Check rate limit for this tool
    RateLimit->>RateLimit: Is tool < 10 calls/min?
    alt Rate Limit OK
        RateLimit-->>MCP: ✓ Allowed
    else Rate Limit Exceeded
        RateLimit-->>Client: ✗ 429 Too Many Requests
        MCP->>Logger: Log rejection
    end
    MCP->>MCP: Find "GetData" in registered tools
    MCP->>Tool: Call GenericProviderTools.GetData(123)
    Tool->>Tool: Validate input (123 is valid)
    Tool->>ApiClient: Call GetDataAsync(123)
    ApiClient->>ApiClient: Build HTTP request
    ApiClient->>API: GET /api/data?id=123
    API-->>ApiClient: { "id": 123, "name": "...", "value": "..." }
    ApiClient->>Formatter: Format response
    Formatter->>Formatter: Transform to readable text
    Formatter-->>Tool: "📊 Data Item #123\n  Name: ...\n  Value: ..."
    Tool-->>MCP: Return formatted result
    MCP->>Logger: Log tool completion (duration, result size)
    Logger-->>Transport: ✓ Done
    Transport-->>Client: Return result
```

---

## 3. Startup Initialization Sequence

```mermaid
sequenceDiagram
    participant Main as Main Entry Point
    participant Logger as Serilog Bootstrap
    participant Builder as WebApplication Builder
    participant DI as Dependency Injection
    participant Config as Configuration
    participant Transport as Transport Setup
    participant HealthCheck as Health Probe

    Main->>Logger: Initialize Serilog (catch startup errors)
    Logger-->>Main: ✓ Logger ready
    Main->>Builder: CreateBuilder()
    Builder->>DI: Configure services
    DI->>Config: Load appsettings.json
    Config->>Config: Resolve environment (Development/Production)
    Config->>Config: Load appsettings.{Environment}.json
    Config->>Config: Apply environment variables
    Config-->>DI: Configuration ready
    DI->>DI: Register GenericProvider<br/>(Config + HttpClient)
    DI->>DI: Register MCP Server
    DI->>DI: Register Middleware filters
    Builder->>Transport: Select transport (stdio or HTTP)
    alt HTTP Transport
        Transport->>Transport: Configure Kestrel<br/>(port, limits)
        Transport->>Transport: Setup CORS<br/>(allow/deny rules)
        Transport->>Transport: Setup Rate Limiting<br/>(per-client)
    else stdio Transport
        Transport-->>Main: No additional setup needed
    end
    Builder->>Builder: Build() → Application instance
    Main->>HealthCheck: Run health probe<br/>(can I reach the upstream API?)
    alt Upstream API OK
        HealthCheck-->>Main: ✓ Startup OK
    else Upstream API Unreachable
        HealthCheck-->>Main: ⚠ Warning logged<br/>(but startup continues)
    end
    Main->>Main: app.RunAsync()
    Main-->>Main: Server is now running
```

---

## 4. Provider Structure & Relationships

```mermaid
graph LR
    subgraph GenericProvider["GenericProvider Package"]
        Config["GenericConfig.cs<br/>────────────<br/>- BaseUrl<br/>- ApiKey<br/>- Timeout"]
        
        ApiClient["GenericApiClient.cs<br/>────────────<br/>+ GetDataAsync()<br/>+ CreateDataAsync()<br/>+ DeleteDataAsync()"]
        
        Tools["GenericTools.cs<br/>────────────<br/>[McpServerTool]<br/>+ GetData()<br/>+ CreateData()<br/>+ DeleteData()"]
        
        Models["Models/<br/>────────────<br/>- DataItem.cs<br/>- DataResponse.cs"]
        
        Formatters["GenericFormatters.cs<br/>────────────<br/>+ FormatDataItem()<br/>+ FormatList()"]
        
        DI["GenericServiceRegistration.cs<br/>────────────<br/>+ AddGenericProvider()"]
    end

    subgraph "Program.cs"
        Program["var builder = ....<br/>builder.Services<br/>&nbsp;&nbsp;.AddGenericProvider()"]
    end

    subgraph "appsettings.json"
        AppSettings["'Providers': {<br/>&nbsp;&nbsp;'Generic': {<br/>&nbsp;&nbsp;&nbsp;&nbsp;'BaseUrl': '...',<br/>&nbsp;&nbsp;&nbsp;&nbsp;'ApiKey': '...'<br/>&nbsp;&nbsp;}<br/>}"]
    end

    Program -->|Calls| DI
    DI -->|Reads| AppSettings
    DI -->|Binds to Config| Config
    DI -->|Registers| ApiClient
    ApiClient -->|Uses| Models
    Tools -->|Calls| ApiClient
    Tools -->|Calls| Formatters
    Formatters -->|Uses| Models

    style GenericProvider fill:#fff9c4
    style Program fill:#c8e6c9
    style AppSettings fill:#bbdefb
```

---

## 5. Middleware & Filter Chain

```mermaid
graph LR
    Request["Incoming<br/>Request"] -->|1. Route| Auth["ApiKeyMiddleware<br/>────────────<br/>Validate X-Api-Key<br/>header"]
    
    Auth -->|2. Log Start| Logger["ToolCallLoggingFilter<br/>────────────<br/>Record timestamp<br/>Log parameters"]
    
    Logger -->|3. Check Limit| RateLimit["ToolCallThrottleFilter<br/>────────────<br/>Check: tool calls<br/>this minute?<br/>< 10?"]
    
    RateLimit -->|4. Allowed| Execute["Execute Tool<br/>(Provider logic)"]
    RateLimit -->|4. Denied| TooMany["Return 429<br/>Too Many Requests"]
    
    Execute -->|5. Complete| LogEnd["ToolCallLoggingFilter<br/>────────────<br/>Log completion<br/>Response size<br/>Duration"]
    
    LogEnd -->|6. Return| Response["Response to<br/>Client"]
    
    TooMany -->|Reject| Response

    style Auth fill:#ffccbc
    style Logger fill:#c5e1a5
    style RateLimit fill:#ffccbc
    style Execute fill:#c8e6c9
    style TooMany fill:#ffcdd2
    style Response fill:#e1f5fe
```

---

## 6. Configuration Flow: Precedence & Merging

```mermaid
graph TB
    subgraph "1. Base Config"
        Base["appsettings.json<br/>────────────<br/>Transport: stdio<br/>RateLimit: 10/min"]
    end

    subgraph "2. Environment-Specific"
        Env["appsettings.Development.json<br/>────────────<br/>Log Level: Debug<br/>RateLimit: 30/min<br/>(override)"]
    end

    subgraph "3. Environment Variables"
        EnvVars["$env:Transport = 'http'<br/>$env:RateLimit__MaxCallsPerToolPerMinute = '20'"]
    end

    subgraph "Result"
        Final["Final Config<br/>────────────<br/>Transport: http<br/>(from env var)<br/>Log Level: Debug<br/>(from env appsettings)<br/>RateLimit: 20/min<br/>(from env var)"]
    end

    Base -->|Merge| Env
    Env -->|Override| EnvVars
    EnvVars -->|Result| Final

    style Base fill:#b3e5fc
    style Env fill:#fff9c4
    style EnvVars fill:#ffe0b2
    style Final fill:#c8e6c9
```

---

## 7. Tool Invocation: What Happens Behind the Scenes

```mermaid
graph TD
    A["Client: 'Get data for ID 123'"] -->|MCP Protocol| B["MCP Server receives request"]
    
    B --> C{"Tool exists?<br/>GetData registered<br/>with [McpServerTool]?"}
    
    C -->|Yes| D["Resolve Tool Handler<br/>GenericProviderTools.GetData"]
    
    C -->|No| E["Return Error:<br/>Unknown tool"]
    
    D --> F["Dependency Injection<br/>Inject GenericApiClient"]
    
    F --> G["Call Tool Method<br/>GetData 123"]
    
    G --> H["Input Validation<br/>Is 123 valid?<br/>Within bounds?"]
    
    H -->|Invalid| I["Return Error<br/>with recovery hint"]
    
    H -->|Valid| J["Call ApiClient<br/>GetDataAsync 123"]
    
    J --> K["Build HTTP Request<br/>GET /api/data?id=123<br/>With resilience:<br/>- Retry policy<br/>- Circuit breaker<br/>- Timeout"]
    
    K --> L["Send to External API"]
    
    L --> M["Parse Response<br/>Deserialize JSON<br/>Into DataItem model"]
    
    M --> N["Format Output<br/>GenericFormatters<br/>.FormatDataItem"]
    
    N --> O["Return to Client<br/>Human-readable text"]

    E -->|End| Z1["❌"]
    I -->|End| Z2["❌"]
    O -->|End| Z3["✅"]

    style A fill:#c8e6c9
    style D fill:#bbdefb
    style F fill:#f8bbd0
    style J fill:#fdd835
    style L fill:#ff9800
    style N fill:#9c27b0
    style O fill:#c8e6c9
```

---

## 8. Error Handling Flow

```mermaid
graph LR
    Start["Tool Called"] --> Input{Input Validation}
    
    Input -->|Invalid| E1["InvalidArgumentException<br/>────────────<br/>Return error message<br/>with recovery hint"]
    
    Input -->|Valid| ApiCall{"HTTP Request<br/>to Upstream"}
    
    ApiCall -->|Timeout| E2["TimeoutException<br/>────────────<br/>Retry with backoff<br/>Max 3 attempts<br/>Then fail"]
    
    ApiCall -->|500+ Error| E3["HttpRequestException<br/>────────────<br/>Circuit breaker<br/>activates if > 5<br/>consecutive failures"]
    
    ApiCall -->|404 Not Found| E4["HttpRequestException<br/>────────────<br/>Resource doesn't exist<br/>Return not found"]
    
    ApiCall -->|200 OK| Success["Parse & Format<br/>Return result"]
    
    E1 --> LogError["Log Error<br/>with context"]
    E2 --> LogError
    E3 --> LogError
    E4 --> LogError
    LogError --> Return["Return to Client"]
    Success --> Return

    style Start fill:#e3f2fd
    style E1 fill:#ffebee
    style E2 fill:#fff3e0
    style E3 fill:#fce4ec
    style E4 fill:#f1f8e9
    style Success fill:#c8e6c9
    style Return fill:#e1f5fe
```

---

## 9. Deployment: stdio vs HTTP

```mermaid
graph TB
    subgraph "Stdio Mode (Local Development)"
        Config1["Transport: stdio"]
        Client1["VS Code IDE<br/>Claude Desktop<br/>(on same machine)"]
        Server1["MCP Server"]
        
        Client1 -->|stdin/stdout| Server1
        Config1 -.-> Server1
    end

    subgraph "HTTP Mode (Hosted)"
        Config2["Transport: http<br/>Port: 3001<br/>ApiKey required"]
        Clients2["Multiple Clients<br/>(AI assistants,<br/>tools, services)"]
        Server2["MCP Server"]
        
        Clients2 -->|HTTP + X-Api-Key| Server2
        Config2 -.-> Server2
    end

    subgraph "Security Stack (HTTP)"
        TLS["TLS/SSL<br/>(via reverse proxy)"]
        KestrelLimits["Kestrel Limits<br/>- Max body: 1MB<br/>- Max connections: 100<br/>- Timeout: 30s"]
        CORS["CORS Policy<br/>Deny-all by default"]
        RateLimit["Per-Client Rate Limit<br/>60 req/min"]
    end

    Server2 --> TLS
    Server2 --> KestrelLimits
    Server2 --> CORS
    Server2 --> RateLimit

    style Config1 fill:#c8e6c9
    style Client1 fill:#bbdefb
    style Server1 fill:#fff9c4
    style Config2 fill:#c8e6c9
    style Clients2 fill:#bbdefb
    style Server2 fill:#fff9c4
    style TLS fill:#ffccbc
    style KestrelLimits fill:#ffccbc
    style CORS fill:#ffccbc
    style RateLimit fill:#ffccbc
```

---

## 10. Adding a New Provider: Step-by-Step

```mermaid
graph LR
    A["1. Create Folder<br/>Providers/GitHub"] --> B["2. Create Files"]
    
    B --> B1["GithubConfig.cs<br/>(settings)"]
    B --> B2["GithubApiClient.cs<br/>(HTTP calls)"]
    B --> B3["GithubTools.cs<br/>(MCP tools)"]
    B --> B4["GithubFormatters.cs<br/>(output)"]
    B --> B5["GithubServiceRegistration.cs<br/>(DI wiring)"]
    B --> B6["Models/"]
    
    B1 --> C["3. Implement DI<br/>in Program.cs"]
    B2 --> C
    B3 --> C
    B4 --> C
    B5 --> C
    B6 --> C
    
    C --> D["4. Add Config<br/>to appsettings.json"]
    
    D --> E["5. Auto-Discovery<br/>WithToolsFromAssembly()"]
    
    E --> F["✅ GitHub Tools<br/>Available to Users"]

    style A fill:#fff9c4
    style B fill:#bbdefb
    style B1 fill:#c8e6c9
    style B2 fill:#c8e6c9
    style B3 fill:#c8e6c9
    style B4 fill:#c8e6c9
    style B5 fill:#c8e6c9
    style B6 fill:#c8e6c9
    style C fill:#ffe0b2
    style D fill:#ffe0b2
    style E fill:#f8bbd0
    style F fill:#a5d6a7
```
