namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Liveness and readiness endpoints for an orchestrator.
///
/// contract-001 · G-7 — these replace the startup upstream probe. Nothing here calls an
/// upstream: a liveness probe that depends on a third party turns that third party's outage
/// into a restart loop of this server, and a readiness probe that does so takes the server
/// out of rotation for a fault it has no part in.
///
/// Both are registered before authentication on purpose. An orchestrator's probe carries no
/// API key, and neither endpoint discloses anything beyond whether this process is serving.
/// </summary>
public static class HealthEndpoints
{
    public const string LivenessPath = "/healthz";
    public const string ReadinessPath = "/readyz";

    /// <summary>
    /// Answers the two probe paths and passes everything else along. Terminal for those paths,
    /// so it must sit at the point in the pipeline where probes are meant to be answered.
    /// </summary>
    public static IApplicationBuilder UseHealthEndpoints(this IApplicationBuilder app, IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(lifetime);

        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // Liveness: this process is running and its request pipeline is reachable.
            // The only honest answer to "should I restart you" that does not involve anyone else.
            if (path.Equals(LivenessPath, StringComparison.Ordinal))
            {
                await WriteAsync(context, StatusCodes.Status200OK, "alive");
                return;
            }

            // Readiness: the host has finished starting and can take traffic.
            if (path.Equals(ReadinessPath, StringComparison.Ordinal))
            {
                var started = lifetime.ApplicationStarted.IsCancellationRequested;
                var stopping = lifetime.ApplicationStopping.IsCancellationRequested;
                var ready = started && !stopping;

                await WriteAsync(
                    context,
                    ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable,
                    ready ? "ready" : "not ready");
                return;
            }

            await next(context);
        });
    }

    private static Task WriteAsync(HttpContext context, int statusCode, string body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(body, context.RequestAborted);
    }
}
