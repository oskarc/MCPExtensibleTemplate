using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Enforces the provider-to-identity-provider binding on both paths a caller can reach a tool by.
///
/// contract-002 · G-5 — checked twice, because hiding a tool is not refusing it. A caller that
/// never sees a tool in <c>tools/list</c> can still name it in <c>tools/call</c>, so the list
/// path is discretion and the call path is enforcement. Doing only the first would be a listing
/// that lies; doing only the second would tell every caller what every other team's tools are
/// called.
/// </summary>
public static class TrustDomainFilters
{
    /// <summary>
    /// The identity provider a principal was authenticated by. Written as a claim at token
    /// validation, and by the development principal for stdio.
    /// </summary>
    public const string IdentityProviderClaim = "idp";

    public static string? IdentityProviderOf(ClaimsPrincipal? principal) =>
        principal?.FindFirst(IdentityProviderClaim)?.Value;

    /// <summary>The normalised scopes a principal holds.</summary>
    public static IReadOnlyCollection<string> ScopesOf(ClaimsPrincipal? principal) =>
        principal?.FindAll(ScopeClaim).Select(c => c.Value).ToArray() ?? [];

    /// <summary>
    /// Removes tools whose provider answers to a different identity provider.
    /// </summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> List() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            var binding = context.Services?.GetService<ProviderBinding>();
            if (binding is null)
            {
                return result;
            }

            var caller = CallerOf(context.Services);
            var idp = IdentityProviderOf(caller);
            var scopes = ScopesOf(caller);

            result.Tools = [.. result.Tools.Where(tool => binding.Allows(tool.Name, idp, scopes))];
            return result;
        };

    /// <summary>
    /// Refuses a call to a tool whose provider answers to a different identity provider.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Call() =>
        next => async (context, cancellationToken) =>
        {
            var toolName = (context.MatchedPrimitive as McpServerTool)?.ProtocolTool.Name;

            // Nothing matched: not this filter's business. The handler reports an unknown tool.
            if (toolName is null)
            {
                return await next(context, cancellationToken);
            }

            var binding = context.Services?.GetService<ProviderBinding>();
            if (binding is null)
            {
                return await next(context, cancellationToken);
            }

            var caller = CallerOf(context.Services);
            var idp = IdentityProviderOf(caller);

            // The two refusals are named separately, because they call for different action and an
            // audit trail that says only "denied" cannot tell them apart. A missing scope is
            // something the caller can go and ask for; a trust-domain mismatch is not.
            if (!binding.Allows(toolName, idp))
            {
                throw new McpException(
                    $"authz_fail (rule: idp-binding). The tool '{toolName}' belongs to a provider "
                    + "bound to a different identity provider than the one that issued your token. "
                    + "This is not a scope you can request; the binding is a deployment decision.");
            }

            if (!binding.Allows(toolName, idp, ScopesOf(caller)))
            {
                throw new McpException(
                    $"authz_fail (rule: insufficient_scope). The tool '{toolName}' requires the scope "
                    + $"'{binding.ScopeOf(toolName)}', which your token does not carry. Request a "
                    + "token with that scope and call again.");
            }

            return await next(context, cancellationToken);
        };

    /// <summary>
    /// The principal on the current request: the authenticated caller over HTTP, or the declared
    /// development principal over stdio, which has no HttpContext.
    /// </summary>
    private static ClaimsPrincipal? CallerOf(IServiceProvider? services) =>
        services?.GetService<IHttpContextAccessor>()?.HttpContext?.User
        ?? services?.GetService<ClaimsPrincipal>();

    /// <summary>
    /// The claims a validated token contributes beyond its own: which identity provider vouched
    /// for it, so the binding can be checked without re-reading the token.
    /// </summary>
    /// <summary>The scopes a principal holds, read through its identity provider's claim name.</summary>
    public const string ScopeClaim = "mcp_scope";

    /// <remarks>
    /// Built eagerly, not as an iterator. The caller adds these to the very identity this reads
    /// from, and a lazy sequence enumerates it while it is being added to — which threw
    /// "Collection was modified" on every authenticated request.
    /// </remarks>
    public static IReadOnlyList<Claim> NormalizedClaims(
        string identityProvider, IdentityProviderConfig provider, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(principal);

        var claims = new List<Claim> { new(IdentityProviderClaim, identityProvider) };

        // Identity providers disagree about the claim name — scope, scp — and about whether it is
        // space-delimited or repeated. Normalising here means nothing downstream has to know which
        // issuer a principal came from to read what it may do.
        foreach (var claim in principal.FindAll(provider.ScopeClaim).ToArray())
        {
            foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim(ScopeClaim, scope));
            }
        }

        // The key everywhere downstream is {idp}:{sub}, so two identical subjects from two
        // identity providers are two principals rather than one.
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            claims.Add(new Claim("principal", $"{identityProvider}:{subject}"));
        }

        return claims;
    }
}
