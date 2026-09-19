namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// The scope a caller must hold to see or use the tools of this type.
///
/// contract-002 · G-4, UC-5 — a caller sees only what their scopes allow. The scope is declared
/// beside the provider rather than inferred, for the same reason: a tool that declares nothing
/// cannot be checked, and an unchecked tool is reachable by any token the server accepts.
///
/// Scope names mean something only inside the identity provider that issued them, so the name
/// here is matched against the bound identity provider's catalog at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class McpScopeAttribute : Attribute
{
    public McpScopeAttribute(string scope) => Scope = scope;

    /// <summary>The scope required, e.g. "weather:read".</summary>
    public string Scope { get; }
}
