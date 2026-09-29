using McpServerTemplate.E2EHost;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Providers;
using ModelContextProtocol.Server;
using Serilog;
using Serilog.Events;

// ============================================================================
// McpServerTemplate.E2EHost — test only (contract-005 · G-10)
//
// The shipped server's HTTP composition, through the same public entry points a deployment's host would call —
// HttpServerComposition.AddHttpServer and UseHttpServer — with the built-in providers plus test modules that exist
// only here (TestModules.cs): an irreversible tool whose every run is reported to a witness outside this process,
// and a prompt provider that offers completions. The frame is installed by those two calls and by nothing here, and
// T-8 holds this host's "Frame installed" line to the shipped image's.
//
// It is not the program's Program.cs, whose startup functions are its own; nothing the test host is asked to prove
// depends on them. It reads its settings as the program does (HostBuilders), checks its log sinks as the program
// does, and never passes AddHttpServer's configureIdentityForTests: every token it accepts is verified against an
// identity provider over HTTPS, as the shipped server's are.
//
// CONTAINMENT (Containment.cs): it refuses to start unless Authentication:Resource and every identity provider's
// Authority and Issuer are hosts under .test, checked on the fully built configuration, under any environment name.
//
// EXIT CODES, as the program's: 0 normal shutdown · 70 unhandled failure · 78 configuration it will not honour.
// ============================================================================

// Serilog's own failures, on stderr, as the program has them (contract-005 · G-12 (5)).
Serilog.Debugging.SelfLog.Enable(Console.Error);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(
        standardErrorFromLevel: LogEventLevel.Verbose,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = HostBuilders.ForHttp(args);
    var configuration = builder.Configuration;

    // HTTP only: the test host has no stdio mode, and a transport it would not honour is refused rather than ignored.
    var transport = (configuration["Transport"] ?? string.Empty).Trim();
    if (!transport.Equals("http", StringComparison.OrdinalIgnoreCase))
    {
        throw new ConfigurationException($"Transport is '{transport}'; the test host serves HTTP only. Set Transport=http.");
    }

    LogSinkGuard.Validate(configuration, Directory.GetCurrentDirectory());
    builder.Services.AddSerilog(logging => logging
        .ReadFrom.Configuration(configuration)
        .Enrich.FromLogContext()
        .Filter.ByExcluding(logEvent => logEvent.Exception is { } exception
            && logEvent.MessageTemplate.Text == "Hosting failed to start"
            && HttpServerComposition.IsBindFailure(exception)));

    var port = ConfigurationGuard.IntegerInRange(
        configuration, "HttpTransport:Port", minimum: 1, maximum: 65535, fallback: 3001, because: "a TCP port");
    var bindAddress = configuration.GetValue("HttpTransport:BindAddress", "localhost") ?? "localhost";

    HttpServerComposition.AddHttpServer(builder, [.. BuiltInProviders.Create(), .. TestModules.Create()]);

    // The prompt provider's completions (TestModules.cs, CompletingPromptModule): the SDK's one completion handler,
    // which no module may install. The frame's completion filter runs in front of it.
    builder.Services.Configure<McpServerOptions>(options => options.Handlers.CompleteHandler = CompletingPrompts.CompleteAsync);

    Log.Information("Starting the end-to-end test host with HTTP transport on {BindAddress}:{Port}", bindAddress, port);
    builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

    await using var app = builder.Build();

    // contract-005 · G-10 — on the fully built configuration, before anything is served.
    Containment.Verify(app.Configuration, app.Services);

    app.UseHttpServer();
    try
    {
        await app.StartAsync();
    }
    catch (Exception ex) when (HttpServerComposition.BindFailure(ex, bindAddress, port) is { } refusal)
    {
        throw refusal;
    }

    await app.WaitForShutdownAsync();
    return ExitCode.Ok;
}
catch (ConfigurationException ex)
{
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
