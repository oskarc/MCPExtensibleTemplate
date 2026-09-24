using System.Globalization;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// The principal a developer runs as over stdio.
///
/// contract-002 · G-6 — stdio has no token and no identity provider to ask, but the rest of the
/// server is built on there always being a principal. Rather than let that code path carry a
/// null, Development declares one explicitly in configuration: which identity provider it stands
/// for, who it is, and what it may do.
///
/// It exists only in Development. The transport gate refuses stdio anywhere else (contract-001
/// G-1), so this cannot become a way to run unauthenticated in production — but the scopes are
/// still declared rather than assumed, so a developer sees the same refusals a real principal
/// would meet instead of discovering them after deploying.
/// </summary>
public static class DevelopmentPrincipal
{
    /// <summary>The authentication type that marks the Development principal; the gate treats it as freshly issued.</summary>
    public const string AuthenticationType = "development";

    /// <summary>
    /// Builds the principal from <c>Development:DevPrincipal</c>, refusing anything it cannot
    /// honour. A development identity that is wrong is worse than absent: it quietly grants
    /// what the deployed server will refuse.
    /// </summary>
    /// <param name="configuration">Configuration to read the principal from.</param>
    /// <param name="identity">
    /// The identity configuration: the configured one, or — for a local stdio run that configured
    /// none — the one <see cref="SynthesizeIdentity"/> builds from the providers' policies, so a
    /// local run needs no production-shaped config file and still meets every check. The scope
    /// check below runs whenever there is a catalog to check against; null is accepted for callers
    /// that have no identity at all, and then the principal is built unchecked.
    /// </param>
    public static ClaimsPrincipal Create(IConfiguration configuration, AuthenticationConfig? identity)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Development:DevPrincipal");

        var identityProvider = section["IdentityProvider"] ?? "development";
        var subject = section["Subject"] ?? "dev-user";
        var clientId = section["ClientId"] ?? "dev-ide";
        var scopes = section.GetSection("Scopes").Get<string[]>() ?? [];
        var scopeClaim = "scope";

        if (identity is { IdentityProviders.Count: > 0 })
        {
            if (!identity.IdentityProviders.TryGetValue(identityProvider, out var provider))
            {
                throw new ConfigurationException(
                    $"Development:DevPrincipal:IdentityProvider names '{identityProvider}', which is not "
                    + $"configured. Configured: {string.Join(", ", identity.IdentityProviders.Keys)}.");
            }

            scopeClaim = provider.ScopeClaim;

            var outsideCatalog = scopes
                .Where(s => !provider.ScopeCatalog.Contains(s, StringComparer.Ordinal))
                .ToArray();

            if (outsideCatalog.Length > 0)
            {
                throw new ConfigurationException(
                    $"Development:DevPrincipal:Scopes asks for {string.Join(", ", outsideCatalog)}, which "
                    + $"'{identityProvider}' cannot issue. Its catalog is "
                    + $"{string.Join(", ", provider.ScopeCatalog)}. A development principal holding a scope "
                    + "the identity provider does not have would pass locally and fail deployed.");
            }
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("client_id", clientId),
            new("idp", identityProvider),

            // A jti, because everything downstream expects a token to be identifiable — and a
            // fixed one, so a development trace reads the same across runs.
            new(JwtRegisteredClaimNames.Jti, $"dev-{subject}"),

            // An iat, because the bearer path now requires one (roadmap P1.1) and a development
            // principal that could not satisfy the rule it stands in for would be a stub that
            // proves nothing. Seconds since the epoch, as a real token carries it.
            new(
                JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
        };

        if (scopes.Length > 0)
        {
            claims.Add(new Claim(scopeClaim, string.Join(' ', scopes)));
        }

        // contract-003 · G-14 — normalized exactly as a validated token is, so stdio meets the same
        // gate HTTP does rather than a principal the gate cannot read.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
        var normalized = PrincipalNormalization.NormalizedClaims(
            identityProvider,
            new IdentityProviderConfig { Name = identityProvider, ScopeClaim = scopeClaim },
            principal);
        var claimsIdentity = (ClaimsIdentity)principal.Identity!;
        PrincipalNormalization.RemoveReserved(claimsIdentity);
        claimsIdentity.AddClaims(normalized);
        return principal;
    }

    /// <summary>
    /// An identity configuration for a local stdio run that configured none (contract-003 · G-14):
    /// one identity provider per name the enabled providers are bound to, able to issue exactly the
    /// scopes their policies require. It exists so the gate runs locally with nothing switched off;
    /// it is never used over HTTP, where identity providers are required and validated.
    /// </summary>
    public static AuthenticationConfig SynthesizeIdentity(
        IConfiguration configuration, IEnumerable<McpServerTemplate.Infrastructure.Frame.IProviderModule> modules)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var identity = new AuthenticationConfig { Resource = "stdio://development" };
        foreach (var module in modules)
        {
            var bound = configuration[$"Providers:{module.Name}:IdentityProvider"];
            if (string.IsNullOrWhiteSpace(bound))
            {
                continue; // the policy check names the missing binding
            }

            if (!identity.IdentityProviders.TryGetValue(bound, out var provider))
            {
                provider = new IdentityProviderConfig { Name = bound };
                identity.IdentityProviders[bound] = provider;
            }

            provider.ScopeCatalog = [.. provider.ScopeCatalog
                .Concat(module.Policy.Tools.Values.Select(t => t.Scope))
                .Concat(module.Policy.Resources.Values.Select(r => r.Scope))
                .Concat(module.Policy.Prompts.Values.Select(p => p.Scope))
                .Distinct(StringComparer.Ordinal)];
        }

        return identity;
    }
}
