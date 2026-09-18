using System.Reflection;
using System.Threading.RateLimiting;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using McpServerTemplate.Providers.JsonPlaceholder;
using McpServerTemplate.Providers.Smhi;
using McpServerTemplate.Providers.SmhiObs;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
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
//   Transport=stdio — (default) stdin/stdout, for a local IDE. Development only: it builds a
//                     plain host with no web server and authenticates nobody.
//   Transport=http  — a hosted server behind the middleware pipeline fixed below.
//
// EXIT CODES (contract-001 · G-2):
//   0  normal shutdown · 70 unhandled failure · 78 configuration the server will not honour
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
    // The transport decides which kind of host is built, so it has to be read before either
    // builder exists. Both environment variables are consulted: the generic host reads
    // DOTNET_ENVIRONMENT and the web host reads ASPNETCORE_ENVIRONMENT, and the stdio guard
    // below must not be escapable by setting only the one this process would otherwise ignore.
    var environmentName =
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environments.Production;

    var bootstrapConfiguration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var transport = (bootstrapConfiguration["Transport"] ?? "stdio").Trim();

    if (transport.Equals("stdio", StringComparison.OrdinalIgnoreCase))
    {
        return await RunStdioAsync(args, environmentName);
    }

    if (transport.Equals("http", StringComparison.OrdinalIgnoreCase))
    {
        return await RunHttpAsync(args);
    }

    throw new ConfigurationException(
        $"Unknown transport '{transport}'. Set Transport to 'stdio' (local IDE, Development only) "
        + "or 'http' (hosted).");
}
catch (ConfigurationException ex)
{
    // The deployment asked for something the server will not do. The operator needs to change
    // configuration, not read a stack trace — so the message is the log, and the code says which.
    Log.Fatal("MCP Server cannot start: {Reason}", ex.Message);
    return ExitCode.Configuration;
}
catch (Exception ex)
{
    Log.Fatal(ex, "MCP Server terminated unexpectedly");
    return ExitCode.Software;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// ── stdio transport ──
// A plain generic host: no Kestrel, no port, no middleware. Nothing binds a socket.
static async Task<int> RunStdioAsync(string[] args, string environmentName)
{
    if (!environmentName.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase))
    {
        throw new ConfigurationException(
            $"Transport 'stdio' is permitted only in Development; this process is running as "
            + $"'{environmentName}'. stdio exposes the server's whole tool surface to whoever owns "
            + "the process, with no authentication and no rate limit per caller. Set Transport=http "
            + "for any hosted environment.");
    }

    var builder = Host.CreateApplicationBuilder(args);

    // contract-002 · G-6 — stdio has no token to verify, so the principal it runs as is declared
    // in configuration and validated against the identity provider it claims to come from. It is
    // built here, at startup, so a wrong development identity stops the process rather than
    // surfacing as a puzzling refusal later.
    // Identity providers are bound only if a deployment configured them; stdio serves a local
    // IDE and calls none of them.
    var configuredIdentity = builder.Configuration.GetSection("Authentication:IdentityProviders").GetChildren().Any()
        ? IdentityConfigurationBinder.Bind(builder.Configuration)
        : null;

    builder.Services.AddSingleton(DevelopmentPrincipal.Create(builder.Configuration, configuredIdentity));

    // contract-002 · G-5 — when a developer has configured identity, stdio enforces the same
    // trust-domain binding HTTP does, so a local run meets the refusals a deployed one would.
    // With no identity configured there is nothing to bind against, and a plain local run for an
    // IDE is left unencumbered rather than shown an empty tool list.
    if (configuredIdentity is not null)
    {
        builder.Services.AddSingleton(services => ProviderBinding.Create(
            builder.Configuration, configuredIdentity, services.GetServices<McpServerTool>()));
    }

    ConfigureLogging(builder.Services, builder.Configuration);
    var mcpBuilder = ConfigureMcpServer(builder.Services, builder.Configuration);
    mcpBuilder.WithStdioServerTransport();
    RegisterProviders(builder.Services, builder.Configuration);

    var host = builder.Build();

    // Forced at startup for the same reason as the HTTP path: a binding that cannot be honoured
    // stops the server rather than surfacing when a call is wrongly allowed.
    host.Services.GetService<ProviderBinding>();

    await host.RunAsync();
    return ExitCode.Ok;
}

// ── HTTP transport ──
static async Task<int> RunHttpAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    var configuration = builder.Configuration;

    ConfigureLogging(builder.Services, configuration);

    // Transport settings are read first, so a mistyped port is reported as a mistyped port
    // rather than behind whichever other configuration failure happens to be checked first.
    var port = ConfigurationGuard.IntegerInRange(
        configuration, "HttpTransport:Port", minimum: 1, maximum: 65535, fallback: 3001,
        because: "a TCP port");
    var bindAddress = configuration.GetValue("HttpTransport:BindAddress", "localhost") ?? "localhost";

    // The whole composition lives in HttpServerComposition so a test can build the same server
    // in-process and reach the bearer handler's backchannel (contract-002 · G-10).
    HttpServerComposition.AddHttpServer(builder, ConfigureMcpServer);

    Log.Information("Starting MCP server with HTTP transport on {BindAddress}:{Port}", bindAddress, port);
    builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

    RegisterProviders(builder.Services, configuration);

    var app = builder.Build().UseHttpServer();

    var binding = app.Services.GetRequiredService<ProviderBinding>();
    Log.Information(
        "Trust domains bound: {Bindings}",
        string.Join(", ", binding.Providers.Select(p => $"{p} -> {binding.IdentityProviderOf(p)}")));

    await app.RunAsync();
    return ExitCode.Ok;
}

// ── Shared registration ──

static void ConfigureLogging(IServiceCollection services, IConfiguration configuration)
{
    // A log path that can climb out of its directory is a write primitive, so it is refused
    // rather than normalised.
    var logPath = configuration["Serilog:WriteTo:1:Args:path"];
    if (logPath?.Contains("..", StringComparison.Ordinal) == true)
    {
        throw new ConfigurationException(
            $"Log file path '{logPath}' contains path traversal characters (..). Use an absolute path.");
    }

    services.AddSerilog(config => config
        .ReadFrom.Configuration(configuration)
        .Enrich.FromLogContext());
}

static IMcpServerBuilder ConfigureMcpServer(IServiceCollection services, IConfiguration configuration)
{
    var assemblyVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.0.0";

    // A limit below 1 would reject every call to every tool; an operator who meant "unlimited"
    // and typed 0 would take the whole server down with no error to read.
    var maxCallsPerToolPerMinute = ConfigurationGuard.IntegerInRange(
        configuration, "RateLimit:MaxCallsPerToolPerMinute", minimum: 1, maximum: 1_000_000,
        fallback: 10, because: "calls per tool per minute");
    var throttle = new ToolCallThrottleFilter(maxCallsPerToolPerMinute);
    services.AddSingleton(throttle);

    return services
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
            filters.AddCallToolFilter(throttle.AsFilter());

            // contract-002 · G-5 — checked on both paths. Hiding a tool from the listing is
            // discretion; refusing the call is enforcement, and a caller can name a tool it was
            // never shown.
            filters.AddListToolsFilter(TrustDomainFilters.List());
            filters.AddCallToolFilter(TrustDomainFilters.Call());

        });
}

// ── PROVIDER-SPECIFIC: Register your provider's services here ──
static void RegisterProviders(IServiceCollection services, IConfiguration configuration)
{
    services.AddSmhiProvider(configuration);
    services.AddSmhiObsProvider(configuration);
    services.AddJsonPlaceholderProvider(configuration);
}
