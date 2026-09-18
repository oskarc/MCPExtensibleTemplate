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

            var idp = IdentityProviderOf(CallerOf(context.Services));
            result.Tools = [.. result.Tools.Where(tool => binding.Allows(tool.Name, idp))];
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

            if (!binding.Allows(toolName, IdentityProviderOf(CallerOf(context.Services))))
            {
                // Named rule, because an audit trail that says only "denied" cannot distinguish a
                // missing scope from a trust-domain violation, and they call for different action.
                throw new McpException(
                    $"authz_fail (rule: idp-binding). The tool '{toolName}' belongs to a provider "
                    + "bound to a different identity provider than the one that issued your token. "
                    + "This is not a scope you can request; the binding is a deployment decision.");
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
    public static IEnumerable<Claim> NormalizedClaims(string identityProvider, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        yield return new Claim(IdentityProviderClaim, identityProvider);

        // The key everywhere downstream is {idp}:{sub}, so two identical subjects from two
        // identity providers are two principals rather than one.
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            yield return new Claim("principal", $"{identityProvider}:{subject}");
        }
    }
}
