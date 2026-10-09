using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Identity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// What the audit trail knows about one incoming message while it is being decided (contract-006 · G-1): who sent it, how,
/// under which trace, and — as the gate works through it — what it asks for. Opened by the audit step, the first thing
/// every message meets, and carried in the message's own Items, which the SDK shares between the message filters and the
/// request filters. It is also the ambient <see cref="Current"/> for code that runs under a request without the SDK's
/// context in hand: the gate's one refusal path, and an outbound call a tool makes.
/// </summary>
public sealed class AuditScope
{
    private const string ItemsKey = "McpServerTemplate.Audit.Scope";

    private static readonly AsyncLocal<AuditScope?> Ambient = new();

    private int _attempts;

    private AuditScope(string method, string? requestId, AuditPrincipal? principal, AuditHttp? http, string traceId)
    {
        Method = method;
        RequestId = requestId;
        Principal = principal;
        Http = http;
        TraceId = traceId;
    }

    /// <summary>The message being decided on this path of execution, when there is one.</summary>
    public static AuditScope? Current => Ambient.Value;

    /// <summary>The JSON-RPC method, as the caller wrote it; for a message that has none, what kind of message it is.</summary>
    public string Method { get; }

    /// <summary>The HTTP request's id, or over stdio the JSON-RPC message's.</summary>
    public string? RequestId { get; }

    /// <summary>Who sent it; null when nobody is signed in.</summary>
    public AuditPrincipal? Principal { get; }

    /// <summary>The HTTP facts; null over stdio.</summary>
    public AuditHttp? Http { get; }

    /// <summary>The W3C trace the message belongs to.</summary>
    public string TraceId { get; }

    /// <summary>tool, resource, prompt, completion or list, once the gate knows.</summary>
    public string? Kind { get; private set; }

    /// <summary>What the message asks for, once the gate knows.</summary>
    public string? Target { get; private set; }

    /// <summary>The provider that owns the target, once the gate has found it.</summary>
    public string? Provider { get; private set; }

    /// <summary>The tool's risk class, once the gate has found it.</summary>
    public string? Risk { get; private set; }

    /// <summary>How many records were tried for this message, written or not.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    private IDictionary<string, JsonElement>? Arguments { get; set; }

    private IReadOnlyCollection<string>? Sensitive { get; set; }

    /// <summary>Opens the scope of <paramref name="context"/>'s message, puts it in the message's Items and makes it current.</summary>
    public static AuditScope Open(MessageContext context, string traceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(traceId);

        var (method, id) = context.JsonRpcMessage switch
        {
            JsonRpcRequest request => (request.Method, request.Id.ToString()),
            JsonRpcNotification notification => (notification.Method, null),
            var other => (other.GetType().Name, (string?)null),
        };

        ClaimsPrincipal? principal = context.User;
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            // stdio has no request user; its Development principal is a registered singleton (RequestGate.CallerOf).
            principal = context.Services?.GetService<ClaimsPrincipal>();
        }

        var http = AuditHttp.Current;
        var scope = new AuditScope(
            method,
            http?.RequestId ?? id,
            AuditPrincipal.From(principal, context.Services?.GetService<AuthenticationConfig>()),
            http,
            traceId);

        context.Items[ItemsKey] = scope;
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// The scope the audit step opened for <paramref name="context"/>'s message, made current on this path of execution; null
    /// when there is none, which a server the frame composed never has.
    /// </summary>
    public static AuditScope? Enter(MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = context.Items.TryGetValue(ItemsKey, out var held) ? held as AuditScope : Ambient.Value;
        if (scope is not null)
        {
            Ambient.Value = scope;
        }

        return scope;
    }

    /// <summary>What the gate has learned the message asks for. A null leaves what is known as it is.</summary>
    public void Describe(
        string? kind = null,
        string? target = null,
        string? provider = null,
        string? risk = null,
        IDictionary<string, JsonElement>? arguments = null,
        IReadOnlyCollection<string>? sensitive = null)
    {
        Kind = kind ?? Kind;
        Target = target ?? Target;
        Provider = provider ?? Provider;
        Risk = risk ?? Risk;
        Arguments = arguments ?? Arguments;
        Sensitive = sensitive ?? Sensitive;
    }

    /// <summary>An event about this message: its caller, trace, HTTP facts and what it asks for filled in.</summary>
    public AuditEvent Event(string name, string description, AuditDecision? decision = null) => new(name, description)
    {
        TraceId = TraceId,
        RequestId = RequestId,
        Principal = Principal,
        Http = Http,
        Mcp = new AuditMcp(Method, Provider, Kind, Target, Risk),
        Arguments = Arguments,
        SensitiveArguments = Sensitive,
        Decision = decision,
    };

    /// <summary>Writes one record about this message. False when the trail could not take it.</summary>
    public bool Record(AuditTrail trail, string name, string description, AuditOutcome outcome, string? rule = null)
    {
        ArgumentNullException.ThrowIfNull(trail);
        Interlocked.Increment(ref _attempts);
        return trail.Record(Event(name, description, new AuditDecision(outcome, rule)));
    }
}

/// <summary>
/// Which requests the audit step has met and not yet seen answered (contract-006 · G-2). A request's id is its client's to
/// choose and unique only among that client's requests in flight, so over HTTP the ids are kept per HTTP request — the one
/// thing the way in and the way out of a stateless request share — and held weakly, so they go when the request does. With
/// no HTTP request there is one client, on the stream the server was started with, and one set of ids. Each set is bounded,
/// so a client that sends requests and never reads an answer cannot grow it without end; emptied, a request refused by a
/// check of the frame's may be recorded once more on its way out, never less.
/// </summary>
public sealed class AuditInFlight
{
    private const int Capacity = 10_000;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> _perHttpRequest = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _onTheStream = new(StringComparer.Ordinal);

