namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// Strongly-typed configuration for the SMHI provider.
/// Bound from <c>appsettings.json → Providers:Smhi</c>.
///
/// TEMPLATE GUIDANCE:
/// Each provider defines its own config record. This keeps configuration
/// self-contained. When you delete a provider, delete its section in
/// appsettings.json as well: a section for a provider the server does not
/// have is a setting it would ignore, and it refuses to start on one.
///
/// A provider that needs a credential for its upstream — an upstream API
/// key, say — adds the property here, declares its name in the module's
/// <c>Settings</c> (the server refuses any undeclared key under
/// <c>Providers:{Name}</c>), and sets it outside the repository, for example
/// from an environment variable or a secret store.
/// </summary>
public sealed record SmhiConfig
{
    public required string BaseUrl { get; init; }
    public required string UserAgent { get; init; }
}
