using System.Reflection;
using System.Security.Claims;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
    // Review round 7 — each row with what its refusal must say: an accepted form that is not loopback asks for allowed
    // hosts; a host name is not an accepted form, and its refusal says what Kestrel would do with it.
    [InlineData("0.0.0.0", "HttpTransport:AllowedHosts must name")]
    [InlineData("::", "HttpTransport:AllowedHosts must name")]
    [InlineData("*", "HttpTransport:AllowedHosts must name")]
    [InlineData("10.1.2.3", "HttpTransport:AllowedHosts must name")]
    [InlineData("mcp.internal", "Kestrel does not read 'mcp.internal' as an address")]
    public void G12_2_a_non_loopback_bind_with_no_allowed_host_is_refused(string bindAddress, string says)
    {
        // The bind address used to become the allowlist, and 0.0.0.0 there means any host.
        var ex = Assert.Throws<ConfigurationException>(() =>
            HttpServerComposition.AllowedHosts(Config(new() { ["HttpTransport:BindAddress"] = bindAddress })));

        Assert.Contains(says, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{bindAddress}'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 7 — a bind address is accepted only in a declared form: an IP address
    /// Kestrel reads as one, localhost, or an explicit all-interfaces spelling. Anything else refuses to start, allowed
    /// hosts or none, saying what Kestrel would do with it: a path, a scheme, a socket or a pipe stopped Kestrel ("A
    /// path base can only be configured...", exit 70) once the checks had passed; text it does not read as an address
    /// listened on every interface without a word; a name under .localhost was called not loopback though Kestrel
    /// binds loopback for it; and a zone on ::1 is ignored on Linux and cannot be bound on Windows.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1/x", true, "as a path")]
    [InlineData("127.0.0.1/x", false, "as a path")]
    [InlineData("localhost/", true, "as a path")]
    [InlineData("http://127.0.0.1", true, "as a path")]
    [InlineData("unix:/tmp/mcp.sock", true, "Unix socket")]
    [InlineData("pipe:/mcp", true, "named pipe")]
    [InlineData("[127.0.0.1]", true, "would listen on every interface")]
    [InlineData("localhost.", true, "would listen on every interface")]
    [InlineData(" 127.0.0.1", true, "would listen on every interface")]
    [InlineData("myhost.example", true, "look a host name up")]
    [InlineData("foo.localhost", true, "reads it as localhost")]
    [InlineData("foo.localhost", false, "reads it as localhost")]
    [InlineData("::1%1", true, "zone")]
    [InlineData("::1%1", false, "zone")]
    public void G12_2_a_bind_address_outside_the_accepted_forms_is_refused_saying_what_kestrel_would_do(string bindAddress, bool allowedHosts, string says)
    {
        var settings = new Dictionary<string, string?> { ["HttpTransport:BindAddress"] = bindAddress };
        if (allowedHosts)
        {
            settings["HttpTransport:AllowedHosts:0"] = "mcp.example.com";
        }

        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(settings)));

        Assert.StartsWith($"HttpTransport:BindAddress is '{bindAddress}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(says, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not a loopback address", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 7 — the explicit all-interfaces spellings are accepted forms: with allowed
    /// hosts the server binds, and without them the refusal says the address listens on every interface.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("*")]
    [InlineData("+")]
    public void G12_2_an_all_interfaces_bind_address_is_accepted_and_asks_for_allowed_hosts(string bindAddress)
    {
        Assert.Equal(["mcp.example.com"], HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = bindAddress,
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
        })));

        var ex = Assert.Throws<ConfigurationException>(() =>
            HttpServerComposition.AllowedHosts(Config(new() { ["HttpTransport:BindAddress"] = bindAddress })));
        Assert.StartsWith("HttpTransport:AllowedHosts must name", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"HttpTransport:BindAddress is '{bindAddress}', which listens on every interface.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 7 — the control for zones: on a link-local address a zone is what Kestrel
    /// binds by (that interface's address; a wrong zone fails to bind), so it is an accepted form, not loopback.
    /// </summary>
    [Fact]
    public void G12_2_a_link_local_address_with_its_zone_is_an_accepted_form()
    {
        Assert.Equal(["mcp.example.com"], HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = "fe80::1%1",
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
        })));

        var ex = Assert.Throws<ConfigurationException>(() =>
            HttpServerComposition.AllowedHosts(Config(new() { ["HttpTransport:BindAddress"] = "fe80::1%1" })));
        Assert.Contains("HttpTransport:BindAddress is 'fe80::1%1', which is not a loopback address.", ex.Message, StringComparison.Ordinal);
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
    /// contract-005 · G-12 (2), review round 5 — an entry no request can match leaves the server refusing
    /// every host, its own included, without saying why; a setting it would never act on, which the frame
    /// refuses everywhere else. An unset variable in a compose file (HttpTransport__AllowedHosts__0=${MCP_HOST})
    /// leaves it empty. A Host header loses its surrounding whitespace in HTTP, never carries a path or user
    /// information, and puts an IPv6 address in brackets; the filter compares a request's name without its
    /// port, so an entry with a port matches nothing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" mcp.example.com")]
    [InlineData("mcp.example.com ")]
    [InlineData("mcp.example.com:443")]
    [InlineData("::1")]
    [InlineData("mcp.example.com/mcp")]
    [InlineData("user@mcp.example.com")]
    // Found by asking Kestrel and the filter: an empty port, an IPv6 address with a port, a zone, or never
    // closed, a port that is not digits, and ASP.NET Core's own AllowedHosts habit of one list in one value.
    [InlineData("mcp.example.com:")]
    [InlineData("[::1]:443")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("[::1")]
    [InlineData("mcp.example.com:abc")]
    [InlineData("mcp.example.com;mcp2.example.com")]
    [InlineData("mcp.example.com,mcp2.example.com")]
    public void G12_2_an_allowed_host_no_request_can_match_is_refused_naming_its_key(string entry)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = "0.0.0.0",
            ["HttpTransport:AllowedHosts:0"] = entry,
        })));

        Assert.StartsWith("HttpTransport:AllowedHosts:0 is", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 — the server writes out Kestrel's rule for the Host a request can
    /// carry, because Kestrel keeps it internal (HttpUtilities.IsHostHeaderValid). This asks Kestrel's own, by
    /// reflection, about every ASCII character at the start, in the middle and at the end of a name, inside and
    /// after an IPv6 address, and in a port, and holds the server to its answers: an entry is refused whenever
    /// Kestrel would refuse a request whose Host is that entry, or the entry is empty or has a port; and one
    /// Kestrel lets through, with no port, is never refused as matching no request.
    ///
    /// Review round 6 — a documented limit: this runs on the runtime the tests run on (10.0.1, from the SDK
    /// global.json pins), and the image ships 10.0.12. When either runtime moves to another patch, run this
    /// comparison again on the shipped runtime, as the review did. Should the rules drift apart, the cost is
    /// precision only, and it fails closed.
    /// </summary>
    [Fact]
    public void G12_2_an_allowed_host_is_refused_as_matching_no_request_exactly_when_kestrel_or_the_filter_would_refuse_it()
    {
        var kestrel = typeof(KestrelServerOptions).Assembly
            .GetType("Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.HttpUtilities")
            ?.GetMethod("IsHostHeaderValid", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Kestrel's Host rule is no longer HttpUtilities.IsHostHeaderValid: find where Kestrel checks a Host "
                + "header now, and hold HttpServerComposition's copy of it to that.");

        var entries = new List<string> { string.Empty, "::1", "[::1", "[]", "[1]", "[::1]", "[::1]:443", "[::1]:", ":443", "mcp.example.com:" };
        for (var code = 0; code < 128; code++)
        {
            var c = (char)code;
            entries.AddRange([$"{c}mcp.example.com", $"mcp{c}example.com", $"mcp.example.com{c}", $"[:{c}:1]", $"[::1]{c}", $"mcp.example.com:4{c}3"]);
        }

        var wrong = new List<string>();
        foreach (var entry in entries)
        {
            var carried = (bool)kestrel.Invoke(null, [entry])!;
            var hasPort = entry.StartsWith('[') ? entry.Contains("]:", StringComparison.Ordinal) : entry.Contains(':', StringComparison.Ordinal);
            string? refusal = null;
            try
            {
                HttpServerComposition.AllowedHosts(Config(new()
                {
                    ["HttpTransport:BindAddress"] = "0.0.0.0",
                    ["HttpTransport:AllowedHosts:0"] = entry,
                }));
            }
            catch (ConfigurationException ex)
            {
                refusal = ex.Message;
            }

            var shown = string.Concat(entry.Select(c => c is < ' ' or > '~' ? $"U+{(int)c:X4}" : $"{c}"));
            if (refusal is null && (!carried || hasPort || entry.Length == 0))
            {
                wrong.Add($"'{shown}' binds, and no request can match it (Kestrel lets a request with it as its Host through: {carried}).");
            }
            else if (carried && !hasPort && entry.Length > 0 && refusal?.Contains("which no request can match", StringComparison.Ordinal) == true)
            {
                wrong.Add($"'{shown}' is refused as matching no request, and Kestrel lets a request with it as its Host through: {refusal}");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {entries.Count} entries:{Environment.NewLine}{string.Join(Environment.NewLine, wrong)}");
    }

    /// <summary>
    /// contract-005 · G-12 (2) — belt and braces: the host filter itself is asked, over the final list,
    /// whether it lets through a request for a random name under .invalid, bare and with a trailing dot.
    /// The entry rules refuse every spelling that would, so this is reached only through the list
    /// itself. Review round 2 — 0.0.0.0, [::] and a full-width asterisk passed it: the matcher alone
    /// does not read them as "any host", while the middleware that uses it does.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData("*.invalid")]
    [InlineData("*.")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("\uFF0A")]
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

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 addendum — the loopback names are the default however the loopback
    /// address is written. Kestrel binds [::1] for 0::1, 0:0:0:0:0:0:0:1 and [::1], 127.0.0.1 for 127.1, and both for
    /// LOCALHOST; the default used to be the bind address as written unless it was spelled localhost, 127.0.0.1 or
    /// ::1, and 0::1, unbracketed, matches no request, so such a server started and answered every request 400.
    /// Review round 8 — an IPv4 address is written in its standard form only, so 127.1 is refused now, saying how it
    /// would be read and what to write; the IPv6 spellings stand.
    /// </summary>
    [Theory]
    [InlineData("0::1", null)]
    [InlineData("0:0:0:0:0:0:0:1", null)]
    [InlineData("[::1]", null)]
    [InlineData("127.1", "which would be read as 127.0.0.1; write 127.0.0.1")]
    [InlineData("LOCALHOST", null)]
    public void G12_2_a_loopback_bind_however_written_defaults_to_the_loopback_names(string bindAddress, string? refusedSaying)
    {
        var settings = Config(new() { ["HttpTransport:BindAddress"] = bindAddress });
        if (refusedSaying is null)
        {
            Assert.Equal(["localhost", "127.0.0.1", "[::1]"], HttpServerComposition.AllowedHosts(settings));
        }
        else
        {
            var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(settings));
            Assert.Contains(refusedSaying, ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 addendum 2 — any other loopback address gives that address alone, in
    /// its standard form. Kestrel binds 127.0.0.2 alone for each of these spellings, and a request reaching it carries
    /// 127.0.0.2 as its Host; the loopback names reach none of it, and the spelling as written is not what a request
    /// carries. Review round 8 — which supersedes the other spellings: an IPv4 address is written in its standard form
    /// only, so they are refused now, saying how they would be read and to write 127.0.0.2.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.2", true)]
    [InlineData("127.2", false)]
    [InlineData("127.0.2", false)]
    [InlineData("0x7f.0.0.2", false)]
    [InlineData("2130706434", false)]
    public void G12_2_another_loopback_address_defaults_to_itself_alone_in_its_standard_form(string bindAddress, bool standard)
    {
        var settings = Config(new() { ["HttpTransport:BindAddress"] = bindAddress });
        if (standard)
        {
            Assert.Equal(["127.0.0.2"], HttpServerComposition.AllowedHosts(settings));
        }
        else
        {
            var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(settings));
            Assert.Contains("which would be read as 127.0.0.2; write 127.0.0.2", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 8 — an IPv4 bind address is accepted in its standard form only: four
    /// decimal parts, without leading zeros, as IPAddress writes it. The parser reads the others as some address, not
    /// always the one meant (010.0.0.1 is octal, 8.0.0.1), and the server then failed to bind it with a stack trace, or
    /// bound what nobody wrote. The refusal shows how it would be read, and what to write.
    /// </summary>
    [Theory]
    [InlineData("010.0.0.1", "8.0.0.1", "10.0.0.1")]
    [InlineData("127.2", "127.0.0.2", null)]
    [InlineData("127.000.000.001", "127.0.0.1", null)]
    [InlineData("0x7f.0.0.2", "127.0.0.2", null)]
    [InlineData("2130706434", "127.0.0.2", null)]
    [InlineData("1", "0.0.0.1", null)]
    [InlineData("10.01.2.3", "10.1.2.3", null)]
    public void G12_2_an_ipv4_bind_address_not_in_its_standard_form_is_refused_showing_how_it_would_be_read(string bindAddress, string read, string? meant)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = bindAddress,
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
        })));

        Assert.StartsWith($"HttpTransport:BindAddress is '{bindAddress}', which would be read as {read}; write {read}", ex.Message, StringComparison.Ordinal);
        if (meant is null)
        {
            Assert.DoesNotContain("if that is what you meant", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($", or {meant} if that is what you meant", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 8 — a link-local bind address without its interface's zone is refused
    /// before binding, allowed hosts or none. The address parser drops a zone it cannot resolve without a word — %0, an
    /// unknown name, a percent-encoded one, and on Windows any name at all — and the server then failed to bind it,
    /// with a stack trace; the refusal says the zone was dropped where the text had one.
    /// </summary>
    [Theory]
    [InlineData("fe80::1", false)]
    [InlineData("fe80::1%0", true)]
    [InlineData("fe80::1%nosuchnic", true)]
    [InlineData("fe80::1%25eth0", true)]
    public void G12_2_a_link_local_bind_address_without_its_zone_is_refused(string bindAddress, bool hadOne)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = bindAddress,
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
        })));

        Assert.StartsWith($"HttpTransport:BindAddress is '{bindAddress}', a link-local address", ex.Message, StringComparison.Ordinal);
        Assert.Contains("needs its interface's zone, as fe80::1%eth0 or fe80::1%2", ex.Message, StringComparison.Ordinal);
        Assert.Equal(hadOne, ex.Message.Contains("dropped", StringComparison.Ordinal));
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 9 — a bind address that cannot accept connections refuses to start,
    /// allowed hosts or none: the IPv4 limited broadcast address, IPv4 multicast (224.0.0.0/4) and IPv6 multicast
    /// (ff00::/8). The server used to pass every check and log "Now listening on http://255.255.255.255:3001", and
    /// nobody could connect.
    /// </summary>
    [Theory]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("239.255.255.250", "multicast")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("[ff02::1]", "multicast")]
    public void G12_2_a_bind_address_that_cannot_accept_connections_is_refused(string bindAddress, string kind)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = bindAddress,
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
        })));

        Assert.StartsWith($"HttpTransport:BindAddress is '{bindAddress}', a {kind} address, which cannot accept connections", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 9 — the proxies and networks forwarded headers are trusted from are read at
    /// startup, and an entry that is not one is refused naming its key: it used to be parsed only as the pipeline was
    /// built, and stopped the server with exit 70 and a stack trace. An IPv4 address, alone or as a network's, is held
    /// to the standard form, as the bind address is: 010.0.0.1 would be read as 8.0.0.1. A network needs its prefix
    /// length, 0-32 for IPv4 and 0-128 for IPv6; an IPv4 address alone is that one address, and an IPv6 address alone was
    /// read as a /32 network, so it is refused.
    /// </summary>
    [Theory]
    [InlineData("HttpTransport:KnownProxies:0", "not-an-ip", "which is not an IP address")]
    [InlineData("HttpTransport:KnownProxies:0", "010.0.0.1", "which would be read as 8.0.0.1; write 8.0.0.1, or 10.0.0.1 if that is what you meant")]
    [InlineData("HttpTransport:KnownNetworks:0", "10.0.0.0/99", "whose prefix length is outside 0-32")]
    [InlineData("HttpTransport:KnownNetworks:0", "fd00::/200", "whose prefix length is outside 0-128")]
    [InlineData("HttpTransport:KnownNetworks:0", "10.0.0.0/x", "whose prefix length is outside 0-32")]
    [InlineData("HttpTransport:KnownNetworks:0", "not-an-ip/8", "whose address, 'not-an-ip', is not an IP address")]
    [InlineData("HttpTransport:KnownNetworks:0", "010.0.0.0/8", "whose address would be read as 8.0.0.0; write 8.0.0.0/8, or 10.0.0.0/8 if that is what you meant")]
    [InlineData("HttpTransport:KnownNetworks:0", "fd00::1", "an IPv6 address with no prefix length")]
    public void G12_2_a_trusted_proxy_or_network_that_is_not_one_is_refused_naming_its_key(string key, string value, string says)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.TrustedProxies(Config(new() { [key] = value })));

        Assert.StartsWith($"{key} is '{value}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(says, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>contract-005 · G-12 (2), review round 9 — the control: entries that are what they say are read as written.</summary>
    [Fact]
    public void G12_2_trusted_proxies_and_networks_written_as_addresses_are_read_as_written()
    {
        var (proxies, networks) = HttpServerComposition.TrustedProxies(Config(new()
        {
            ["HttpTransport:KnownProxies:0"] = "10.0.0.2",
            ["HttpTransport:KnownProxies:1"] = "fd00::2",
            ["HttpTransport:KnownNetworks:0"] = "10.0.0.0/8",
            ["HttpTransport:KnownNetworks:1"] = "10.1.2.3",
            ["HttpTransport:KnownNetworks:2"] = "fd00::/8",
        }));

        Assert.Equal(["10.0.0.2", "fd00::2"], proxies.Select(p => p.ToString()));
        Assert.Equal(["10.0.0.0/8", "10.1.2.3/32", "fd00::/8"], networks.Select(n => n.ToString()));
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 9 — a bind failure the server has no plain words for says what the operating
    /// system said, not only the error's name. A real one, raised inside Kestrel's socket transport: a file's handle
    /// given to it as a socket's, bound through the transport's factory as Kestrel binds an address, which creates the
    /// socket and then listens on it. Windows refuses the handle as the socket is created; Linux takes it without a word
    /// and refuses it as the transport listens on it, so creating the socket alone raised nothing there (CI run
    /// 36497415078). On both it is not a socket, which the server has no plain words for.
    /// </summary>
    [Fact]
    public async Task G12_2_a_bind_failure_without_plain_words_says_what_the_operating_system_said()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp-tests-not-a-socket-{Guid.NewGuid():N}");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var transport = new Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportFactory(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var raised = await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() =>
            transport.BindAsync(new Microsoft.AspNetCore.Connections.FileHandleEndPoint(
                (ulong)file.SafeFileHandle.DangerousGetHandle(), Microsoft.AspNetCore.Connections.FileHandleType.Auto)).AsTask());

        var refusal = HttpServerComposition.BindFailure(raised, "127.0.0.1", 3001);

        Assert.NotNull(refusal);
        Assert.Contains($"({raised.SocketErrorCode}: {raised.Message})", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 8 — a zone on the any-address: Kestrel binds :: with both IPv4 and IPv6
    /// only for :: itself, so ::%1 binds :: for IPv6 alone on Linux, the zone ignored; the refusal says so.
    /// </summary>
    [Fact]
    public void G12_2_a_zone_on_the_any_address_is_refused_saying_it_would_bind_ipv6_alone()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            HttpServerComposition.AllowedHosts(Config(new() { ["HttpTransport:BindAddress"] = "::%1" })));

        Assert.Contains("on Linux it binds :: for IPv6 alone and ignores the zone", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 addendum — a bind address is loopback as Kestrel binds it. Kestrel
    /// reads it with IPAddress.TryParse as written and binds every interface for text that is not an address; the
    /// server took the brackets off first, so it took [127.0.0.1] for loopback and asked for no allowed host while
    /// Kestrel listened on every interface. Review round 6 — the refusal says so plainly, and how to write loopback,
    /// before any advice about allowed hosts: whitespace around an address, or a host name, is not an address to
    /// Kestrel either.
    /// </summary>
    [Theory]
    [InlineData("[127.0.0.1]")]
    [InlineData("[::1")]
    [InlineData("::1]")]
    [InlineData("[[::1]]")]
    [InlineData(" 127.0.0.1")]
    [InlineData("mcp.internal")]
    public void G12_2_a_bind_address_kestrel_binds_on_every_interface_is_not_loopback(string bindAddress)
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            HttpServerComposition.AllowedHosts(Config(new() { ["HttpTransport:BindAddress"] = bindAddress })));

        var kestrel = $"Kestrel does not read '{bindAddress}' as an address and would listen on every interface.";
        var loopback = ex.Message.IndexOf("127.0.0.1, or [::1]", StringComparison.Ordinal);
        var allowedHosts = ex.Message.IndexOf("HttpTransport:AllowedHosts", StringComparison.Ordinal);
        Assert.True(
            ex.Message.IndexOf(kestrel, StringComparison.Ordinal) is >= 0 and var said && said < loopback && loopback < allowedHosts,
            $"the refusal should say \"{kestrel}\", then how to write loopback, then the advice about allowed hosts: {ex.Message}");
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 6 — a bind address that carries a port refuses to start, whatever the
    /// form, and allowed hosts or none. The server listens on HttpTransport:Port: IPAddress reads [::1]:9999 as ::1,
    /// dropping 9999, and Kestrel reads 127.0.0.1:9999 or localhost:9999 as no address at all.
    /// </summary>
    [Theory]
    [InlineData("[::1]:9999", "[::1]")]
    [InlineData("[::1]:", "[::1]")]
    [InlineData("127.0.0.1:9999", "127.0.0.1")]
    [InlineData("127.0.0.1:0", "127.0.0.1")]
    [InlineData("localhost:9999", "localhost")]
    [InlineData("mcp.internal:9999", "mcp.internal")]
    public void G12_2_a_bind_address_that_carries_a_port_is_refused_saying_where_the_port_goes(string bindAddress, string address)
    {
        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(new()
        {
            ["HttpTransport:BindAddress"] = bindAddress,
            ["HttpTransport:AllowedHosts:0"] = "mcp.example.com",
        })));

        Assert.StartsWith($"HttpTransport:BindAddress is '{bindAddress}', which carries a port.", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"Write the address alone, as {address}, and set the port in HttpTransport:Port.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 6 addendum — an empty bind address, or one of whitespace alone, refuses
    /// to start naming the key, allowed hosts or none. An unset variable in a compose file leaves it empty, and the
    /// server used to pass its checks and stop in Kestrel instead, "Invalid url", exit 70.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("", false)]
    [InlineData("   ", true)]
    [InlineData("   ", false)]
    public void G12_2_an_empty_bind_address_is_refused_naming_the_key(string bindAddress, bool allowedHosts)
    {
        var settings = new Dictionary<string, string?> { ["HttpTransport:BindAddress"] = bindAddress };
        if (allowedHosts)
        {
            settings["HttpTransport:AllowedHosts:0"] = "mcp.example.com";
        }

        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(settings)));

        Assert.StartsWith("HttpTransport:BindAddress is empty", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 6 — an IPv4-mapped bind address refuses to start, bracketed or not, and
    /// allowed hosts or none: Kestrel binds any IPv6 address but [::] on an IPv6-only socket, which cannot take an
    /// IPv4-mapped one. [::ffff:127.0.0.1] counted as loopback, and the server then failed to bind it.
    /// </summary>
    [Theory]
    [InlineData("[::ffff:127.0.0.1]", false, "127.0.0.1")]
    [InlineData("[::ffff:127.0.0.1]", true, "127.0.0.1")]
    [InlineData("::ffff:127.0.0.1", true, "127.0.0.1")]
    [InlineData("::ffff:10.1.2.3", true, "10.1.2.3")]
    public void G12_2_an_ipv4_mapped_bind_address_is_refused_naming_why(string bindAddress, bool allowedHosts, string ipv4)
    {
        var settings = new Dictionary<string, string?> { ["HttpTransport:BindAddress"] = bindAddress };
        if (allowedHosts)
        {
            settings["HttpTransport:AllowedHosts:0"] = "mcp.example.com";
        }

        var ex = Assert.Throws<ConfigurationException>(() => HttpServerComposition.AllowedHosts(Config(settings)));

        Assert.StartsWith($"HttpTransport:BindAddress is '{bindAddress}', an IPv4-mapped IPv6 address", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"Write the IPv4 address itself, {ipv4}.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 addendum — the entry rules hold for the list the server runs with
    /// wherever it came from, a loopback bind included. An IPv4-mapped loopback address is loopback, and not an
    /// address the loopback names reach; the list it gave was the address as written, which, unbracketed, no
    /// request can match. Review round 6 — it is refused before any list is made, as an IPv4-mapped address
    /// (above), still naming the bind address.
    /// </summary>
    [Fact]
    public void G12_2_a_default_allowed_host_no_request_can_match_is_refused_naming_the_bind_address()
    {
        var ex = Assert.Throws<ConfigurationException>(() =>
            HttpServerComposition.AllowedHosts(Config(new() { ["HttpTransport:BindAddress"] = "::ffff:127.0.0.1" })));

        Assert.StartsWith("HttpTransport:BindAddress is '::ffff:127.0.0.1'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IPv4-mapped", ex.Message, StringComparison.Ordinal);
    }
}
