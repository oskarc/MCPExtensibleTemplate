using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Lightweight startup health check that verifies upstream provider APIs are reachable.
///
/// Runs once at startup and logs warnings for any unreachable endpoints.
/// Does NOT block startup — the server starts regardless, so transient network issues
/// during deployment don't prevent the process from running.
///
/// TEMPLATE INFRASTRUCTURE — add your own provider checks in <see cref="CheckUpstreamAsync"/>.
/// </summary>
public static class HealthProbe
{
    /// <summary>
    /// Checks upstream API connectivity and logs the results.
    /// Each check is independent and uses a short timeout to avoid delaying startup.
    /// </summary>
    public static async Task CheckUpstreamAsync(IServiceProvider services)
    {
        var logger = services.GetService<ILogger<McpServer>>();
        var factory = services.GetService<IHttpClientFactory>();

        if (factory is null)
        {
            logger?.LogWarning("IHttpClientFactory not available — skipping upstream health checks");
            return;
        }

        var endpoints = new List<(string Name, string Url)>();

        // Read upstream URLs from configuration instead of hardcoding,
        // so health checks match the actual configured provider endpoints.
        var config = services.GetService<IConfiguration>();
        var smhiBase = config?["Providers:Smhi:BaseUrl"];
        if (smhiBase is not null)
            endpoints.Add(("SMHI Forecast", $"{smhiBase.TrimEnd('/')}/api/category/snow1g/version/1.json"));

        var obsBase = config?["Providers:SmhiObs:BaseUrl"];
        if (obsBase is not null)
            endpoints.Add(("SMHI Observations", $"{obsBase.TrimEnd('/')}/api/version/1.0.json"));

        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var (name, url) in endpoints)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    logger?.LogInformation("Health check: {Provider} is reachable", name);
                }
                else
                {
                    logger?.LogWarning(
                        "Health check: {Provider} returned HTTP {StatusCode}",
                        name, (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Health check: {Provider} is unreachable", name);
            }
        }
    }
}
