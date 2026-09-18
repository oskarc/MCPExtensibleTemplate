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
                        // A token without jti cannot be revoked individually or de-duplicated in
                        // an audit trail, so it is refused here rather than accepted and logged
                        // as unattributable.
                        var jti = context.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
                        if (string.IsNullOrWhiteSpace(jti))
                        {
                            context.Fail("The token carries no jti claim, so it cannot be attributed or revoked.");
                        }

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
