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
/// </summary>
public static class HostBuilders
{
    /// <summary>
    /// The host setting every default settings file takes its reload flag from
    /// (HostingHostBuilderExtensions.ApplyDefaultAppConfiguration), switched off. Last on the command line,
    /// which the host reads after its environment variables, so no argument or DOTNET_ variable before it
    /// can switch reloading back on.
    /// </summary>
    private const string ReadOnce = "--hostBuilder:reloadConfigOnChange=false";

    /// <summary>The web host for Transport=http.</summary>
    public static WebApplicationBuilder ForHttp(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return WebApplication.CreateBuilder([.. args, ReadOnce]);
    }

    /// <summary>The plain generic host for Transport=stdio: no Kestrel, no port, no middleware.</summary>
    public static HostApplicationBuilder ForStdio(string[] args, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [.. args, ReadOnce],
            EnvironmentName = environmentName,
        });
    }
}
