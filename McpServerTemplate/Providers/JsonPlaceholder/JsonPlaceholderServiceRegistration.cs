using McpServerTemplate.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace McpServerTemplate.Providers.JsonPlaceholder;

/// <summary>
/// DI registration entry point for the JSONPlaceholder provider.
///
/// TEMPLATE GUIDANCE:
/// This is the single method called from Program.cs:
///   <c>builder.Services.AddJsonPlaceholderProvider(builder.Configuration);</c>
///
/// It registers everything the provider needs:
///   1. Strongly-typed configuration (bound from appsettings.json via IOptions)
///   2. HttpClient via IHttpClientFactory (with base address + user-agent)
///   3. The API client as a singleton/scoped service
///
/// When creating your own provider, follow this pattern:
///   - Create <c>Add{YourProvider}Provider</c> extension method
///   - Bind config from <c>Configuration.GetSection("Providers:{YourProvider}")</c>
///   - Register your API client with a typed HttpClient
/// </summary>
public static class JsonPlaceholderServiceRegistration
{
    public static IServiceCollection AddJsonPlaceholderProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // IOptions<JsonPlaceholderConfig> enables hot-reload and cleaner testability.
        var section = configuration.GetSection("Providers:JsonPlaceholder");
        services.Configure<JsonPlaceholderConfig>(section);

        var config = section.Get<JsonPlaceholderConfig>()
            ?? throw new ConfigurationException(
                "Missing configuration section 'Providers:JsonPlaceholder' in appsettings.json.");

        // Security: validate that BaseUrl is an absolute HTTPS URL to prevent
        // SSRF if configuration is tampered with.
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != "https")
        {
            throw new ConfigurationException(
                $"Providers:JsonPlaceholder:BaseUrl must be an absolute HTTPS URL, got: '{config.BaseUrl}'");
        }

        // Declared here, not in the options callback: the callback runs lazily when the
        // first HttpClient is built, which is a tool call, not startup.
        var budget = ResilienceBudget.Create(
            providerName: "JsonPlaceholder",
attemptTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 2,
            // Must exceed 5s x 3 = 15s.
            totalTimeout: TimeSpan.FromSeconds(20),
            samplingDuration: TimeSpan.FromSeconds(30),
            breakDuration: TimeSpan.FromSeconds(15));

        services.AddHttpClient<JsonPlaceholderApiClient>(client =>
        {
            client.BaseAddress = new Uri(config.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
            client.Timeout = ResilienceBudget.ClientTimeout;
        })
        .AddStandardResilienceHandler(budget.Apply);

        return services;
    }
}
