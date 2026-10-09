using System.Security.Claims;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Audit;
using McpServerTemplate.Infrastructure.Identity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// The checks every request passes, in a fixed order (contract-003 · G-4, G-5, G-7, G-8, G-9):
/// the request kind is one the frame governs; the caller is signed in; the item exists; it belongs
/// to a provider bound to the caller's identity provider; the caller holds its scope; the risk gate
/// holds; the caller is within their limits; the arguments fit the declared shape; and the answer
/// fits its cap. Each refusal is logged as a security event with its rule, and names its rule to
/// the caller — but a tool, resource or prompt of a provider bound to another identity provider,
/// which is answered as one that does not exist (contract-005 · T-5, the pioneer's decision of
/// 2026-09-29; for the resource and the prompt, G-17 round 2) while the log keeps idp-binding.
/// </summary>
public sealed class RequestGate(
    PolicyRegistry registry,
    ILimitStore limits,
    TimeProvider clock,
    int perPrincipalPerMinute,
    ConfirmationService? confirmation,
    ILogger<RequestGate> logger,
    AuditTrail audit)
{
    /// <summary>A write needs a token issued within this long (roadmap §3.3).</summary>
    public static readonly TimeSpan WriteFreshness = TimeSpan.FromMinutes(5);

    /// <summary>An irreversible call needs a token issued within this long.</summary>
    public static readonly TimeSpan IrreversibleFreshness = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The request kinds the frame governs. Anything else — subscriptions, log levels, tasks, and any
    /// method a future protocol version adds — is refused until someone governs it (contract-003 · G-5).
    /// </summary>
    public static readonly IReadOnlySet<string> GovernedMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestMethods.Initialize,

        // How a client reaches the 2026-07-28 revision, which has no initialize handshake. Without
        // it every client falls back to an older revision — and loses the input-required round-trip
        // the Irreversible gate relies on. It answers with versions and capabilities, nothing else.
        RequestMethods.ServerDiscover,
        RequestMethods.Ping,
        RequestMethods.ToolsList,
        RequestMethods.ToolsCall,
        RequestMethods.ResourcesList,
        RequestMethods.ResourcesTemplatesList,
        RequestMethods.ResourcesRead,
        RequestMethods.PromptsList,
        RequestMethods.PromptsGet,
        RequestMethods.CompletionComplete,
        NotificationMethods.InitializedNotification,
        NotificationMethods.CancelledNotification,
    };

    /// <summary>
    /// contract-006 · G-2 — the governed kinds that name an item or a list, each with a request filter of its own that
    /// records its decision. Every other governed kind (sign-on, discovery, ping, a notification) is decided by its kind
    /// alone, so the request-kind gate records it.
    /// </summary>
    private static readonly HashSet<string> ItemMethods = new(StringComparer.Ordinal)
    {
        RequestMethods.ToolsList,
        RequestMethods.ToolsCall,
        RequestMethods.ResourcesList,
        RequestMethods.ResourcesTemplatesList,
        RequestMethods.ResourcesRead,
        RequestMethods.PromptsList,
        RequestMethods.PromptsGet,
        RequestMethods.CompletionComplete,
    };

    /// <summary>
    /// The request-kind gate: request kinds nobody governs go no further. The second incoming message filter, behind the
    /// audit step (contract-006 · G-1).
    /// </summary>
    public static McpMessageFilter RequestKinds() => next => async (context, cancellationToken) =>
    {
        var method = context.JsonRpcMessage switch
        {
            JsonRpcRequest request => request.Method,
            JsonRpcNotification notification => notification.Method,
            _ => null,
        };

        var scope = AuditScope.Enter(context);
        var trail = context.Services?.GetService<AuditTrail>();

        if (method is not null && GovernedMethods.Contains(method))
        {
            // contract-006 · G-2 — recorded before it is handed on. While the trail cannot be written, a kind decided
            // here is one that changes nothing, so it follows Audit:ReadWhenUnavailable: refused unless that says allow.
            if (!ItemMethods.Contains(method) && scope is not null && trail is not null
                && !scope.Record(trail, AuditEvents.McpRequest, "The request kind is one the frame governs; it is handed on after this record.", AuditOutcome.Allow)
                && !trail.ReadWhenUnavailable)
            {
                var unrecorded = AuditUnavailable();
                context.Services?.GetService<ILogger<RequestGate>>()?.LogWarning(
                    "{Event}: rule={Rule} method={Method} — {Detail}",
                    unrecorded.SecurityEvent, unrecorded.Rule, SafeText.Value(method), unrecorded.Message);

                if (context.JsonRpcMessage is JsonRpcRequest)
                {
                    throw unrecorded.ToException();
                }

                // A notification gets no answer; it simply goes no further.
                return;
            }

            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        // contract-005 · G-17 round 2 (SEC-R2-03) — the method is the caller's to write: made safe for a log line and cut
        // (LogSafe.Text), in the line and in the reply, which the SDK logs with the exception. Round 3 (SEC-R3-04) — escaped,
        // not stripped, and once: as SafeText, which the log pipeline passes on as it is.
        var shown = SafeText.Value(method ?? context.JsonRpcMessage.GetType().Name);

        // contract-006 · G-2 — the refusal goes on the audit trail first, as every refusal does (Refuse).
        if (scope is not null && trail is not null)
        {
            scope.Record(trail, AuditEvents.AuthzFail, SafeText.Line($"The frame does not govern '{shown}' requests, so it refuses them.").ToString(), AuditOutcome.Deny, "request-kind");
        }

        context.Services?.GetService<ILogger<RequestGate>>()?.LogWarning(
            "authz_fail: rule=request-kind method={Method} — refused, the frame does not govern it",
            shown);

        if (context.JsonRpcMessage is JsonRpcRequest)
        {
            throw new McpProtocolException(
                $"authz_fail (rule: request-kind). This server does not accept '{shown}' requests.",
                McpErrorCode.MethodNotFound);
        }

        // A notification or a response nobody asked for gets no answer; it simply goes no further.
    };

    /// <summary>The tool-call filter.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        AuditScope.Enter(context);
        var claim = await gate.AdmitCallAsync(context, cancellationToken).ConfigureAwait(false);

        // contract-006 · G-8 — the tool's span, under the request's: what the tool calls carries the same trace.
        using var span = AuditFilter.Source.StartActivity("mcp.tool");
        span?.SetTag("mcp.tool", context.Params?.Name);
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        return gate.CheckOutput(context.Params?.Name ?? string.Empty, CallerOf(context, gate.Clock), result, claim);
    };

    /// <summary>The tool-list filter: a caller sees what it may call.</summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListTools() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        gate.AdmitList(context, caller);
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        result.Tools = [.. result.Tools.Where(t => gate.Registry.Tools.TryGetValue(t.Name, out var e) && Sees(caller, e.IdentityProvider, e.Policy.Scope))];
        return result;
    };

    /// <summary>The resource-list filter.</summary>
    public static McpRequestFilter<ListResourcesRequestParams, ListResourcesResult> ListResources() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        gate.AdmitList(context, caller);
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        result.Resources = [.. result.Resources.Where(r => SeesScoped(caller, gate.Registry.Resources, r.Uri))];
        return result;
    };

    /// <summary>The resource-template-list filter.</summary>
    public static McpRequestFilter<ListResourceTemplatesRequestParams, ListResourceTemplatesResult> ListResourceTemplates() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        gate.AdmitList(context, caller);
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        result.ResourceTemplates = [.. result.ResourceTemplates.Where(r => SeesScoped(caller, gate.Registry.Resources, r.UriTemplate))];
        return result;
    };

    /// <summary>The prompt-list filter.</summary>
    public static McpRequestFilter<ListPromptsRequestParams, ListPromptsResult> ListPrompts() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        gate.AdmitList(context, caller);
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        result.Prompts = [.. result.Prompts.Where(p => SeesScoped(caller, gate.Registry.Prompts, p.Name))];
        return result;
    };

    /// <summary>
    /// The resource-read filter.
    ///
    /// contract-005 · G-17 round 2 (SEC-R2-02) — the SDK matches the URI read to a resource ignoring the case of its scheme
    /// and host and reading an empty path as /, and matches a resource template to any URI it describes. The resource is
    /// governed under its own key (its URI, or its URI template), which is what the gate looks up and what the log records;
    /// the reply names the URI as the caller sent it, so SMHI://weather-symbols is refused naming SMHI://weather-symbols, as
    /// SMHI://no-such-thing is, and never names the key it was matched to, which confirmed that a hidden resource exists.
    /// </summary>
    public static McpRequestFilter<ReadResourceRequestParams, ReadResourceResult> ReadResource() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var key = context.MatchedPrimitive is McpServerResource resource ? PolicyRegistry.KeyOf(resource) : context.Params?.Uri;
        await gate.AdmitScopedAsync(context, gate.Registry.Resources, key, context.Params?.Uri, "resource", AuditEvents.McpResourceRead, cancellationToken).ConfigureAwait(false);
        return await next(context, cancellationToken).ConfigureAwait(false);
    };

    /// <summary>The prompt-get filter. contract-005 · G-17 round 2 (SEC-R2-02) — the reply names the prompt as the caller sent it.</summary>
    public static McpRequestFilter<GetPromptRequestParams, GetPromptResult> GetPrompt() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var key = context.MatchedPrimitive is McpServerPrompt prompt ? prompt.ProtocolPrompt.Name : context.Params?.Name;
        await gate.AdmitScopedAsync(context, gate.Registry.Prompts, key, context.Params?.Name, "prompt", AuditEvents.McpPromptGet, cancellationToken).ConfigureAwait(false);
        return await next(context, cancellationToken).ConfigureAwait(false);
    };

    /// <summary>The completion filter: completing a prompt's or resource's arguments needs that prompt's or resource's scope.</summary>
    public static McpRequestFilter<CompleteRequestParams, CompleteResult> Complete() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        switch (context.Params?.Ref)
        {
            case PromptReference prompt:
                await gate.AdmitScopedAsync(context, gate.Registry.Prompts, prompt.Name, prompt.Name, "prompt", AuditEvents.McpRequest, cancellationToken).ConfigureAwait(false);
                break;
            case ResourceTemplateReference resource:
                await gate.AdmitScopedAsync(context, gate.Registry.Resources, resource.Uri, resource.Uri, "resource", AuditEvents.McpRequest, cancellationToken).ConfigureAwait(false);
                break;
            default:
                AuditScope.Enter(context)?.Describe(kind: "completion");
                throw gate.Refuse(CallerOf(context, gate.Clock), "completion", Refusal.Of("unknown-item", "authz_fail", "Completion is available only for a prompt or a resource template you may use."));
        }

        return await next(context, cancellationToken).ConfigureAwait(false);
    };

    internal TimeProvider Clock => clock;

    internal PolicyRegistry Registry => registry;

    private async Task<string?> AdmitCallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var name = context.Params?.Name ?? string.Empty;

        // contract-006 · G-2 — what is asked for, told to the audit scope as it is learned: every record of this request,
        // whichever check refuses it, then says what it was for. The arguments are redacted by the record's writer.
        var scope = AuditScope.Enter(context);
        scope?.Describe(kind: "tool", target: name, arguments: context.Params?.Arguments);
        var caller = CallerOf(context, clock) ?? throw Refuse(null, name, NotSignedIn());

        if (!registry.Tools.TryGetValue(name, out var entry))
        {
            throw Refuse(caller, name, NotPermitted("tool", name));
        }

        scope?.Describe(provider: entry.Provider, risk: entry.Policy.Risk.ToString(), sensitive: entry.Policy.SensitiveArgs);

        if (caller.IdentityProvider != entry.IdentityProvider)
        {
            // contract-005 · T-5 — the pioneer's decision of 2026-09-29 (contract-005's revision of that date, "1: A"): a caller
            // from another trust domain is answered in the words for a tool that does not exist, as a resource or a prompt of
            // another trust domain already is (AdmitScopedAsync), so the reply never confirms that the tool exists in a trust
            // domain the caller is not in. The log keeps the rule that refused it, idp-binding, and why. Until then the reply
            // named idp-binding, and told the caller the tool was there (contract-002 · T-6 asserted it; it now asserts the log).
            throw Refuse(caller, name, Refusal.Concealed(
                "idp-binding",
                "authz_fail",
                $"The tool '{name}' belongs to a provider bound to a different identity provider than the one that issued the caller's token; the binding is a deployment decision, not a scope the caller can request. The caller was answered as for a tool that does not exist.",
                NotPermitted("tool", name)));
        }

        if (!caller.Scopes.Contains(entry.Policy.Scope))
        {
            throw Refuse(caller, name, Refusal.Of(
                "insufficient_scope",
                "authz_fail",
                $"The tool '{name}' requires the scope '{entry.Policy.Scope}', which your token does not carry. Request a token with that scope and call again."));
        }

        string? confirmationId = null;
        if (entry.Policy.Risk != RiskClass.Read)
        {
            var freshness = entry.Policy.Risk == RiskClass.Write ? WriteFreshness : IrreversibleFreshness;
            var age = clock.GetUtcNow() - caller.IssuedAt;

            // contract-005 · G-17 round 1 — a token that says it was issued later than now has a negative age, and counted as
            // fresh until its iat plus the freshness window, however long ago it was really issued: an identity provider whose
            // clock runs ahead, on a token with no nbf, held the gate open on one side. Past the clock skew the token's
            // validation allows (IdentityRegistration.ClockSkew; Confirmation bounds its own age the same way), its age
            // cannot be told, so it is refused by this rule. Follow-up — authentication refuses such a token first, for every
            // request (IdentityRegistration, OnTokenValidated); this stays, as defence in depth, should one ever reach the gate.
            if (age < -IdentityRegistration.ClockSkew)
            {
                throw Refuse(caller, name, Refusal.Of(
                    "token-age",
                    "authz_fail",
                    $"The tool '{name}' is a {entry.Policy.Risk} tool and needs a token issued within the last {freshness.TotalSeconds:0} seconds; yours says it was issued {-age.TotalSeconds:0} seconds from now, further ahead of this server's clock than the {IdentityRegistration.ClockSkew.TotalSeconds:0} seconds it allows, so its age cannot be told. Get a fresh token from an issuer whose clock is right, and call again."));
            }

            if (age > freshness)
            {
                throw Refuse(caller, name, Refusal.Of(
                    "token-age",
                    "authz_fail",
                    $"The tool '{name}' is a {entry.Policy.Risk} tool and needs a token issued within the last {freshness.TotalSeconds:0} seconds; yours was issued {age.TotalSeconds:0} seconds ago. Get a fresh token and call again."));
            }
        }

        if (entry.Policy.Risk == RiskClass.Irreversible)
        {
            confirmationId = Confirm(context, caller, name);
        }

        await WithinLimitsAsync(caller, name, entry.Policy.PerPrincipalPerMinute, cancellationToken).ConfigureAwait(false);

        if (entry.Guard.Check(context.Params?.Arguments) is { } badArguments)
        {
            throw Refuse(caller, name, badArguments);
        }

        // Claimed last, so a confirmation is spent only on a call that is about to run.
        if (confirmationId is not null &&
            !await Store(() => limits.TryClaimOnceAsync($"confirm:{confirmationId}", ConfirmationService.Validity + TimeSpan.FromSeconds(30), cancellationToken), caller, name).ConfigureAwait(false))
        {
            throw Refuse(caller, name, Refusal.Of(
                "confirmation-replayed",
                "authz_fail",
                "This confirmation has already been used. Each confirmation runs the tool once; call the tool again to be asked afresh."));
        }

        // contract-006 · G-2 — the last thing before the tool runs: its admission, on the audit trail. After the
        // confirmation is claimed, so one request leaves one admission; a confirmation spent on a call the trail could not
        // record is spent, and the caller is asked afresh.
        RecordAdmission(
            caller,
            name,
            confirmationId is null ? AuditEvents.McpToolCall : AuditEvents.McpToolConfirmed,
            confirmationId is null
                ? "The tool call was admitted; the tool runs after this record."
                : "The irreversible tool call was admitted on a verified, unused confirmation; the tool runs once, after this record.",
            change: entry.Policy.Risk != RiskClass.Read);

        return confirmationId;
    }

    private string? Confirm(RequestContext<CallToolRequestParams> context, Caller caller, string name)
    {
        if (confirmation is null)
        {
            // Unreachable when startup validation passed; refused rather than assumed.
            throw Refuse(caller, name, Refusal.Of("confirmation", "authz_fail", "This server cannot confirm irreversible calls."));
        }

        var state = context.Params?.RequestState;
        if (state is null)
        {
            if (!context.Server.IsMrtrSupported)
            {
                throw Refuse(caller, name, Refusal.Of(
                    "confirmation-unsupported",
                    "authz_fail",
                    $"The tool '{name}' cannot be undone and runs only after a confirmation round-trip, which your client does not support. Use a client that supports input requests."));
            }

            // contract-006 · G-2 — asking is a decision too: recorded before the question goes out. An irreversible tool's
            // request the trail cannot record is refused, as its run would be.
            if (!TryRecord(AuditEvents.McpConfirmationRequired, "The tool cannot be undone; the caller was asked to confirm, and nothing ran.", AuditOutcome.Challenge, "confirmation"))
            {
                throw Refuse(caller, name, AuditUnavailable());
            }

            throw new InputRequiredException(
                new Dictionary<string, InputRequest> { [ConfirmationService.InputKey] = ConfirmationService.Question(name) },
                confirmation.Issue(caller, name, context.Params?.Arguments));
        }

        var (id, refusal) = confirmation.Verify(state, context.Params?.InputResponses, caller, name, context.Params?.Arguments);
        return refusal is null ? id : throw Refuse(caller, name, refusal);
    }

    /// <param name="context">The request.</param>
    /// <param name="entries">The resources or prompts the frame governs, by key.</param>
    /// <param name="key">What the caller asked for is governed under: the matched resource's URI or URI template, the prompt's name.</param>
    /// <param name="sent">What the caller asked for, as they wrote it.</param>
    /// <param name="kind">resource or prompt.</param>
    /// <param name="admitted">contract-006 — the event its admission is recorded as.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    private async Task AdmitScopedAsync<TParams>(
        RequestContext<TParams> context,
        System.Collections.Frozen.FrozenDictionary<string, PolicyRegistry.ScopedEntry> entries,
        string? key,
        string? sent,
        string kind,
        string admitted,
        CancellationToken cancellationToken)
    {
        // contract-005 · G-17 round 2 (SEC-R2-02) — a refusal names what the caller asked for as they wrote it, never the key
        // it was matched to: a missing item's reply names what was sent, and so must a hidden one's. The log names the key.
        var asked = sent ?? key ?? string.Empty;

        // contract-006 · G-2 — as for a tool (AdmitCallAsync): the audit scope is told what is asked for as it is learned.
        var scope = AuditScope.Enter(context);
        scope?.Describe(kind: kind, target: key ?? asked);
        var caller = CallerOf(context, clock) ?? throw Refuse(null, key ?? kind, NotSignedIn());
        if (key is null || !entries.TryGetValue(key, out var entry))
        {
            throw Refuse(caller, key ?? kind, NotPermitted(kind, asked));
        }

        scope?.Describe(provider: entry.Provider);

        if (caller.IdentityProvider != entry.IdentityProvider)
        {
            // contract-005 · G-17 round 2 — the pioneer's decision of 2026-09-29 ("1: A"), as for a tool (AdmitCallAsync): the
            // reply is the one an item that does not exist gets, and the log keeps the rule that refused it, idp-binding. A
            // resource or a prompt of another trust domain was logged as not-permitted, a tool as idp-binding.
            throw Refuse(caller, key, Refusal.Concealed(
                "idp-binding",
                "authz_fail",
                $"The {kind} '{key}' belongs to a provider bound to a different identity provider than the one that issued the caller's token; the binding is a deployment decision, not a scope the caller can request. The caller was answered as for a {kind} that does not exist.",
                NotPermitted(kind, asked)));
        }

        if (!caller.Scopes.Contains(entry.Scope))
        {
            throw Refuse(caller, key, NotPermitted(kind, asked));
        }

        await WithinLimitsAsync(caller, null, 0, cancellationToken).ConfigureAwait(false);

        // contract-006 · G-2 — admitted: on the audit trail before the resource is read or the prompt built.
        RecordAdmission(caller, key, admitted, $"The {kind} was admitted; it is served after this record.", change: false);
    }

    /// <summary>
    /// contract-006 · G-2 — a list's admission, recorded before the list is built. A list shows a caller what they may
    /// use and changes nothing, so while the trail cannot be written it follows Audit:ReadWhenUnavailable.
    /// </summary>
    private void AdmitList<TParams>(RequestContext<TParams> context, Caller? caller)
    {
        AuditScope.Enter(context)?.Describe(kind: "list");
        RecordAdmission(caller, "list", AuditEvents.McpRequest, "The list was admitted; it shows the caller what they may use.", change: false);
    }

    /// <summary>
    /// contract-006 · G-2, G-4 — writes the admission of a request to the audit trail, and returns only if what follows
    /// may run. When the trail cannot take the record, a request that changes something is refused, and any other is
    /// refused too unless Audit:ReadWhenUnavailable says allow: nothing the frame governs takes effect on a decision that
    /// is not on the record. The trail is asked every time; whether it took the last record is never remembered here.
    /// </summary>
    private void RecordAdmission(Caller? caller, string target, string name, string description, bool change)
    {
        if (TryRecord(name, description, AuditOutcome.Allow, rule: null) || (!change && audit.ReadWhenUnavailable))
        {
            return;
        }

        throw Refuse(caller, target, AuditUnavailable());
    }

    /// <summary>Writes one record about the request being decided on this path. False when the trail could not take it.</summary>
    private bool TryRecord(string name, string description, AuditOutcome outcome, string? rule) =>
        AuditScope.Current is { } scope
            ? scope.Record(audit, name, description, outcome, rule)
            : audit.Record(new AuditEvent(name, description) { Decision = new AuditDecision(outcome, rule) });

    private static Refusal AuditUnavailable() =>
        Refusal.Of(
            "audit-unavailable",
            AuditEvents.SysMonitorDisabled,
            "The server cannot record this request right now, so it refuses it rather than act unrecorded. Try again shortly.");

    private async Task WithinLimitsAsync(Caller caller, string? tool, int toolLimit, CancellationToken cancellationToken)
    {
        if (!await Store(() => limits.TryAcquireAsync($"caller:{caller.Key}", perPrincipalPerMinute, Minute, cancellationToken), caller, tool ?? "request").ConfigureAwait(false))
        {
            throw Refuse(caller, tool ?? "request", Refusal.Of(
                "caller-rate",
                "excess_rate_limit_exceeded",
                $"You have made {perPrincipalPerMinute} requests in the last minute, which is your limit. Wait and try again."));
        }

        if (tool is not null &&
            !await Store(() => limits.TryAcquireAsync($"tool:{caller.Key}:{tool}", toolLimit, Minute, cancellationToken), caller, tool).ConfigureAwait(false))
        {
            throw Refuse(caller, tool, Refusal.Of(
                "tool-rate",
                "excess_rate_limit_exceeded",
                $"You have called '{tool}' {toolLimit} times in the last minute, which is its limit. Wait and try again."));
        }
    }

    // The limit store failing is a refusal, never permission (roadmap D7).
    private async Task<bool> Store(Func<Task<bool>> operation, Caller caller, string target)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException or ObjectDisposedException)
        {
            throw Refuse(caller, target, Refusal.Of(
                "limits-unavailable",
                "sys_availability",
                "The server cannot check your limits right now, so it refuses rather than guessing. Try again shortly."));
        }
    }

    private CallToolResult CheckOutput(string name, Caller? caller, CallToolResult result, string? claim)
    {
        _ = claim;
        if (!registry.Tools.TryGetValue(name, out var entry))
        {
            return result;
        }

        var size = JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult))).Length;
        if (size > entry.Policy.MaxOutputBytes)
        {
            throw Refuse(caller, name, Refusal.Of(
                "output-cap",
                "mcp_tool_denied",
                $"The tool '{name}' produced {size} bytes, more than the {entry.Policy.MaxOutputBytes} its policy allows. The answer is withheld rather than cut short, because a truncated answer can mislead."));
        }

        return result;
    }

    private static bool SeesScoped(Caller? caller, System.Collections.Frozen.FrozenDictionary<string, PolicyRegistry.ScopedEntry> entries, string key) =>
        entries.TryGetValue(key, out var entry) && Sees(caller, entry.IdentityProvider, entry.Scope);

    private static bool Sees(Caller? caller, string identityProvider, string scope) =>
        caller is not null && caller.IdentityProvider == identityProvider && caller.Scopes.Contains(scope);

    // contract-005 · G-17 round 2 (SEC-R2-03) — the target is often the caller's to write (a tool that is not there, a URI
    // matched to nothing), and so is the principal's subject at some identity providers: each is made safe for a log line
    // and cut (LogSafe.Text). The message names what the caller wrote only through the refusals built for it, which make it
    // safe themselves (NotPermitted, ArgumentGuard). Round 3 (SEC-R3-04) — escaped, not stripped: a target or a subject
    // that differs from a real one by an invisible character is logged as what it is, and each is SafeText, escaped once.
    //
    // contract-006 · G-2 — and every refusal goes on the audit trail here, before the caller is answered: the one path all
    // of the gate's refusals take, so none can be made that is not recorded. In the log's words, never the words a
    // concealed refusal is shown as. A refusal the trail cannot take is a refusal still.
    private McpException Refuse(Caller? caller, string target, Refusal refusal)
    {
        var scope = AuditScope.Current;
        scope?.Describe(target: scope.Target ?? target);
        TryRecord(
            AuditEvents.IsKnown(refusal.SecurityEvent) ? refusal.SecurityEvent : AuditEvents.AuthzFail,
            refusal.Message.ToString(),
            AuditOutcome.Deny,
            refusal.Rule);

        logger.LogWarning(
            "{Event}: rule={Rule} principal={Principal} target={Target} — {Detail}",
            refusal.SecurityEvent, refusal.Rule, SafeText.Value(caller?.Key ?? "anonymous"), SafeText.Value(target), refusal.Message);
        return refusal.ToException();
    }

    private static Refusal NotSignedIn() =>
        Refusal.Of("principal", "authz_fail", "The request carries no principal the server can attribute. Sign in with a configured identity provider.");

    // Something hidden and something forbidden are refused in the same words, so a refusal does not
    // confirm that a hidden item exists; and so is a tool, a resource or a prompt of another trust domain
    // (contract-005 · T-5; G-17 round 2 for the resource and the prompt), whose log line keeps the rule that refused it.
    // contract-005 · G-17 round 2 — the name is as the caller wrote it (SEC-R2-02), made safe for a log line and cut
    // (SEC-R2-03): the reply is logged too, by the SDK, with the exception it travels in. Round 3 (SEC-R3-04) — escaped, not
    // stripped: get_fore + U+200B + cast is answered as get_fore​cast, what was sent, and never as the real get_forecast.
    private static Refusal NotPermitted(string kind, string name) =>
        Refusal.Of("not-permitted", "authz_fail", SafeText.Line($"You may not use the {kind} '{name}', or it does not exist."));

    private static RequestGate GateOf(IServiceProvider? services) =>
        services?.GetService<RequestGate>()
        ?? throw new McpException("authz_fail (rule: frame). The request gate is not available, so nothing is served.");

    private static Caller? CallerOf<TParams>(RequestContext<TParams> context, TimeProvider clock)
    {
        ClaimsPrincipal? principal = context.User;
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            // stdio has no request user; its Development principal is a registered singleton.
            principal = context.Services?.GetService<ClaimsPrincipal>();
        }

        return Caller.From(principal, clock);
    }
}
