namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// One identity provider the server accepts tokens from, bound from
/// <c>Authentication:IdentityProviders:{name}</c>.
///
/// contract-002 · G-1, G-5 — the server trusts more than one identity provider, and every
/// provider is bound to exactly one of them. Scope names mean something only inside the
/// identity provider that issued them, which is why the catalog lives here rather than at the
/// root: "demo:write" from one issuer is not the same permission as "demo:write" from another.
///
/// Phase 4 adds introspection and revocation fields to this record. They are absent rather than
/// unused, so nothing reads as configured when it is not yet enforced.
/// </summary>
public sealed class IdentityProviderConfig
{
    /// <summary>The key this entry was configured under; filled in during binding.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>OIDC authority, used for discovery and key retrieval.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The exact issuer value tokens must carry. Pinned, never inferred from the token.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Signing algorithms this issuer is allowed to use. An allowlist rather than a denylist:
    /// "none" and the HMAC family are refused by being absent, not by being named.
    /// </summary>
    public string[] Algorithms { get; set; } = [];

    /// <summary>Claim carrying scopes: "scope" (Keycloak, Auth0) or "scp" (Entra ID).</summary>
    public string ScopeClaim { get; set; } = "scope";

    /// <summary>Claim carrying the calling application: "client_id" or "azp".</summary>
    public string ClientIdClaim { get; set; } = "client_id";

    /// <summary>Every scope this identity provider is permitted to assert.</summary>
    public string[] ScopeCatalog { get; set; } = [];
}

/// <summary>
/// The server's identity configuration as a whole, bound from <c>Authentication</c>.
/// </summary>
public sealed class AuthenticationConfig
{
    /// <summary>
    /// The canonical resource URI. It is this server's identity as an OAuth resource: the same
    /// value every identity provider must put in <c>aud</c>, and the <c>resource</c> field of the
    /// metadata document. One resource, several authorization servers.
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>The one identity provider whose <c>mcp:admin</c> scope is honoured, if any.</summary>
    public string? AdminIdentityProvider { get; set; }

    /// <summary>Identity providers by name.</summary>
    public Dictionary<string, IdentityProviderConfig> IdentityProviders { get; set; } = [];
}
