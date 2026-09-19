using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// An identity provider that exists only inside the test process.
///
/// contract-002 · G-10 — every test in this phase runs without reaching the network. This holds
/// its own RSA key, serves the two documents a JWT bearer handler fetches (OpenID discovery and
/// JWKS) through <see cref="Handler"/>, and mints tokens signed with that key. Nothing here
/// resolves a hostname.
///
/// It counts the requests made to it, which is what makes one of Phase 1's exit criteria
/// checkable at all: an unregistered issuer must be refused *before* any key lookup, and the
/// only way to see a lookup that did not happen is to be the thing that would have served it.
/// </summary>
public sealed class TestIdentityProvider : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public TestIdentityProvider(string name, string issuer)
    {
        Name = name;
        Issuer = issuer;
        _key = new RsaSecurityKey(_rsa) { KeyId = $"{name}-key" };
        Handler = new DocumentHandler(this);
    }

    public string Name { get; }

    /// <summary>The issuer value this provider puts in <c>iss</c>, and pins on its scheme.</summary>
    public string Issuer { get; }

    /// <summary>The authority a deployment would configure. Never resolved: the handler answers.</summary>
    public string Authority => Issuer.TrimEnd('/');

    /// <summary>Serves discovery and JWKS in-process, and counts what was asked for.</summary>
    public DocumentHandler Handler { get; }

    /// <summary>How many times a key set has been fetched from this provider.</summary>
    public int JwksRequests => Handler.JwksRequests;

    /// <summary>How many requests of any kind have been made to this provider.</summary>
    public int TotalRequests => Handler.TotalRequests;

    public SecurityKey PublicKey => _key;

    /// <summary>
    /// Mints a token. Every parameter has a valid default, so a test names only the one thing it
    /// is making wrong — which is what keeps the six rejection cases readable as six sentences.
    /// </summary>
    public string MintToken(
        string audience,
        string? issuer = null,
        string? subject = "user-1",
        string? clientId = "client-1",
        string? jti = "token-1",
        string[]? scopes = null,
        string scopeClaim = "scope",
        DateTime? expires = null,
        DateTime? notBefore = null,
        SigningCredentials? signingCredentials = null)
    {
        // Each required claim is omissible on its own, so a rejection test names exactly the one
        // claim it is making wrong (contract-002 revision, 2026-09-20 — roadmap P1.1).
        var claims = new Dictionary<string, object>();

        if (subject is not null)
        {
            claims[JwtRegisteredClaimNames.Sub] = subject;
        }

        if (clientId is not null)
        {
            claims["client_id"] = clientId;
        }

        if (jti is not null)
        {
            claims[JwtRegisteredClaimNames.Jti] = jti;
        }

        if (scopes is { Length: > 0 })
        {
            claims[scopeClaim] = string.Join(' ', scopes);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = signingCredentials
                ?? new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// A validly signed token with one claim removed from its payload.
    ///
    /// <see cref="MintToken"/> cannot omit <c>iat</c>: <see cref="JsonWebTokenHandler"/> stamps one
    /// whether or not the descriptor asks for it, so passing <c>issuedAt: false</c> produced a
    /// token that still carried the claim and was rightly accepted — the test caught it. Stripping
    /// the claim and re-signing is the only way to present a token a standard library would not
    /// have produced, which is exactly the token a hand-rolled issuer might send.
    /// </summary>
    public string MintTokenWithout(string claim, string audience, string[]? scopes = null)
    {
        var parts = MintToken(audience, scopes: scopes).Split('.');

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Base64UrlEncoder.DecodeBytes(parts[1]))!;
        payload.Remove(claim);

        var header = parts[0];
        var body = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = Base64UrlEncoder.Encode(
            _rsa.SignData(
                Encoding.ASCII.GetBytes($"{header}.{body}"),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));

        return $"{header}.{body}.{signature}";
    }

    /// <summary>Signing credentials for this provider's key, for minting on another's behalf.</summary>
    public SigningCredentials Credentials => new(_key, SecurityAlgorithms.RsaSha256);

    /// <summary>
    /// A token signed with a symmetric key. No identity provider here is configured to accept
    /// HS256, so this is the "alg the allowlist excludes" case.
    /// </summary>
    public string MintHmacToken(string audience)
    {
        var secret = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('k', 64)));
        return MintToken(
            audience,
            signingCredentials: new SigningCredentials(secret, SecurityAlgorithms.HmacSha256));
    }

    public void Dispose() => _rsa.Dispose();

    /// <summary>
    /// Answers the discovery and JWKS requests a bearer handler makes, and records them.
    /// Anything else 404s, so a fetch this provider was not expecting is visible rather than
    /// silently satisfied.
    /// </summary>
    public sealed class DocumentHandler : HttpMessageHandler
    {
        private readonly TestIdentityProvider _provider;

        internal DocumentHandler(TestIdentityProvider provider) => _provider = provider;

        private static readonly string[] SigningAlgorithms = ["RS256"];
        private static readonly string[] ResponseTypes = ["code"];
        private static readonly string[] SubjectTypes = ["public"];

        public int JwksRequests { get; private set; }

        public int TotalRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TotalRequests++;
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new Dictionary<string, object>
                {
                    ["issuer"] = _provider.Issuer,
                    ["jwks_uri"] = $"{_provider.Authority}/jwks",
                    ["authorization_endpoint"] = $"{_provider.Authority}/authorize",
                    ["token_endpoint"] = $"{_provider.Authority}/token",
                    ["id_token_signing_alg_values_supported"] = SigningAlgorithms,
                    ["response_types_supported"] = ResponseTypes,
                    ["subject_types_supported"] = SubjectTypes,
                }));
            }

            if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                JwksRequests++;
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_provider._key);
                return Task.FromResult(Json(new Dictionary<string, object>
                {
                    ["keys"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["kty"] = jwk.Kty,
                            ["use"] = "sig",
                            ["alg"] = "RS256",
                            ["kid"] = jwk.Kid,
                            ["n"] = jwk.N,
                            ["e"] = jwk.E,
                        },
                    },
                }));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object payload) =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}
