namespace McpServerTemplate.Providers.SmhiObs;

/// <summary>
/// Strongly-typed configuration for the SMHI Observations provider.
/// Bound from <c>appsettings.json → Providers:SmhiObs</c>.
/// </summary>
public sealed record SmhiObsConfig
{
    public required string BaseUrl { get; init; }
    public required string UserAgent { get; init; }
}
