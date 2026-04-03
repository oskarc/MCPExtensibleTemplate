using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
            ?? throw new InvalidOperationException(
                "Missing configuration section 'Providers:JsonPlaceholder' in appsettings.json.");

        // Security: validate that BaseUrl is an absolute HTTPS URL to prevent
        // SSRF if configuration is tampered with.
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != "https")
        {
            throw new InvalidOperationException(
                $"Providers:JsonPlaceholder:BaseUrl must be an absolute HTTPS URL, got: '{config.BaseUrl}'");
        }

        services.AddHttpClient<JsonPlaceholderApiClient>(client =>
        {
            client.BaseAddress = new Uri(config.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
        });

        return services;
    }
}
