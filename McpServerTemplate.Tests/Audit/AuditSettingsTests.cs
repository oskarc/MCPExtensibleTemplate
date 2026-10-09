using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Audit;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Tests.Frame;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-12 (and T-11's settings half) — the audit trail's settings are checked like every other: what the
/// server would ignore, or cannot honour, stops it at startup, naming the setting.
/// </summary>
public sealed class AuditSettingsTests : IDisposable
{
    private static readonly IReadOnlyList<IProviderModule> Modules = [new TemplatedResourceModule()];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mcp-audit-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Dictionary<string, string?> Settings(params (string Key, string? Value)[] audit)
    {
        var settings = FrameHarness.Settings(Modules);
        settings["Audit:Path"] = _directory;
        foreach (var (key, value) in audit)
        {
            settings[key] = value;
        }

        return settings;
    }

    private string Refusal(params (string Key, string? Value)[] audit) => FrameHarness.Refusal(Modules, Settings(audit));

    private static AuditOtlpSettings? Otlp(string environment, params (string Key, string? Value)[] settings) =>
        AuditOtlpSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value)).Build(),
            new HostingEnvironment { EnvironmentName = environment });

    [Fact]
    public void T12_with_nothing_set_the_trail_has_its_defaults()
    {
        var settings = FrameHarness.Settings(Modules);
        settings["Audit:Path"] = _directory;

        using var provider = FrameHarness.Compose(Modules, settings);
        var audit = provider.GetRequiredService<AuditSettings>();

        Assert.Equal(Path.GetFullPath(_directory), audit.Directory);
        Assert.False(audit.ReadWhenUnavailable);
        Assert.Equal(400, audit.RetentionDays);
        Assert.Equal("mcp-server", audit.AppId);
        Assert.Null(audit.Otlp);

        // And composing it wrote nothing: the first record is the host's own start, and no host was started.
        Assert.Empty(AuditFiles.In(_directory));
        Assert.Equal("logs/audit", AuditSettings.DefaultPath);
    }

    [Fact]
    public void T12_an_audit_setting_the_server_does_not_read_stops_startup_naming_the_nearest_it_does()
    {
        var refusal = Refusal(("Audit:ReadWhenUnavailabel", "allow"));

        Assert.Contains("'Audit:ReadWhenUnavailabel' is not a setting this server reads. Did you mean 'Audit:ReadWhenUnavailable'?", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("true")]
    public void T12_a_mode_that_is_neither_deny_nor_allow_stops_startup(string mode)
    {
        var refusal = Refusal(("Audit:ReadWhenUnavailable", mode));

        Assert.Contains($"Audit:ReadWhenUnavailable is '{mode}'. It is 'deny'", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Deny", false)]
    [InlineData(" allow ", true)]
    public void T12_the_mode_is_read_whatever_its_case(string mode, bool expected)
    {
        using var provider = FrameHarness.Compose(Modules, Settings(("Audit:ReadWhenUnavailable", mode)));

        Assert.Equal(expected, provider.GetRequiredService<AuditSettings>().ReadWhenUnavailable);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("a year")]
    public void T12_a_retention_that_is_not_a_number_of_days_stops_startup(string days)
    {
        Assert.Contains("Audit:RetentionDays", Refusal(("Audit:RetentionDays", days)), StringComparison.Ordinal);
    }

    [Fact]
    public void T12_a_directory_the_server_cannot_write_stops_startup_naming_where_it_resolved()
    {
        // A path through a file: nothing can be created under it, on any operating system.
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "a-file");
        File.WriteAllText(file, "x");
        var under = Path.Combine(file, "audit");

        var refusal = Refusal(("Audit:Path", under));

        Assert.Contains($"Audit:Path is '{under}'", refusal, StringComparison.Ordinal);
        Assert.Contains("this process cannot write the audit trail there", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void T12_a_path_that_climbs_out_of_its_directory_stops_startup()
    {
        Assert.Contains("contains path traversal characters (..)", Refusal(("Audit:Path", "logs/../../audit")), StringComparison.Ordinal);
    }

    [Fact]
    public void T12_an_application_id_that_is_not_a_plain_name_stops_startup()
    {
        Assert.Contains("Audit:AppId is", Refusal(("Audit:AppId", "mcp server\r\nforged")), StringComparison.Ordinal);
    }

    // ── The collector's settings (T-11) ──────────────────────────────────────

    [Fact]
    public void T11_a_collector_over_https_is_accepted_with_its_headers()
    {
        var otlp = Otlp(
            Environments.Production,
            ("Audit:Otlp:Endpoint", "https://collector.example.com:4318/v1/logs"),
            ("Audit:Otlp:Headers:Authorization", "Bearer abc"));

        Assert.Equal(new Uri("https://collector.example.com:4318/v1/logs"), otlp!.Endpoint);
        Assert.Equal("Bearer abc", otlp.Headers["Authorization"]);
    }

    [Fact]
    public void T11_the_collector_is_part_of_the_composition_when_it_is_set_and_absent_when_it_is_not()
    {
        using var with = FrameHarness.Compose(Modules, Settings(("Audit:Otlp:Endpoint", "http://localhost:4318/v1/logs")));
        using var without = FrameHarness.Compose(Modules, Settings());

        Assert.Equal(2, with.GetServices<IAuditSink>().Count());
        Assert.Contains("copy to http://localhost:4318", with.GetRequiredService<AuditTrail>().Description, StringComparison.Ordinal);
        Assert.IsType<FileAuditSink>(Assert.Single(without.GetServices<IAuditSink>()));
    }

    [Theory]
    [InlineData("Production", "http://collector.example.com:4318/v1/logs", "plain http")]
    [InlineData("Production", "http://localhost:4318/v1/logs", "plain http")]
    [InlineData("Development", "http://collector.example.com:4318/v1/logs", "plain http")]
    [InlineData("Production", "collector.example.com:4318", "not an absolute http(s) URL")]
    [InlineData("Production", "ftp://collector.example.com/logs", "not an absolute http(s) URL")]
    public void T11_a_collector_the_records_would_not_be_safe_on_the_way_to_stops_startup(string environment, string endpoint, string because)
    {
        var refusal = Assert.Throws<ConfigurationException>(() => Otlp(environment, ("Audit:Otlp:Endpoint", endpoint))).Message;

        Assert.Contains(because, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://ingest:s3cr3t-pass@collector.example.com/v1/logs")]
    [InlineData("https://collector.example.com/v1/logs?api_key=s3cr3t-pass")]
    [InlineData("https://collector.example.com/v1/logs#token=s3cr3t-pass")]
    public void T11_a_collector_address_carrying_a_credential_stops_startup_and_the_credential_is_not_printed(string endpoint)
    {
        var refusal = Assert.Throws<ConfigurationException>(() => Otlp(Environments.Production, ("Audit:Otlp:Endpoint", endpoint))).Message;

        Assert.Contains("carries user information, a query or a fragment", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-pass", refusal, StringComparison.Ordinal);
        Assert.Contains("***", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void T11_headers_with_no_collector_stop_startup()
    {
        var refusal = Assert.Throws<ConfigurationException>(() => Otlp(Environments.Production, ("Audit:Otlp:Headers:Authorization", "Bearer abc"))).Message;

        Assert.Contains("Audit:Otlp:Headers is set and Audit:Otlp:Endpoint is not", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void T11_a_collector_on_loopback_over_plain_http_is_accepted_in_development_alone()
    {
        Assert.NotNull(Otlp(Environments.Development, ("Audit:Otlp:Endpoint", "http://127.0.0.1:4318/v1/logs")));
        Assert.Null(Otlp(Environments.Production));
    }
}
