using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-6 (G-5) — a provider answers to exactly one identity provider.
///
/// The consequence under test is not subtle: without the binding, a token from any identity
/// provider the server trusts reaches every tool it exposes. A contractor's identity provider,
/// federated for one team, would open the others.
/// </summary>
public class ProviderBindingTests
{
    [McpServerToolType]
    [McpProvider("Weather")]
    [McpScope("weather:read")]
    private static class WeatherTools
    {
        [McpServerTool]
        public static string GetForecast() => "sunny";
    }

    [McpServerToolType]
    [McpProvider("Billing")]
    [McpScope("weather:read")]
    private static class BillingTools
    {
        [McpServerTool]
        public static string GetInvoice() => "paid";
    }

    [McpServerToolType]
    [McpScope("weather:read")]
    private static class UnownedTools
    {
        [McpServerTool]
        public static string DoSomething() => "done";
    }

    private static AuthenticationConfig Identity(params string[] names)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Resource"] = "https://mcp.example.com/mcp",
        };

        foreach (var name in names)
        {
            settings[$"Authentication:IdentityProviders:{name}:Authority"] = $"https://{name}.example.com";
            settings[$"Authentication:IdentityProviders:{name}:Issuer"] = $"https://{name}.example.com/";
            settings[$"Authentication:IdentityProviders:{name}:Algorithms:0"] = "RS256";
            settings[$"Authentication:IdentityProviders:{name}:ScopeCatalog:0"] = "weather:read";
        }

        return IdentityConfigurationBinder.Bind(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static IConfiguration Bindings(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IEnumerable<McpServerTool> Tools(params Type[] types) =>
        types.SelectMany(t => t.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false).Length > 0)
            .Select(m => McpServerTool.Create(m, target: null)));

    [Fact]
    public void T6_a_caller_reaches_only_the_providers_bound_to_its_identity_provider()
    {
        var binding = ProviderBinding.Create(
            Bindings(new()
            {
                ["Providers:Weather:IdentityProvider"] = "corp",
                ["Providers:Billing:IdentityProvider"] = "finance",
            }),
            Identity("corp", "finance"),
            Tools(typeof(WeatherTools), typeof(BillingTools)));

        Assert.True(binding.Allows("get_forecast", "corp"));
        Assert.False(binding.Allows("get_invoice", "corp"));

        Assert.True(binding.Allows("get_invoice", "finance"));
        Assert.False(binding.Allows("get_forecast", "finance"));
    }

    [Fact]
    public void T6_two_providers_may_share_one_identity_provider()
    {
        var binding = ProviderBinding.Create(
            Bindings(new()
            {
                ["Providers:Weather:IdentityProvider"] = "corp",
                ["Providers:Billing:IdentityProvider"] = "corp",
            }),
            Identity("corp"),
            Tools(typeof(WeatherTools), typeof(BillingTools)));

        Assert.True(binding.Allows("get_forecast", "corp"));
        Assert.True(binding.Allows("get_invoice", "corp"));
    }

    [Fact]
    public void T6_a_caller_with_no_identity_provider_reaches_nothing()
    {
        var binding = ProviderBinding.Create(
            Bindings(new() { ["Providers:Weather:IdentityProvider"] = "corp" }),
            Identity("corp"),
            Tools(typeof(WeatherTools)));

        Assert.False(binding.Allows("get_forecast", null));
        Assert.False(binding.Allows("get_forecast", ""));
    }

    [Fact]
    public void T6_an_unknown_tool_is_refused_rather_than_assumed_open()
    {
        var binding = ProviderBinding.Create(
            Bindings(new() { ["Providers:Weather:IdentityProvider"] = "corp" }),
            Identity("corp"),
            Tools(typeof(WeatherTools)));

        Assert.False(binding.Allows("some_tool_that_does_not_exist", "corp"));
    }

    [Fact]
    public void T6_a_provider_with_no_binding_refuses_to_start()
    {
        var ex = Assert.Throws<ConfigurationException>(() => ProviderBinding.Create(
            Bindings([]), Identity("corp"), Tools(typeof(WeatherTools))));

        Assert.Contains("Providers:Weather:IdentityProvider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T6_a_provider_bound_to_an_unconfigured_identity_provider_refuses_to_start()
    {
        var ex = Assert.Throws<ConfigurationException>(() => ProviderBinding.Create(
            Bindings(new() { ["Providers:Weather:IdentityProvider"] = "nowhere" }),
            Identity("corp"),
            Tools(typeof(WeatherTools))));

        Assert.Contains("nowhere", ex.Message, StringComparison.Ordinal);
    }

    [McpServerToolType]
    [McpProvider("Weather")]
    private static class UnscopedTools
    {
        [McpServerTool]
        public static string GetSomethingUnscoped() => "open to all";
    }

    [Fact]
    public void T6_a_tool_requiring_no_scope_refuses_to_start()
    {
        // The same reasoning as an unowned tool, one level along: a tool that requires no scope is
        // reachable by every token this server accepts, which is a permission nobody granted.
        var ex = Assert.Throws<ConfigurationException>(() => ProviderBinding.Create(
            Bindings(new() { ["Providers:Weather:IdentityProvider"] = "corp" }),
            Identity("corp"),
            Tools(typeof(UnscopedTools))));

        Assert.Contains("get_something_unscoped", ex.Message, StringComparison.Ordinal);
        Assert.Contains("McpScope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T6_a_tool_whose_scope_its_identity_provider_cannot_issue_refuses_to_start()
    {
        // Configured, bound, and unreachable by anyone — the deployment would not know.
        var ex = Assert.Throws<ConfigurationException>(() => ProviderBinding.Create(
            Bindings(new() { ["Providers:Weather:IdentityProvider"] = "corp" }),
            Identity("corp"),
            Tools(typeof(BillingToolsWantingAnUnissuableScope))));

        Assert.Contains("cannot issue", ex.Message, StringComparison.Ordinal);
    }

    [McpServerToolType]
    [McpProvider("Weather")]
    [McpScope("nobody:issues-this")]
    private static class BillingToolsWantingAnUnissuableScope
    {
        [McpServerTool]
        public static string GetUnreachable() => "never";
    }

    [Fact]
    public void T6_a_tool_belonging_to_no_provider_refuses_to_start()
    {
        // The dangerous case: a tool nobody owns cannot be bound, so it would be reachable from
        // every identity provider the server trusts. Refusing to start is the only honest answer.
        var ex = Assert.Throws<ConfigurationException>(() => ProviderBinding.Create(
            Bindings(new() { ["Providers:Weather:IdentityProvider"] = "corp" }),
            Identity("corp"),
            Tools(typeof(WeatherTools), typeof(UnownedTools))));

        Assert.Contains("do_something", ex.Message, StringComparison.Ordinal);
        Assert.Contains("McpProvider", ex.Message, StringComparison.Ordinal);
    }
}
