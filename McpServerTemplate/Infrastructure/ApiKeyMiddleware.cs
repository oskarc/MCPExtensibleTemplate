using System.Security.Cryptography;
using System.Text;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Middleware that validates an API key from the <c>X-Api-Key</c> request header.
/// Applied only when using HTTP transport to protect the server from unauthorized access.
///
/// Uses <see cref="CryptographicOperations.FixedTimeEquals"/> to prevent timing attacks
/// on the key comparison.
///
/// Configure the expected key via <c>Authentication:ApiKey</c> in appsettings.json
/// or the <c>Authentication__ApiKey</c> environment variable.
/// </summary>
public sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKeyBytes;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;

        var apiKey = configuration.GetValue<string>("Authentication:ApiKey");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Authentication:ApiKey must be configured when using HTTP transport. "
                + "Set it in appsettings.json or via environment variable Authentication__ApiKey.");
        }

        _expectedKeyBytes = Encoding.UTF8.GetBytes(apiKey);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKey) ||
            string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Missing API key. Provide it in the X-Api-Key header.");
            return;
        }

        var providedKeyBytes = Encoding.UTF8.GetBytes(providedKey.ToString());

        if (!CryptographicOperations.FixedTimeEquals(_expectedKeyBytes, providedKeyBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Invalid API key.");
            return;
        }

        await _next(context);
    }
}
