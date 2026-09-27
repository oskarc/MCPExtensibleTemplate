using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using McpServerTemplate.Testing;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;

namespace McpServerTemplate.TestIssuer;

/// <summary>
/// The issuers this container answers as, one per host name, and the requests each has seen.
///
/// contract-005 · G-6 — which issuer a request is for is decided by the name it was sent to, so the
/// server under test and the test process reach the same issuer by the same name, and a count is
/// kept per name: "zero key lookups at every issuer" is a statement about each name, not the sum.
/// </summary>
public sealed class IssuerSet : IDisposable
{
    /// <summary>An audience no deployment in the environment uses, for the wrong-audience token.</summary>
    public const string ForeignAudience = "https://not-this-resource.e2e.test/mcp";

    private static readonly string[] CodeOnly = ["code"];
    private static readonly string[] AuthorizationCodeOnly = ["authorization_code"];
    private static readonly string[] S256Only = ["S256"];
    private static readonly string[] PublicClient = ["none"];

    private readonly Dictionary<string, Issuer> _issuers;

    /// <param name="identifiers">
    /// The issuer identifiers to answer as, each https://{host} or https://{host}/ (Program.cs checks
    /// them). A request is for the issuer whose host it was sent to.
    /// </param>
    public IssuerSet(IEnumerable<string> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        _issuers = identifiers.ToDictionary(
            identifier => new Uri(identifier).Host.ToLowerInvariant(),
            identifier => new Issuer(new Uri(identifier).Host.ToLowerInvariant(), identifier),
            StringComparer.Ordinal);
    }

    public void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A request for a name this container does not hold is refused, not answered by whichever
        // issuer happens to be first: a misrouted request must be visible.
        app.Use(async (context, next) =>
        {
            if (Find(context) is not { } issuer)
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            // The admin door is the test talking to the issuer, not a client of it, so it is not
            // counted: counts are what the server under test and OAuth clients asked for.
            if (!context.Request.Path.StartsWithSegments("/admin", StringComparison.Ordinal))
            {
                issuer.Count(context.Request.Path.Value ?? "/");
            }

            await next(context);
        });

