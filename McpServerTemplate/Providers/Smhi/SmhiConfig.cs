namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// Strongly-typed configuration for the SMHI provider.
/// Bound from <c>appsettings.json → Providers:Smhi</c>.
///
/// TEMPLATE GUIDANCE:
/// Each provider defines its own config record. This keeps configuration
/// self-contained — when you delete this provider folder, its config
/// section in appsettings.json becomes inert (no compile errors).
///
/// For providers that need API keys, add the property here and use
/// environment variable override in production:
///   <c>Providers__YourProvider__ApiKey=secret</c>
/// </summary>
public sealed record SmhiConfig
{
    public required string BaseUrl { get; init; }
    public required string UserAgent { get; init; }
}
