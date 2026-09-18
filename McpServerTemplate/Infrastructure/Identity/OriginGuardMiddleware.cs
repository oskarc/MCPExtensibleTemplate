namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Refuses a request whose Origin header this server does not answer for.
///
/// contract-002 · G-7 — the specification requires an MCP server to validate Origin, and the C#
/// SDK does not do it (the TypeScript one does). Without it a page in a browser can drive this
/// server using a session the browser already holds: the request carries credentials the user
/// never chose to spend here, and every other control sees a perfectly valid principal.
///
/// It sits before authentication on purpose. A cross-origin caller should be refused for being
/// cross-origin, not told whether its token was any good — and the refusal should cost nothing
/// downstream.
///
/// Absent Origin is allowed through: a native client, a CLI or a server-to-server caller sends
/// none, and they are the ordinary case. The header only means something when a browser set it.
/// </summary>
public sealed class OriginGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowed;
    private readonly ILogger<OriginGuardMiddleware> _logger;

    public OriginGuardMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<OriginGuardMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _next = next;
        _logger = logger;
        _allowed = new HashSet<string>(
            configuration.GetSection("HttpTransport:AllowedOrigins").Get<string[]>() ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var origin = context.Request.Headers.Origin.ToString();

        if (!string.IsNullOrEmpty(origin) && !_allowed.Contains(origin))
        {
            // OWASP logging vocabulary: this is the event a SIEM correlates when a browser is
            // being used to reach a server it was never meant to reach.
            _logger.LogWarning(
                "malicious_cors: refused a request from origin {Origin} for {Path}",
                origin, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
