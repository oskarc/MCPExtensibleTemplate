using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace McpServerTemplate.Providers.SmhiObs;

/// <summary>
/// DI registration entry point for the SMHI Observations provider.
/// Called from Program.cs alongside the forecast provider.
/// </summary>
public static class SmhiObsServiceRegistration
{
    public static IServiceCollection AddSmhiObsProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // IOptions<SmhiObsConfig> enables hot-reload and cleaner testability.
        var section = configuration.GetSection("Providers:SmhiObs");
        services.Configure<SmhiObsConfig>(section);

        var config = section.Get<SmhiObsConfig>()
            ?? throw new InvalidOperationException(
                "Missing configuration section 'Providers:SmhiObs' in appsettings.json.");

        // Security: validate that BaseUrl is an absolute HTTPS URL.
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != "https")
        {
            throw new InvalidOperationException(
                $"Providers:SmhiObs:BaseUrl must be an absolute HTTPS URL, got: '{config.BaseUrl}'");
        }

        services.AddMemoryCache();

        services.AddHttpClient<SmhiObsApiClient>(client =>
        {
            client.BaseAddress = new Uri(config.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(30); // Historical data can be larger/slower
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromMilliseconds(500);

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

            // Higher total timeout for historical data which can be large.
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        });

        return services;
    }
}
