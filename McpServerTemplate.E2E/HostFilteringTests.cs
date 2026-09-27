using System.Net;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-11 (2) (G-12 (2) · UC-11) — host filtering cannot be switched off by the bind.
///
/// The host allowlist is what stops DNS rebinding from turning a browser into a client of this server.
/// ASP.NET Core's host filter treats *, 0.0.0.0 and [::] as "allow any host", and the server used to
/// fall back to the bind address when no AllowedHosts was set, so a 0.0.0.0 bind — the one every
/// container deployment uses — ran with no host filtering at all. Each variant here now refuses to
/// start, naming the key; with a real name set, a foreign Host header is refused.
/// </summary>
public sealed class HostFilteringTests(HostFilteringTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<HostFilteringTests.Server>
{
    /// <summary>The environment's server: bound to 0.0.0.0, AllowedHosts mcp.e2e.test.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None);

    private const string Attacker = "attacker.example.com";

    /// <summary>Each way a 0.0.0.0 bind has been left open, with the key the refusal must name.</summary>
    public static TheoryData<string, string, string?> OpenVariants() => new()
    {
        { "no-allowed-hosts", "HttpTransport:AllowedHosts", null },
        { "allowed-ipv4-any", "HttpTransport:AllowedHosts:0", "0.0.0.0" },
        { "allowed-star", "HttpTransport:AllowedHosts:0", "*" },
        { "allowed-ipv6-any-bracketed", "HttpTransport:AllowedHosts:0", "[::]" },
        { "allowed-ipv6-any", "HttpTransport:AllowedHosts:0", "::" },
    };

    [Theory]
    [MemberData(nameof(OpenVariants))]
    public async Task T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start(string variant, string key, string? value)
    {
        // The environment binds 0.0.0.0 (G-8); the delta takes away the real name, or replaces it.
        var delta = value is null
            ? SettingsDelta.None.Remove(key)
            : SettingsDelta.None.Set(key, value);

        await using var outcome = await fixture.Environment.StartupAsync($"hosts-{variant}", delta);
        output.WriteLine(outcome.Describe());

        var served = outcome.Started
            ? $" A GET /healthz sent to it with Host: {Attacker} got {(int)await outcome.GetDirectAsync("/healthz", Attacker)}."
            : string.Empty;
        Assert.True(
            outcome.ExitCode == 78 && outcome.RefusalLine?.Contains(key, StringComparison.Ordinal) == true,
            $"a 0.0.0.0 bind with {(value is null ? "no AllowedHosts" : $"{key}={value}")}: {outcome.Describe()}.{served}");
    }

    /// <summary>
    /// Allowed-host entries the filter reads as more than one name, or as another name, each with a
    /// host the filter admits under it, which the failure message shows when the server starts. The
    /// filter honours *.example.com as every name under example.com, so *.com and *. admit nearly
    /// anything; it converts an entry to punycode before matching, so a non-ASCII spelling is not the
    /// name written (where the runtime has ICU, a full-width asterisk, U+FF0A, folds into *; the image
    /// runs globalization-invariant and fails closed on it instead); and it compares names as written,
    /// so a trailing dot is another name.
    /// </summary>
    public static TheoryData<string, string, string> NotOneNameVariants() => new()
    {
        { "subdomain-wildcard-tld", "*.com", "evil.com" },
        { "wildcard-root-dot", "*.", "attacker.example.com." },
        { "subdomain-wildcard", "*.example.com", Attacker },
        { "fullwidth-star", "\uFF0A", Attacker },
        { "trailing-dot", $"{TlsFront.Host}.", $"{TlsFront.Host}." },

        // Review round 5 — HttpTransport__AllowedHosts__0=${MCP_HOST} in a compose file, with MCP_HOST unset:
        // an empty entry, which no request matches, so the server answers every host 400, its own included.
        { "unset-variable", string.Empty, TlsFront.Host },
    };

    [Theory]
    [MemberData(nameof(NotOneNameVariants))]
    public async Task T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start(string variant, string value, string admits)
    {
        const string key = "HttpTransport:AllowedHosts:0";
        await using var outcome = await fixture.Environment.StartupAsync($"hosts-{variant}", SettingsDelta.None.Set(key, value));
        output.WriteLine(outcome.Describe());

        var served = outcome.Started
            ? $" A GET /healthz sent to it with Host: {admits} got {(int)await outcome.GetDirectAsync("/healthz", admits)}."
            : string.Empty;
        Assert.True(
            outcome.ExitCode == 78 && outcome.RefusalLine?.Contains(key, StringComparison.Ordinal) == true,
            $"a 0.0.0.0 bind with {key}={value}: {outcome.Describe()}.{served}");
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 6 — an IPv4-mapped bind address refuses to start, naming why,
    /// allowed hosts or none: Kestrel binds any IPv6 address but [::] on an IPv6-only socket, which cannot take an
    /// IPv4-mapped one, so the image used to fail at the bind instead. Here with the environment's allowed host set.
    /// </summary>
    [Theory]
    [InlineData("ipv4-mapped-bracketed", "[::ffff:127.0.0.1]")]
    [InlineData("ipv4-mapped", "::ffff:127.0.0.1")]
    public async Task T11_2_an_ipv4_mapped_bind_address_refuses_to_start(string variant, string bindAddress)
    {
        const string key = "HttpTransport:BindAddress";
        await using var outcome = await fixture.Environment.StartupAsync($"bind-{variant}", SettingsDelta.None.Set(key, bindAddress));
        output.WriteLine(outcome.Describe());

        Assert.True(
            outcome.ExitCode == 78 && outcome.RefusalLine?.Contains($"{key} is '{bindAddress}', an IPv4-mapped", StringComparison.Ordinal) == true,
            $"{key}={bindAddress}: {outcome.Describe()}");
    }

    /// <summary>
    /// contract-005 · G-12 (2) — ASP.NET Core reads the Kestrel section on its own, and its endpoints
    /// override the address the server binds: here the server says it binds localhost, with the
    /// loopback names as its host allowlist, and Kestrel:Endpoints puts it on every interface.
    /// </summary>
    [Fact]
    public async Task T11_2_a_kestrel_setting_refuses_to_start()
    {
        const string key = "Kestrel:Endpoints:Web:Url";
        var delta = SettingsDelta.None
            .Remove("HttpTransport:BindAddress")
            .Remove("HttpTransport:AllowedHosts")
            .Set(key, $"http://0.0.0.0:{ServerUnderTest.Port}");

        await using var outcome = await fixture.Environment.StartupAsync("hosts-kestrel-endpoint", delta);
        output.WriteLine(outcome.Describe());

        var served = outcome.Started
            ? $" A GET /healthz sent to it from off the machine with Host: localhost got {(int)await outcome.GetDirectAsync("/healthz", "localhost")}."
            : string.Empty;
        Assert.True(
            outcome.ExitCode == 78 && outcome.Refusal?.Contains($"'{key}'", StringComparison.Ordinal) == true,
            $"a server whose BindAddress is loopback, with {key}=http://0.0.0.0:{ServerUnderTest.Port}: {outcome.Describe()}.{served}");
    }

    [Fact]
    public async Task T11_2_once_a_real_name_is_set_a_foreign_host_is_refused()
    {
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var healthz = new Uri(fixture.Server.DirectEndpoint, "/healthz");

        // Positive control: the name the server answers for.
        using (var own = new HttpRequestMessage(HttpMethod.Get, healthz))
        {
            own.Headers.Host = TlsFront.Host;
            using var response = await direct.SendAsync(own);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var foreign = new HttpRequestMessage(HttpMethod.Get, healthz);
        foreign.Headers.Host = Attacker;
        using var refused = await direct.SendAsync(foreign);
        Assert.True(
            refused.StatusCode == HttpStatusCode.BadRequest,
            $"with AllowedHosts={TlsFront.Host}, a request with Host: {Attacker} got {(int)refused.StatusCode}, not 400.");
    }
}
