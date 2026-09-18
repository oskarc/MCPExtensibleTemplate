using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// The fixture that every token test in contract-002 rests on, checked before anything rests on
/// it. A fixture that mints tokens nothing would accept, or accepts tokens nothing should, makes
/// every test built over it meaningless in a way those tests cannot show.
/// </summary>
public class TestIdentityProviderTests
{
    private const string Resource = "https://mcp.example.com/mcp";

    private static async Task<TokenValidationResult> ValidateAsync(
        TestIdentityProvider idp, string token, string audience = Resource) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = idp.Issuer,
            ValidAudience = audience,
            IssuerSigningKey = idp.PublicKey,
            ValidAlgorithms = ["RS256"],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        });

    [Fact]
    public async Task A_minted_token_validates_against_the_providers_own_key()
    {
        using var idp = new TestIdentityProvider("corp", "https://login.test/");

        var result = await ValidateAsync(idp, idp.MintToken(Resource));

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("user-1", result.ClaimsIdentity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
    }

    [Fact]
    public async Task A_token_from_one_provider_does_not_validate_against_another()
    {
        // The fixture has to be able to express the attack it is used to test: two issuers whose
        // keys are genuinely different.
        using var corp = new TestIdentityProvider("corp", "https://login.test/");
        using var other = new TestIdentityProvider("other", "https://elsewhere.test/");

        var result = await ValidateAsync(corp, other.MintToken(Resource, issuer: corp.Issuer));

        Assert.False(result.IsValid, "a token signed by another provider's key validated");
    }

    [Theory]
    [InlineData("wrong audience")]
    [InlineData(Resource)]
    public async Task The_audience_is_checked(string audience)
    {
        using var idp = new TestIdentityProvider("corp", "https://login.test/");

        var result = await ValidateAsync(idp, idp.MintToken(audience));

        Assert.Equal(audience == Resource, result.IsValid);
    }

    [Fact]
    public async Task An_expired_token_is_expired()
    {
        using var idp = new TestIdentityProvider("corp", "https://login.test/");

        var result = await ValidateAsync(
            idp,
            idp.MintToken(Resource, notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddHours(-1)));

        Assert.False(result.IsValid, "an expired token validated");
    }

    [Fact]
    public async Task An_hmac_signed_token_is_refused_where_only_rsa_is_allowed()
    {
        using var idp = new TestIdentityProvider("corp", "https://login.test/");

        var result = await ValidateAsync(idp, idp.MintHmacToken(Resource));

        Assert.False(result.IsValid, "an HS256 token validated where only RS256 is permitted");
    }

    [Fact]
    public void A_token_can_be_minted_without_a_jti()
    {
        using var idp = new TestIdentityProvider("corp", "https://login.test/");

        var token = new JsonWebTokenHandler().ReadJsonWebToken(idp.MintToken(Resource, jti: null));

        Assert.False(token.TryGetClaim(JwtRegisteredClaimNames.Jti, out _));
    }

    [Fact]
    public async Task The_provider_serves_discovery_and_jwks_and_counts_what_was_asked()
    {
        using var idp = new TestIdentityProvider("corp", "https://login.test/");
        using var client = new HttpClient(idp.Handler);

        Assert.Equal(0, idp.TotalRequests);

        using var discovery = await client.GetAsync($"{idp.Authority}/.well-known/openid-configuration");
        Assert.True(discovery.IsSuccessStatusCode);
        Assert.Equal(0, idp.JwksRequests);

        using var jwks = await client.GetAsync($"{idp.Authority}/jwks");
        Assert.True(jwks.IsSuccessStatusCode);

        // The count is the instrument for "no key lookup happened", so it has to move only when
        // a key set is actually fetched.
        Assert.Equal(1, idp.JwksRequests);
        Assert.Equal(2, idp.TotalRequests);

        var body = await jwks.Content.ReadAsStringAsync();
        Assert.Contains("\"kid\":\"corp-key\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anything_the_provider_was_not_asked_to_serve_is_a_404()
    {
        using var idp = new TestIdentityProvider("corp", "https://login.test/");
        using var client = new HttpClient(idp.Handler);

        using var response = await client.GetAsync($"{idp.Authority}/somewhere-else");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
