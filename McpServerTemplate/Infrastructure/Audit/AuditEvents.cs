using System.Collections.Frozen;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>How serious a recorded event is, in the OWASP logging vocabulary's three words.</summary>
public enum AuditLevel
{
    /// <summary>Something that happened as it should.</summary>
    Info,

    /// <summary>Something an operator should be able to find.</summary>
    Warn,

    /// <summary>Something a security monitor should alert on.</summary>
    Critical,
}

/// <summary>What a decision did with the request.</summary>
public enum AuditOutcome
{
    /// <summary>Let through.</summary>
    Allow,

    /// <summary>Refused.</summary>
    Deny,

    /// <summary>Asked to confirm; nothing ran.</summary>
    Challenge,
}

/// <summary>
/// Every event name the audit trail can carry (contract-006 · G-3): the OWASP logging vocabulary's names (roadmap LOG-01)
/// and the project's own extensions (roadmap §3.5, and this contract's). The list is closed: the writer refuses a name
/// that is not here, so a typo cannot become an event no alert looks for, and the alert rules shipped with the repository
/// are checked against it (docs/siem/alerts).
/// </summary>
public static class AuditEvents
{
    // ── OWASP logging vocabulary ──

    /// <summary>A token the server refused.</summary>
    public const string AuthnLoginFail = "authn_login_fail";

    /// <summary>A token revoked at its identity provider (Phase 4).</summary>
    public const string AuthnTokenRevoked = "authn_token_revoked";

    /// <summary>A revoked principal's token presented again (Phase 4).</summary>
    public const string AuthnTokenReuse = "authn_token_reuse";

    /// <summary>A request the caller was not allowed to make.</summary>
    public const string AuthzFail = "authz_fail";

    /// <summary>An administrative action (Phase 4).</summary>
    public const string AuthzAdmin = "authz_admin";

    /// <summary>A rate limit reached.</summary>
    public const string ExcessRateLimitExceeded = "excess_rate_limit_exceeded";

    /// <summary>Input that does not fit its declared shape.</summary>
    public const string InputValidationFail = "input_validation_fail";

    /// <summary>Input the declared shape has no place for.</summary>
    public const string MaliciousExtraneous = "malicious_extraneous";

    /// <summary>A request from an origin this server does not answer for.</summary>
    public const string MaliciousCors = "malicious_cors";

    /// <summary>Instructions found in a tool's output (Phase 4).</summary>
    public const string McpPromptInjection = "mcp_prompt_injection";

    /// <summary>A provider's budget or concurrency exhausted (the egress contract).</summary>
    public const string McpResourceExhaustion = "mcp_resource_exhaustion";

    /// <summary>A tool definition that is not the one that was reviewed (Phase 5).</summary>
    public const string McpToolPoisoning = "mcp_tool_poisoning";

    /// <summary>The server began serving.</summary>
    public const string SysStartup = "sys_startup";

    /// <summary>The server stopped cleanly.</summary>
    public const string SysShutdown = "sys_shutdown";

    /// <summary>The server stopped on a failure nobody handled.</summary>
    public const string SysCrash = "sys_crash";

    /// <summary>The audit trail stopped accepting records.</summary>
    public const string SysMonitorDisabled = "sys_monitor_disabled";

    // ── Project extensions (roadmap §3.5) ──

    /// <summary>A tool call admitted; recorded before it runs.</summary>
    public const string McpToolCall = "mcp_tool_call";

    /// <summary>A tool's answer withheld.</summary>
    public const string McpToolDenied = "mcp_tool_denied";

    /// <summary>An irreversible tool call admitted on a verified confirmation; recorded before it runs.</summary>
    public const string McpToolConfirmed = "mcp_tool_confirmed";

    /// <summary>An outbound call the frame refused (the egress contract).</summary>
    public const string McpEgressDenied = "mcp_egress_denied";

    /// <summary>A revocation the identity provider did not take (Phase 4).</summary>
    public const string McpRevocationFailed = "mcp_revocation_failed";

    // ── Project extensions (contract-006) ──

    /// <summary>A governed request with no item of its own admitted: sign-on, discovery, ping, a list, a completion, a notification.</summary>
    public const string McpRequest = "mcp_request";

    /// <summary>A resource read admitted; recorded before it is read.</summary>
    public const string McpResourceRead = "mcp_resource_read";

    /// <summary>A prompt admitted; recorded before it is built.</summary>
    public const string McpPromptGet = "mcp_prompt_get";

    /// <summary>An irreversible tool call answered with a request to confirm; nothing ran.</summary>
    public const string McpConfirmationRequired = "mcp_confirmation_required";

    /// <summary>A store the frame depends on did not answer, so the request was refused (contract-003: the limit store).</summary>
    public const string SysAvailability = "sys_availability";

    /// <summary>Each event's level. The OWASP names carry the vocabulary's own; a stopped trail is this server's to call critical.</summary>
    public static FrozenDictionary<string, AuditLevel> Levels { get; } = new Dictionary<string, AuditLevel>(StringComparer.Ordinal)
    {
        [AuthnLoginFail] = AuditLevel.Warn,
        [AuthnTokenRevoked] = AuditLevel.Info,
        [AuthnTokenReuse] = AuditLevel.Critical,
        [AuthzFail] = AuditLevel.Critical,
        [AuthzAdmin] = AuditLevel.Warn,
        [ExcessRateLimitExceeded] = AuditLevel.Warn,
        [InputValidationFail] = AuditLevel.Warn,
        [MaliciousExtraneous] = AuditLevel.Critical,
        [MaliciousCors] = AuditLevel.Critical,
        [McpPromptInjection] = AuditLevel.Critical,
        [McpResourceExhaustion] = AuditLevel.Warn,
        [McpToolPoisoning] = AuditLevel.Critical,
        [SysStartup] = AuditLevel.Warn,
        [SysShutdown] = AuditLevel.Warn,
        [SysCrash] = AuditLevel.Warn,
        [SysMonitorDisabled] = AuditLevel.Critical,
        [McpToolCall] = AuditLevel.Info,
        [McpToolDenied] = AuditLevel.Warn,
        [McpToolConfirmed] = AuditLevel.Info,
        [McpEgressDenied] = AuditLevel.Critical,
        [McpRevocationFailed] = AuditLevel.Critical,
        [McpRequest] = AuditLevel.Info,
        [McpResourceRead] = AuditLevel.Info,
        [McpPromptGet] = AuditLevel.Info,
        [McpConfirmationRequired] = AuditLevel.Info,
        [SysAvailability] = AuditLevel.Warn,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="name"/> is an event the trail can carry.</summary>
    public static bool IsKnown(string name) => name is not null && Levels.ContainsKey(name);

    /// <summary>The level <paramref name="name"/> is recorded at.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not in the list.</exception>
    public static AuditLevel LevelOf(string name) =>
        name is not null && Levels.TryGetValue(name, out var level)
            ? level
            : throw new ArgumentException($"'{name}' is not an event the audit trail knows. Add it to {nameof(AuditEvents)}, with its level.", nameof(name));

    /// <summary>A level as the record writes it.</summary>
    public static string Word(AuditLevel level) => level switch
    {
        AuditLevel.Info => "INFO",
        AuditLevel.Warn => "WARN",
        _ => "CRITICAL",
    };

    /// <summary>An outcome as the record writes it.</summary>
    public static string Word(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Allow => "allow",
        AuditOutcome.Deny => "deny",
        _ => "challenge",
    };
}
