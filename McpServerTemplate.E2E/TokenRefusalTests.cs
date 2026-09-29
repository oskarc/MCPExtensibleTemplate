using System.Net;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-3 (G-6, G-11 · UC-3) — token refusals on the image.
///
/// No token is challenged with the metadata on https://mcp.e2e.test. Every token the test issuer mints wrong in one
/// way — for another audience, expired, unsigned, HMAC-signed, signed with another issuer's key, or missing sub, jti,
/// the client claim or iat — is refused with 401, and the server's log says why, in words that name that one fault and
/// never the token. A write tool called with a token issued six minutes ago is refused for the token's age. And a
/// token from an issuer no server is configured with costs no key lookup at any issuer: on a server started for that
/// test alone, whose issuers are both test issuers, it is the first token to arrive, and every issuer name — the
/// stranger's own among them — records no discovery and no key-set request from that server, until a registered
/// token makes its issuer's counts rise from zero.
///
/// The test issuer mints every token (G-6): Keycloak will not mint a broken one. The class's server enables the demo
/// provider, whose tools are the only ones that write, bound to the test issuer that can age a token.
/// </summary>
public sealed class TokenRefusalTests(TokenRefusalTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<TokenRefusalTests.Server>
{
    /// <summary>The environment's server with the demo provider enabled and bound to idp-a.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Set("Providers:Enabled:2", "JsonPlaceholder")
        .Set("Providers:JsonPlaceholder:IdentityProvider", E2EEnvironment.IdpA));

    private const string IssuerA = "idp-a.e2e.test";
    private const string IssuerB = "idp-b.e2e.test";
    private const string WriteTool = "create_user_todo";
    private const string AuthenticationFailed = "authn_login_fail";

    /// <summary>
    /// Sabotage (G-11): send the request straight to the server's published port instead of through the front. The
    /// server honours no forwarded scheme from there, its challenge names http://mcp.e2e.test/…, and the claim's
    /// assertion goes red.
    /// </summary>
    [Fact]
    public async Task T3_no_token_is_challenged_with_the_metadata_on_the_front()
    {
        using var http = fixture.CreateClient();
        using var response = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint));
        var named = McpRequests.ResourceMetadataOf(response);
        output.WriteLine($"{(int)response.StatusCode}, resource_metadata={named}");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized && named?.StartsWith($"https://{TlsFront.Host}/", StringComparison.Ordinal) == true,
            $"a request with no token got {(int)response.StatusCode} with resource_metadata={named ?? "(none)"}, not a 401 naming "
            + $"metadata on https://{TlsFront.Host}/.");
    }

    /// <summary>Each token wrong in one way, and the words in the server's log that say which.</summary>
    public static TheoryData<string, string?, string> BadTokens() => new()
    {
        // The token library's own reasons, by their stable codes.
        { "wrong-audience", null, "IDX10214" },
        { "expired", null, "IDX10223" },
        { "alg-none", null, "IDX10504" },
        { "hs256", null, "IDX10517" },
        { "cross-signed", null, "IDX10503" },

        // The product's own (IdentityRegistration, OnTokenValidated): each names the claim it missed.
        { "missing-claim", "sub", "missing sub," },
        { "missing-claim", "jti", "missing jti," },
        { "missing-claim", "client_id", "missing client_id (the client claim)" },
        { "missing-claim", "iat", "missing iat," },
    };

    /// <summary>
    /// Sabotage (G-11): mint the row's token with kind "valid" instead. The claim's assertion goes red on the status
    /// (200, not 401); with the status kept, a red on the reason is the log's own.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(string kind, string? claim, string reason)
    {
        using var http = fixture.CreateClient();

        // Positive control: this issuer's good token is accepted, so a refusal below is the flaw's.
        var good = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read"]);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, good));

        var extra = new Dictionary<string, object>();
        if (claim is not null)
        {
            extra["claim"] = claim;
        }

        if (kind == "cross-signed")
        {
            extra["signedBy"] = IssuerB;
        }

        var token = await TestIssuerService.MintAsync(http, IssuerA, kind, ServerUnderTest.Resource, ["weather:read"], extra);
        var before = (await fixture.Server.StderrLinesAsync(AuthenticationFailed)).Count;
        var status = await StatusAsync(http, token);
        var logged = await fixture.Server.StderrLinesAfterAsync(AuthenticationFailed, before);
        var what = claim is null ? kind : $"{kind} {claim}";
        output.WriteLine($"{what}: {(int)status}; logged: {string.Join(" | ", logged)}");

        Assert.True(
            status == HttpStatusCode.Unauthorized,
            $"a {what} token from {IssuerA} got {(int)status}, not 401.");
        Assert.True(
            logged.Count > 0 && logged.All(line => line.Contains(reason, StringComparison.Ordinal)),
            $"a {what} token was refused with 401, and the server's log {(logged.Count == 0 ? $"has no {AuthenticationFailed} line for it" : $"says '{string.Join(" | ", logged)}'")}, "
            + $"not its reason ('{reason}').");

        // Never the token: not whole, and not its claims.
        var stderr = await fixture.Server.StderrAsync();
        Assert.False(
            stderr.Contains(token, StringComparison.Ordinal) || stderr.Contains(token.Split('.')[1], StringComparison.Ordinal),
            $"the server's log holds the {what} token it refused.");
    }

    /// <summary>
    /// Sabotage (G-11): mint the stale token issued 60 seconds ago instead of 360. It passes the write gate, and the
    /// claim's assertion goes red: the call is not refused with token-age.
    /// </summary>
    [Fact]
    public async Task T3_a_write_tool_called_with_a_token_issued_six_minutes_ago_is_refused_for_its_age()
    {
        using var http = fixture.CreateClient();
        var server = await fixture.Server.NetworkAddressAsync();
        var arguments = new Dictionary<string, object> { ["userId"] = 1, ["title"] = $"e2e-{Guid.NewGuid():N}", ["completed"] = false };

        // The write tool is on the image's startup line, as a Write tool.
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        Assert.Contains($"tool:JsonPlaceholder/{WriteTool}:demo:write:Write", line.Manifest);

        // Positive control, and the witness seen active: a token issued now reaches the tool, which calls its upstream.
        var fresh = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["demo:write"]);
        var ran = await CallAsync(http, fresh, arguments);
        var upstream = await UpstreamPostsAsync(http, server);
        Assert.True(
            McpRequests.RuleOf(McpRequests.TextOf(ran)) is null && upstream > 0,
            $"a token issued now did not reach {WriteTool}: it answered '{McpRequests.TextOf(ran)}', and the fake recorded {upstream} "
            + "POSTs from this server.");

        var stale = await TestIssuerService.MintAsync(
            http, IssuerA, "stale-iat", ServerUnderTest.Resource, ["demo:write"], new Dictionary<string, object> { ["issuedSecondsAgo"] = 360 });
        var issued = DateTimeOffset.UtcNow - new JsonWebToken(stale).IssuedAt;
        var refused = await CallAsync(http, stale, arguments);
        var text = McpRequests.TextOf(refused);
        output.WriteLine($"A token issued {issued.TotalSeconds:0} s ago: {text}");

        Assert.True(
            McpRequests.RuleOf(text) == "token-age" && !McpRequests.ReturnsContent(refused),
            $"{WriteTool} called with a token issued {issued.TotalSeconds:0} s ago answered '{text}', not a token-age refusal with no content.");
        Assert.Equal(upstream, await UpstreamPostsAsync(http, server));
    }

    /// <summary>
    /// Sabotage (G-11): the server's delta also registers the stranger as an identity provider
    /// (Authentication:IdentityProviders:stranger, its Authority and Issuer https://stranger.e2e.test). Its token is then
    /// routed to a scheme of its own, which fetches the stranger's discovery document and key set, and the claim's
    /// assertion — no lookup at any issuer name — goes red, before the status is looked at.
    /// </summary>
    [Fact]
    public async Task T3_an_unregistered_issuers_token_costs_no_key_lookup_at_any_issuer()
    {
        var environment = fixture.Environment;
        var stranger = E2EEnvironment.Issuers.StrangerNamed(E2EEnvironment.Stranger);

        // A server of this test's own, whose issuers are both test issuers: Keycloak, which counts nothing, is not one.
        await using var server = await environment.StartServerAsync(
            "token-refusals-stranger",
            SettingsDelta.None
                .Remove($"Authentication:IdentityProviders:{E2EEnvironment.KeycloakIssuer}")
                .Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA)
                .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpA));
        using var http = server.CreateClient(ClientAddresses.Next());
        var address = await server.NetworkAddressAsync();
        var names = E2EEnvironment.Issuers.HostsServedBy(IssuerRegistry.Owner.TestIssuer);

        // Cold: nothing this server has done so far asked any issuer for anything.
        var cold = await TestIssuerService.CountsAsync(http, IssuerA, address);
        Assert.True(
            names.All(name => Lookups(cold, name) == 0),
            $"the server was not fresh: it had asked the issuers for {JsonSerializer.Serialize(cold)} before any token arrived.");

        // The stranger's token is the first token the server sees.
        var token = await TestIssuerService.MintAsync(http, stranger.Host, "valid", ServerUnderTest.Resource, ["weather:read"]);
        Assert.Equal(stranger.Issuer, new JsonWebToken(token).Issuer);
        HttpStatusCode status;
        using (var refused = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, token)))
        {
            status = refused.StatusCode;
        }

        var after = await TestIssuerService.CountsAsync(http, IssuerA, address);
        output.WriteLine($"After the stranger's token ({(int)status}), from {address}: {JsonSerializer.Serialize(after)}");
        Assert.True(
            names.All(name => after.ContainsKey(name) && Lookups(after, name) == 0),
            $"a token from {stranger.Issuer}, which no server is configured with, made this server ask the issuers for "
            + $"{JsonSerializer.Serialize(after)}; every issuer name ({string.Join(", ", names)}) should show no discovery and no key-set request.");
        Assert.Equal(HttpStatusCode.Unauthorized, status);

        // The witness is live: a registered issuer's token makes that issuer's counts rise from zero.
        var registered = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read"]);
        using (var accepted = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, registered)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var raised = await TestIssuerService.CountsAsync(http, IssuerA, address);
        output.WriteLine($"After {IssuerA}'s token: {JsonSerializer.Serialize(raised)}");
        Assert.True(
            Count(raised, IssuerA, "/.well-known/openid-configuration") > 0 && Count(raised, IssuerA, "/jwks") > 0,
            $"{IssuerA}'s own token did not raise its counts from zero ({JsonSerializer.Serialize(raised)}): the witness saw nothing.");
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient http, string token)
    {
        using var response = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, token));
        return response.StatusCode;
    }

    private static Task<JsonElement> CallAsync(HttpClient http, string token, IReadOnlyDictionary<string, object> arguments) =>
        McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, "tools/call", new { name = WriteTool, arguments }));

    /// <summary>The POSTs the fake recorded from <paramref name="server"/>: the write tool's upstream requests.</summary>
    private static async Task<int> UpstreamPostsAsync(HttpClient http, IPAddress server) =>
        (await WireMockService.EntriesAsync(http)).Count(e => server.Equals(e.Client) && e.Method == "POST" && e.Path == "/todos");

    private static int Lookups(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> counts, string host) =>
        Count(counts, host, "/.well-known/openid-configuration") + Count(counts, host, "/jwks");

    private static int Count(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> counts, string host, string path) =>
        counts.TryGetValue(host, out var paths) && paths.TryGetValue(path, out var n) ? n : 0;
}
