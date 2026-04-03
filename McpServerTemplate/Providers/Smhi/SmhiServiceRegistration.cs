using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// DI registration entry point for the SMHI provider.
///
/// TEMPLATE GUIDANCE:
/// This is the single method called from Program.cs:
///   <c>builder.Services.AddSmhiProvider(builder.Configuration);</c>
///
/// It registers everything the provider needs:
///   1. Strongly-typed configuration (bound from appsettings.json via IOptions)
///   2. HttpClient via IHttpClientFactory (with base address + user-agent + resilience)
///   3. The API client as a singleton/scoped service
///
/// When creating your own provider, follow this pattern:
///   - Create <c>Add{YourProvider}Provider</c> extension method
///   - Bind config from <c>Configuration.GetSection("Providers:{YourProvider}")</c>
///   - Register your API client with a typed HttpClient
/// </summary>
public static class SmhiServiceRegistration
{
    public static IServiceCollection AddSmhiProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // IOptions<SmhiConfig> enables hot-reload and cleaner testability.
        var section = configuration.GetSection("Providers:Smhi");
        services.Configure<SmhiConfig>(section);

        var config = section.Get<SmhiConfig>()
            ?? throw new InvalidOperationException(
                "Missing configuration section 'Providers:Smhi' in appsettings.json.");

        // Security: validate that BaseUrl is an absolute HTTPS URL to prevent
        // SSRF if configuration is tampered with.
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != "https")
        {
            throw new InvalidOperationException(
                $"Providers:Smhi:BaseUrl must be an absolute HTTPS URL, got: '{config.BaseUrl}'");
        }

        services.AddHttpClient<SmhiApiClient>(client =>
        {
            client.BaseAddress = new Uri(config.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);

            // Guardrail: explicit timeout prevents a hanging upstream API from
            // blocking the agent indefinitely. Default HttpClient timeout is 100s —
            // far too long for an agentic flow where responsiveness matters.
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .AddStandardResilienceHandler(options =>
        {
            // Retry: 3 attempts with exponential backoff for transient HTTP errors (5xx, 408, 429).
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromMilliseconds(500);

            // Circuit breaker: after 5 failures in 30s, open for 15s.
            // Prevents hammering a down upstream and gives it time to recover.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

            // Total request timeout including retries — caps worst-case latency.
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
