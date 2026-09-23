namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// How much harm a tool can do, which decides the gate in front of it (roadmap §3.3).
/// </summary>
public enum RiskClass
{
    /// <summary>Reads; a valid token, its scope and the limits are enough.</summary>
    Read,

    /// <summary>Changes something that can be changed back; also needs a token under 5 minutes old.</summary>
    Write,

    /// <summary>
    /// Changes something that cannot be changed back; also needs a token under 60 seconds old and a
    /// single-use confirmation tied to the exact arguments.
    /// </summary>
    Irreversible,
}

/// <summary>
/// What one tool is allowed (contract-003 · G-1). Declared in code by the provider module, keyed by
/// the tool's wire name, and copied by the frame at startup so nothing can change it afterwards.
/// </summary>
/// <param name="Scope">The scope a token must carry, from the bound identity provider's catalog.</param>
/// <param name="Risk">The risk class, which decides the gate and the tool's annotations.</param>
/// <param name="PerPrincipalPerMinute">How many calls one caller may make to this tool per minute.</param>
/// <param name="MaxOutputBytes">An answer larger than this is refused rather than returned.</param>
/// <param name="MaxStringArgLength">The longest string any argument may carry.</param>
/// <param name="Idempotent">Whether calling twice has the effect of calling once; shown to clients.</param>
public sealed record ToolPolicy(
    string Scope,
    RiskClass Risk,
    int PerPrincipalPerMinute,
    int MaxOutputBytes = 32_768,
    int MaxStringArgLength = 512,
    bool Idempotent = false);

/// <summary>What one resource is allowed, keyed by its URI or URI template.</summary>
/// <param name="Scope">The scope a token must carry to list or read it.</param>
public sealed record ResourcePolicy(string Scope);

/// <summary>What one prompt is allowed, keyed by its name.</summary>
/// <param name="Scope">The scope a token must carry to list it, get it or complete its arguments.</param>
public sealed record PromptPolicy(string Scope);

/// <summary>
/// Where a provider may call out to, and how. Declared and checked at startup by this contract;
/// enforced on every outbound call by the second half of Phase 2.
/// </summary>
/// <param name="Hosts">Exact host names. No wildcards, no IP literals; the scheme is always HTTPS.</param>
/// <param name="Methods">The HTTP methods the provider may use.</param>
/// <param name="MaxResponseBytes">The largest upstream response the provider may read.</param>
/// <param name="AttemptTimeout">How long one attempt may take.</param>
/// <param name="MaxRetryAttempts">How many times a failed attempt may be retried.</param>
/// <param name="TotalTimeout">How long the whole call may take; must exceed every attempt plus retries.</param>
/// <param name="MaxConcurrentUpstreamCalls">How many calls to the upstream may be in flight at once.</param>
/// <param name="DailyCallBudget">How many upstream calls the provider may make per day.</param>
public sealed record EgressPolicy(
    IReadOnlyList<string> Hosts,
    IReadOnlyList<HttpMethod> Methods,
    long MaxResponseBytes,
    TimeSpan AttemptTimeout,
    int MaxRetryAttempts,
    TimeSpan TotalTimeout,
    int MaxConcurrentUpstreamCalls,
    int DailyCallBudget);

/// <summary>
/// Everything one provider is allowed to do. The frame enforces it; provider code cannot alter it
/// once the server has started, because the frame keeps its own frozen copy.
/// </summary>
/// <param name="Name">The provider's name, matching its <c>Providers:{Name}</c> configuration section.</param>
/// <param name="Egress">Where it may call out to.</param>
/// <param name="Tools">A policy for every tool it exposes, keyed by wire name.</param>
/// <param name="Resources">A policy for every resource it exposes, keyed by URI or URI template.</param>
/// <param name="Prompts">A policy for every prompt it exposes, keyed by name.</param>
public sealed record ProviderPolicy(
    string Name,
    EgressPolicy Egress,
    IReadOnlyDictionary<string, ToolPolicy> Tools,
    IReadOnlyDictionary<string, ResourcePolicy> Resources,
    IReadOnlyDictionary<string, PromptPolicy> Prompts);
