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
    /// contract-005 · T-10 — how long the web host gives the requests in flight once it is told to stop: 6 seconds, inside
    /// Docker's 10-second stop grace. The framework's own default is 30 seconds (HostOptions.ShutdownTimeout in
    /// Microsoft.Extensions.Hosting 10.0), so a server stopped while it held a request waited past the grace and was
    /// killed — exit 137, no shutdown line — however cleanly it would have stopped. Now a request still running after 6
    /// seconds has its connection closed, and the server stops and says so, inside the grace.
    ///
    /// contract-005 · G-17 round 1 — 6, not 8: the timeout bounds the host's own stop and nothing after it — the shutdown
    /// line, disposing the app and its Redis connection, flushing the log, the runtime's exit — which took 0.3 to 1.6 seconds
    /// under load, so at 8 a busy stop finished 0.4 to 1.7 seconds inside the grace. At 6 the rest has 4 seconds.
    /// </summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(6);

    /// <summary>The framework's own host setting for that time, in whole seconds (HostOptions reads it from the host's settings).</summary>
    private const string ShutdownTimeoutSetting = "shutdownTimeoutSeconds";

    /// <summary>The web host for Transport=http.</summary>
    /// <exception cref="ConfigurationException">A route the host reads its settings from sets <c>shutdownTimeoutSeconds</c>.</exception>
    public static WebApplicationBuilder ForHttp(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder([StartNoWatcher, .. args]);
        ReadOnce(builder.Configuration);
        RefuseShutdownTimeoutSetting(builder.Configuration);

        // contract-005 · T-10 — registered after the host's own reading of its settings, so this is the timeout it stops with.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);
        return builder;
    }

    /// <summary>
    /// contract-005 · T-10 — the framework's own shutdownTimeoutSeconds is a setting this server would ignore: whatever it
    /// says, the web host stops within <see cref="ShutdownTimeout"/>, so it finishes inside a container's stop grace. A
    /// setting the server would ignore is refused everywhere else in the frame, and this one is too, from every route the
    /// host reads it by — the command line, the environment as DOTNET_, ASPNETCORE_ or unprefixed, a settings file — naming
    /// the route, as SettingsReadOnce names a request to read the settings again. The stdio host keeps the framework's
    /// timeout, and the setting, as they are: it runs no container.
    /// </summary>
    private static void RefuseShutdownTimeoutSetting(ConfigurationManager configuration)
    {
        foreach (var provider in ((IConfigurationRoot)configuration).Providers)
        {
            if (provider.TryGet(ShutdownTimeoutSetting, out var value) && !string.IsNullOrEmpty(value))
            {
                throw new ConfigurationException(
                    $"{ShutdownTimeoutSetting} is '{value}' from {Frame.SettingsReadOnce.Route(provider, ShutdownTimeoutSetting)}: this "
                    + $"server stops within {ShutdownTimeout.TotalSeconds:0} seconds of being told to, so that it finishes inside a "
                    + "container's stop grace (Docker waits 10 seconds before it kills), and would ignore the setting. Remove it.");
            }
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 2 — ASP.NET Core's own listening settings, each read by the web host from its settings, and
    /// what each asks for.
    /// </summary>
    private static readonly (string Key, string Asks)[] ListeningSettings =
    [
        ("urls", "the addresses ASP.NET Core's web host would listen on"),
        ("http_ports", "the ports ASP.NET Core's web host would listen on for HTTP, on every interface"),
        ("https_ports", "the ports ASP.NET Core's web host would listen on for HTTPS, on every interface; this server does not "
            + "terminate TLS itself, the proxy in front of it does"),
    ];

    /// <summary>
    /// contract-005 · G-17 round 2 — refuses ASP.NET Core's own listening settings: <c>urls</c> (ASPNETCORE_URLS),
    /// <c>http_ports</c> (ASPNETCORE_HTTP_PORTS, which the aspnet base image sets to 8080) and <c>https_ports</c>. The program
    /// listens where HttpTransport:BindAddress and HttpTransport:Port say, and nowhere else (it sets the web host's addresses
    /// itself, UseUrls), so the web host overrides each of these: the ports with no more than a warning, "Overriding HTTP_PORTS
    /// '8080' …", and urls without a word — an operator who set ASPNETCORE_URLS=http://0.0.0.0:9999 believed the server
    /// listened there. The same class as the host list (HttpServerComposition, AllowedHosts): a setting the server would
    /// ignore is refused, from every route the web host reads it by — the command line, the environment as DOTNET_, ASPNETCORE_
    /// or unprefixed, a settings file — naming the route, as <see cref="RefuseShutdownTimeoutSetting"/> does. An empty value
    /// asks for nothing and is not refused: the image clears the base image's ASPNETCORE_HTTP_PORTS that way (Dockerfile).
    ///
    /// The program calls it before it sets its own addresses: the web host writes those into every settings source, after
    /// which no source says what the operator wrote. The stdio host reads none of these, and is not asked.
    /// </summary>
    /// <exception cref="ConfigurationException">A route the web host reads its settings from sets one of them.</exception>
    public static void RefuseListeningSettings(IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var provider in configuration.Providers)
        {
            foreach (var (key, asks) in ListeningSettings)
            {
                if (provider.TryGet(key, out var value) && !string.IsNullOrEmpty(value))
                {
                    // The key as the source holds it, so an environment variable is named as it was written: ASPNETCORE_URLS.
                    var written = provider.GetChildKeys([], parentPath: null)
                        .FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
                    throw new ConfigurationException(
                        $"{key} is '{LogSafe.Url(value)}' from {Frame.SettingsReadOnce.Route(provider, written)}: it is {asks}, and this "
                        + "server would ignore it: it listens where HttpTransport:BindAddress and HttpTransport:Port say, and nowhere "
                        + "else. Remove it, and set HttpTransport:BindAddress and HttpTransport:Port instead.");
                }
            }
        }
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
