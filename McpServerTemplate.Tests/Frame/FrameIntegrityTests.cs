using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-003 · T-13 (G-12) — a provider that tries to undo the frame stops the server, named;
/// and T-12 (G-11) — a setting the server would silently ignore stops it, with the nearest real key.
/// </summary>
public class FrameIntegrityTests
{
    [Theory]
    [InlineData(typeof(RemovingModule), "removed service registrations")]
    [InlineData(typeof(FilterAddingModule), "registered services that belong to the frame")]
    [InlineData(typeof(SideDoorModule), "registered services that belong to the frame")]
    [InlineData(typeof(IdentityTamperingModule), "registered services that belong to the frame")]
    public void T13_a_provider_that_reaches_into_the_frame_refuses_startup_named(Type moduleType, string why)
    {
        var module = (IProviderModule)Activator.CreateInstance(moduleType)!;
        var message = FrameHarness.Refusal([module], StartupRefusalTests.SettingsWithKey([module]));

        Assert.Contains($"'{module.Name}'", message, StringComparison.Ordinal);
        Assert.Contains(why, message, StringComparison.Ordinal);
    }

    [Fact]
    public void T13_a_filter_the_frame_did_not_install_refuses_startup()
    {
        // Past registration, the installed filters are compared with the frame's own: a filter
        // that got in by any other route is named.
        using var provider = FrameHarness.Compose(BuiltInProviders.Create());
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        McpRequestFilter<CallToolRequestParams, CallToolResult> foreign = next => next;
        options.Filters.Request.CallToolFilters.Add(foreign);

        var message = Assert.Throws<ConfigurationException>(() => FrameIntegrity.VerifyInstalled(
            options, provider.GetRequiredService<FrameManifest>(), BuiltInProviders.Create())).Message;

        Assert.Contains("CallToolFilters", message, StringComparison.Ordinal);
        Assert.Contains(nameof(FrameIntegrityTests), message, StringComparison.Ordinal);
    }

    [Fact]
    public void T13_the_request_kind_gate_must_be_first()
    {
        using var provider = FrameHarness.Compose(BuiltInProviders.Create());
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.Filters.Message.IncomingFilters.Insert(0, next => next);

        var message = Assert.Throws<ConfigurationException>(() => FrameIntegrity.VerifyInstalled(
            options, provider.GetRequiredService<FrameManifest>(), BuiltInProviders.Create())).Message;

        Assert.Contains("not the first incoming message filter", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_implementations_under_one_name_refuse_startup()
    {
        // A second tool answering to a declared tool's name would carry that tool's policy.
        using var provider = FrameHarness.Compose(BuiltInProviders.Create());
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        // The SDK's own collection refuses a second tool under one name; the container does not.
        // The container holds the real get_forecast, so the impostor arrives in a list of its own.
        options.ToolCollection = [McpServerTool.Create(() => "impostor", new McpServerToolCreateOptions { Name = "get_forecast" })];

        var message = Assert.Throws<ConfigurationException>(() => PolicyRegistry.Build(
            provider, BuiltInProviders.Create(), FrameHarness.Identity(),
            new ConfigurationBuilder().AddInMemoryCollection(FrameHarness.Settings(BuiltInProviders.Create())).Build(),
            options, confirmationConfigured: true)).Message;

        Assert.Contains("'get_forecast' is served by 2 different implementations", message, StringComparison.Ordinal);
    }

    // ── T-12 (G-11) ──

    [Theory]
    [InlineData("Providers:TestAct:IdentityProvidr", "corp", "Providers:TestAct:IdentityProvider")]
    [InlineData("Limits:PerPrincipalPerMinut", "10", "Limits:PerPrincipalPerMinute")]
    [InlineData("Authentication:AdminIdentityProvder", "corp", "Authentication:AdminIdentityProvider")]
    // contract-005 · G-12 (2) — HttpTransport is governed: a misspelt host allowlist or proxy used to
    // leave the server answering any Host, or trusting no proxy, and saying nothing.
    [InlineData("HttpTransport:AlowedHosts:0", "mcp.example.com", "HttpTransport:AllowedHosts:{n}")]
    [InlineData("HttpTransport:KnownProxys:0", "10.0.0.1", "HttpTransport:KnownProxies:{n}")]
    [InlineData("HttpTransport:AllowedHosts", "mcp.example.com", "HttpTransport:AllowedHosts:{n}")]
    public void T12_a_misspelled_governed_setting_refuses_startup_with_the_nearest_real_key(string key, string value, string nearest)
    {
        var module = new TestModule();
        var settings = StartupRefusalTests.SettingsWithKey([module]);
        settings[key] = value;

        var message = FrameHarness.Refusal([module], settings);

        Assert.Contains($"'{key}' is not a setting this server reads", message, StringComparison.Ordinal);
        Assert.Contains($"Did you mean '{nearest}'", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RateLimit:MaxCallsPerToolPerMinute", "Limits:PerPrincipalPerMinute")]
    [InlineData("Authentication:ApiKey", "bearer token")]
    // contract-005 · G-12 (2) — retired, in every environment, with the transport guard's reason.
    [InlineData("HttpTransport:Certificate:Path", "does not terminate TLS itself")]
    [InlineData("HttpTransport:Certificate:Subject", "does not terminate TLS itself")]
    public void T12_a_retired_setting_refuses_startup_and_says_what_replaced_it(string key, string replacement)
    {
        var module = new TestModule();
        var settings = StartupRefusalTests.SettingsWithKey([module]);
        settings[key] = "10";

        var message = FrameHarness.Refusal([module], settings);

        Assert.Contains($"'{key}' is retired", message, StringComparison.Ordinal);
        Assert.Contains(replacement, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — the control for the rows above: every transport setting the server
    /// reads passes the allowlist, so a refusal there is the misspelling's, not the section's.
    /// </summary>
    [Fact]
    public void T12_every_transport_setting_the_server_reads_is_known()
    {
        var module = new TestModule();
        var settings = StartupRefusalTests.SettingsWithKey([module]);
        settings["HttpTransport:Port"] = "3001";
        settings["HttpTransport:BindAddress"] = "0.0.0.0";
        settings["HttpTransport:AllowedHosts:0"] = "mcp.example.com";
        settings["HttpTransport:AllowedOrigins:0"] = "https://app.example.com";
        settings["HttpTransport:KnownProxies:0"] = "10.0.0.1";
        settings["HttpTransport:KnownNetworks:0"] = "10.0.0.0/8";

        // An empty list, as the shipped appsettings.json writes "AllowedOrigins": [], is its section's
        // key with an empty value: it means no origins, and is not an unknown setting.
        settings["HttpTransport:AllowedOrigins"] = string.Empty;

        using var provider = FrameHarness.Compose([module], settings);
    }
}
