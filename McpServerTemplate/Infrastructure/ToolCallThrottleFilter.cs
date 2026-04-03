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
/// This filter uses <see cref="SlidingWindowRateLimiter"/> per tool name, which is
/// thread-safe, memory-bounded, and automatically evicts old windows.
///
/// TEMPLATE INFRASTRUCTURE — works for any provider.
///
/// Register AFTER <see cref="ToolCallLoggingFilter"/> so rejected calls are still logged,
/// providing full audit trail visibility.
/// </summary>
public static class ToolCallThrottleFilter
{
    /// <summary>
    /// Creates a rate-limiting filter with a per-tool sliding window.
    /// </summary>
    /// <param name="maxCallsPerToolPerMinute">
    /// Maximum calls allowed per tool name within a 1-minute window.
    /// Default 10 is generous for human-paced interaction but catches agentic loops.
    /// Tune down for expensive APIs, up for chatty legitimate patterns.
    /// Configurable via <c>RateLimit:MaxCallsPerToolPerMinute</c> in appsettings.json.
    /// </param>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        int maxCallsPerToolPerMinute = 10)
    {
        // ConcurrentDictionary<string, SlidingWindowRateLimiter> — each tool gets its own limiter.
        // SlidingWindowRateLimiter is thread-safe and automatically manages its internal state,
        // eliminating the manual Queue<DateTimeOffset> + lock pattern and its memory leak.
        var limiters = new System.Collections.Concurrent.ConcurrentDictionary<string, SlidingWindowRateLimiter>();

        return next => async (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toolName = context.Params?.Name ?? "unknown";

            var limiter = limiters.GetOrAdd(toolName, _ => new SlidingWindowRateLimiter(
                new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6, // 10-second segments for smooth sliding
                    PermitLimit = maxCallsPerToolPerMinute,
                    QueueLimit = 0, // Reject immediately, don't queue
                    AutoReplenishment = true
                }));

            using var lease = limiter.AttemptAcquire();

            if (!lease.IsAcquired)
            {
                throw new McpException(
                    $"Rate limit: '{toolName}' has exceeded {maxCallsPerToolPerMinute} calls per minute. "
                    + "Please reuse the results from previous calls instead of calling this tool again.");
            }

            return await next(context, cancellationToken);
        };
    }
}
