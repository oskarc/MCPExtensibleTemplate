using System.Reflection;
using System.Threading.RateLimiting;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
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
//   1. Create Providers/YourApi/ with static tool, resource and prompt classes and a client
//   2. Write an IProviderModule declaring its policy — a scope, risk and limits for every tool,
//      a scope for every resource and prompt, and the hosts it may call
//   3. List the module in Providers/BuiltInProviders.cs and its name in Providers:Enabled
//   Nothing is found by scanning: a class no module names is not served, and a primitive with
//   no policy stops the server from starting (contract-003).
//
// TRANSPORT:
//   Transport=stdio — (default) stdin/stdout, for a local IDE. Development only: it builds a
//                     plain host with no web server and verifies no token; it runs as the
//                     Development principal, through the same checks as an HTTP caller.
//   Transport=http  — a hosted server behind the middleware pipeline fixed below.
//
// EXIT CODES (contract-001 · G-2):
//   0  normal shutdown · 70 unhandled failure · 78 configuration the server will not honour
// ============================================================================

// ── Serilog's own failures, on stderr ──
// contract-005 · G-12 (5) — a sink that fails (a log directory it cannot write, a disk that fills, a
// sink name Serilog does not know) reports to Serilog's self-log, which is off unless enabled. Off,
// the sink wrote nothing and said nothing. On stderr, beside the logs, so stdout stays the protocol's.
Serilog.Debugging.SelfLog.Enable(Console.Error);

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

    // The generic host reads DOTNET_ENVIRONMENT only; the guard above also accepts
    // ASPNETCORE_ENVIRONMENT. Without this the guard could pass as Development while the host ran
    // as Production, loading Production settings — and, since contract-003, Production's rules.
    // contract-005 review round 2 — and it reads its settings once: no settings file is watched.
    var builder = HostBuilders.ForStdio(args, environmentName);
    var modules = BuiltInProviders.Create();

    // contract-002 · G-6 — stdio has no token to verify, so the principal it runs as is declared
    // in configuration and validated against the identity provider it claims to come from.
    // contract-003 · G-14 — the same gate runs here as over HTTP. With no identity providers
    // configured, a Development-only identity is synthesised: one per identity provider the
    // providers are bound to, able to issue exactly the scopes their policies require, so the
    // binding, scope, argument and limit checks all still run against the local principal.
    var identity = builder.Configuration.GetSection("Authentication:IdentityProviders").GetChildren().Any()
        ? IdentityConfigurationBinder.Bind(builder.Configuration)
        : DevelopmentPrincipal.SynthesizeIdentity(builder.Configuration, modules);

    builder.Services.AddSingleton(identity);
    builder.Services.AddSingleton(DevelopmentPrincipal.Create(builder.Configuration, identity));

    ConfigureLogging(builder.Services, builder.Configuration);
    builder.Services.AddGovernedMcpServer(builder.Configuration, builder.Environment, modules)
        .WithStdioServerTransport();

    var host = builder.Build();

    // Checked at startup for the same reason as the HTTP path: a policy or binding that cannot be
    // honoured stops the server rather than surfacing when a call is wrongly allowed.
    GovernedServer.ValidateAtStartup(host.Services);

    await host.RunAsync();
    return ExitCode.Ok;
}

// ── HTTP transport ──
static async Task<int> RunHttpAsync(string[] args)
{
    // contract-005 review round 2 — settings read once, at startup, where they are checked: a settings
    // file changed while the server runs changes nothing until it restarts (HostBuilders).
    var builder = HostBuilders.ForHttp(args);
    var configuration = builder.Configuration;

    ConfigureLogging(builder.Services, configuration);

    // Transport settings are read first, so a mistyped port is reported as a mistyped port
    // rather than behind whichever other configuration failure happens to be checked first.
    var port = ConfigurationGuard.IntegerInRange(
        configuration, "HttpTransport:Port", minimum: 1, maximum: 65535, fallback: 3001,
        because: "a TCP port");
    var bindAddress = configuration.GetValue("HttpTransport:BindAddress", "localhost") ?? "localhost";

    // The whole composition lives in HttpServerComposition so a test can build the same server
    // in-process and reach the bearer handler's backchannel (contract-002 · G-10), and the MCP
    // server inside it is composed by the frame alone (contract-003 · G-3).
    HttpServerComposition.AddHttpServer(builder, BuiltInProviders.Create());

    Log.Information("Starting MCP server with HTTP transport on {BindAddress}:{Port}", bindAddress, port);
    builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

    await using var app = builder.Build().UseHttpServer();
    try
    {
        await app.StartAsync();
    }
    catch (Exception ex) when (HttpServerComposition.BindFailure(ex, bindAddress, port) is { } refusal)
    {
        // contract-005 · G-12 (2), review round 8 — a failure to bind the configured address is the operator's to
        // fix, said in plain words (exit 78), not a stack trace; anything else at startup stays unexpected (exit 70).
        throw refusal;
    }

    await app.WaitForShutdownAsync();

    // contract-005 · T-10 — the stop said aloud, as the start is ("Frame installed"). docker stop sends SIGTERM, the host
    // stops accepting requests and finishes its work, and this line is the record, written only once the host has stopped:
    // a server killed before it finished writes none. sys_shutdown is the roadmap's standard name for the event (LOG-01).
    var lifetime = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServerTemplate.Lifetime");
    if (lifetime.IsEnabled(LogLevel.Information))
    {
        lifetime.LogInformation("sys_shutdown: the server has stopped; it accepts no more requests and exits with code {ExitCode}", ExitCode.Ok);
    }

    return ExitCode.Ok;
}

// ── Shared registration ──

static void ConfigureLogging(IServiceCollection services, IConfiguration configuration)
{
    // contract-005 · G-12 (5) — every File sink, at every index, where it will write: a path that
    // climbs out of its directory, or one this process cannot write, stops the server here. The
    // check used to read Serilog:WriteTo:1 alone, for traversal only.
    LogSinkGuard.Validate(configuration, Directory.GetCurrentDirectory());

    services.AddSerilog(config => config
        .ReadFrom.Configuration(configuration)
        .Enrich.FromLogContext()
        // contract-005 · G-12 (2), review round 8 — a failure to bind is refused in plain words, exit 78
        // (HttpServerComposition.BindFailure); the host's own "Hosting failed to start", with its stack trace, is
        // left out for that, and for nothing else.
        .Filter.ByExcluding(logEvent => logEvent.Exception is { } exception
            && logEvent.MessageTemplate.Text == "Hosting failed to start"
            && HttpServerComposition.IsBindFailure(exception)));
}
