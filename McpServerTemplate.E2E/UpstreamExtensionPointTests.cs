using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-15 (G-16 · the bearing) — an extension point works.
///
/// The upstream registry is where a later contract puts its own container in front of an upstream: egress's fault proxy
/// is the named example. Here a stand-in fake that is not WireMock (<see cref="StandInUpstream"/>) is registered for one
/// provider host, SMHI's observations host, in place of the fake (<see cref="E2EEnvironment.Upstreams"/>); the
/// environment starts it, gives it that name on the network and a certificate for it, and nothing else about the
/// server or the harness changes. The server's call to that host reaches the stand-in, which records it; the fake it
/// replaced — seen recording this server's call to another provider host earlier in the same test — records none.
/// </summary>
public sealed class UpstreamExtensionPointTests(UpstreamExtensionPointTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<UpstreamExtensionPointTests.Server>
{
    /// <summary>Both SMHI providers bound to idp-a, which mints this class's tokens.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA)
        .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpA));

    private const string IssuerA = "idp-a.e2e.test";

    /// <summary>The provider host the stand-in is registered for.</summary>
    private const string StoodIn = "opendata-download-metobs.smhi.se";

    /// <summary>A provider host the fake still answers: the witness that the fake records this server's calls.</summary>
    private const string StillTheFake = "opendata-download-metfcst.smhi.se";

    /// <summary>
    /// Sabotage (G-11): leave the observations host registered to the fake (drop the .Replace in
    /// <see cref="E2EEnvironment.Upstreams"/>, the one place it is registered). The server's call then reaches WireMock, no
    /// stand-in is started, and the claim's assertion goes red.
    /// </summary>
    [Fact]
    public async Task T15_a_stand_in_registered_for_a_provider_host_receives_the_servers_call()
    {
        var environment = fixture.Environment;
        using var http = fixture.CreateClient();
        var server = await fixture.Server.NetworkAddressAsync();

        // What the image calls, from its own startup line (G-11): a forecast tool and an observations tool.
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        var entries = line.Entries(E2EEnvironment.Issuers.Entries.SelectMany(e => e.ScopeCatalog));
        Assert.Contains(new FrameLine.Entry("tool", "Smhi", "get_forecast", "weather:read", "Read"), entries);
        Assert.Contains(new FrameLine.Entry("tool", "SmhiObs", "get_recent_temperature", "observations:read", "Read"), entries);

        var token = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read", "observations:read"]);
        var fake = environment.UpstreamFor(StillTheFake);

        // The witness is live: a call to a host the fake still answers is recorded by the fake, from this server.
        await CallAsync(http, token, "get_forecast", new { latitude = 59.33, longitude = 18.07 });
        var forecast = (await fake.RecordedAsync(http)).Where(r => server.Equals(r.Client) && r.Host == StillTheFake).ToList();
        Assert.True(forecast.Count > 0, $"the fake recorded no call from this server ({server}) to {StillTheFake}: the witness saw nothing.");

        // The observations host's call — the station list is fetched first, on a fresh server whose cache is cold.
        var answer = await CallAsync(http, token, "get_recent_temperature", new { latitude = 59.33, longitude = 18.07 });
        output.WriteLine($"get_recent_temperature answered: {McpRequests.TextOf(answer)}");

        var standIn = environment.UpstreamServices.TryGetValue(StandInUpstream.Owner, out var started) ? started : null;
        var received = standIn is null
            ? []
            : (await standIn.RecordedAsync(http)).Where(r => server.Equals(r.Client) && r.Host == StoodIn).ToList();
        var byTheFake = (await fake.RecordedAsync(http)).Where(r => server.Equals(r.Client) && r.Host == StoodIn).ToList();
        output.WriteLine($"The owner answering {StoodIn}: {environment.UpstreamFor(StoodIn).Container.Name}; the stand-in recorded "
            + $"[{string.Join(", ", received.Select(r => $"{r.Method} {r.Path}"))}]; the fake [{string.Join(", ", byTheFake.Select(r => $"{r.Method} {r.Path}"))}].");

        Assert.True(
            standIn is not null && received.Count > 0 && byTheFake.Count == 0,
            $"the server's call to {StoodIn} was {(standIn is null ? "made with no stand-in started: nothing registered one for the host" : $"recorded by the stand-in {received.Count} time(s)")}, "
            + $"and by the fake it replaced {byTheFake.Count} time(s) [{string.Join(", ", byTheFake.Select(r => $"{r.Method} {r.Path}"))}]: a host registered "
            + "to the stand-in is answered by the stand-in, and only by it.");
    }

    private static Task<JsonElement> CallAsync(HttpClient http, string token, string tool, object arguments) =>
        McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, "tools/call", new { name = tool, arguments }));
}
