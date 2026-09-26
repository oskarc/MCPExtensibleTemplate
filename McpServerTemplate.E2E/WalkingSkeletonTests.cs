using System.Net;
using System.Text;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Microsoft.IdentityModel.JsonWebTokens;
using ModelContextProtocol.Authentication;
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
/// scheme and client address are honoured by the server.
///
/// A fault in the environment rather than the product fails with an <see cref="EnvironmentFaultException"/>
/// naming its phase and cause, never as one of these assertions.
/// </summary>
public sealed class WalkingSkeletonTests(WalkingSkeletonTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<WalkingSkeletonTests.Server>
{
    /// <summary>The server exactly as the environment configures it: no delta.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None);

    private const string IssuerA = "idp-a.e2e.test";

    [Fact]
    public async Task T1_a_keycloak_token_lists_tools_through_the_front()
    {
        var timings = E2EEnvironment.Timings;
        try
        {
            var token = await timings.MeasureAsync("t1: keycloak client-credentials token", () => fixture.KeycloakTokenAsync());

            var log = new NameMapLog();
            var tools = await timings.MeasureAsync("t1: tools/list through the front", async () =>
            {
                using var http = fixture.CreateClient(log);
                return await ListToolsAsync(http, token);
            });

            // The token came from the real identity provider, for this server's resource.
            var claims = new JsonWebToken(token);
            Assert.Equal(KeycloakService.Issuer, claims.Issuer);
            Assert.Contains(ServerUnderTest.Resource, claims.Audiences);

            // The claim: the call succeeded, and what came back is what the image says it serves.
            var startupLine = await fixture.Server.StartupLineAsync();
            Assert.NotEmpty(tools);
            Assert.All(tools, tool => Assert.Contains($"/{tool.Name}:", startupLine, StringComparison.Ordinal));
            Assert.Contains(log.Requests, r => r.Uri.Host == TlsFront.Host && r.Status == (int)HttpStatusCode.OK);

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

    [Fact]
    public async Task T1_the_test_issuer_is_one_issuer_by_one_name_from_the_server_and_from_the_test()
    {
        var log = new NameMapLog();
        using var http = fixture.CreateClient(log);

        var before = await TestIssuerService.CountsAsync(http, IssuerA);

        // From the test: minted through idp-a.e2e.test's own door, over TLS the test CA validates.
        var token = await E2EEnvironment.Timings.MeasureAsync("t1: mint at the test issuer", () =>
            TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read"]));
        Assert.Equal("https://idp-a.e2e.test", new JsonWebToken(token).Issuer);
        Assert.Contains(log.Connections, c => c.StartsWith($"{IssuerA}:443 -> ", StringComparison.Ordinal));

        // From the server: it can accept that token only by fetching idp-a.e2e.test's discovery
        // document and key set itself, under the same name, from inside the network.
        await ListToolsAsync(http, token);

        var after = await TestIssuerService.CountsAsync(http, IssuerA);
        Assert.True(
            Count(after, IssuerA, "/.well-known/openid-configuration") > Count(before, IssuerA, "/.well-known/openid-configuration"),
            $"the server fetched no discovery document from {IssuerA}; counts {JsonSerializer.Serialize(after)}");
        Assert.True(
            Count(after, IssuerA, "/jwks") > Count(before, IssuerA, "/jwks"),
            $"the server fetched no key set from {IssuerA}; counts {JsonSerializer.Serialize(after)}");
    }

    [Fact]
    public async Task T1_the_sdk_clients_traffic_and_its_oauth_discovery_go_through_the_name_map()
    {
        var log = new NameMapLog();
        using var http = fixture.CreateClient(log);

        // The SDK client on its own: no token, only OAuth options, so it must discover where to get
        // one. Whether the flow completes is T-4's claim; this test's claim is where the traffic went.
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = ServerUnderTest.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                OAuth = new ClientOAuthOptions
                {
                    ClientId = "e2e-standard-client",
                    RedirectUri = new Uri("http://localhost/callback"),
                    AuthorizationCallbackHandler = (context, cancellationToken) => AuthorizeAsync(http, context.AuthorizationUri, cancellationToken),
                },
            },
            http,
            loggerFactory: null,
            ownsHttpClient: false);

        try
        {
            await using var client = await McpClient.CreateAsync(transport);
            await client.ListToolsAsync();
            output.WriteLine("The SDK client completed its OAuth flow.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine($"The SDK client's OAuth attempt ended: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }

        output.WriteLine("Requests through the name map:");
        output.WriteLine(log.ToString());

        // It was challenged through the front, and fetched the protected-resource metadata the
        // challenge named — OAuth discovery, through the map, validated against the test CA.
        Assert.Contains(log.Requests, r => r.Method == HttpMethod.Post && r.Uri.Host == TlsFront.Host && r.Status == (int)HttpStatusCode.Unauthorized);
        Assert.Contains(log.Requests, r =>
            r.Method == HttpMethod.Get
            && r.Uri.Host == TlsFront.Host
            && r.Uri.AbsolutePath.StartsWith("/.well-known/oauth-protected-resource", StringComparison.Ordinal)
            && r.Status == (int)HttpStatusCode.OK);

        // Nothing it asked for lay outside the map.
        Assert.Empty(log.Refused);
        Assert.All(log.Connections, c => Assert.Contains(c.Split(':')[0], fixture.Server.Names.Entries.Keys.Select(k => k.Host)));
    }

    [Fact]
    public async Task T1_the_fronts_forwarded_scheme_and_address_are_honoured()
    {
        // Scheme: the server builds its challenge's metadata URL from the request's scheme and Host;
        // it is https only because the front's X-Forwarded-Proto was trusted.
        using (var http = fixture.CreateClient())
        using (var response = await http.SendAsync(Initialize()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
            Assert.Contains($"resource_metadata=\"https://{TlsFront.Host}/", challenge, StringComparison.Ordinal);
        }

        // Address: the server's per-address limit (60 a minute) counts by the address the front
        // forwards. One forwarded address is refused on its 61st request while another, arriving
        // through the same front at the same moment, is not. Were the forwarded address ignored,
        // both would be the front's own address and share one count.
        var first = ClientAddresses.Next();
        var second = ClientAddresses.Next();
        using var asFirst = fixture.CreateClient(clientAddress: first);
        using var asSecond = fixture.CreateClient(clientAddress: second);
        var healthz = new Uri($"https://{TlsFront.Host}/healthz");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 61; i++)
        {
            using var response = await asFirst.GetAsync(healthz);
            statuses.Add(response.StatusCode);
        }

        using var other = await asSecond.GetAsync(healthz);

        Assert.All(statuses.Take(60), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[60]);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
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
    /// What a person's browser would do at the authorization endpoint: follow the URL, and hand the
    /// redirect's code, state and iss back to the client. Through the name map like everything else.
    /// </summary>
    private static async Task<AuthorizationResult?> AuthorizeAsync(HttpClient http, Uri authorizationUri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(authorizationUri, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location is not { } location)
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
    }

    private static HttpRequestMessage Initialize()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ServerUnderTest.Endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    private static int Count(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> counts, string host, string path) =>
        counts.TryGetValue(host, out var paths) && paths.TryGetValue(path, out var n) ? n : 0;
}