    /// <summary>Notes that the audit step met request <paramref name="id"/>, arriving in <paramref name="httpRequest"/> or, when null, on the stream.</summary>
    public void Met(object? httpRequest, string id)
    {
        var ids = httpRequest is null ? _onTheStream : _perHttpRequest.GetOrCreateValue(httpRequest);
        if (ids.Count >= Capacity)
        {
            ids.Clear();
        }

        ids[id] = 0;
    }

    /// <summary>Whether request <paramref name="id"/> was met; it is forgotten as it is answered.</summary>
    public bool Answered(object? httpRequest, string id) =>
        httpRequest is null
            ? _onTheStream.TryRemove(id, out _)
            : _perHttpRequest.TryGetValue(httpRequest, out var ids) && ids.TryRemove(id, out _);
}

/// <summary>
/// The audit step: the first thing every incoming message meets (contract-006 · G-1, G-2, G-8).
///
/// It opens the message's <see cref="AuditScope"/> and the request's span, and hands on. The decisions themselves are
/// recorded where they are made — by the request-kind gate and the request gate — before they take effect. This step
/// records only when a message ends and nothing did: a request the SDK turned away before any check ran. So every message
/// leaves a record, and none leaves by a path that records nothing.
/// </summary>
public static class AuditFilter
{
    /// <summary>The source the server's spans are opened under (roadmap P3.4).</summary>
    public static readonly ActivitySource Source = new("McpServer");

    /// <summary>
    /// The last thing every outgoing message passes (contract-006 · G-2): it closes the one gap the audit step cannot see
    /// from the way in. The SDK turns some requests away before any incoming filter runs — a method its negotiated protocol
    /// version does not have (ping and logging/setLevel under 2026-07-28) — so the audit step never meets them, and they
    /// would be answered with an error and leave no record at all. Every request the audit step does meet is noted
    /// (<see cref="AuditInFlight"/>); an error going out for a request that was never noted is one of those, and is
    /// recorded here, late, in the SDK's own words. Nothing the frame governs ran for it: the frame never saw it.
    /// </summary>
    public static McpMessageFilter Outgoing() => next => async (context, cancellationToken) =>
    {
        var answered = context.JsonRpcMessage switch
        {
            JsonRpcError error => error.Id.ToString(),
            JsonRpcResponse response => response.Id.ToString(),
            _ => null,
        };

        if (answered is not null
            && context.Services?.GetService<AuditInFlight>() is { } inFlight
            && !inFlight.Answered(AuditHttp.CurrentContext, answered)
            && context.JsonRpcMessage is JsonRpcError rejected
            && context.Services?.GetService<AuditTrail>() is { } trail)
        {
            var http = AuditHttp.Current;
            trail.Record(new AuditEvent(
                AuditEvents.InputValidationFail,
                SafeText.Line($"The request was rejected before it reached any check: {rejected.Error.Message}").ToString())
            {
                TraceId = (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString(),
                RequestId = http?.RequestId ?? answered,
                Principal = AuditPrincipal.From(
                    AuditHttp.CurrentUser ?? context.Services.GetService<ClaimsPrincipal>(),
                    context.Services.GetService<AuthenticationConfig>()),
                Http = http,
                Mcp = new AuditMcp(Method: null),
                Decision = new AuditDecision(AuditOutcome.Deny, "rejected-by-protocol"),
            });
        }

        await next(context, cancellationToken).ConfigureAwait(false);
    };

    /// <summary>The first incoming message filter.</summary>
    public static McpMessageFilter Message() => next => async (context, cancellationToken) =>
    {
        var trail = context.Services?.GetService<AuditTrail>()
            ?? throw new McpException("authz_fail (rule: frame). The audit trail is not available, so nothing is served.");

        // The request's span: a child of the caller's trace when a traceparent arrived (the HTTP host, or AuditHttpCapture,
        // has made it current), a new trace when none did. Always there, so every record has a trace id and whatever the
        // tool calls carries it on.
        using var span = Source.StartActivity("mcp.request", ActivityKind.Server)
            ?? (Activity.Current is null ? new Activity("mcp.request").Start() : null);
        var traceId = (span ?? Activity.Current)!.TraceId.ToHexString();

        var scope = AuditScope.Open(context, traceId);
        span?.SetTag("mcp.method", scope.Method);

        // Noted as met, so the way out can tell a request the SDK turned away before this step from every other (Outgoing).
        if (context.JsonRpcMessage is JsonRpcRequest met)
        {
            context.Services?.GetService<AuditInFlight>()?.Met(AuditHttp.CurrentContext, met.Id.ToString());
        }

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (scope.Attempts == 0 && ex is not OperationCanceledException)
        {
            // Turned away before any check ran: by the SDK, for parameters it could not read. Not the frame's decision, and
            // still a request that was refused; the reason is the SDK's own words, made safe for a line.
            scope.Record(
                trail,
                AuditEvents.InputValidationFail,
                SafeText.Line($"The request was rejected before any check ran: {ex.Message}").ToString(),
                AuditOutcome.Deny,
                "malformed-request");
            throw;
        }

        if (scope.Attempts == 0)
        {
            // Nothing recorded it on the way: said here, late, so that no message is ever answered with no record at all.
            scope.Record(trail, AuditEvents.McpRequest, "The request was answered and no check recorded a decision for it.", AuditOutcome.Allow, "unrecorded");
        }
    };
}
