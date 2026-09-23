using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
using McpServerTemplate.Providers.Smhi;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-003 · T-1 (G-2) — a declaration the server cannot honour stops it from starting, and
/// says what is wrong. Each case throws the <see cref="ConfigurationException"/> the shipped
/// process turns into exit code 78; the Redis case below runs the real process to the exit code.
///
/// Also carried here: contract-002's T-6 binding guarantees, which were written against the
/// attribute-based binding this contract replaced. Each one is kept, against the policy registry.
/// </summary>
public class StartupRefusalTests
{
    [Fact]
    public void The_built_in_providers_start()
    {
        using var provider = FrameHarness.Compose(BuiltInProviders.Create());
        Assert.Equal(13, provider.GetRequiredService<PolicyRegistry>().Tools.Count);
    }

    [Fact]
    public void T1_a_served_tool_with_no_policy_refuses_to_start()
    {
        var policies = TestModule.ActPolicies();
        policies.Remove("test_flood");
        var message = FrameHarness.Refusal([new TestModule(tools: policies)]);
        Assert.Contains("'test_flood'", message, StringComparison.Ordinal);
        Assert.Contains("has no policy", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_a_policy_for_a_tool_nothing_declares_refuses_to_start()
    {
        var policies = TestModule.ActPolicies();
        policies["test_ghost"] = new("test:act", RiskClass.Read, 10);
        var message = FrameHarness.Refusal([new TestModule(tools: policies)]);
        Assert.Contains("'test_ghost'", message, StringComparison.Ordinal);
        Assert.Contains("none of its types declares", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_a_resource_with_no_policy_refuses_to_start()
    {
        var smhi = new UndeclaredResourceModule();
        var message = FrameHarness.Refusal([smhi]);
        Assert.Contains("smhi://coverage-area", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_a_scope_the_identity_provider_cannot_issue_refuses_to_start()
    {
        // contract-002 T-6 carried: "a tool whose scope its identity provider cannot issue".
        var message = FrameHarness.Refusal([new TestModule()], identity: FrameHarness.Identity("weather:read"));
        Assert.Contains("'test:act'", message, StringComparison.Ordinal);
        Assert.Contains("cannot issue", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_a_wildcard_scope_refuses_to_start()
    {
        // contract-002 T-6 carried: "a tool requiring no scope" — now any scope that is not one name.
        var policies = TestModule.ActPolicies();
        policies["test_echo"] = new("test:*", RiskClass.Read, 10);
        var message = FrameHarness.Refusal([new TestModule(tools: policies)]);
        Assert.Contains("no wildcard", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*.example.com")]
    [InlineData("10.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    public void T1_a_host_that_is_not_an_exact_domain_name_refuses_to_start(string host)
    {
        var egress = TestModule.DefaultEgress with { Hosts = [host] };
        var message = FrameHarness.Refusal([new TestModule(egress: egress)]);
        Assert.Contains($"'{host}'", message, StringComparison.Ordinal);
        Assert.Contains("exact domain names", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_contradictory_timeouts_refuse_to_start()
    {
        var egress = TestModule.DefaultEgress with { AttemptTimeout = TimeSpan.FromSeconds(3), MaxRetryAttempts = 1, TotalTimeout = TimeSpan.FromSeconds(6) };
        var message = FrameHarness.Refusal([new TestModule(egress: egress)]);
        Assert.Contains("cannot fit 2 attempts", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_hints_that_contradict_the_risk_class_refuse_to_start()
    {
        var module = new TestModule(
            toolTypes: [typeof(TestMislabelledTools)],
            tools: new Dictionary<string, ToolPolicy> { ["test_mislabelled"] = new("test:act", RiskClass.Read, 10) });
        var message = FrameHarness.Refusal([module]);
        Assert.Contains("contradict its Read risk class", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_a_configured_upstream_the_policy_does_not_allow_refuses_to_start()
    {
        var module = new TestModule();
        var settings = FrameHarness.Settings([module]);
        settings["Providers:TestAct:BaseUrl"] = "https://elsewhere.example.org";
        var message = FrameHarness.Refusal([module], settings);
        Assert.Contains("'elsewhere.example.org'", message, StringComparison.Ordinal);
        Assert.Contains("does not allow", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_an_unknown_enabled_provider_refuses_to_start()
    {
        var module = new TestModule();
        var settings = FrameHarness.Settings([module]);
        settings["Providers:Enabled:1"] = "Nonexistent";
        var message = FrameHarness.Refusal([module], settings);
        Assert.Contains("Nonexistent", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_no_enabled_list_outside_development_refuses_to_start()
    {
        var module = new TestModule();
        var settings = FrameHarness.Settings([module]);
        settings.Remove("Providers:Enabled:0");
        settings["Limits:Redis"] = "localhost:1";
        var message = FrameHarness.Refusal([module], settings, environment: "Production");
        Assert.Contains("Providers:Enabled is required", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_an_irreversible_tool_without_a_confirmation_key_refuses_to_start()
    {
        var message = FrameHarness.Refusal([new TestModule()]);
        Assert.Contains("Confirmation:Key is not set", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_a_short_confirmation_key_refuses_to_start()
    {
        var module = new TestModule();
        var settings = FrameHarness.Settings([module]);
        settings["Confirmation:Key"] = Convert.ToBase64String(new byte[16]);
        var message = FrameHarness.Refusal([module], settings);
        Assert.Contains("at least 32", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_limits_outside_development_need_redis()
    {
        var module = new TestModule();
        var message = FrameHarness.Refusal([module], FrameHarness.Settings([module]), environment: "Production");
        Assert.Contains("Limits:Redis is required", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T1_a_primitive_type_that_is_not_static_refuses_to_start()
    {
        var module = new TestModule(toolTypes: [typeof(NotStaticTools)], tools: new Dictionary<string, ToolPolicy>());
        var message = FrameHarness.Refusal([module]);
        Assert.Contains("is not a static class", message, StringComparison.Ordinal);
    }

    // ── contract-002 · T-6, carried against the registry ──

    [Fact]
    public void T6_a_provider_with_no_binding_refuses_to_start()
    {
        var module = new TestModule();
        var settings = FrameHarness.Settings([module]);
        settings.Remove("Providers:TestAct:IdentityProvider");
        var message = FrameHarness.Refusal([module], settings);
        Assert.Contains("Providers:TestAct:IdentityProvider is required", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T6_a_provider_bound_to_an_unconfigured_identity_provider_refuses_to_start()
    {
        var module = new TestModule();
        var message = FrameHarness.Refusal([module], FrameHarness.Settings([module], idp: "nowhere"));
        Assert.Contains("'nowhere', which is not configured", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T6_a_tool_belonging_to_no_provider_refuses_to_start()
    {
        // Registered around the modules, not through one: served, but declared by no provider.
        var message = Assert.Throws<ConfigurationException>(() => FrameHarness.Compose([new SideDoorModule()])).Message;
        Assert.Contains("SideDoor", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T6_two_providers_may_share_one_identity_provider()
    {
        using var provider = FrameHarness.Compose(
            [new TestModule(), new TestModule("Other", toolTypes: [typeof(TestUndeclaredTools)], tools: new Dictionary<string, ToolPolicy> { ["test_orphan"] = new("test:act", RiskClass.Read, 10) })],
            SettingsWithKey([new TestModule(), new TestModule("Other")]));

        var registry = provider.GetRequiredService<PolicyRegistry>();
        Assert.Equal("corp", registry.Tools["test_echo"].IdentityProvider);
        Assert.Equal("corp", registry.Tools["test_orphan"].IdentityProvider);
    }

    internal static Dictionary<string, string?> SettingsWithKey(IEnumerable<IProviderModule> modules)
    {
        var settings = FrameHarness.Settings(modules);
        settings["Confirmation:Key"] = Identity.InProcessServer.ConfirmationKey;
        return settings;
    }

    /// <summary>The Smhi provider with one of its two resources left out of its policy.</summary>
    private sealed class UndeclaredResourceModule : IProviderModule
    {
        private readonly SmhiModule _smhi = new();

        public string Name => _smhi.Name;

        public ProviderPolicy Policy => _smhi.Policy with
        {
            Resources = new Dictionary<string, ResourcePolicy> { ["smhi://weather-symbols"] = new("weather:read") },
        };

        public IReadOnlyList<Type> ToolTypes => _smhi.ToolTypes;

        public IReadOnlyList<Type> ResourceTypes => _smhi.ResourceTypes;

        public IReadOnlyList<Type> PromptTypes => _smhi.PromptTypes;

        public IReadOnlyCollection<string> Settings => _smhi.Settings;

        public void Register(IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration) =>
            _smhi.Register(services, configuration);
    }
}
