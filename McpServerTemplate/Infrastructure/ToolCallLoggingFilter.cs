using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog.Context;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Cross-cutting filter that logs every MCP tool call with timing, arguments,
/// and a correlation ID for tracing calls across concurrent sessions.
///
/// This is TEMPLATE INFRASTRUCTURE — it works for any provider and does not need
/// modification when swapping providers.
///
/// Security: tool arguments are logged at Information level with argument NAMES only.
/// Full argument values are logged at Debug level to avoid leaking secrets/PII in
/// production logs where a future provider might accept sensitive parameters.
/// </summary>
public static class ToolCallLoggingFilter
{
    /// <summary>
    /// Creates a <c>CallToolFilter</c> delegate that logs tool invocations with
    /// correlation IDs and timing.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (context, cancellationToken) =>
        {
            var logger = context.Services?.GetService<ILogger<McpServer>>();

            // contract-005 · G-17 round 2 (SEC-R2-03) — this filter runs before the gate, so the name is whatever the caller
            // wrote, a tool or not: made safe for a log line and cut (LogSafe.Text), in every line below and in the log context.
            var toolName = LogSafe.Text(context.Params?.Name ?? "unknown");

            var correlationId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

            using (LogContext.PushProperty("CorrelationId", correlationId))
            using (LogContext.PushProperty("ToolName", toolName))
            {
                LogToolCallStart(logger, correlationId, toolName, context);
                LogToolCallDebugArgs(logger, correlationId, toolName, context);

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var result = await next(context, cancellationToken);
                    stopwatch.Stop();
                    LogToolCallResult(logger, correlationId, toolName, stopwatch.ElapsedMilliseconds, result);
                    return result;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    logger?.LogError(ex,
                        "[{CorrelationId}] Tool {ToolName} threw exception after {ElapsedMs}ms",
                        correlationId, toolName, stopwatch.ElapsedMilliseconds);
                    throw;
                }
            }
        };
    }

    private static void LogToolCallStart(
        ILogger? logger, string correlationId, string toolName,
        RequestContext<CallToolRequestParams> context)
    {
        // The guard comes before the argument is built, not after. Joining the argument names
        // costs an allocation on every tool call, and paid it even when the level was off.
        if (logger is null || !logger.IsEnabled(LogLevel.Information))
            return;

        // contract-005 · G-17 round 2 (SEC-R2-03) — the names are the caller's to write, as many and as long as they like.
        var argNames = context.Params?.Arguments is { } a
            ? LogSafe.Text(string.Join(", ", a.Keys))
            : "(none)";

        logger.LogInformation(
            "[{CorrelationId}] Tool call: {ToolName} with args: [{ArgNames}]",
            correlationId, toolName, argNames);
    }

    private static void LogToolCallDebugArgs(
        ILogger? logger, string correlationId, string toolName,
        RequestContext<CallToolRequestParams> context)
    {
        // Serialising every argument is the most expensive thing this filter does; it must not
        // happen unless the message will actually be written.
        if (logger is null || !logger.IsEnabled(LogLevel.Debug))
            return;

        var argsJson = context.Params?.Arguments is { } args
            ? JsonSerializer.Serialize(args)
            : "{}";

        logger.LogDebug(
            "[{CorrelationId}] Tool {ToolName} full args: {Args}",
            correlationId, toolName, argsJson);
    }

    private static void LogToolCallResult(
        ILogger? logger, string correlationId, string toolName,
        long elapsedMs, CallToolResult result)
    {
        if (logger is null)
            return;

        if (result.IsError is true)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[{CorrelationId}] Tool {ToolName} returned error in {ElapsedMs}ms",
                    correlationId, toolName, elapsedMs);
            }
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[{CorrelationId}] Tool {ToolName} completed in {ElapsedMs}ms",
                correlationId, toolName, elapsedMs);
        }
    }
}
