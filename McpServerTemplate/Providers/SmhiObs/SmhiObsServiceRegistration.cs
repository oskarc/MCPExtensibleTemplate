using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Frame;
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
        IConfiguration configuration,
        EgressPolicy egress)
    {
        // IOptions<SmhiObsConfig> enables hot-reload and cleaner testability.
        var section = configuration.GetSection("Providers:SmhiObs");
        services.Configure<SmhiObsConfig>(section);

        var config = section.Get<SmhiObsConfig>()
            ?? throw new ConfigurationException(
                "Missing configuration section 'Providers:SmhiObs' in appsettings.json.");

        // Security: validate that BaseUrl is an absolute HTTPS URL.
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != "https")
        {
            throw new ConfigurationException(
                $"Providers:SmhiObs:BaseUrl must be an absolute HTTPS URL, got: '{config.BaseUrl}'");
        }

        services.AddMemoryCache();

        // Declared here, not in the options callback: the callback runs lazily when the
        // first HttpClient is built, which is a tool call, not startup.
        var budget = ResilienceBudget.Create(
            providerName: "SmhiObs",
            // Historical observation series are larger and slower than a forecast.
            attemptTimeout: egress.AttemptTimeout,
            maxRetryAttempts: egress.MaxRetryAttempts,
            totalTimeout: egress.TotalTimeout,
            samplingDuration: TimeSpan.FromSeconds(45),
            breakDuration: TimeSpan.FromSeconds(15));

        services.AddHttpClient<SmhiObsApiClient>(client =>
        {
            client.BaseAddress = new Uri(config.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
            client.Timeout = ResilienceBudget.ClientTimeout;
        })
        .AddStandardResilienceHandler(budget.Apply);

        return services;
    }
}
