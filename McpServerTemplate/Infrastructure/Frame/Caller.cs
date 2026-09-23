using System.Globalization;
using System.Security.Claims;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using ModelContextProtocol;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// The principal a request runs as, reduced to what the gate decides on: which identity provider
/// issued it, who it is, what scopes it holds and when its token was issued.
/// </summary>
public sealed record Caller(string IdentityProvider, string Subject, IReadOnlySet<string> Scopes, DateTimeOffset IssuedAt)
{
    /// <summary>The key everything per-caller is kept under: identical subjects from two identity providers are two callers.</summary>
    public string Key => $"{IdentityProvider}:{Subject}";

    /// <summary>
    /// Reads the caller from a principal, or returns null when the principal cannot be attributed.
    /// The Development-only local principal counts as freshly issued on every request: it stands for
    /// a developer at the keyboard, and exists in no other environment (contract-003 · UC-6).
    /// </summary>
    public static Caller? From(ClaimsPrincipal? principal, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (principal?.Identity is not { IsAuthenticated: true } identity)
        {
            return null;
        }

        var idp = principal.FindFirst(TrustDomainClaims.IdentityProvider)?.Value;
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(idp) || string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var scopes = principal.FindAll(TrustDomainClaims.Scope).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        DateTimeOffset issuedAt;
        if (identity.AuthenticationType == DevelopmentPrincipal.AuthenticationType)
        {
            issuedAt = clock.GetUtcNow();
        }
        else if (long.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Iat)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        else
        {
            // A token with no issue time cannot be aged, so it is treated as infinitely old: it
            // passes the read gate and no gate that asks how recent it is.
            issuedAt = DateTimeOffset.MinValue;
        }

        return new Caller(idp, subject, scopes, issuedAt);
    }
}

/// <summary>
/// A request the frame refused. Each refusal has a rule name the caller can act on and a security
/// event name in the OWASP logging vocabulary (contract-003 · G-4).
/// </summary>
public sealed class Refusal
{
    private Refusal(string rule, string securityEvent, string message)
    {
        Rule = rule;
        SecurityEvent = securityEvent;
        Message = message;
    }

    /// <summary>Which check refused the request, for example <c>scope</c> or <c>idp-binding</c>.</summary>
    public string Rule { get; }

    /// <summary>The logged event, for example <c>authz_fail</c>.</summary>
    public string SecurityEvent { get; }

    /// <summary>What the caller is told: what was refused and what they can do about it.</summary>
    public string Message { get; }

    /// <summary>Creates a refusal.</summary>
    public static Refusal Of(string rule, string securityEvent, string message) => new(rule, securityEvent, message);

    /// <summary>The exception the SDK turns into an error for the caller.</summary>
    public McpException ToException() => new($"{SecurityEvent} (rule: {Rule}). {Message}");
}

/// <summary>Claim names the identity layer writes and the frame reads.</summary>
public static class TrustDomainClaims
{
    /// <summary>The identity provider that issued the principal.</summary>
    public const string IdentityProvider = "idp";

    /// <summary>One scope per claim, normalized from the identity provider's own claim shape.</summary>
    public const string Scope = "mcp_scope";

    /// <summary>The <c>{idp}:{sub}</c> key, for logs.</summary>
    public const string Principal = "principal";
}
