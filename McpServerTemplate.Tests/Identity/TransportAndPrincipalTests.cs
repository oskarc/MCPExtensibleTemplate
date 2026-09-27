using System.Security.Claims;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-7 (G-6) and T-12 (G-12) — the development principal, and the refusal to
/// serve bearer tokens over an unprotected transport.
/// </summary>
public class TransportAndPrincipalTests
{
    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static AuthenticationConfig Identity(params string[] catalog)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Resource"] = "https://mcp.example.com/mcp",
            ["Authentication:IdentityProviders:corp:Authority"] = "https://login.example.com",
            ["Authentication:IdentityProviders:corp:Issuer"] = "https://login.example.com/",
            ["Authentication:IdentityProviders:corp:Algorithms:0"] = "RS256",
        };

        for (var i = 0; i < catalog.Length; i++)
        {
            settings[$"Authentication:IdentityProviders:corp:ScopeCatalog:{i}"] = catalog[i];
        }

        return IdentityConfigurationBinder.Bind(Config(settings));
    }

    // ── G-6: the development principal ────────────────────────────────────────

    [Fact]
    public void T7_stdio_runs_as_a_named_principal_without_any_identity_configured()
    {
        // Running the server for a local IDE must not require a production-shaped config file.
        var principal = DevelopmentPrincipal.Create(Config([]), identity: null);

        Assert.Equal("dev-user", principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal("dev-ide", principal.FindFirst("client_id")?.Value);

        // Everything downstream expects a token to be identifiable, including this one.
        Assert.False(string.IsNullOrWhiteSpace(principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value));
    }

    [Fact]
    public void T7_the_development_principal_is_what_configuration_says_it_is()
    {
        var principal = DevelopmentPrincipal.Create(
            Config(new()
            {
                ["Development:DevPrincipal:IdentityProvider"] = "corp",
                ["Development:DevPrincipal:Subject"] = "alice",
                ["Development:DevPrincipal:ClientId"] = "alices-editor",
                ["Development:DevPrincipal:Scopes:0"] = "weather:read",
            }),
            Identity("weather:read", "demo:read"));

        Assert.Equal("alice", principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal("alices-editor", principal.FindFirst("client_id")?.Value);
        Assert.Equal("corp", principal.FindFirst("idp")?.Value);
        Assert.Equal("weather:read", principal.FindFirst("scope")?.Value);
    }

    [Fact]
    public void T7_a_development_scope_the_identity_provider_cannot_issue_is_refused()
    {
        // The failure this prevents is the quiet one: a developer grants themselves a scope
        // locally, everything works, and the same call is refused once deployed.
        var ex = Assert.Throws<ConfigurationException>(() => DevelopmentPrincipal.Create(
            Config(new()
            {
                ["Development:DevPrincipal:IdentityProvider"] = "corp",
                ["Development:DevPrincipal:Scopes:0"] = "mcp:admin",
            }),
            Identity("weather:read")));

        Assert.Contains("mcp:admin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("weather:read", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T7_a_development_principal_naming_an_unconfigured_provider_is_refused()
    {
        var ex = Assert.Throws<ConfigurationException>(() => DevelopmentPrincipal.Create(
            Config(new() { ["Development:DevPrincipal:IdentityProvider"] = "nowhere" }),
            Identity("weather:read")));

        Assert.Contains("nowhere", ex.Message, StringComparison.Ordinal);
    }

    // ── G-12: no bearer tokens over an unprotected transport ──────────────────

    [Fact]
    public void T12_production_without_a_trusted_proxy_is_refused()
    {
        var ex = Assert.Throws<ConfigurationException>(
            () => TransportSecurityGuard.Validate(Config([]), isProduction: true));

        Assert.Contains("TLS", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KnownProxies", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HttpTransport:KnownProxies:0", "10.0.0.1")]
    [InlineData("HttpTransport:KnownNetworks:0", "10.0.0.0/8")]
    public void T12_naming_the_proxy_is_what_satisfies_the_guard(string key, string value)
    {
        TransportSecurityGuard.Validate(Config(new() { [key] = value }), isProduction: true);
    }

    [Theory]
    [InlineData("HttpTransport:Certificate:Path", "/etc/certs/server.pfx")]
    [InlineData("HttpTransport:Certificate:Subject", "CN=mcp.example.com")]
    public void T12_a_certificate_setting_no_longer_satisfies_the_guard(string key, string value)
    {
        // This is the defect the narrowing removes. Until 2026-09-20 either of these satisfied the
        // check while Program.cs bound http:// unconditionally, so a Production deployment could
        // start, believe it was terminating TLS, and serve bearer tokens in cleartext. The Kestrel
        // listener moved to the phase that handles it; the branch comes back when the listener does.
        var ex = Assert.Throws<ConfigurationException>(
            () => TransportSecurityGuard.Validate(Config(new() { [key] = value }), isProduction: true));

        Assert.Contains("does not terminate TLS itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T12_development_is_not_asked_to_configure_tls()
    {
        // Refusing here would teach developers to set a flag that turns the check off, which is
        // how a Production deployment ends up with the flag set.
        TransportSecurityGuard.Validate(Config([]), isProduction: false);
    }

    // ── contract-005 · G-12 (2): host filtering cannot be switched off ─────────

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("*")]
    [InlineData("10.1.2.3")]
    [InlineData("mcp.internal")]
    public void G12_2_a_non_loopback_bind_with_no_allowed_host_is_refused(string bindAddress)
    {
        // The bind address used to become the allowlist, and 0.0.0.0 there means any host.
        var ex = Assert.Throws<ConfigurationException>(() =>
            HttpServerComposition.AllowedHosts(Config(new() { ["HttpTransport:BindAddress"] = bindAddress })));

        Assert.StartsWith("HttpTransport:AllowedHosts must name", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{bindAddress}'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("::")]
    [InlineData(" * ")]
    // The filter honours *.example.com as every name under example.com: *.com and *. admit nearly any
    // host, and a subdomain wildcard admits names nobody listed.
    [InlineData("*.com")]
    [InlineData("*.")]
    [InlineData("*.example.com")]
    // A full-width asterisk: where the runtime has ICU, the filter's punycode conversion folds it into *.
    [InlineData("\uFF0A")]
    public void G12_2_a_wildcard_allowed_host_is_refused_naming_its_key(string entry)
    {
        // At any index, and on a loopback bind too: the entry widens the filter wherever it is.
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = "localhost",
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
            ["HttpTransport:AllowedHosts:1"] = entry,
        })));

        Assert.StartsWith("HttpTransport:AllowedHosts:1 is", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — the filter does not match an entry as written: it converts it to
    /// punycode first (IdnMapping, which on a runtime with ICU applies Unicode's IDNA mapping), and
    /// compares names exactly. Each of these is refused rather than left to mean what the conversion
    /// makes of it: a soft hyphen or a zero-width space vanishes beside *, full-width digits, an
    /// ideographic full stop and full-width colons fold into 0.0.0.0 and [::] — each switched filtering
    /// off on such a runtime — and a trailing dot is another name than the one without it. An
    /// internationalised name is written in its punycode form.
    /// </summary>
    [Theory]
    [InlineData("*\u00AD")]
    [InlineData("*\u200B")]
    [InlineData("\uFF10.\uFF10.\uFF10.\uFF10")]
    [InlineData("0\u30020\u30020\u30020")]
    [InlineData("[\uFF1A\uFF1A]")]
    [InlineData("b\u00FCcher.example")]
    [InlineData("mcp.example.com.")]
    public void G12_2_an_allowed_host_the_filter_would_not_match_as_written_is_refused_naming_its_key(string entry)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = "0.0.0.0",
            ["HttpTransport:AllowedHosts:0"] = entry,
        })));

        Assert.StartsWith("HttpTransport:AllowedHosts:0 is", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — belt and braces: the host filter's own matcher is asked, over the final
    /// list, whether it admits a random name under .invalid, bare and with a trailing dot. The entry
    /// rules refuse every spelling that would, so this is reached only through the list itself.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData("*.invalid")]
    [InlineData("*.")]
    public void G12_2_a_list_the_filter_would_widen_is_refused_by_the_filters_own_matcher(string pattern)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.RefuseHostsNobodyNamed(["mcp.example.com", pattern]));

        Assert.StartsWith("HttpTransport:AllowedHosts (mcp.example.com, ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void G12_2_the_filters_own_matcher_passes_names_and_the_loopback_default()
    {
        HttpServerComposition.RefuseHostsNobodyNamed(["mcp.example.com", "10.1.2.3", "[::1]"]);
        HttpServerComposition.RefuseHostsNobodyNamed(HttpServerComposition.AllowedHosts(Config([])));
    }

    [Theory]
    [InlineData("xn--bcher-kva.example")]
    [InlineData("MCP.Example.com")]
    [InlineData("10.1.2.3")]
    [InlineData("[::1]")]
    public void G12_2_a_host_named_as_the_filter_matches_it_binds(string entry)
    {
        // The controls for the rows above: a punycode name, capitals (the filter ignores case) and
        // address literals each name one host, and pass.
        var hosts = HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = "0.0.0.0",
            ["HttpTransport:AllowedHosts:0"] = entry,
        }));

        Assert.Equal([entry], hosts);
    }

    [Fact]
    public void G12_2_a_named_host_on_a_wildcard_bind_is_what_the_filter_allows()
    {
        var hosts = HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = "0.0.0.0",
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
        }));

        Assert.Equal(["mcp.example.com"], hosts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void G12_2_a_loopback_bind_still_defaults_to_the_loopback_names(string? bindAddress)
    {
        var settings = new Dictionary<string, string?>();
        if (bindAddress is not null)
        {
            settings["HttpTransport:BindAddress"] = bindAddress;
        }

        Assert.Equal(["localhost", "127.0.0.1", "[::1]"], HttpServerComposition.AllowedHosts(Config(settings)));
    }
}
