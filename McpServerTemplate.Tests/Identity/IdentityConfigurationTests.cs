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
    // contract-005 · G-12 (4) — an Issuer is published as an authorization server: https, like Authority.
    [InlineData("Authentication:IdentityProviders:corp:Issuer", "http://login.example.com/", "Issuer must be an absolute https URI")]
    [InlineData("Authentication:IdentityProviders:corp:Issuer", "login.example.com", "Issuer must be an absolute https URI")]
    [InlineData("Authentication:IdentityProviders:corp:ScopeClaim", "", "ScopeClaim")]
    // contract-005 · G-12 (4) — an issuer identifier has no query or fragment (RFC 8414 §2), and is
    // published as written; discovery and keys are fetched from paths appended to the Authority. User
    // information in either is a credential in a URL.
    [InlineData("Authentication:IdentityProviders:corp:Issuer", "https://login.example.com/?tenant=corp", "Issuer must not carry a query")]
    [InlineData("Authentication:IdentityProviders:corp:Issuer", "https://login.example.com/#corp", "Issuer must not carry a fragment")]
    [InlineData("Authentication:IdentityProviders:corp:Issuer", "https://user:secret@login.example.com/", "Issuer must not carry user information")]
    [InlineData("Authentication:IdentityProviders:corp:Authority", "https://login.example.com?tenant=corp", "Authority must not carry a query")]
    [InlineData("Authentication:IdentityProviders:corp:Authority", "https://login.example.com#corp", "Authority must not carry a fragment")]
    [InlineData("Authentication:IdentityProviders:corp:Authority", "https://user:secret@login.example.com", "Authority must not carry user information")]
    public void T9_a_malformed_setting_is_refused_by_name(string key, string value, string named)
    {
        var settings = Wellformed();
        settings[key] = value;

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (1) — MCP answers at /mcp, so a resource at any other path names a URL
    /// where nothing answers, and a standard client that checks the metadata against the URL it
    /// connected to cannot connect. The path is compared exactly: routing would answer /MCP too, but
    /// the resource is also every token's audience, which is compared exactly.
    /// </summary>
    [Theory]
    [InlineData("https://mcp.example.com/")]
    [InlineData("https://mcp.example.com")]
    [InlineData("https://mcp.example.com/api/mcp")]
    [InlineData("https://mcp.example.com/mcp/tools")]
    [InlineData("https://mcp.example.com/MCP")]
    public void T9_a_resource_whose_path_is_not_mcp_is_refused(string resource)
    {
        var settings = Wellformed();
        settings["Authentication:Resource"] = resource;

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.Contains("Authentication:Resource", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/mcp", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (1) — the Resource is held as written, because the string is what the
    /// metadata publishes and what every token's audience must equal. Parsed, each of these is the /mcp
    /// endpoint — dot-segments resolved, %6D decoded, \ turned into /, the query, fragment and user
    /// information set aside — and each bound, publishing a resource no client connects to.
    /// </summary>
    [Theory]
    [InlineData("https://mcp.example.com/./mcp")]
    [InlineData("https://mcp.example.com/x/../mcp")]
    [InlineData("https://mcp.example.com/%6Dcp")]
    [InlineData("https://mcp.example.com/mcp#frag")]
    [InlineData("https://mcp.example.com/mcp?x")]
    [InlineData("https://mcp.example.com/mcp//")]
    [InlineData("https://mcp.example.com/mcp\\")]
    [InlineData("https://user@mcp.example.com/mcp")]
    [InlineData("https://user:secret@mcp.example.com/mcp")]
    public void T9_a_resource_whose_path_is_mcp_only_once_parsed_is_refused(string resource)
    {
        var settings = Wellformed();
        settings["Authentication:Resource"] = resource;

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.StartsWith("Authentication:Resource", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{resource}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T9_a_resource_at_mcp_with_a_trailing_slash_binds()
    {
        var settings = Wellformed();
        settings["Authentication:Resource"] = "https://mcp.example.com/mcp/";

        Assert.Equal("https://mcp.example.com/mcp/", IdentityConfigurationBinder.Bind(Config(settings)).Resource);
    }

    /// <summary>
    /// contract-005 · G-12 (3) — ClientIdClaim is required of every token, so a claim every accepted
    /// token carries, or one that means something else, would require nothing. It must be one of the
    /// four claims identity providers name the client in, exactly: a validated token's claims are
    /// found by exact name, so SUB or AZP would find no claim at all. typ, ver, tid, acr, sid,
    /// auth_time, nonce and amr are on every token of one provider or another, and each passed the
    /// check that named only the registered claims and the scope claim.
    /// </summary>
    [Theory]
    [InlineData("iss")]
    [InlineData("sub")]
    [InlineData("aud")]
    [InlineData("exp")]
    [InlineData("nbf")]
    [InlineData("iat")]
    [InlineData("jti")]
    [InlineData("SUB")]
    [InlineData(" jti ")]
    [InlineData("scope")]
    [InlineData("Scope")]
    [InlineData("typ")]
    [InlineData("ver")]
    [InlineData("tid")]
    [InlineData("acr")]
    [InlineData("sid")]
    [InlineData("auth_time")]
    [InlineData("nonce")]
    [InlineData("amr")]
    [InlineData("AZP")]
    public void T9_a_client_claim_that_names_no_client_is_refused(string claim)
    {
        var settings = Wellformed();
        settings["Authentication:IdentityProviders:corp:ClientIdClaim"] = claim;

        var ex = Assert.Throws<ConfigurationException>(() => IdentityConfigurationBinder.Bind(Config(settings)));

        Assert.StartsWith("Authentication:IdentityProviders:corp:ClientIdClaim is", ex.Message, StringComparison.Ordinal);
        foreach (var name in new[] { "azp", "cid", "appid", "client_id" })
        {
            Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("azp")]
    [InlineData("cid")]
    [InlineData("appid")]
    [InlineData("client_id")]
    public void T9_a_client_claim_an_identity_provider_uses_binds(string claim)
    {
        var settings = Wellformed();
        settings["Authentication:IdentityProviders:corp:ClientIdClaim"] = claim;

        Assert.Equal(claim, IdentityConfigurationBinder.Bind(Config(settings)).IdentityProviders["corp"].ClientIdClaim);
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
