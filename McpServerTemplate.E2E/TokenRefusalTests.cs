using System.Net;
using System.Net.Http.Json;
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

    private const string SentStraightToTheServer = "t3-no-token-sent-straight-to-the-server";
    private const string BadTokenMintedValid = "t3-bad-token-minted-valid";
    private const string KeyConfusionMintedValid = "t3-key-confusion-token-minted-valid";
    private const string StaleTokenIssuedAMinuteAgo = "t3-stale-token-issued-a-minute-ago";
    private const string StrangerRegistered = "t3-stranger-registered-on-the-server";

    [Fact]
    [Sabotage(SentStraightToTheServer, SabotageActs.Network,
        "The request goes straight to the server's published port instead of through the front, so no forwarded scheme reaches "
        + "the server from a proxy it trusts.")]
    public async Task T3_no_token_is_challenged_with_the_metadata_on_the_front()
    {
        using var http = fixture.CreateClient();
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var straight = Sabotage.Applies(SentStraightToTheServer);
        using var request = McpRequests.Initialize(straight ? fixture.Server.DirectEndpoint : ServerUnderTest.Endpoint);
        if (straight)
        {
            request.Headers.Host = TlsFront.Host;
        }

        using var response = await (straight ? direct : http).SendAsync(request);
        var named = McpRequests.ResourceMetadataOf(response);
        output.WriteLine($"{(int)response.StatusCode}, resource_metadata={named}");

        Claim.True(
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

    [Theory]
    [MemberData(nameof(BadTokens))]
    [Sabotage(BadTokenMintedValid, SabotageActs.Inputs, "The row's token is minted as kind valid instead of its own, wrong in no way.")]
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

        var token = await TestIssuerService.MintAsync(http, IssuerA, Sabotage.Choose(BadTokenMintedValid, kind, "valid"), ServerUnderTest.Resource, ["weather:read"], extra);
        var before = (await fixture.Server.StderrLinesAsync(AuthenticationFailed)).Count;
        var status = await StatusAsync(http, token);
        var logged = await fixture.Server.StderrLinesAfterAsync(AuthenticationFailed, before);
        var what = claim is null ? kind : $"{kind} {claim}";
        output.WriteLine($"{what}: {(int)status}; logged: {string.Join(" | ", logged)}");

        Claim.True(
            status == HttpStatusCode.Unauthorized,
            $"a {what} token from {IssuerA} got {(int)status}, not 401.");
        Claim.True(
            logged.Count > 0 && logged.All(line => line.Contains(reason, StringComparison.Ordinal)),
            $"a {what} token was refused with 401, and the server's log {(logged.Count == 0 ? $"has no {AuthenticationFailed} line for it" : $"says '{string.Join(" | ", logged)}'")}, "
            + $"not its reason ('{reason}').");

        // Never the token: not whole, and not its claims.
        var stderr = await fixture.Server.StderrAsync();
        Claim.True(
            !(stderr.Contains(token, StringComparison.Ordinal) || stderr.Contains(token.Split('.')[1], StringComparison.Ordinal)),
            $"the server's log holds the {what} token it refused.");
    }

    /// <summary>
    /// contract-005 · T-3 (G-6), strengthened — the algorithm-confusion attack. The HS256 row above carries no key id and
    /// is refused for that (IDX10517) before its algorithm is weighed. This token is HS256 signed with idp-a's own public
    /// key — the PEM anyone can derive from idp-a's key set — as the HMAC secret, under the key id idp-a publishes, every
    /// claim valid: a verifier that let the token's alg choose how to use the key its kid names would accept it. It is
    /// refused with 401, the refusal is logged, and the token is not. The contract asks only that each refusal's reason be
    /// logged (T-3): the reason logged for this one is the signature's (IDX10511), which is true, and names no algorithm.
    /// </summary>
    [Fact]
    [Sabotage(KeyConfusionMintedValid, SabotageActs.Inputs, "The token is minted as kind valid instead: idp-a's own token, RS256, every claim valid.")]
    public async Task T3_a_key_confusion_token_is_refused_and_its_refusal_logged()
    {
        using var http = fixture.CreateClient();

        // Positive control: this issuer's good token is accepted, so a refusal below is the attack's.
        var good = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read"]);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, good));

        var (token, status, logged) = await KeyConfusionAsync(http, Sabotage.Choose(KeyConfusionMintedValid, "key-confusion", "valid"));

        Claim.True(
            status == HttpStatusCode.Unauthorized && logged.Count > 0,
            $"an HS256 token keyed with {IssuerA}'s own public key, under its real key id, got {(int)status}"
            + (logged.Count == 0 ? $", and the server's log has no {AuthenticationFailed} line for it." : "."));

        var stderr = await fixture.Server.StderrAsync();
        Claim.True(
            !(stderr.Contains(token, StringComparison.Ordinal) || stderr.Contains(token.Split('.')[1], StringComparison.Ordinal)),
            "the server's log holds the key-confusion token it refused.");
    }

    [Fact]
    [Sabotage(StaleTokenIssuedAMinuteAgo, SabotageActs.Inputs,
        "The stale token is minted as issued 60 seconds ago instead of 360, inside the write gate's five minutes.")]
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
            http, IssuerA, "stale-iat", ServerUnderTest.Resource, ["demo:write"],
            new Dictionary<string, object> { ["issuedSecondsAgo"] = Sabotage.Choose(StaleTokenIssuedAMinuteAgo, 360, 60) });
        var issued = DateTimeOffset.UtcNow - new JsonWebToken(stale).IssuedAt;
        var refused = await CallAsync(http, stale, arguments);
        var text = McpRequests.TextOf(refused);
        output.WriteLine($"A token issued {issued.TotalSeconds:0} s ago: {text}");

        Claim.True(
            McpRequests.RuleOf(text) == "token-age" && !McpRequests.ReturnsContent(refused),
            $"{WriteTool} called with a token issued {issued.TotalSeconds:0} s ago answered '{text}', not a token-age refusal with no content.");
        var after = await UpstreamPostsAsync(http, server);
        Claim.Holds(() => Assert.Equal(upstream, after));
    }

    [Fact]
    [Sabotage(StrangerRegistered, SabotageActs.ContainerEnvironment,
        "The test's server also registers the stranger as an identity provider (Authentication:IdentityProviders:stranger, "
        + "Authority and Issuer https://stranger.e2e.test, as the issuer registry registers every provider), so its token is "
        + "routed to a scheme of its own, which fetches keys.")]
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
                .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpA)
                .Sabotaged(StrangerRegistered, d => new IssuerRegistry()
                    .Register(new(E2EEnvironment.Stranger, stranger.Authority, stranger.Issuer, stranger.ServedBy, "client_id", KeycloakService.Scopes))
                    .ToSettings()
                    .Aggregate(d, (delta, setting) => delta.Set(setting.Key, setting.Value))));
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
        Claim.True(
            names.All(name => after.ContainsKey(name) && Lookups(after, name) == 0),
            $"a token from {stranger.Issuer}, which no server is configured with, made this server ask the issuers for "
            + $"{JsonSerializer.Serialize(after)}; every issuer name ({string.Join(", ", names)}) should show no discovery and no key-set request.");
        Claim.Holds(() => Assert.Equal(HttpStatusCode.Unauthorized, status));

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

    /// <summary>
    /// contract-005 · T-3 — a token of <paramref name="kind"/> from idp-a, sent once: the token, the status it got, and the
    /// refusal lines the server logged for it. A self-check first holds the key-confusion token to what the attack is:
    /// HS256, under the key id idp-a publishes in its key set.
    /// </summary>
    private async Task<(string Token, HttpStatusCode Status, IReadOnlyList<string> Logged)> KeyConfusionAsync(HttpClient http, string kind)
    {
        var token = await TestIssuerService.MintAsync(http, IssuerA, kind, ServerUnderTest.Resource, ["weather:read"]);
        if (kind == "key-confusion")
        {
            var keys = await http.GetFromJsonAsync<JsonElement>(new Uri($"https://{IssuerA}/jwks"));
            var published = keys.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();
            var header = new JsonWebToken(token);
            Assert.True(
                header.Alg == "HS256" && published.Contains(header.Kid),
                $"the key-confusion token is not the attack's: alg {header.Alg}, kid {header.Kid}; {IssuerA} publishes [{string.Join(", ", published)}].");
        }

        var before = (await fixture.Server.StderrLinesAsync(AuthenticationFailed)).Count;
        var status = await StatusAsync(http, token);
        var logged = await fixture.Server.StderrLinesAfterAsync(AuthenticationFailed, before);
        output.WriteLine($"{kind}: {(int)status}; logged: {string.Join(" | ", logged)}");
        return (token, status, logged);
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
