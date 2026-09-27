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
