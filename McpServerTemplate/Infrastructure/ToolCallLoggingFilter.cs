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
            var toolName = context.Params?.Name ?? "unknown";

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
        var argNames = context.Params?.Arguments is { } a
            ? string.Join(", ", a.Keys)
            : "(none)";
        logger?.LogInformation(
            "[{CorrelationId}] Tool call: {ToolName} with args: [{ArgNames}]",
            correlationId, toolName, argNames);
    }

    private static void LogToolCallDebugArgs(
        ILogger? logger, string correlationId, string toolName,
        RequestContext<CallToolRequestParams> context)
    {
        if (logger?.IsEnabled(LogLevel.Debug) is not true)
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
        if (result.IsError is true)
        {
            logger?.LogWarning(
                "[{CorrelationId}] Tool {ToolName} returned error in {ElapsedMs}ms",
                correlationId, toolName, elapsedMs);
        }
        else
        {
            logger?.LogInformation(
                "[{CorrelationId}] Tool {ToolName} completed in {ElapsedMs}ms",
                correlationId, toolName, elapsedMs);
        }
    }
}
