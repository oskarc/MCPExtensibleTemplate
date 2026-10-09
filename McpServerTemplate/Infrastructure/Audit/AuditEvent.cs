using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// One decision, or one moment of the server's own life, as the audit trail records it (contract-006 · G-3; roadmap §3.5).
/// The trail adds when it happened, where, its place in the chain and the two hashes; this is everything else.
/// </summary>
/// <param name="Name">The event, one of <see cref="AuditEvents"/>. Any other name throws: the list is closed.</param>
/// <param name="Description">What happened, in words an operator can read. A caller's value in it is escaped already (SafeText).</param>
public sealed record AuditEvent(string Name, string Description)
{
    /// <summary>The event's level: the vocabulary's own unless set.</summary>
    public AuditLevel Level { get; init; } = AuditEvents.LevelOf(Name);

    /// <summary>The W3C trace the request belongs to.</summary>
    public string? TraceId { get; init; }

    /// <summary>The request's id: the HTTP request's, or the JSON-RPC message's over stdio.</summary>
    public string? RequestId { get; init; }

    /// <summary>Who asked; null when nobody was signed in.</summary>
    public AuditPrincipal? Principal { get; init; }

    /// <summary>The HTTP facts; null over stdio.</summary>
    public AuditHttp? Http { get; init; }

    /// <summary>What was asked for.</summary>
    public AuditMcp? Mcp { get; init; }

    /// <summary>The arguments as sent. The writer redacts them; nothing here is written as it is.</summary>
    public IDictionary<string, JsonElement>? Arguments { get; init; }

    /// <summary>The argument names the tool's policy marks sensitive.</summary>
    public IReadOnlyCollection<string>? SensitiveArguments { get; init; }

    /// <summary>What was decided, and by which rule.</summary>
    public AuditDecision? Decision { get; init; }

    /// <summary>The upstream an outbound call was for (the egress contract's events).</summary>
    public AuditUpstream? Upstream { get; init; }

    /// <summary>Further facts of this event, by name: the build's hash on sys_startup, the scheme that refused a token.</summary>
    public IReadOnlyDictionary<string, string?>? Detail { get; init; }

    /// <summary>An event of the server's own, outside any request.</summary>
    public static AuditEvent System(string name, string description, IReadOnlyDictionary<string, string?>? detail = null) =>
        new(name, description) { Detail = detail };
}

/// <summary>The caller, as the record names them. Never the token: its id appears as a hash (roadmap I8).</summary>
/// <param name="IdentityProvider">The identity provider that vouched for the caller.</param>
/// <param name="Subject">The caller's subject there.</param>
/// <param name="ClientId">The client the token was issued to, by the claim that identity provider's ClientIdClaim names.</param>
/// <param name="JtiSha256">The SHA-256 of the token's id, in hex.</param>
/// <param name="Scopes">The scopes the token carries.</param>
public sealed record AuditPrincipal(string IdentityProvider, string Subject, string? ClientId, string? JtiSha256, IReadOnlyList<string> Scopes)
{
    /// <summary>The key limits and strikes are kept under: <c>{idp}:{sub}</c>.</summary>
    public string Key => $"{IdentityProvider}:{Subject}";

    /// <summary>Reads the caller from a principal the identity layer normalised, or null when it cannot be attributed.</summary>
    public static AuditPrincipal? From(ClaimsPrincipal? principal, AuthenticationConfig? identity)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return null;
        }

        var idp = principal.FindFirst(TrustDomainClaims.IdentityProvider)?.Value;
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(idp) || string.IsNullOrEmpty(subject))
        {
            return null;
        }

        string? client = null;
        if (identity is not null && identity.IdentityProviders.TryGetValue(idp, out var provider))
        {
            client = principal.FindFirst(provider.ClientIdClaim.Trim())?.Value;
        }

        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        return new AuditPrincipal(
            idp,
            subject,
            string.IsNullOrEmpty(client) ? null : client,
            string.IsNullOrEmpty(jti) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(jti))),
            [.. principal.FindAll(TrustDomainClaims.Scope).Select(c => c.Value).Order(StringComparer.Ordinal)]);
    }
}

/// <summary>The HTTP facts of a request (roadmap LOG-02), captured once, after forwarded headers (contract-006 · G-10).</summary>
/// <param name="SourceIp">The client's address, as forwarded headers from a trusted proxy established it.</param>
/// <param name="RequestUri">The request's path. Never its query, where a credential can ride.</param>
/// <param name="RequestMethod">The HTTP method.</param>
/// <param name="UserAgent">The User-Agent header, as sent.</param>
/// <param name="RequestId">The server's own id for the request.</param>
public sealed record AuditHttp(string? SourceIp, string? RequestUri, string? RequestMethod, string? UserAgent, string? RequestId)
{
    private static readonly AsyncLocal<AuditHttp?> Ambient = new();
    private static readonly AsyncLocal<HttpContext?> AmbientContext = new();

    /// <summary>The HTTP request this code runs under, when there is one.</summary>
    public static AuditHttp? Current => Ambient.Value;

    /// <summary>
    /// Who the HTTP request this code runs under is from, once authentication has said: read from the request when it is
    /// asked for, since the facts above are captured before anyone is signed in.
    /// </summary>
    public static ClaimsPrincipal? CurrentUser => AmbientContext.Value?.User;

    /// <summary>The HTTP request itself, as an identity: what the way in and the way out of one request share.</summary>
    internal static object? CurrentContext => AmbientContext.Value;

    /// <summary>Reads the facts from <paramref name="context"/>.</summary>
    public static AuditHttp From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AuditHttp(
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Path.ToString(),
            context.Request.Method,
            context.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null,
            context.TraceIdentifier);
    }

    /// <summary>Makes <paramref name="context"/>'s request the one everything downstream of the caller runs under.</summary>
    internal static void Enter(HttpContext context)
    {
        Ambient.Value = From(context);
        AmbientContext.Value = context;
    }
}

/// <summary>What a request asked for.</summary>
/// <param name="Method">The JSON-RPC method, as the caller wrote it.</param>
/// <param name="Provider">The provider that owns the target, once the frame has found it.</param>
/// <param name="Kind">tool, resource, prompt, completion or list; null for a request with no item.</param>
/// <param name="Target">The tool's name, the resource's URI or template, the prompt's name: the key it is governed under, or what the caller sent when there is none.</param>
/// <param name="Risk">The tool's risk class.</param>
public sealed record AuditMcp(string? Method, string? Provider = null, string? Kind = null, string? Target = null, string? Risk = null);

/// <summary>A decision and the rule that made it.</summary>
/// <param name="Outcome">Allowed, refused, or asked to confirm.</param>
/// <param name="Rule">The rule that refused or asked; null when the request was simply allowed.</param>
public sealed record AuditDecision(AuditOutcome Outcome, string? Rule = null);

/// <summary>The upstream an outbound call was for.</summary>
/// <param name="Host">The host named.</param>
/// <param name="Method">The HTTP method.</param>
public sealed record AuditUpstream(string Host, string Method);
