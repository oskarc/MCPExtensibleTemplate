using System.Net;
using Microsoft.IdentityModel.Tokens;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-2, T-4, T-5 (G-1, G-3) — the six ways a token is refused, the unregistered
/// issuer that must cost no key lookup, and the token that names one issuer while another signed
/// it.
///
/// These are the guarantees that could not be checked at all until the server could be composed
/// in this process: a spawned server fetches its keys from a real authority, so no test could
/// mint a token it would accept, and "accepted" is half of what "refused" means.
/// </summary>
public class TokenRejectionTests
{
    private const string Resource = "https://mcp.example.com/mcp";

    private static TestIdentityProvider Corp() => new("corp", "https://login.corp.test/");

    [Fact]
    public async Task T2_a_good_token_is_accepted()
    {
        // The control. Without it the six refusals below prove only that this server refuses
        // everything, which any broken server also does.
        using var corp = Corp();
        await using var server = await InProcessServer.StartAsync([corp]);

        using var response = await server.PostAsync(corp.MintToken(Resource));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> RejectionCases() =>
    [
        "wrong audience", "wrong issuer", "expired", "unsigned", "hmac signed", "no jti",

        // contract-002 revision, 2026-09-20 — roadmap P1.1 requires sub, jti, client_id (or azp)
        // and iat. Only jti was enforced, so a token naming no subject and no client was accepted
        // and became an audit entry attributable to nobody.
        "no sub", "no client_id", "no iat",
    ];

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public async Task T2_a_bad_token_is_refused(string flaw)
    {
        using var corp = Corp();
        using var elsewhere = new TestIdentityProvider("elsewhere", "https://login.elsewhere.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var token = flaw switch
        {
            "wrong audience" => corp.MintToken("https://another-server.example/mcp"),
            "wrong issuer" => corp.MintToken(Resource, issuer: "https://impostor.test/"),
            "expired" => corp.MintToken(
                Resource,
                notBefore: DateTime.UtcNow.AddHours(-2),
                expires: DateTime.UtcNow.AddHours(-1)),
            "unsigned" => UnsignedToken(corp),
            "hmac signed" => corp.MintHmacToken(Resource),
            "no jti" => corp.MintToken(Resource, jti: null),
            "no sub" => corp.MintToken(Resource, subject: null),
            "no client_id" => corp.MintToken(Resource, clientId: null),
            "no iat" => corp.MintTokenWithout("iat", Resource),
            _ => throw new ArgumentOutOfRangeException(nameof(flaw), flaw, "unknown case"),
        };

        using var response = await server.PostAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(elsewhere.Name));
    }

    /// <summary>A token whose header says alg=none, which is the "no signature at all" case.</summary>
    private static string UnsignedToken(TestIdentityProvider idp)
    {
        var signed = idp.MintToken(Resource);
        var parts = signed.Split('.');
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        return $"{header}.{parts[1]}.";
    }

    [Fact]
    public async Task T4_an_unregistered_issuer_is_refused_without_any_key_lookup()
    {
        // The rule is "before any key lookup", and the only way to see a lookup that did not
        // happen is to be the thing that would have served it. An unknown issuer must not be
        // able to make this server fetch a document from a host of the caller's choosing.
        using var corp = Corp();
        using var stranger = new TestIdentityProvider("stranger", "https://stranger.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var before = corp.JwksRequests;

        using var response = await server.PostAsync(stranger.MintToken(Resource));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, stranger.TotalRequests);
        Assert.Equal(before, corp.JwksRequests);
    }

    [Fact]
    public async Task T5_a_token_naming_one_issuer_but_signed_by_another_is_refused()
    {
        // Routing reads iss without validating it, which is safe only because the scheme it
        // routes to pins that issuer and checks the signature. This is the test of that "only
        // because": the token is routed to corp and must die there.
        using var corp = Corp();
        using var impostor = new TestIdentityProvider("impostor", "https://login.corp.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var token = impostor.MintToken(Resource, issuer: corp.Issuer);

        using var response = await server.PostAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task T2_an_oversized_token_is_refused_before_it_is_parsed()
    {
        using var corp = Corp();
        await using var server = await InProcessServer.StartAsync([corp]);

        using var response = await server.PostAsync(new string('a', 9 * 1024));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task T8_a_foreign_origin_is_refused_even_with_a_valid_token()
    {
        // The origin guard sits ahead of authentication, so a perfectly good token does not buy
        // a way past it — which is the whole point when the browser holds the token already.
        using var corp = Corp();
        await using var server = await InProcessServer.StartAsync([corp]);

        using var response = await server.PostAsync(corp.MintToken(Resource), origin: "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