        // Every handler takes a second parameter, so none can bind as a RequestDelegate — whose
        // Task would be awaited and whose IResult discarded, answering 200 with an empty body. An
        // expression-bodied lambda over HttpContext alone binds that way even when async (ASP0016
        // catches only the synchronous form); the discovery documents went out empty until this.
        app.MapGet("/.well-known/openid-configuration", (HttpContext context, CancellationToken cancellationToken) =>
            Get(context).DocumentAsync("/.well-known/openid-configuration", cancellationToken));
        app.MapGet("/jwks", (HttpContext context, CancellationToken cancellationToken) =>
            Get(context).DocumentAsync("/jwks", cancellationToken));
        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext context, CancellationToken _) =>
            Get(context).AuthorizationServerMetadata());
        app.MapGet("/authorize", (HttpContext context, CancellationToken _) =>
            Get(context).Authorize(context.Request.Query));
        app.MapPost("/token", async (HttpContext context, CancellationToken cancellationToken) =>
            Get(context).Token(await context.Request.ReadFormAsync(cancellationToken)));

        app.MapPost("/admin/mint", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var request = await context.Request.ReadFromJsonAsync<MintRequest>(cancellationToken);
            return request is null
                ? Results.BadRequest("A mint request needs a JSON body naming its kind.")
                : Get(context).Mint(request, this);
        });
        app.MapGet("/admin/counts", () => Results.Json(_issuers.ToDictionary(i => i.Key, i => i.Value.Counts())));
        app.MapGet("/admin/authorizations", () => Results.Json(_issuers.ToDictionary(i => i.Key, i => i.Value.Authorizations())));
        app.MapGet("/admin/clock", () => Results.Json(new Dictionary<string, object> { ["utc"] = DateTimeOffset.UtcNow }));
    }

    public void Dispose()
    {
        foreach (var issuer in _issuers.Values)
        {
            issuer.Dispose();
        }
    }

    private Issuer? Find(HttpContext context) =>
        _issuers.GetValueOrDefault(context.Request.Host.Host.ToLowerInvariant());

    private Issuer Get(HttpContext context) => Find(context)!;

    private Issuer? Other(Issuer issuer, string? named) =>
        named is not null
            ? _issuers.GetValueOrDefault(named.ToLowerInvariant())
            : _issuers.Values.FirstOrDefault(i => !ReferenceEquals(i, issuer));

    /// <summary>What the test asks the admin endpoint for.</summary>
    /// <param name="Kind">valid, wrong-audience, expired, alg-none, hs256, cross-signed, missing-claim or stale-iat.</param>
    /// <param name="Audience">The audience; the server's resource for every kind but wrong-audience.</param>
    /// <param name="Scopes">Scopes to put in the scope claim.</param>
    /// <param name="Subject">The sub claim.</param>
    /// <param name="ClientId">The client_id claim.</param>
    /// <param name="Claim">For missing-claim: sub, jti, client_id or iat.</param>
    /// <param name="IssuedSecondsAgo">For stale-iat: how long ago iat says the token was issued.</param>
    /// <param name="SignedBy">For cross-signed: the issuer name whose key signs; another name when omitted.</param>
    public sealed record MintRequest(
        string Kind,
        string? Audience = null,
        string[]? Scopes = null,
        string? Subject = null,
        string? ClientId = null,
        string? Claim = null,
        int? IssuedSecondsAgo = null,
        string? SignedBy = null);

    private sealed record PendingCode(string ClientId, string RedirectUri, string Challenge, string ChallengeMethod, string? Resource, string? Scope);

    /// <summary>
    /// contract-005 · T-4 — what one /authorize or /token request carried, as this issuer saw it: the
    /// witness that a standard client's flow reached it, once, with the resource and PKCE method named.
    /// </summary>
    /// <param name="Path">/authorize or /token.</param>
    /// <param name="ClientId">The client the request was for.</param>
    /// <param name="Resource">The resource parameter, or null when there was none.</param>
    /// <param name="ChallengeMethod">
    /// /authorize: the code_challenge_method sent. /token: the method of the challenge its verifier was
    /// checked against, or null when no code matched.
    /// </param>
    /// <param name="Outcome">approved, issued, or the OAuth error code it was refused with.</param>
    public sealed record AuthorizationRecord(string Path, string? ClientId, string? Resource, string? ChallengeMethod, string Outcome);

    private sealed class Issuer : IDisposable
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<AuthorizationRecord> _authorizations = new();

        public Issuer(string host, string identifier)
        {
            Host = host;
            Provider = new TestIdentityProvider(host, identifier);
        }

        public string Host { get; }

        /// <summary>The Testing library's provider: its key was generated when this container started.</summary>
        public TestIdentityProvider Provider { get; }

        public void Count(string path) => _counts.AddOrUpdate(path, 1, (_, n) => n + 1);

        public SortedDictionary<string, int> Counts() => new(_counts, StringComparer.Ordinal);

        public AuthorizationRecord[] Authorizations() => [.. _authorizations];

        /// <summary>Discovery and JWKS, served by the same handler the fast suite reads in-process.</summary>
        public async Task<IResult> DocumentAsync(string path, CancellationToken cancellationToken)
        {
            using var invoker = new HttpMessageInvoker(Provider.Handler, disposeHandler: false);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Provider.Authority + path));
            using var response = await invoker.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Results.Text(body, "application/json", Encoding.UTF8, (int)response.StatusCode);
        }

        /// <summary>RFC 8414 metadata. S256 is the only challenge method, so a client that cannot do it fails.</summary>
        public IResult AuthorizationServerMetadata() => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Provider.Issuer,
            ["authorization_endpoint"] = $"{Provider.Authority}/authorize",
            ["token_endpoint"] = $"{Provider.Authority}/token",
            ["jwks_uri"] = $"{Provider.Authority}/jwks",
            ["response_types_supported"] = CodeOnly,
            ["grant_types_supported"] = AuthorizationCodeOnly,
            ["code_challenge_methods_supported"] = S256Only,
            ["token_endpoint_auth_methods_supported"] = PublicClient,
        });

        /// <summary>
        /// Approves at once — there is no person to ask — and redirects with code, state and iss.
        /// Everything a code-flow client must send is checked, because a client that omits PKCE or
        /// the resource must not be seen to succeed here.
        /// </summary>
        public IResult Authorize(IQueryCollection query)
        {
            string? One(string key) => query.TryGetValue(key, out var v) && v.Count == 1 ? v[0] : null;

            var clientId = One("client_id");
            var redirectUri = One("redirect_uri");
            var challenge = One("code_challenge");
            var method = One("code_challenge_method");

            IResult Refused(string error, string description)
            {
                _authorizations.Enqueue(new AuthorizationRecord("/authorize", clientId, One("resource"), method, error));
                return Error(error, description);
            }

            if (One("response_type") != "code")
            {
                return Refused("unsupported_response_type", "response_type must be code.");
            }

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri) || !Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
            {
                return Refused("invalid_request", "client_id and an absolute redirect_uri are required.");
            }

            if (method != "S256" || string.IsNullOrEmpty(challenge))
            {
                return Refused("invalid_request", "PKCE with code_challenge_method=S256 is required.");
            }

            var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            _codes[code] = new PendingCode(clientId, redirectUri, challenge, method, One("resource"), One("scope"));
            _authorizations.Enqueue(new AuthorizationRecord("/authorize", clientId, One("resource"), method, "approved"));

            var location = new QueryBuilder
            {
                { "code", code },
                { "state", One("state") ?? string.Empty },
                { "iss", Provider.Issuer },
            };
            var separator = redirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return Results.Redirect(redirectUri + separator + location.ToQueryString().Value![1..]);
        }

        /// <summary>
        /// Exchanges a code once. The verifier must hash to the challenge, and the token's audience is
        /// the resource the client names — which is how a client that asks for the wrong resource gets
        /// a token the server refuses.
        /// </summary>
        public IResult Token(IFormCollection form)
        {
            string? One(string key) => form.TryGetValue(key, out StringValues v) && v.Count == 1 ? v[0] : null;

            PendingCode? pending = null;
            IResult Refused(string error, string description)
            {
                _authorizations.Enqueue(new AuthorizationRecord("/token", pending?.ClientId ?? One("client_id"), One("resource"), pending?.ChallengeMethod, error));
                return Error(error, description);
            }

            if (One("grant_type") != "authorization_code")
            {
                return Refused("unsupported_grant_type", "Only authorization_code is issued here.");
            }

            if (One("code") is not { } code || !_codes.TryRemove(code, out pending))
            {
                return Refused("invalid_grant", "The code is unknown or was already used.");
            }

            if (One("redirect_uri") != pending.RedirectUri || One("client_id") is { } id && id != pending.ClientId)
            {
                return Refused("invalid_grant", "redirect_uri and client_id must match the authorization request.");
            }

            // The challenge was accepted only as S256 (Authorize), so a verifier that hashes to it under
            // SHA-256 is the S256 proof the code was issued against.
            var verifier = One("code_verifier");
            if (string.IsNullOrEmpty(verifier) ||
                !string.Equals(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), pending.Challenge, StringComparison.Ordinal))
            {
                return Refused("invalid_grant", "code_verifier does not match the code_challenge.");
            }

            var resource = One("resource");
            if (string.IsNullOrEmpty(resource) || pending.Resource is { } asked && asked != resource)
            {
                return Refused("invalid_target", "resource is required, and must be the one the authorization request named.");
            }

            _authorizations.Enqueue(new AuthorizationRecord("/token", pending.ClientId, resource, pending.ChallengeMethod, "issued"));

            var scopes = (One("scope") ?? pending.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var token = Provider.MintToken(
                audience: resource,
                subject: $"user-of-{pending.ClientId}",
                clientId: pending.ClientId,
                jti: Guid.NewGuid().ToString("N"),
                scopes: scopes);

            return Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = token,
                ["token_type"] = "Bearer",
                ["expires_in"] = 600,
                ["scope"] = string.Join(' ', scopes),
            });
        }

        /// <summary>The tokens Keycloak will not mint, each wrong in exactly one way.</summary>
        public IResult Mint(MintRequest request, IssuerSet set)
        {
            var audience = request.Audience ?? string.Empty;
            var subject = request.Subject ?? "e2e-user";
            var clientId = request.ClientId ?? "e2e-client";
            var jti = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;

            string token;
            switch (request.Kind)
            {
                case "valid":
                    token = Provider.MintToken(audience, subject: subject, clientId: clientId, jti: jti, scopes: request.Scopes);
                    break;

                case "wrong-audience":
                    token = Provider.MintToken(ForeignAudience, subject: subject, clientId: clientId, jti: jti, scopes: request.Scopes);
                    break;

                case "expired":
                    // Past the server's 30-second clock skew by a wide margin.
                    token = Provider.MintToken(
                        audience, subject: subject, clientId: clientId, jti: jti, scopes: request.Scopes,
                        issuedAt: now.AddMinutes(-20), notBefore: now.AddMinutes(-20), expires: now.AddMinutes(-5));
                    break;

                case "alg-none":
                    // A valid payload under a header that asks for no signature at all.
                    var payload = Provider.MintToken(audience, subject: subject, clientId: clientId, jti: jti, scopes: request.Scopes).Split('.')[1];
                    token = $"{Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{payload}.";
                    break;

                case "hs256":
                    token = Provider.MintHmacToken(audience);
                    break;

                case "cross-signed":
                    if (set.Other(this, request.SignedBy) is not { } signer)
                    {
                        return Error("invalid_request", "cross-signed needs a second issuer name in this container.");
                    }

                    // This issuer's name in iss, the other issuer's key on the signature.
                    token = Provider.MintToken(
                        audience, subject: subject, clientId: clientId, jti: jti, scopes: request.Scopes,
                        signingCredentials: signer.Provider.Credentials);
                    break;

                case "missing-claim":
                    if (request.Claim is not ("sub" or "jti" or "client_id" or "iat"))
                    {
                        return Error("invalid_request", "missing-claim needs claim: sub, jti, client_id or iat.");
                    }

                    token = Provider.MintTokenWithout(request.Claim, audience, request.Scopes);
                    break;

                case "stale-iat":
                    var issued = now.AddSeconds(-(request.IssuedSecondsAgo ?? 360));
                    token = Provider.MintToken(
                        audience, subject: subject, clientId: clientId, jti: jti, scopes: request.Scopes,
                        issuedAt: issued, notBefore: issued.AddMinutes(-1), expires: now.AddMinutes(10));
                    break;

                default:
                    return Error("invalid_request", $"Unknown kind '{request.Kind}'.");
            }

            return Results.Json(new Dictionary<string, object> { ["token"] = token });
        }

        public void Dispose() => Provider.Dispose();

        private static IResult Error(string error, string description) =>
            Results.Json(
                new Dictionary<string, object> { ["error"] = error, ["error_description"] = description },
                statusCode: StatusCodes.Status400BadRequest);
    }
}
