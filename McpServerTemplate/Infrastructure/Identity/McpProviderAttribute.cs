namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Declares which provider a tool, resource or prompt type belongs to.
///
/// contract-002 · G-5 — a provider is bound to exactly one identity provider, and enforcing that
/// means knowing which provider each tool came from. The alternative was inferring it from the
/// namespace, which works until someone moves a file; this says it, and the SDK carries
/// class-level attributes through to the matched primitive's metadata, so the binding can be
/// checked at the moment a call arrives.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class McpProviderAttribute : Attribute
{
    public McpProviderAttribute(string name) => Name = name;

    /// <summary>The provider's configuration key, e.g. "Smhi" for <c>Providers:Smhi</c>.</summary>
    public string Name { get; }
}
