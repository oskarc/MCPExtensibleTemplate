using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Registers one JWT bearer scheme per identity provider, the policy scheme that routes between
/// them, and the MCP scheme that tells an unauthenticated caller where to go.
///
/// contract-002 · G-1, G-2, G-3.
/// </summary>
public static class IdentityRegistration
{
    /// <summary>The policy scheme every request enters through.</summary>
    public const string RoutingScheme = "Bearer";

    /// <summary>
    /// The largest bearer token this server will look at. A token is read before it is trusted —
    /// the routing scheme has to parse it to find its issuer — so the size limit comes first, or
    /// an unauthenticated caller chooses how much work the server does per request.
    /// </summary>
    public const int MaxTokenBytes = 8 * 1024;

    /// <summary>
    /// Where the router sends anything it cannot attribute to a registered issuer.
    ///
    /// It must not be the MCP scheme: that handler authenticates through the default scheme,
    /// which is the router, which forwarded to it — the server died of a stack overflow on its
    /// first unauthenticated request before this existed. It must also not be a JWT scheme,
    /// because reaching one lets an unregistered issuer provoke a discovery fetch, which is
    /// exactly what G-3 forbids. So it is a scheme that holds no keys and consults nothing.
    /// </summary>
    public const string UnattributableScheme = "unattributable";

    public static string SchemeFor(string identityProvider) => $"idp:{identityProvider}";

