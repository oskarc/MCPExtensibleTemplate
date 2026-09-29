namespace McpServerTemplate.Infrastructure;

/// <summary>
/// The two hosts the server runs in — the web host for HTTP, the generic host for stdio — built so that
/// each reads its settings once, at startup, where every check reads them.
///
/// contract-003 · G-11, contract-005 review round 2 — the default hosts watch every settings file they
/// read (appsettings.json, appsettings.{Environment}.json, McpServerTemplate.settings.json and its
/// {Environment} form, and in Development the user secrets) and read a changed file back into the
/// running server. The startup checks never see what a file gains afterwards, and the framework applies
/// it: a bearer scheme first used after the change is built from Authentication:Schemes:{scheme} — its
/// MetadataAddress sent to another provider's keys took over that trust domain on the shipped image —
/// Kestrel opens the endpoints Kestrel:Endpoints names, and Serilog takes new levels. Each of those keys
/// is refused at startup; none was refused afterwards. Now no file is watched: a changed setting takes
/// effect when the server restarts, and is checked then.
///
/// Review round 3 — that used to rest on a setting appended to the launch arguments, and a switch with
/// no value at the end of a launch (--MyFlag) took it as its value, so every file was watched again. It
/// rests on no argument now: once the host is built, every settings file it reads is set not to be read
/// again, and every provider is built afresh from its source, so none holds a watch. Nothing in a launch
/// comes after that. GovernedServer checks the result (SettingsReadOnce), whatever built it.
/// </summary>
public static class HostBuilders
{
    /// <summary>
    /// The host setting the default settings files take their reload flag from
    /// (HostingHostBuilderExtensions.ApplyDefaultAppConfiguration), switched off. Not relied on —
    /// <see cref="ReadOnce"/> is the guarantee — but a host told so starts no file watcher it would then
    /// leave with nothing to do. First, where no switch can take it as its value. A launch that asks for
    /// reloading instead — on the command line or as DOTNET_ in the environment, and for the web host as
    /// ASPNETCORE_ too (the generic host does not read those) — is a request the server would ignore, and
    /// SettingsReadOnce refuses it at startup, naming where it came from.
    /// </summary>
    private const string StartNoWatcher = "--hostBuilder:reloadConfigOnChange=false";

    /// <summary>
    /// contract-005 · T-10 — how long the web host gives the requests in flight once it is told to stop: 8 seconds, inside
    /// Docker's 10-second stop grace. The framework's own default is 30 seconds (HostOptions.ShutdownTimeout in
    /// Microsoft.Extensions.Hosting 10.0), so a server stopped while it held a request waited past the grace and was
    /// killed — exit 137, no shutdown line — however cleanly it would have stopped. Now a request still running after 8
    /// seconds has its connection closed, and the server stops and says so, inside the grace.
    /// </summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The web host for Transport=http.</summary>
    public static WebApplicationBuilder ForHttp(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder([StartNoWatcher, .. args]);
        ReadOnce(builder.Configuration);

        // contract-005 · T-10 — registered after the host's own reading of its settings, so this is the timeout it stops with.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);
        return builder;
    }

    /// <summary>The plain generic host for Transport=stdio: no Kestrel, no port, no middleware.</summary>
    public static HostApplicationBuilder ForStdio(string[] args, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [StartNoWatcher, .. args],
            EnvironmentName = environmentName,
        });
        ReadOnce(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Every settings file the host reads, set not to be read again, and every provider built afresh from
    /// its source: clearing the sources disposes the providers the host built first, with any watch they
    /// held, and none built now watches. The same sources, in the same order, so every value is what the
    /// host read (HostBuildersTests compares them, key by key).
    /// </summary>
    private static void ReadOnce(ConfigurationManager configuration)
    {
        var sources = configuration.Sources.ToArray();
        foreach (var file in sources.OfType<FileConfigurationSource>())
        {
            file.ReloadOnChange = false;
        }

        configuration.Sources.Clear();
        foreach (var source in sources)
        {
            configuration.Sources.Add(source);
        }
    }
}
