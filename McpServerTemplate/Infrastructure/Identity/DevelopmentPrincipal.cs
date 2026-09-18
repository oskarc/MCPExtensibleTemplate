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
    /// <summary>
    /// Builds the principal from <c>Development:DevPrincipal</c>, refusing anything it cannot
    /// honour. A development identity that is wrong is worse than absent: it quietly grants
    /// what the deployed server will refuse.
    /// </summary>
    /// <param name="configuration">Configuration to read the principal from.</param>
    /// <param name="identity">
    /// The identity configuration, when there is one. Running a server over stdio for a local IDE
    /// should not require configuring identity providers it will never call — so this is optional,
    /// and the scope check below happens only when there is a catalog to check against. Requiring
    /// it would have made the template unrunnable locally without a production-shaped config file.
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
        };

        if (scopes.Length > 0)
        {
            claims.Add(new Claim(scopeClaim, string.Join(' ', scopes)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "development"));
    }
}
