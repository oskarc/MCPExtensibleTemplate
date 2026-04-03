namespace McpServerTemplate.Providers.JsonPlaceholder;

/// <summary>
/// Configuration for the JSONPlaceholder API provider.
/// 
/// Binds from appsettings.json section "Providers:JsonPlaceholder".
/// </summary>
public class JsonPlaceholderConfig
{
    /// <summary>
    /// Base URL of the JSONPlaceholder API.
    /// Must be an absolute HTTPS URL for security validation.
    /// </summary>
    public string BaseUrl { get; set; } = "https://jsonplaceholder.typicode.com";

    /// <summary>
    /// User-Agent header to identify requests from this MCP server.
    /// </summary>
    public string UserAgent { get; set; } = "McpServerTemplate/1.0";
}