    /// <summary>
    /// Returns "no credential I can act on". The challenge that follows is issued by the MCP
    /// scheme, which is what tells the caller where to go.
    /// </summary>
    private sealed class UnattributableHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public UnattributableHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    public static IServiceCollection AddIdentity(
        this IServiceCollection services,
        AuthenticationConfig config,
        Action<string, JwtBearerOptions>? configureForTests = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var builder = services.AddAuthentication(options =>
        {
            // Authenticate through the router, which picks the scheme for the token's issuer.
            options.DefaultAuthenticateScheme = RoutingScheme;
            // Challenge through the MCP scheme, so a caller with no token is told where to get
            // one rather than simply refused.
            options.DefaultChallengeScheme = UnattributableScheme;
        });

        builder.AddPolicyScheme(RoutingScheme, RoutingScheme, options =>
        {
            options.ForwardDefaultSelector = context => SelectScheme(context, config);
        });

        builder.AddScheme<AuthenticationSchemeOptions, UnattributableHandler>(
            UnattributableScheme,
            displayName: null,
            configureOptions: options =>
            {
                // This scheme refuses; it does not explain. The explanation is the metadata
                // document, so the challenge is forwarded to the scheme that names it. Without
                // this the caller gets a bare 401 and still has nowhere to go, which is the
                // failure T-1 exists to catch.
                options.ForwardChallenge = McpAuthenticationDefaults.AuthenticationScheme;
            });

        foreach (var (name, provider) in config.IdentityProviders)
        {
            var scheme = SchemeFor(name);

            builder.AddJwtBearer(scheme, options =>
            {
                options.Authority = provider.Authority;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // The issuer is pinned to this entry, so a token routed here by its own
                    // unvalidated iss claim still has to prove it came from this issuer.
                    ValidateIssuer = true,
                    ValidIssuer = provider.Issuer,

                    // One resource, several authorization servers: every identity provider must
                    // issue for this same audience.
                    ValidateAudience = true,
                    ValidAudience = config.Resource,

                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,

                    // The allowlist the configuration validated. "none" and the HMAC family are
                    // absent rather than denied, so this cannot be widened by a typo.
                    ValidAlgorithms = provider.Algorithms,

                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        // contract-002 revision, 2026-09-20 — roadmap P1.1 requires sub, jti,
                        // client_id (or azp) and iat. Only jti was enforced; a token missing the
                        // others was accepted and became an audit entry naming nobody.
                        //
                        // Each is refused for its own reason, and the reason is in the message
                        // because a caller who cannot see which claim is missing cannot fix it:
                        //   sub        — no subject, so nothing to attribute the call to
                        //   jti        — cannot be revoked individually or de-duplicated
                        //   client_id  — no client, so a compromised one cannot be scoped out
                        //   iat        — no issue time, so age cannot be reasoned about
                        static string? Claim(TokenValidatedContext c, params string[] names) =>
                            names.Select(n => c.Principal?.FindFirst(n)?.Value)
                                 .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                        var missing = new List<string>();
                        if (Claim(context, JwtRegisteredClaimNames.Sub) is null) missing.Add("sub");
                        if (Claim(context, JwtRegisteredClaimNames.Jti) is null) missing.Add("jti");
                        if (Claim(context, "client_id", "azp") is null) missing.Add("client_id (or azp)");
                        if (Claim(context, JwtRegisteredClaimNames.Iat) is null) missing.Add("iat");

                        if (missing.Count > 0)
                        {
                            context.Fail(
                                "The token is missing " + string.Join(", ", missing)
                                + ", so it cannot be attributed, revoked or aged.");
                            return Task.CompletedTask;
                        }

                        // Normalise the principal. Without this a validated token carries no idp
                        // claim, so the trust-domain binding cannot name who vouched for it and
                        // refuses everything — the server would authenticate a caller correctly
                        // and then deny them every tool.
                        if (context.Principal?.Identity is System.Security.Claims.ClaimsIdentity identity)
                        {
                            var normalized = PrincipalNormalization.NormalizedClaims(name, provider, context.Principal);

                            // contract-003 — the names the frame reads are the frame's. An identity
                            // provider may put its own claim under one of them (Entra ID issues an
                            // "idp" claim for guest users), and the frame reads the first it finds:
                            // left in place, the token's value would decide the trust domain.
                            PrincipalNormalization.RemoveReserved(identity);
                            identity.AddClaims(normalized);
                        }

                        return Task.CompletedTask;
                    },

                    // contract-002 revision, 2026-09-20 — roadmap P1.1 maps a failed
                    // authentication to authn_login_fail. Without it the one event a SIEM
                    // correlates for credential attacks was the only stage of this pipeline that
                    // said nothing. Follows the malicious_cors precedent in OriginGuardMiddleware.
                    //
                    // The exception message is logged and the token is not: a token in a log is a
                    // credential in a log (roadmap I8).
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("McpServerTemplate.Identity")
                            .LogWarning(
                                "authn_login_fail: {Scheme} refused a token for {Path} — {Reason}",
                                scheme, context.HttpContext.Request.Path, context.Exception.Message);

                        return Task.CompletedTask;
                    },
                };

                configureForTests?.Invoke(name, options);
            });
        }

        builder.AddMcp(options =>
        {
            options.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = config.Resource,
                AuthorizationServers = { },
                ScopesSupported = { },
            };

            foreach (var provider in config.IdentityProviders.Values)
            {
                options.ResourceMetadata.AuthorizationServers.Add(provider.Authority);
            }

            // The union across identity providers. A scope means something only inside the
            // identity provider that issued it; this list tells a client what may be asked for,
            // and the binding checks decide what it is worth.
            foreach (var scope in config.IdentityProviders.Values
                         .SelectMany(p => p.ScopeCatalog)
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                options.ResourceMetadata.ScopesSupported.Add(scope);
            }
        });

        return services;
    }

    /// <summary>
    /// Picks the scheme for a presented token by reading its issuer without validating anything.
    ///
    /// contract-002 · G-3 — reading an untrusted value to choose a validator is safe only because
    /// the chosen validator pins the issuer itself: a token claiming issuer A but signed by B is
    /// routed to A's scheme and fails there. An issuer nobody registered is refused here, before
    /// any key lookup, so an unknown issuer cannot make this server fetch a document from a host
    /// of the caller's choosing.
    /// </summary>
    private static string SelectScheme(HttpContext context, AuthenticationConfig config)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return UnattributableScheme;
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0 || token.Length > MaxTokenBytes)
        {
            return UnattributableScheme;
        }

        string? issuer;
        try
        {
            issuer = new JsonWebTokenHandler().ReadJsonWebToken(token)?.Issuer;
        }
        catch (ArgumentException)
        {
            // Not a readable token. Nothing to route, and no reason to consult a key store.
            return UnattributableScheme;
        }

        if (string.IsNullOrEmpty(issuer))
        {
            return UnattributableScheme;
        }

        var match = config.IdentityProviders.Values
            .FirstOrDefault(p => string.Equals(p.Issuer, issuer, StringComparison.Ordinal));

        return match is null
            ? UnattributableScheme
            : SchemeFor(match.Name);
    }
}
