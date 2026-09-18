using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-9 (G-8) — a misconfigured identity setup exits 78 naming the setting.
///
/// Each case here is a way a deployment can be wrong while looking plausible in a config file.
/// The point is not that the server stops; it is that it stops before serving, and says which
/// line to change.
/// </summary>
public class IdentityConfigurationTests
{
    private const string Resource = "https://mcp.example.com/mcp";

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Dictionary<string, string?> Wellformed() => new()
    {
        ["Authentication:Resource"] = Resource,
        ["Authentication:IdentityProviders:corp:Authority"] = "https://login.example.com",
        ["Authentication:IdentityProviders:corp:Issuer"] = "https://login.example.com/",
        ["Authentication:IdentityProviders:corp:Algorithms:0"] = "RS256",
        ["Authentication:IdentityProviders:corp:ScopeClaim"] = "scope",
        ["Authentication:IdentityProviders:corp:ClientIdClaim"] = "client_id",
        ["Authentication:IdentityProviders:corp:ScopeCatalog:0"] = "weather:read",
    };

    [Fact]
    public void T9_a_wellformed_configuration_binds()
    {
        var config = IdentityConfigurationBinder.Bind(Config(Wellformed()));

        Assert.Equal(Resource, config.Resource);
        var corp = Assert.Single(config.IdentityProviders).Value;

        // The name comes from the configuration key, so the entry knows what it is called.
        Assert.Equal("corp", corp.Name);
        Assert.Equal(["weather:read"], corp.ScopeCatalog);
    }

    [Fact]
    public void T9_no_identity_provider_at_all_is_refused()
    {
        var ex = Assert.Throws<ConfigurationException>(
            () => IdentityConfigurationBinder.Bind(Config(new() { ["Authentication:Resource"] = Resource })));

        Assert.Contains("Authentication:IdentityProviders", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authentication:Resource", "", "Resource")]
    [InlineData("Authentication:Resource", "http://mcp.example.com/mcp", "Resource")]
    [InlineData("Authentication:IdentityProviders:corp:Authority", "http://login.example.com", "Authority")]
    [InlineData("Authentication:IdentityProviders:corp:Authority", "not-a-uri", "Authority")]
    [InlineData("Authentication:IdentityProviders:corp:Issuer", "", "Issuer")]
    [InlineData("Authentication:IdentityProviders:corp:ScopeClaim", "", "ScopeClaim")]
    public void T9_a_malformed_setting_is_refused_by_name(string key, string value, string named)
    {
        var settings = Wellformed();
        settings[key] = value;

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS256,HS256")]
    public void T9_an_algorithm_outside_the_allowlist_is_refused(string algorithms)
    {
        // The two that matter: "none" makes every token valid, and HMAC makes the verification
        // secret a signing key. Both are refused by absence from the allowlist, and the message
        // has to say so — an operator who wrote HS256 believed it was a security setting.
        var settings = Wellformed();
        settings.Remove("Authentication:IdentityProviders:corp:Algorithms:0");
        foreach (var (algorithm, index) in algorithms.Split(',').Select((a, i) => (a, i)))
        {
            settings[$"Authentication:IdentityProviders:corp:Algorithms:{index}"] = algorithm;
        }

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains("Algorithms", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T9_an_empty_algorithm_list_is_refused()
    {
        var settings = Wellformed();
        settings.Remove("Authentication:IdentityProviders:corp:Algorithms:0");

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains("Algorithms", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T9_an_empty_scope_catalog_is_refused()
    {
        var settings = Wellformed();
        settings.Remove("Authentication:IdentityProviders:corp:ScopeCatalog:0");

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains("ScopeCatalog", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T9_a_wildcard_scope_is_refused()
    {
        var settings = Wellformed();
        settings["Authentication:IdentityProviders:corp:ScopeCatalog:0"] = "weather:*";

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains("wildcard", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T9_an_admin_identity_provider_that_does_not_exist_is_refused()
    {
        var settings = Wellformed();
        settings["Authentication:AdminIdentityProvider"] = "nowhere";

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains("nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T9_an_admin_identity_provider_without_the_admin_scope_is_refused()
    {
        // Configured, named, and unable to do the one thing it was named for.
        var settings = Wellformed();
        settings["Authentication:AdminIdentityProvider"] = "corp";

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains("mcp:admin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T9_an_admin_identity_provider_holding_the_admin_scope_binds()
    {
        var settings = Wellformed();
        settings["Authentication:AdminIdentityProvider"] = "corp";
        settings["Authentication:IdentityProviders:corp:ScopeCatalog:1"] = "mcp:admin";

        var config = IdentityConfigurationBinder.Bind(Config(settings));

        Assert.Equal("corp", config.AdminIdentityProvider);
    }
}
