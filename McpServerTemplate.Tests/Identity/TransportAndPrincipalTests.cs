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
    public void T12_production_without_tls_or_a_trusted_proxy_is_refused()
    {
        var ex = Assert.Throws<ConfigurationException>(
            () => TransportSecurityGuard.Validate(Config([]), isProduction: true));

        Assert.Contains("TLS", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HttpTransport:Certificate:Path", ex.Message, StringComparison.Ordinal);
        Assert.Contains("KnownProxies", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HttpTransport:Certificate:Path", "/etc/certs/server.pfx")]
    [InlineData("HttpTransport:Certificate:Subject", "CN=mcp.example.com")]
    [InlineData("HttpTransport:KnownProxies:0", "10.0.0.1")]
    [InlineData("HttpTransport:KnownNetworks:0", "10.0.0.0/8")]
    public void T12_either_terminating_tls_or_naming_the_proxy_is_enough(string key, string value)
    {
        TransportSecurityGuard.Validate(Config(new() { [key] = value }), isProduction: true);
    }

    [Fact]
    public void T12_development_is_not_asked_to_configure_tls()
    {
        // Refusing here would teach developers to set a flag that turns the check off, which is
        // how a Production deployment ends up with the flag set.
        TransportSecurityGuard.Validate(Config([]), isProduction: false);
    }
}
