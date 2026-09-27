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
/// </summary>
public sealed class StandardClientTests(StandardClientTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<StandardClientTests.Server>
{
    /// <summary>The weather provider bound to idp-a, the authorization server a standard client picks first.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None.Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA));

    private const string IssuerA = "idp-a.e2e.test";
    private const string Tool = "get_forecast_model_info";

    /// <summary>The upstream request that tool makes: SMHI's point forecast for Stockholm.</summary>
    private const string ToolUpstreamRequest = "/api/category/snow1g/version/1/geotype/point/lon/18.070000/lat/59.330000/data.json";

    [Fact]
    public async Task T4_the_sdk_client_given_only_the_resource_url_discovers_authorizes_and_calls_a_tool()
    {
        // Unique to this run, so the issuer's record of /authorize and /token is this test's alone.
        var clientId = $"e2e-standard-client-{Guid.NewGuid():N}";
        var endpoint = new Uri(ServerUnderTest.Resource);
        var log = new NameMapLog();
        using var http = fixture.CreateClient(log);

        var upstreamBefore = await WireMockService.CountAsync(http, ToolUpstreamRequest);

        CallToolResult? result = null;
        Exception? failure = null;
        try
        {
            await using var client = await McpClient.CreateAsync(StandardClient.Transport(http, endpoint, clientId));
            result = await client.CallToolAsync(Tool);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
        }

        output.WriteLine($"Requests through the name map:{Environment.NewLine}{log}");

        // The claim: the client, given only the URL, called a tool.
        Assert.True(
            failure is null,
            $"the SDK client, given only {endpoint}, did not call {Tool}: {failure?.GetType().Name}: {failure?.Message.Split('\n')[0]} "
            + $"Requests it made: {log.ToString().Replace(Environment.NewLine, " ;", StringComparison.Ordinal)}");

        // It was the tool that answered, not the frame refusing: a refusal names its rule.
        var text = string.Join(" ", result!.Content.OfType<TextContentBlock>().Select(c => c.Text));
        output.WriteLine($"{Tool} answered (isError={result.IsError}): {text}");
        Assert.DoesNotContain("rule:", text, StringComparison.Ordinal);
        Assert.True(
            await WireMockService.CountAsync(http, ToolUpstreamRequest) > upstreamBefore,
            $"{Tool} answered '{text}', but the fake recorded no request from it: the tool did not run.");

        // The flow, as the authorization server saw it: one authorization request and one token
        // request from this client, each for this resource and with S256.
        var seen = (await TestIssuerService.AuthorizationsAsync(http, IssuerA))
            .GetValueOrDefault(IssuerA, [])
            .Where(r => r.ClientId == clientId)
            .ToList();
        output.WriteLine($"{IssuerA} saw: {string.Join("; ", seen)}");

        var authorize = Assert.Single(seen, r => r.Path == "/authorize");
        var token = Assert.Single(seen, r => r.Path == "/token");
        Assert.Equal((ServerUnderTest.Resource, "S256", "approved"), (authorize.Resource, authorize.ChallengeMethod, authorize.Outcome));
        Assert.Equal((ServerUnderTest.Resource, "S256", "issued"), (token.Resource, token.ChallengeMethod, token.Outcome));
    }
}
