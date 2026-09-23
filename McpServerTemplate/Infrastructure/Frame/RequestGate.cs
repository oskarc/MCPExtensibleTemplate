using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// The checks every request passes, in a fixed order (contract-003 · G-4, G-5, G-7, G-8, G-9):
/// the request kind is one the frame governs; the caller is signed in; the item exists; it belongs
/// to a provider bound to the caller's identity provider; the caller holds its scope; the risk gate
/// holds; the caller is within their limits; the arguments fit the declared shape; and the answer
/// fits its cap. Each refusal names its rule and is logged as a security event.
/// </summary>
public sealed class RequestGate(
    PolicyRegistry registry,
    ILimitStore limits,
    TimeProvider clock,
    int perPrincipalPerMinute,
    ConfirmationService? confirmation,
    ILogger<RequestGate> logger)
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

    /// <summary>The first incoming message filter: request kinds nobody governs go no further.</summary>
    public static McpMessageFilter RequestKinds() => next => async (context, cancellationToken) =>
    {
        var method = context.JsonRpcMessage switch
        {
            JsonRpcRequest request => request.Method,
            JsonRpcNotification notification => notification.Method,
            _ => null,
        };

        if (method is not null && GovernedMethods.Contains(method))
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Services?.GetService<ILogger<RequestGate>>()?.LogWarning(
            "authz_fail: rule=request-kind method={Method} — refused, the frame does not govern it",
            method ?? context.JsonRpcMessage.GetType().Name);

        if (context.JsonRpcMessage is JsonRpcRequest)
        {
            throw new McpProtocolException(
                $"authz_fail (rule: request-kind). This server does not accept '{method}' requests.",
                McpErrorCode.MethodNotFound);
        }

        // A notification or a response nobody asked for gets no answer; it simply goes no further.
    };

    /// <summary>The tool-call filter.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var claim = await gate.AdmitCallAsync(context, cancellationToken).ConfigureAwait(false);
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        return gate.CheckOutput(context.Params?.Name ?? string.Empty, CallerOf(context, gate.Clock), result, claim);
    };

    /// <summary>The tool-list filter: a caller sees what it may call.</summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListTools() => next => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        result.Tools = [.. result.Tools.Where(t => gate.Registry.Tools.TryGetValue(t.Name, out var e) && Sees(caller, e.IdentityProvider, e.Policy.Scope))];
        return result;
    };

    /// <summary>The resource-list filter.</summary>
    public static McpRequestFilter<ListResourcesRequestParams, ListResourcesResult> ListResources() => next => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        result.Resources = [.. result.Resources.Where(r => SeesScoped(caller, gate.Registry.Resources, r.Uri))];
        return result;
    };

    /// <summary>The resource-template-list filter.</summary>
    public static McpRequestFilter<ListResourceTemplatesRequestParams, ListResourceTemplatesResult> ListResourceTemplates() => next => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        result.ResourceTemplates = [.. result.ResourceTemplates.Where(r => SeesScoped(caller, gate.Registry.Resources, r.UriTemplate))];
        return result;
    };

    /// <summary>The prompt-list filter.</summary>
    public static McpRequestFilter<ListPromptsRequestParams, ListPromptsResult> ListPrompts() => next => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        var gate = GateOf(context.Services);
        var caller = CallerOf(context, gate.Clock);
        result.Prompts = [.. result.Prompts.Where(p => SeesScoped(caller, gate.Registry.Prompts, p.Name))];
        return result;
    };

    /// <summary>The resource-read filter.</summary>
    public static McpRequestFilter<ReadResourceRequestParams, ReadResourceResult> ReadResource() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var key = context.MatchedPrimitive is McpServerResource resource ? PolicyRegistry.KeyOf(resource) : context.Params?.Uri;
        await gate.AdmitScopedAsync(context, gate.Registry.Resources, key, "resource", cancellationToken).ConfigureAwait(false);
        return await next(context, cancellationToken).ConfigureAwait(false);
    };

    /// <summary>The prompt-get filter.</summary>
    public static McpRequestFilter<GetPromptRequestParams, GetPromptResult> GetPrompt() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        var key = context.MatchedPrimitive is McpServerPrompt prompt ? prompt.ProtocolPrompt.Name : context.Params?.Name;
        await gate.AdmitScopedAsync(context, gate.Registry.Prompts, key, "prompt", cancellationToken).ConfigureAwait(false);
        return await next(context, cancellationToken).ConfigureAwait(false);
    };

    /// <summary>The completion filter: completing a prompt's or resource's arguments needs that prompt's or resource's scope.</summary>
    public static McpRequestFilter<CompleteRequestParams, CompleteResult> Complete() => next => async (context, cancellationToken) =>
    {
        var gate = GateOf(context.Services);
        switch (context.Params?.Ref)
        {
            case PromptReference prompt:
                await gate.AdmitScopedAsync(context, gate.Registry.Prompts, prompt.Name, "prompt", cancellationToken).ConfigureAwait(false);
                break;
            case ResourceTemplateReference resource:
                await gate.AdmitScopedAsync(context, gate.Registry.Resources, resource.Uri, "resource", cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw gate.Refuse(CallerOf(context, gate.Clock), "completion", Refusal.Of("unknown-item", "authz_fail", "Completion is available only for a prompt or a resource template you may use."));
        }

        return await next(context, cancellationToken).ConfigureAwait(false);
    };

    internal TimeProvider Clock => clock;

    internal PolicyRegistry Registry => registry;

    private async Task<string?> AdmitCallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var name = context.Params?.Name ?? string.Empty;
        var caller = CallerOf(context, clock) ?? throw Refuse(null, name, NotSignedIn());

        if (!registry.Tools.TryGetValue(name, out var entry))
        {
            throw Refuse(caller, name, NotPermitted("tool", name));
        }

        if (caller.IdentityProvider != entry.IdentityProvider)
        {
            throw Refuse(caller, name, Refusal.Of(
                "idp-binding",
                "authz_fail",
                $"The tool '{name}' belongs to a provider bound to a different identity provider than the one that issued your token. This is not a scope you can request; the binding is a deployment decision."));
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

            throw new InputRequiredException(
                new Dictionary<string, InputRequest> { [ConfirmationService.InputKey] = ConfirmationService.Question(name) },
                confirmation.Issue(caller, name, context.Params?.Arguments));
        }

        var (id, refusal) = confirmation.Verify(state, context.Params?.InputResponses, caller, name, context.Params?.Arguments);
        return refusal is null ? id : throw Refuse(caller, name, refusal);
    }

    private async Task AdmitScopedAsync<TParams>(
        RequestContext<TParams> context,
        System.Collections.Frozen.FrozenDictionary<string, PolicyRegistry.ScopedEntry> entries,
        string? key,
        string kind,
        CancellationToken cancellationToken)
    {
        var caller = CallerOf(context, clock) ?? throw Refuse(null, key ?? kind, NotSignedIn());
        if (key is null || !entries.TryGetValue(key, out var entry) || !Sees(caller, entry.IdentityProvider, entry.Scope))
        {
            throw Refuse(caller, key ?? kind, NotPermitted(kind, key ?? string.Empty));
        }

        await WithinLimitsAsync(caller, null, 0, cancellationToken).ConfigureAwait(false);
    }

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

    private McpException Refuse(Caller? caller, string target, Refusal refusal)
    {
        logger.LogWarning(
            "{Event}: rule={Rule} principal={Principal} target={Target} — {Detail}",
            refusal.SecurityEvent, refusal.Rule, caller?.Key ?? "anonymous", target, refusal.Message);
        return refusal.ToException();
    }

    private static Refusal NotSignedIn() =>
        Refusal.Of("principal", "authz_fail", "The request carries no principal the server can attribute. Sign in with a configured identity provider.");

    // Something hidden and something forbidden are refused in the same words, so a refusal does not
    // confirm that a hidden item exists.
    private static Refusal NotPermitted(string kind, string name) =>
        Refusal.Of("not-permitted", "authz_fail", $"You may not use the {kind} '{name}', or it does not exist.");

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
