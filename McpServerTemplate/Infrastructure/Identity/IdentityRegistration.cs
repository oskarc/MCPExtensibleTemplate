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
    /// How far a token's times may be off this server's clock and still be taken as they are: the bearer handler's
    /// allowance for nbf and exp, and contract-005 · G-17 round 1, the request gate's for iat (a token issued further in
    /// the future than this has no age anyone can tell).
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// contract-005 · T-3, G-17 round 1 — where the router leaves why it refused to route a token, for
    /// <see cref="UnattributableHandler"/> to log.
    /// </summary>
    private const string RefusedTokenItem = "McpServerTemplate.Identity.RefusedToken";

    public static string SchemeFor(string identityProvider) => $"idp:{identityProvider}";

    /// <summary>
    /// Returns "no credential I can act on". The challenge that follows is issued by the MCP
    /// scheme, which is what tells the caller where to go.
    ///
    /// contract-005 · T-3 — a token whose issuer no identity provider is configured for is refused here, and was refused
    /// with nothing said: the one refused token the log never showed. It is logged as authn_login_fail with its reason,
    /// once per request (the framework runs a handler's authentication once per request), naming the issuer made safe for
    /// a log line (<see cref="LogSafe"/>), never the token. Nothing is looked up to say it: no key, no document.
    ///
    /// contract-005 · G-17 round 1 — and so is every other token the router cannot attribute, each refused as silently
    /// until now: one it cannot read as a JSON web token, one over <see cref="MaxTokenBytes"/>, and one that names no issuer.
    /// A request with no token at all is not a refused token: the challenge that follows tells its caller where to get one.
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

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Context.Items.TryGetValue(RefusedTokenItem, out var why) && why is string reason)
            {
                LogRefusal(Context, UnattributableScheme, reason);
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }
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

            // contract-005 · G-12 (3) — the claim the startup check held to one of the four client claims,
            // trimmed as it was checked. Looking up the setting as written let " azp " pass the check and
            // then find no claim on any token.
            var clientIdClaim = provider.ClientIdClaim.Trim();

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

                    // contract-002 · G-3 — pinned on the scheme itself, not only by the router. Left to its
                    // defaults the bearer handler also accepts the issuer its discovery document names, which
                    // can differ from the configured Issuer; only the router's exact match kept such a token
                    // off this scheme, and a scheme authenticated any other way took it. The token's own iss
                    // is not echoed: it is the caller's text, and the reason is logged.
                    IssuerValidator = (issuer, _, _) => string.Equals(issuer, provider.Issuer, StringComparison.Ordinal)
                        ? issuer
                        : throw new SecurityTokenInvalidIssuerException(
                            $"The token's issuer is not {provider.Issuer}, the issuer pinned for identity provider '{name}'."),

                    // One resource, several authorization servers: every identity provider must
                    // issue for this same audience.
                    ValidateAudience = true,
                    ValidAudience = config.Resource,

                    // contract-005 · G-17 round 1 — exactly: the resource is held as written, a trailing slash included
                    // or not (G-12 (1)), and the library's default read a trailing slash as nothing, so a token for
                    // https://host/mcp/ passed a server whose resource is https://host/mcp, and the other way round.
                    IgnoreTrailingSlashWhenValidatingAudience = false,

                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,

                    // The allowlist the configuration validated. "none" and the HMAC family are
                    // absent rather than denied, so this cannot be widened by a typo.
                    ValidAlgorithms = provider.Algorithms,

                    ClockSkew = ClockSkew,
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
                        //   the client — no client, so a compromised one cannot be scoped out
                        //   iat        — no issue time, so age cannot be reasoned about
                        //
                        // contract-005 · G-12 (3) — the client claim is the one this identity
                        // provider's ClientIdClaim names (azp, cid, appid, client_id). It used to be
                        // client_id or azp whatever ClientIdClaim said: the setting was read, never
                        // applied, and a token from the wrong kind of provider passed on the other name.
                        static string? Claim(TokenValidatedContext c, string name) =>
                            c.Principal?.FindFirst(name)?.Value is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

                        var missing = new List<string>();
                        if (Claim(context, JwtRegisteredClaimNames.Sub) is null) missing.Add("sub");
                        if (Claim(context, JwtRegisteredClaimNames.Jti) is null) missing.Add("jti");
                        if (Claim(context, clientIdClaim) is null) missing.Add($"{clientIdClaim} (the client claim)");
                        if (Claim(context, JwtRegisteredClaimNames.Iat) is null) missing.Add("iat");

                        if (missing.Count > 0)
                        {
                            var reason = "The token is missing " + string.Join(", ", missing)
                                + ", so it cannot be attributed, revoked or aged.";

                            // contract-005 · T-3 — logged with its reason, as every other refused token is. A failure
                            // set here is returned as it is, so OnAuthenticationFailed never runs for it, and the
                            // framework's own line is an Information event Production filters out: the token was
                            // refused and nothing said why. The reason names the claims, never the token.
                            LogRefusal(context.HttpContext, scheme, reason);
                            context.Fail(reason);
                            return Task.CompletedTask;
                        }

                        // contract-005 · G-17 round 1, follow-up — a token that says it was issued later than now, past the
                        // clock skew the validation allows its nbf and exp, has no age anyone can tell: it counted as fresh
                        // until its iat plus a freshness window, however long ago it was really issued. Validation reads
                        // nbf and exp and never iat, so this refuses it for every request, reads included, logged with its
                        // reason. The request gate refuses it by its own rule as well (token-age), should one reach it.
                        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
                        if (long.TryParse(Claim(context, JwtRegisteredClaimNames.Iat), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var iat)
                            && DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(iat, DateTimeOffset.MinValue.ToUnixTimeSeconds(), DateTimeOffset.MaxValue.ToUnixTimeSeconds())) - now is var ahead
                            && ahead > ClockSkew)
                        {
                            var reason = $"The token says it was issued {ahead.TotalSeconds:0} seconds from now, further ahead of this "
                                + $"server's clock than the {ClockSkew.TotalSeconds:0} seconds it allows, so its age cannot be told.";
                            LogRefusal(context.HttpContext, scheme, reason);
                            context.Fail(reason);
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
                        LogRefusal(context.HttpContext, scheme, context.Exception.Message);
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

            // contract-005 · G-12 (4) — each identity provider's Issuer, not its Authority. A client is
            // sent to this URL and holds the authorization server's own metadata to it exactly (RFC
            // 8414 §3.3); the Authority is where this server fetches keys, and differs from the issuer
            // for some providers (a trailing slash, another host), which made them unreachable.
            foreach (var provider in config.IdentityProviders.Values)
            {
                options.ResourceMetadata.AuthorizationServers.Add(provider.Issuer);
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
    /// contract-002 revision, 2026-09-20; contract-005 · T-3 — one refused token, as the event a SIEM correlates for
    /// credential attacks (roadmap P1.1): which scheme refused it, for which path, and why. The reason is the
    /// validator's message or the claims a token lacks, never the token: a token in a log is a credential in a log
    /// (roadmap I8).
    /// </summary>
    private static void LogRefusal(HttpContext context, string scheme, string reason) =>
        context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("McpServerTemplate.Identity")
            .LogWarning(
                "authn_login_fail: {Scheme} refused a token for {Path} — {Reason}",
                scheme, context.Request.Path, reason);

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
        if (token.Length == 0)
        {
            return UnattributableScheme;
        }

        // contract-005 · T-3, G-17 round 1 — each refusal below is logged by the scheme it is sent to (UnattributableHandler),
        // with its reason and nothing of the token's but the issuer it names, made safe for a log line.
        if (token.Length > MaxTokenBytes)
        {
            return Unattributable(context, $"the token is {token.Length} characters long, more than the {MaxTokenBytes} this server reads");
        }

        string? issuer;
        try
        {
            issuer = new JsonWebTokenHandler().ReadJsonWebToken(token)?.Issuer;
        }
        catch (ArgumentException)
        {
            // Not a readable token. Nothing to route, and no reason to consult a key store. The library's own words are
            // not logged: they can quote the token.
            return Unattributable(context, "the token cannot be read as a JSON web token");
        }

        if (string.IsNullOrEmpty(issuer))
        {
            return Unattributable(context, "the token names no issuer");
        }

        var match = config.IdentityProviders.Values
            .FirstOrDefault(p => string.Equals(p.Issuer, issuer, StringComparison.Ordinal));

        return match is null
            ? Unattributable(context, $"no identity provider is configured for issuer '{LogSafe.Text(issuer)}'")
            : SchemeFor(match.Name);
    }

    /// <summary>
    /// contract-005 · T-3, G-17 round 1 — the scheme for a token the router refuses, leaving <paramref name="reason"/> on the
    /// request for that scheme's handler to log, once (<see cref="UnattributableHandler"/>).
    /// </summary>
    private static string Unattributable(HttpContext context, string reason)
    {
        context.Items[RefusedTokenItem] = reason;
        return UnattributableScheme;
    }
}
