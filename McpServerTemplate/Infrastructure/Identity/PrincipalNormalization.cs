using System.Security.Claims;
using McpServerTemplate.Infrastructure.Frame;
using Microsoft.IdentityModel.JsonWebTokens;

namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Turns a token from any configured identity provider into one canonical principal (roadmap §3.8):
/// the identity provider that issued it, one claim per scope, and the <c>{idp}:{sub}</c> key that
/// limits and logs are kept under. Identity providers disagree on the shape — <c>scp</c> or
/// <c>scope</c>, a space-separated string or an array — and the frame decides on one.
/// </summary>
public static class PrincipalNormalization
{
    /// <summary>The claims to add to a validated principal from <paramref name="identityProvider"/>.</summary>
    public static IReadOnlyList<Claim> NormalizedClaims(
        string identityProvider, IdentityProviderConfig provider, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(principal);

        var claims = new List<Claim> { new(TrustDomainClaims.IdentityProvider, identityProvider) };

        foreach (var claim in principal.FindAll(provider.ScopeClaim).ToArray())
        {
            foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim(TrustDomainClaims.Scope, scope));
            }
        }

        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            claims.Add(new Claim(TrustDomainClaims.Principal, $"{identityProvider}:{subject}"));
        }

        return claims;
    }

    /// <summary>The claim names the frame reads and only the frame may write.</summary>
    public static readonly IReadOnlyList<string> Reserved =
        [TrustDomainClaims.IdentityProvider, TrustDomainClaims.Scope, TrustDomainClaims.Principal];

    /// <summary>Removes every claim under a reserved name, whoever put it there.</summary>
    public static void RemoveReserved(ClaimsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        foreach (var claim in identity.Claims.Where(c => Reserved.Contains(c.Type, StringComparer.Ordinal)).ToArray())
        {
            identity.RemoveClaim(claim);
        }
    }
}
