using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Microsoft.IdentityModel.JsonWebTokens;
using ModelContextProtocol.Client;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-1 (G-1, 4–9, 14 · UC-1) — the walking skeleton.
///
/// The image builds from the repository's Dockerfile, the environment starts, and one tools/list with
/// a real Keycloak token succeeds through the TLS front, with a timing recorded for every phase. It
/// runs on Windows with Docker Desktop and on GitHub's Linux runner, before any other end-to-end test
/// is written, and it proves the mechanics the later tests stand on: the test issuer is the same
/// issuer by the same name from the server and from the test; the client's HTTP traffic, OAuth
/// discovery included, goes through the test's name map and nowhere else; and the front's forwarded
/// client address is honoured by the server (the forwarded scheme is T-3's claim,
/// TokenRefusalTests.T3_no_token_is_challenged_with_the_metadata_on_the_front). Its negative control shows the same
/// forwarded headers sent from anywhere but the front change nothing (G-9).
///
/// A fault in the environment rather than the product fails with an <see cref="EnvironmentFaultException"/>
/// naming its phase and cause, never as one of these assertions: contract-005 · G-17 round 1 (HAR-R1-04) — a call's
/// failure is folded into a claim only when it is the server's answer (<see cref="Claim.IsTheServersAnswer"/>).
/// contract-005 · G-17 round 2 (HAR-R2-03) — and an OAuth flow's only when every name of the environment it asked answered
/// (<see cref="NameMapLog.ThrowIfAMappedNameGaveNoAnswer"/>): the SDK's OAuth client reports a connection or TLS failure at
/// the authorization server as an McpException of its own. contract-005 · G-17 round 3 (HAR-R3-01) — and it reports a name that
/// did not answer within its own budget the same way; the flow runs through <see cref="StandardClient.RunAsync"/>, which counts
/// such a name among those that gave no answer.
/// </summary>
public sealed class WalkingSkeletonTests(WalkingSkeletonTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<WalkingSkeletonTests.Server>
{
    /// <summary>
    /// The server exactly as the environment configures it: no delta, but in a run of one of the sabotages below that
    /// act on it, each weakening it for its own test (G-11).
    /// </summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Sabotaged(KeycloakIssuerElsewhere, d => d.Set($"Authentication:IdentityProviders:{E2EEnvironment.KeycloakIssuer}:Issuer", $"https://{KeycloakService.Host}:{KeycloakService.Port}/realms/other"))
        .Sabotaged(IdpAByAnotherName, d => d.Set($"Authentication:IdentityProviders:{E2EEnvironment.IdpA}:Authority", "https://stranger.e2e.test"))
        .Sabotaged(AuthorizationServerOutside, d => d.Set($"Authentication:IdentityProviders:{E2EEnvironment.IdpA}:Issuer", "https://idp-a.example.com"))
        .Sabotaged(GatewayTrusted, d => d.Set("HttpTransport:KnownProxies:1", E2ENetwork.Gateway.ToString())));

    private const string IssuerA = "idp-a.e2e.test";

    private const string KeycloakIssuerElsewhere = "t1-keycloak-issuer-pinned-to-another-realm";
    private const string IdpAByAnotherName = "t1-idp-a-reached-by-another-name";
    private const string AuthorizationServerOutside = "t1-authorization-server-outside-the-environment";
    private const string SecondAsFirst = "t1-second-address-forwarded-as-the-first";
    private const string GatewayTrusted = "t1-gateway-trusted-as-a-proxy";

    [Fact]
    [Sabotage(KeycloakIssuerElsewhere, SabotageActs.ContainerEnvironment,
        "The class's server pins Keycloak's issuer to another realm of the same Keycloak "
        + "(Authentication:IdentityProviders:keycloak:Issuer …/realms/other), so it accepts no token Keycloak's realm mcp issues.")]
    public async Task T1_a_keycloak_token_lists_tools_through_the_front()
    {
        var timings = E2EEnvironment.Timings;
        try
        {
            var token = await timings.MeasureAsync("t1: keycloak client-credentials token", () => fixture.KeycloakTokenAsync());

            var log = new NameMapLog();
            IList<McpClientTool>? listed = null;
            string? failed = null;
            try
            {
                listed = await timings.MeasureAsync("t1: tools/list through the front", async () =>
                {
                    using var http = fixture.CreateClient(log);
                    return await ListToolsAsync(http, token);
                });
            }
            catch (Exception ex) when (Claim.IsTheServersAnswer(ex))
            {
                // contract-005 · G-17 round 1 (HAR-R1-04) — the server's answer is the claim's, and only that: a connection
                // or TLS failure, or an environment fault, propagates as what it is.
                failed = $"{ex.GetType().Name}: {ex.Message}";
            }

            // contract-005 · G-11 — a red here carries its reason, not only the status: the server logs why it refused a
            // token (authn_login_fail), and that line goes into the failure.
            var refusal = failed is null ? null : await fixture.Server.LatestStderrLineAsync("authn_login_fail");
            Claim.True(
                failed is null,
                $"tools/list with a Keycloak token failed: {failed} The server's latest authn_login_fail line: {refusal ?? "(none on its stderr)"}");
            var tools = listed!;

            // The token came from the real identity provider, for this server's resource.
            var claims = new JsonWebToken(token);
            Assert.Equal(KeycloakService.Issuer, claims.Issuer);
            Assert.Contains(ServerUnderTest.Resource, claims.Audiences);

            // The claim: the call succeeded, and what came back is what the image says it serves.
            var startupLine = await fixture.Server.StartupLineAsync();
            Claim.Holds(() =>
            {
                Assert.NotEmpty(tools);
                Assert.All(tools, tool => Assert.Contains($"/{tool.Name}:", startupLine, StringComparison.Ordinal));
                Assert.Contains(log.Requests, r => r.Uri.Host == TlsFront.Host && r.Status == (int)HttpStatusCode.OK);
            });

            output.WriteLine($"tools/list with a Keycloak token returned {tools.Count} tools: {string.Join(", ", tools.Select(t => t.Name))}");
        }
        finally
        {
            output.WriteLine("Per-phase timings (this run so far):");
            output.WriteLine(timings.Describe());
            if (fixture.Environment is { } environment)
            {
                timings.WriteTo(Path.Combine(environment.ResultsDirectory, "timings.json"));
            }
        }
    }

    /// <summary>
    /// The server's requests are counted from its own address (contract-005 · T-3's per-server counts), so what another
    /// class's server asks the issuer at the same moment is not taken for this one's.
    /// </summary>
    [Fact]
    [Sabotage(IdpAByAnotherName, SabotageActs.ContainerEnvironment,
        "The class's server reaches idp-a by another name: its Authentication:IdentityProviders:idp-a:Authority is "
        + "https://stranger.e2e.test, a name of the same test issuer container that no server is configured with.")]
    public async Task T1_the_test_issuer_is_one_issuer_by_one_name_from_the_server_and_from_the_test()
    {
        var log = new NameMapLog();
        using var http = fixture.CreateClient(log);

        // contract-005 · G-17 round 2 (HAR-R2-02) — this server's requests: from its address since it started.
        var server = await fixture.Server.RequestSourceAsync();

        var before = await TestIssuerService.CountsAsync(http, IssuerA, server);

        // From the test: minted through idp-a.e2e.test's own door, over TLS the test CA validates.
        var token = await E2EEnvironment.Timings.MeasureAsync("t1: mint at the test issuer", () =>
            TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read"]));
        Claim.Holds(() =>
        {
            Assert.Equal("https://idp-a.e2e.test", new JsonWebToken(token).Issuer);
            Assert.Contains(log.Connections, c => c.StartsWith($"{IssuerA}:443 -> ", StringComparison.Ordinal));
        });

        // From the server: it can accept that token only by fetching idp-a.e2e.test's discovery
        // document and key set itself, under the same name, from inside the network. contract-005 · G-17 round 1
        // (HAR-R1-04) — a refusal is the server's answer, and the claim's; any other failure propagates as what it is.
        string? refused = null;
        try
        {
            await ListToolsAsync(http, token);
        }
        catch (Exception ex) when (Claim.IsTheServersAnswer(ex))
        {
            refused = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }

        var after = await TestIssuerService.CountsAsync(http, IssuerA, server);
        var why = refused is null ? string.Empty : $"; and it refused {IssuerA}'s token ({refused})";
        Claim.True(
            Count(after, IssuerA, "/.well-known/openid-configuration") > Count(before, IssuerA, "/.well-known/openid-configuration"),
            $"the server, {server}, fetched no discovery document from {IssuerA}; counts {JsonSerializer.Serialize(after)}{why}");
        Claim.True(
            Count(after, IssuerA, "/jwks") > Count(before, IssuerA, "/jwks"),
            $"the server, {server}, fetched no key set from {IssuerA}; counts {JsonSerializer.Serialize(after)}{why}");
        Claim.True(refused is null, $"the server fetched {IssuerA}'s discovery document and key set, and refused its token: {refused}");
    }

    [Fact]
    [Sabotage(AuthorizationServerOutside, SabotageActs.ContainerEnvironment,
        "The class's server names an authorization server outside the environment first: idp-a's Issuer, which the "
        + "protected-resource metadata lists, is https://idp-a.example.com, a name the test's name map does not hold.")]
    public async Task T1_the_sdk_clients_traffic_and_its_oauth_discovery_go_through_the_name_map()
    {
        // The SDK client on its own: no token, only OAuth options, so it must discover where to get
        // one. Whether the flow completes is T-4's claim; this test's claim is where the traffic went.
        // contract-005 · G-17 round 2 (HAR-R2-03) — the flow's end is this test's only when it is the server's answer, or the name
        // map refused where the flow went (which is this claim's to judge); any other failure propagates as what it is, and a
        // request no name of the environment answered is thrown as that fault. contract-005 · G-17 round 3 (HAR-R3-01, HAR-R3-04) —
        // through the harness's one run of the SDK's OAuth client, which reads the name map's log itself and counts a request a
        // name did not answer in the client's time: a hung or slow test issuer read here as "Filter not matched" on this claim.
        var flow = await StandardClient.RunAsync(
            log => fixture.CreateClient(log), ServerUnderTest.Endpoint, "e2e-standard-client", "t1: the sdk client's oauth flow",
            async client => await client.ListToolsAsync());
        var log = flow.Log;
        output.WriteLine(flow.Failure is { } ended
            ? $"The SDK client's OAuth attempt ended: {ended.GetType().Name}: {ended.Message.Split('\n')[0]}"
            : "The SDK client completed its OAuth flow.");

        output.WriteLine("Requests through the name map:");
        output.WriteLine(log.ToString());

        // It was challenged through the front, and fetched the protected-resource metadata the
        // challenge named — OAuth discovery, through the map, validated against the test CA.
        Claim.Holds(() =>
        {
            Assert.Contains(log.Requests, r => r.Method == HttpMethod.Post && r.Uri.Host == TlsFront.Host && r.Status == (int)HttpStatusCode.Unauthorized);
            Assert.Contains(log.Requests, r =>
                r.Method == HttpMethod.Get
                && r.Uri.Host == TlsFront.Host
                && r.Uri.AbsolutePath.StartsWith("/.well-known/oauth-protected-resource", StringComparison.Ordinal)
                && r.Status == (int)HttpStatusCode.OK);
        });

        // contract-005 · G-12 (1) — with the endpoint at the resource's URL, the flow goes on: to the
        // authorization server the metadata names first (read here through a client of its own, so
        // this read is not in the log), for its RFC 8414 metadata — the rest of its discovery. Its
        // /authorize and /token, and the front's answer to the token, are T-4's claim (StandardClientTests):
        // contract-005 · G-17 round 2 (HAR-R2-03) — this test claimed them as well, so a flow cut short by
        // anything was a red here too. Where the flow went is this test's: through the map, and nowhere else.
        string authorizationServer;
        using (var reader = fixture.CreateClient())
        {
            var metadata = await reader.GetFromJsonAsync<JsonElement>(new Uri($"https://{TlsFront.Host}/.well-known/oauth-protected-resource/mcp"));
            authorizationServer = new Uri(metadata.GetProperty("authorization_servers")[0].GetString()!).Host;
        }

        Claim.Holds(() =>
        {
            Assert.Contains(log.Requests, r => r.Uri.Host == authorizationServer && r.Uri.AbsolutePath == "/.well-known/oauth-authorization-server" && r.Status == (int)HttpStatusCode.OK);

            // Nothing it asked for lay outside the map, and it connected to exactly the names its flow
            // calls for: the front, and the authorization server.
            Assert.Empty(log.Refused);
            Assert.Equal(
                new[] { $"{authorizationServer}:443", $"{TlsFront.Host}:{TlsFront.Port}" }.Order(StringComparer.Ordinal),
                log.Connections.Select(c => c.Split(" -> ")[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        });
    }

    /// <summary>
    /// contract-005 · UC-1 — the front's forwarded address is honoured. The forwarded scheme is not asserted here: it is
    /// T-3's claim (TokenRefusalTests.T3_no_token_is_challenged_with_the_metadata_on_the_front). contract-005 · G-17 round 1
    /// (HAR-R1-05) — the fixture's forwarded-headers self-check proves only the environment's part, that the front sends
    /// both headers to this server and is a proxy the server was started trusting, and reads nothing of what the server does
    /// with them, so this claim and T-3's are the ones that go red when the server stops honouring them.
    /// </summary>
    [Fact]
    [Sabotage(SecondAsFirst, SabotageActs.Inputs,
        "The second address's request is forwarded as the first address instead, so it shares the count the first has spent.")]
    public async Task T1_the_fronts_forwarded_address_is_honoured()
    {
        // The server's per-address limit (60 a minute) counts by the address the front forwards. One
        // forwarded address is refused on its 61st request while another, arriving through the same
        // front at the same moment, is not. Were the forwarded address ignored, both would be the
        // front's own address and share one count.
        var first = ClientAddresses.Next();
        var second = ClientAddresses.Next();
        using var asFirst = fixture.CreateClient(clientAddress: first);
        using var asSecond = fixture.CreateClient(clientAddress: Sabotage.Choose(SecondAsFirst, second, first));
        var healthz = new Uri($"https://{TlsFront.Host}/healthz");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 61; i++)
        {
            using var response = await asFirst.GetAsync(healthz);
            statuses.Add(response.StatusCode);
        }

        using var other = await asSecond.GetAsync(healthz);

        // contract-005 · UC-1 edge — forwarded address not honoured is a fault of the environment (the
        // front, or KnownProxies) that looks like a product 429: with X-Forwarded-For lost, every
        // request through the front counts as the front's own address, so a fresh address is refused
        // for what others spent. The messages name that cause. Note for T-7: its per-address
        // assertions meet the same fault as a 429, and need the same message.
        var early = statuses.Take(60).Select((status, i) => (Status: status, Number: i + 1)).Where(s => s.Status != HttpStatusCode.OK).ToList();
        Claim.True(
            early.Count == 0,
            $"forwarded address not honoured: {early.Count} of the first 60 requests forwarded as the fresh address {first} were refused "
            + $"(first request {early.FirstOrDefault().Number}, with {(int)early.FirstOrDefault().Status}). A fresh address has a count "
            + "of its own only if the server honours the X-Forwarded-For the front sets; without it, every request through the front "
            + "shares the front's own count.");
        Claim.Holds(() => Assert.Equal(HttpStatusCode.TooManyRequests, statuses[60]));
        Claim.True(
            other.StatusCode == HttpStatusCode.OK,
            $"forwarded address not honoured: {second} got {(int)other.StatusCode} just after {first} spent its 60, so the server "
            + "counted both as one address — the front's own, not the ones it forwarded.");
    }

    /// <summary>
    /// contract-005 · G-9 — the negative control. The server trusts a forwarded scheme and address
    /// only because they arrive from KnownProxies. The same forged headers sent straight to its
    /// published port, from outside KnownProxies, change nothing: the challenge's metadata URL stays
    /// http, and requests each forging a different X-Forwarded-For share the one count of the address
    /// they really came from.
    ///
    /// Its sabotage is added beside the front, so the fixture's self-checks still pass and the red is this test's.
    /// </summary>
    [Fact]
    [Sabotage(GatewayTrusted, SabotageActs.ContainerEnvironment,
        "The class's server also trusts the run network's gateway (198.51.100.1), the address a request to a published port "
        + "arrives from, as a proxy: HttpTransport:KnownProxies:1, beside the front.")]
    public async Task T1_forwarded_headers_sent_straight_to_the_server_are_ignored()
    {
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

        using (var response = await direct.SendAsync(Forged(fixture.Server.DirectEndpoint, "192.0.2.1")))
        {
            // Positive control: past host filtering and the limiter, to authentication.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
            Claim.True(
                challenge.Contains($"resource_metadata=\"http://{TlsFront.Host}/", StringComparison.Ordinal),
                $"a forged X-Forwarded-Proto from outside KnownProxies was trusted: the challenge to a request sent straight to the "
                + $"server is '{challenge}', not an http URL on {TlsFront.Host}.");
        }

        // Honoured, each forged address would have a fresh count of 60 and none would be refused.
        // Ignored, they all spend the count of the one address they came from, and it runs out. Up to
        // 121 requests, so a window that turns over during the burst still runs out once.
        int? refusedAt = null;
        var sent = 0;
        while (refusedAt is null && sent < 121)
        {
            sent++;
            using var response = await direct.SendAsync(Forged(fixture.Server.DirectEndpoint, $"192.0.2.{sent + 1}"));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refusedAt = sent;
            }
        }

        Claim.True(
            refusedAt is not null,
            $"a forged X-Forwarded-For from outside KnownProxies was trusted: {sent} requests sent straight to the server, each "
            + "forging a different address, were never refused; the server counted each under the address it forged.");
        output.WriteLine($"Straight to the server, forging a different X-Forwarded-For each time, request {refusedAt} was refused (429).");
    }

    /// <summary>tools/list through the SDK client, with a bearer token and nothing else.</summary>
    private static async Task<IList<McpClientTool>> ListToolsAsync(HttpClient http, string token)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = ServerUnderTest.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            http,
            loggerFactory: null,
            ownsHttpClient: false);

        await using var client = await McpClient.CreateAsync(transport);
        return await client.ListToolsAsync();
    }

    /// <summary>
    /// An unauthenticated initialize sent to <paramref name="endpoint"/> under the one Host the server
    /// allows, forging what a proxy would forward: https, and <paramref name="forwardedFor"/>.
    /// </summary>
    private static HttpRequestMessage Forged(Uri endpoint, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Host = TlsFront.Host;
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static int Count(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> counts, string host, string path) =>
        counts.TryGetValue(host, out var paths) && paths.TryGetValue(path, out var n) ? n : 0;
}
