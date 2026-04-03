using System.Reflection;
using System.Threading.RateLimiting;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Providers.JsonPlaceholder;
using McpServerTemplate.Providers.Smhi;
using McpServerTemplate.Providers.SmhiObs;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

// ============================================================================
// MCP Server Template — Program.cs
//
// This file is TEMPLATE CORE. It should rarely change when swapping providers.
//
// HOW TO SWAP PROVIDERS:
//   1. Delete the Providers/Smhi/ folder
//   2. Create Providers/YourApi/ with your own tools, client, and DI registration
//   3. Change the ONE line below marked with "PROVIDER-SPECIFIC"
//   4. Update appsettings.json with your provider's config section
//   5. WithToolsFromAssembly() / WithResourcesFromAssembly() / WithPromptsFromAssembly()
//      auto-discover all [McpServerToolType], [McpServerResourceType], [McpServerPromptType]
//      classes — no additional wiring needed.
//
// TRANSPORT:
//   Default is stdio (for IDE/local use). Set environment variable or config:
//     Transport=http  — starts an HTTP server on the configured port
//     Transport=stdio — (default) uses stdin/stdout
// ============================================================================

// ── Serilog bootstrap logger ──
// This catches any errors during host startup, before full DI is available.
// Writes to stderr so stdout stays clean for MCP protocol messages.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(
        standardErrorFromLevel: LogEventLevel.Verbose,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog integration ──
    builder.Services.AddSerilog(config => config
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext());

    // ── Server metadata ──
    var assemblyVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.0.0";

    // ── Rate limit from configuration (defaults to 10/min if not set) ──
    var maxCallsPerToolPerMinute = builder.Configuration.GetValue("RateLimit:MaxCallsPerToolPerMinute", 10);

    // ── MCP Server setup ──
    var mcpBuilder = builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "McpServerTemplate",
                Version = assemblyVersion
            };
        })
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly()
        .WithPromptsFromAssembly()
        .WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(ToolCallLoggingFilter.Create());
            filters.AddCallToolFilter(ToolCallThrottleFilter.Create(maxCallsPerToolPerMinute));
        });

    // ── Transport selection ──
    // "stdio" for IDE/local; "http" for hosted multi-client scenarios.
    var transport = builder.Configuration.GetValue<string>("Transport") ?? "stdio";

    if (transport.Equals("http", StringComparison.OrdinalIgnoreCase))
    {
        var port = builder.Configuration.GetValue("HttpTransport:Port", 3001);
        var bindAddress = builder.Configuration.GetValue("HttpTransport:BindAddress", "localhost");
        Log.Information("Starting MCP server with HTTP transport on {BindAddress}:{Port}", bindAddress, port);
        builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

        // ── Kestrel hardening ──
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 1_048_576; // 1 MB
            kestrel.Limits.MaxConcurrentConnections = 100;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
        });

        // ── Per-client (IP) rate limiting for HTTP transport ──
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true
                    }));
        });

        // ── Restrictive CORS — deny all cross-origin by default ──
        var allowedOrigins = builder.Configuration
            .GetSection("HttpTransport:AllowedOrigins").Get<string[]>();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (allowedOrigins is { Length: > 0 })
                    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
                else
                    policy.SetIsOriginAllowed(_ => false);
            });
        });
    }
    else
    {
        mcpBuilder.WithStdioServerTransport();
    }

    // ── Log path safety check ──
    var logPath = builder.Configuration["Serilog:WriteTo:1:Args:path"];
    if (logPath?.Contains("..") == true)
    {
        throw new InvalidOperationException(
            $"Log file path '{logPath}' contains path traversal characters (..). Use an absolute path.");
    }

    // ── PROVIDER-SPECIFIC: Register your provider's services here ──
    builder.Services.AddSmhiProvider(builder.Configuration);
    builder.Services.AddSmhiObsProvider(builder.Configuration);
    builder.Services.AddJsonPlaceholderProvider(builder.Configuration);

    var app = builder.Build();

    // ── HTTP transport: apply security middleware and map MCP endpoints ──
    if (transport.Equals("http", StringComparison.OrdinalIgnoreCase))
    {
        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseCors();
        app.UseRateLimiter();
        app.MapMcp();
    }

    // ── Startup health check ──
    await HealthProbe.CheckUpstreamAsync(app.Services);

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "MCP Server terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
