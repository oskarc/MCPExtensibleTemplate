using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// Refuses to start when a settings source could read settings again while the server runs.
///
/// contract-003 · G-11, contract-005 review rounds 2 and 3 — every check on settings reads them once, at
/// startup, and a source that reads them again afterwards puts settings in force that no check has read:
/// on the shipped image a watched settings file brought in a bearer scheme's MetadataAddress, and a
/// Kestrel endpoint, each of which startup refuses. The hosts are built to read once (HostBuilders); this
/// holds whatever built the configuration, so the next way a source comes to be read again is a refusal
/// that names it, not a server that quietly follows it. There is no general way to ask a source whether
/// it can read again, so the check goes the other way: every source must be of a kind that cannot —
/// settings in memory, from the environment or from the command line, a file that is not watched, or a
/// chained configuration made only of those. And no source may ask for reading again: the server would
/// ignore the request, and a setting it would ignore is one it refuses.
/// </summary>
public static class SettingsReadOnce
{
    /// <summary>The host's own setting for reading its settings files again whenever they change.</summary>
    private const string ReloadSwitch = "hostBuilder:reloadConfigOnChange";

    /// <summary>Throws when any source of <paramref name="configuration"/> could read settings again, or asks to.</summary>
    /// <exception cref="ConfigurationException">A source reads again, is of a kind that might, or asks for it.</exception>
    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration is not IConfigurationRoot root)
        {
            throw new ConfigurationException(
                "The server's settings were handed to it without their sources, so it cannot tell that each is read once. "
                + "Settings are read once, at startup, where they are checked.");
        }

        // contract-005 review round 3 — a request to read them again (hostBuilder:reloadConfigOnChange=true) is one the
        // server ignores: it reads its settings once whatever the request says (HostBuilders). A setting the server
        // would ignore is refused everywhere else in the frame, and this one is too. Each source is asked on its own,
        // not the value that won: the server's own "false" is first on its command line, and would hide a request made
        // in the environment, while a request on the command line comes after it and wins — either way, ignored.
        foreach (var provider in root.Providers)
        {
            if (provider.TryGet(ReloadSwitch, out var value) && bool.TryParse(value, out var reload) && reload)
            {
                throw new ConfigurationException(
                    $"{ReloadSwitch} is '{value}' from {Route(provider)}: it asks for the settings files to be read again "
                    + "whenever they change, and this server reads its settings once, at startup, where they are checked, so it "
                    + "would ignore the request. A changed setting takes effect when the server restarts; remove the request.");
            }
        }

        foreach (var provider in root.Providers)
        {
            if (ReadsAgain(provider) is { } why)
            {
                throw new ConfigurationException(
                    $"The settings source {provider} {why}. Settings are read once, at startup, where they are checked; a "
                    + "source that reads them again while the server runs would put settings in force that no check has "
                    + "read. A changed setting takes effect when the server restarts.");
            }
        }
    }

    /// <summary>Where a setting <paramref name="provider"/> holds came from, as an operator would look for it.</summary>
    private static string Route(IConfigurationProvider provider) => provider switch
    {
        CommandLineConfigurationProvider => "the command line",
        EnvironmentVariablesConfigurationProvider => $"the environment, as {EnvironmentPrefix(provider)}{ReloadSwitch.Replace(":", "__", StringComparison.Ordinal)}",
        FileConfigurationProvider file => $"the settings file {file.Source.Path}",
        _ => provider.ToString() ?? provider.GetType().Name,
    };

    /// <summary>
    /// The prefix an environment source strips (DOTNET_, ASPNETCORE_), which it shows only in its own
    /// description: "EnvironmentVariablesConfigurationProvider Prefix: 'DOTNET_'". None when it has none.
    /// </summary>
    private static string EnvironmentPrefix(IConfigurationProvider provider)
    {
        var description = provider.ToString() ?? string.Empty;
        var start = description.IndexOf("Prefix: '", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += "Prefix: '".Length;
        var end = description.IndexOf('\'', start);
        return end < 0 ? string.Empty : description[start..end];
    }

    /// <summary>How <paramref name="provider"/> could read settings again, or null when it cannot.</summary>
    private static string? ReadsAgain(IConfigurationProvider provider) => provider switch
    {
        FileConfigurationProvider file => file.Source.ReloadOnChange ? "reads its file again whenever the file changes" : null,
        MemoryConfigurationProvider or EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider => null,
        ChainedConfigurationProvider { Configuration: IConfigurationRoot chained } =>
            chained.Providers.Select(inner => ReadsAgain(inner) is { } why ? $"chains {inner}, which {why}" : null)
                .FirstOrDefault(why => why is not null),
        ChainedConfigurationProvider => "chains settings whose sources cannot be seen",
        _ => "is of a kind this server cannot tell reads its settings only once",
    };
}
