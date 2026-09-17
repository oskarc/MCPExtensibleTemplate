using McpServerTemplate.Infrastructure;
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

        // Declared here, not in the options callback: the callback runs lazily when the
        // first HttpClient is built, which is a tool call, not startup.
        var budget = ResilienceBudget.Create(
            providerName: "Smhi",
            // A forecast is a small document; an attempt that has not answered in 10s is stuck.
            attemptTimeout: TimeSpan.FromSeconds(10),
            maxRetryAttempts: 2,
            // Must exceed 10s x 3 = 30s, or the third attempt could never run.
            totalTimeout: TimeSpan.FromSeconds(35),
            samplingDuration: TimeSpan.FromSeconds(30),
            breakDuration: TimeSpan.FromSeconds(15));

        services.AddHttpClient<SmhiApiClient>(client =>
        {
            client.BaseAddress = new Uri(config.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);

            // No client-level deadline: it would wrap the resilience pipeline and cut the
            // retries short. See ProviderResilience for why.
            client.Timeout = ResilienceBudget.ClientTimeout;
        })
        .AddStandardResilienceHandler(budget.Apply);

        return services;
    }
}
