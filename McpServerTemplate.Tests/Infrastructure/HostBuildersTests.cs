using McpServerTemplate.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-003 · G-11, contract-005 review round 2 — both hosts read their settings once, at startup,
/// where every check reads them: no settings file either host reads is watched for changes.
///
/// The default hosts watch appsettings.json, appsettings.{Environment}.json and, in Development, the
/// user secrets, and read a changed file back into the running server, where no startup check sees it.
/// ServerProcessTests shows what that did to the shipped process; this holds the cause, for both hosts,
/// in both environments.
/// </summary>
public class HostBuildersTests
{
    /// <summary>
    /// The server's own application name, as its process has it: the user secrets a Development host reads
    /// are the ones its assembly names (UserSecretsId), and a test process is another application.
    /// </summary>
    private static readonly string[] AsTheServer = ["--applicationName=McpServerTemplate"];

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void The_http_host_watches_no_settings_file(string environment)
    {
        var builder = HostBuilders.ForHttp([.. AsTheServer, $"--environment={environment}"]);
        using var configuration = builder.Configuration;

        AssertReadOnce(configuration, environment);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void The_stdio_host_watches_no_settings_file(string environment)
    {
        var builder = HostBuilders.ForStdio(AsTheServer, environment);
        using var configuration = builder.Configuration;

        AssertReadOnce(configuration, environment);
    }

    private static void AssertReadOnce(ConfigurationManager configuration, string environment)
    {
        var files = configuration.Sources.OfType<FileConfigurationSource>().ToArray();

        // Positive control: the files a host reads are the ones seen here.
        Assert.Contains(files, f => f.Path == "appsettings.json");
        Assert.Contains(files, f => f.Path == $"appsettings.{environment}.json");
        if (environment == "Development")
        {
            Assert.Contains(files, f => f.Path == "secrets.json");
        }

        Assert.All(files, f => Assert.False(f.ReloadOnChange, $"{f.Path} is read again whenever it changes, and no check reads it then."));
    }
}
