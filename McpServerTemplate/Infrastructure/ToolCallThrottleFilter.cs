using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Cross-cutting rate limiter that prevents excessive tool calls.
///
/// WHY THIS EXISTS (AGENTIC AI GUARDRAIL):
/// An LLM in an agentic loop can call tools rapidly — retrying on errors, fetching
/// the same data repeatedly, or spiraling when it misinterprets a response.
/// Without throttling, a single agent session could make hundreds of HTTP requests
/// to the upstream API in seconds.
///
/// contract-001 · G-3 — the limiter is keyed on the tool the server actually matched, never on
/// the name the caller sent. Keying on the caller's string makes the limiter itself the attack:
/// each invented name mints a limiter that is never collected, so an unauthenticated caller can
/// exhaust the server's memory through the very component meant to protect it. A call that
/// matched no tool is passed straight through and fails as an unknown tool, as it should.
///
/// TEMPLATE INFRASTRUCTURE — works for any provider.
///
/// Register AFTER <see cref="ToolCallLoggingFilter"/> so rejected calls are still logged,
/// providing full audit trail visibility.
/// </summary>
public sealed class ToolCallThrottleFilter
{
    // One limiter per matched tool. SlidingWindowRateLimiter is thread-safe and manages its own
    // internal state, so there is no manual queue to leak. The dictionary is bounded by the number
    // of registered tools, which is fixed at startup.
    private readonly ConcurrentDictionary<string, SlidingWindowRateLimiter> _limiters =
        new(StringComparer.Ordinal);

    private readonly int _maxCallsPerToolPerMinute;

    /// <param name="maxCallsPerToolPerMinute">
    /// Maximum calls allowed per tool name within a 1-minute window.
    /// Default 10 is generous for human-paced interaction but catches agentic loops.
    /// Tune down for expensive APIs, up for chatty legitimate patterns.
    /// Configurable via <c>RateLimit:MaxCallsPerToolPerMinute</c> in appsettings.json.
    /// </param>
    public ToolCallThrottleFilter(int maxCallsPerToolPerMinute = 10)
    {
        if (maxCallsPerToolPerMinute < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCallsPerToolPerMinute),
                maxCallsPerToolPerMinute,
                "A per-tool limit below 1 would reject every call. Set RateLimit:MaxCallsPerToolPerMinute to 1 or more.");
        }

        _maxCallsPerToolPerMinute = maxCallsPerToolPerMinute;
    }

    /// <summary>
    /// How many distinct tools this filter is currently tracking. Bounded by the number of
    /// registered tools; a test asserts that unmatched call names never raise it.
    /// </summary>
    public int TrackedToolCount => _limiters.Count;

    /// <summary>
    /// The name the limiter keys on: the matched tool's own name, or null when nothing matched.
    /// Primitive matching happens before the filter pipeline runs, so this is already resolved.
    /// </summary>
    public static string? MatchedToolName(RequestContext<CallToolRequestParams> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.MatchedPrimitive is McpServerTool tool ? tool.ProtocolTool.Name : null;
    }

    /// <summary>
    /// Applies the throttle to a call, delegating to <paramref name="next"/> when it is allowed.
    /// </summary>
    public McpRequestFilter<CallToolRequestParams, CallToolResult> AsFilter() =>
        next => async (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toolName = MatchedToolName(context);

            // Nothing matched. Do not mint a limiter for a name the server does not serve —
            // let the handler reject it as an unknown tool.
            if (toolName is null)
            {
                return await next(context, cancellationToken);
            }

            var limiter = _limiters.GetOrAdd(toolName, _ => new SlidingWindowRateLimiter(
                new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6, // 10-second segments for smooth sliding
                    PermitLimit = _maxCallsPerToolPerMinute,
                    QueueLimit = 0, // Reject immediately, don't queue
                    AutoReplenishment = true
                }));

            using var lease = limiter.AttemptAcquire();

            if (!lease.IsAcquired)
            {
                throw new McpException(
                    $"Rate limit: '{toolName}' has exceeded {_maxCallsPerToolPerMinute} calls per minute. "
                    + "Please reuse the results from previous calls instead of calling this tool again.");
            }

            return await next(context, cancellationToken);
        };
}
