using McpServerTemplate.E2E.Harness;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-4 (G-6, 9, 12 · UC-4) — a standard client connects.
///
/// The MCP SDK's own OAuth client is given https://mcp.e2e.test/mcp and nothing else: no token and no
/// extra header, only what every OAuth client has (a client id, a redirect URI, a browser). From the
/// server's 401 it finds the protected-resource metadata, from that the authorization server, and
/// completes the authorization-code flow with PKCE at the test issuer's auto-approving endpoint, then
/// calls a tool. The test issuer is the witness of the flow: its /authorize and /token each saw
/// exactly one request from this client, carrying resource=https://mcp.e2e.test/mcp and S256.
///
/// Where the client goes is the server's to say: the default client takes the first authorization
/// server the metadata lists, which is idp-a (identity providers are listed in configuration order).
/// On this class's server the weather provider answers to idp-a, so the token the client gets there
/// is one the tool will take. The fake has no forecast stubbed, so the tool answers with its own
/// error; the call is shown to have reached the tool by the fake's record of the upstream request.
///
/// contract-005 · G-17 round 2 (HAR-R2-01) — this server's upstream request: read from the owner of the forecast host, from this
/// server's address since it started, beyond what it held before the flow began. The fake's journal was counted for every
/// server, and two other classes fetch the same Stockholm forecast in parallel, as the saved runs show them doing inside this
/// test's window; a server that never ran the tool passed on theirs. The fake is left without a forecast: a stub for that path
/// would answer every class that asks it, and the tool's own answer is not the claim — that it ran and reached its upstream is.
/// contract-005 · G-17 round 2 (HAR-R2-03) — and a flow that failed because a name of the environment did not answer (the
/// test issuer stopped, or its certificate untrusted) is that environment fault, named, never the claim's red.
/// </summary>
public sealed class StandardClientTests(StandardClientTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<StandardClientTests.Server>
{
    /// <summary>The weather provider bound to idp-a, the authorization server a standard client picks first.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None.Set(
        "Providers:Smhi:IdentityProvider", Sabotage.Choose(WeatherLeftToKeycloak, E2EEnvironment.IdpA, E2EEnvironment.KeycloakIssuer)));

    private const string IssuerA = "idp-a.e2e.test";
    private const string WeatherLeftToKeycloak = "t4-weather-left-bound-to-keycloak";
    private const string Tool = "get_forecast_model_info";

    /// <summary>The host that tool's provider calls.</summary>
    private const string ForecastHost = "opendata-download-metfcst.smhi.se";

    /// <summary>The upstream request that tool makes: SMHI's point forecast for Stockholm.</summary>
    private const string ToolUpstreamRequest = "/api/category/snow1g/version/1/geotype/point/lon/18.070000/lat/59.330000/data.json";

    [Fact]
    [Sabotage(WeatherLeftToKeycloak, SabotageActs.ContainerEnvironment,
        "The class's server leaves the weather provider bound to Keycloak, as the environment has it, rather than to idp-a, the "
        + "authorization server the client is sent to first: the client's token is from the other trust domain.")]
    public async Task T4_the_sdk_client_given_only_the_resource_url_discovers_authorizes_and_calls_a_tool()
    {
        // Unique to this run, so the issuer's record of /authorize and /token is this test's alone.
        var clientId = $"e2e-standard-client-{Guid.NewGuid():N}";
        var endpoint = new Uri(ServerUnderTest.Resource);
        var log = new NameMapLog();
        using var http = fixture.CreateClient(log);

        // contract-005 · G-17 round 2 (HAR-R2-01) — the witness, read for this server alone, through a client of its own so the
        // name map's log holds the client's flow and nothing else.
        using var witness = fixture.CreateClient();
        var server = await fixture.Server.RequestSourceAsync();
        var owner = fixture.Environment.UpstreamFor(ForecastHost);
        var upstreamBefore = (await ProviderCalls.FromServerAsync(owner, witness, server, ForecastHost, ToolUpstreamRequest)).Count;

        CallToolResult? result = null;
        Exception? failure = null;
        try
        {
            await using var client = await McpClient.CreateAsync(StandardClient.Transport(http, endpoint, clientId));
            result = await client.CallToolAsync(Tool);
        }
        catch (Exception ex) when (Claim.IsTheServersAnswer(ex))
        {
            // contract-005 · G-17 round 1 (HAR-R1-04) — the claim's failures only: a status the server answered, or an MCP
            // error (the SDK's own, when the flow the server's metadata sent it on could not be completed). A connection or
            // TLS failure, or an environment fault, propagates as what it is. contract-005 · G-17 round 2 (HAR-R2-03) — and the
            // SDK's OAuth client throws its own McpException for a connection or TLS failure at the authorization server, so the
            // name map's log is read first: a request no name of the environment answered is thrown as that fault.
            log.ThrowIfAMappedNameGaveNoAnswer("t4: the standard client's flow", ex);
            failure = ex;
        }

        output.WriteLine($"Requests through the name map:{Environment.NewLine}{log}");

        // The claim: the client, given only the URL, called a tool.
        Claim.True(
            failure is null,
            $"the SDK client, given only {endpoint}, did not call {Tool}: {failure?.GetType().Name}: {failure?.Message.Split('\n')[0]} "
            + $"Requests it made: {log.ToString().Replace(Environment.NewLine, " ;", StringComparison.Ordinal)}");

        // It was the tool that answered, not the frame refusing: a refusal names its rule.
        var text = string.Join(" ", result!.Content.OfType<TextContentBlock>().Select(c => c.Text));
        output.WriteLine($"{Tool} answered (isError={result.IsError}): {text}");
        Claim.Holds(() => Assert.DoesNotContain("rule:", text, StringComparison.Ordinal));
        var ran = await ProviderCalls.SeenSinceAsync(owner, witness, server, ForecastHost, upstreamBefore, ToolUpstreamRequest);
        Claim.True(
            ran > 0,
            $"{Tool} answered '{text}', but {owner.Container.Name.TrimStart('/')}, which answers {ForecastHost}, recorded no request for "
            + $"{ToolUpstreamRequest} from this server, {server}, after the flow began: the tool did not run.");

        // The flow, as the authorization server saw it: one authorization request and one token
        // request from this client, each for this resource and with S256.
        var seen = (await TestIssuerService.AuthorizationsAsync(http, IssuerA))
            .GetValueOrDefault(IssuerA, [])
            .Where(r => r.ClientId == clientId)
            .ToList();
        output.WriteLine($"{IssuerA} saw: {string.Join("; ", seen)}");

        Claim.Holds(() =>
        {
            var authorize = Assert.Single(seen, r => r.Path == "/authorize");
            var token = Assert.Single(seen, r => r.Path == "/token");
            Assert.Equal((ServerUnderTest.Resource, "S256", "approved"), (authorize.Resource, authorize.ChallengeMethod, authorize.Outcome));
            Assert.Equal((ServerUnderTest.Resource, "S256", "issued"), (token.Resource, token.ChallengeMethod, token.Outcome));
        });
    }
}
