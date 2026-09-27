namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Reads the identity configuration and refuses to start on anything it cannot honour.
///
/// contract-002 · G-8 — an operator who misconfigures identity gets exit 78 and the name of the
/// setting, not a stack trace and not a server that starts and rejects every request for a
/// reason nobody can see. Each check below is a way a deployment can be wrong while looking
/// plausible, so each names the setting and what it needs.
/// </summary>
public static class IdentityConfigurationBinder
{
    /// <summary>
    /// Signing algorithms an identity provider may be configured to use.
    ///
    /// The list is short on purpose. "none" makes any token valid; the HMAC family makes any
    /// party holding the verification secret able to mint tokens, which for a resource server
    /// means the issuer's shared secret becomes a signing key. Neither is refused by name —
    /// both are refused by not being here.
    /// </summary>
    private static readonly string[] PermittedAlgorithms = ["RS256", "PS256", "ES256"];

    /// <summary>The registered JWT claims (RFC 7519 §4.1), none of which names the calling client.</summary>
    private static readonly string[] RegisteredClaims = ["iss", "sub", "aud", "exp", "nbf", "iat", "jti"];

    public static AuthenticationConfig Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Authentication");
        var config = section.Get<AuthenticationConfig>() ?? new AuthenticationConfig();

        foreach (var (name, provider) in config.IdentityProviders)
        {
            provider.Name = name;
        }

        Validate(config);
        return config;
    }

    private static void Validate(AuthenticationConfig config)
    {
        if (config.IdentityProviders.Count == 0)
        {
            throw new ConfigurationException(
                "Authentication:IdentityProviders must name at least one identity provider when "
                + "using HTTP transport. Without one the server can verify no token and would "
                + "refuse every request.");
        }

        if (!Uri.TryCreate(config.Resource, UriKind.Absolute, out var resource) ||
            resource.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConfigurationException(
                $"Authentication:Resource must be an absolute https URI naming this server as an "
                + $"OAuth resource, for example https://mcp.example.com/mcp. It is '{config.Resource}'. "
                + "Every identity provider must issue tokens whose audience is exactly this value.");
        }

        // contract-005 · G-12 (1) — the resource is the URL a client connects to, and a standard client
        // refuses metadata whose resource is not that URL. MCP answers at /mcp, so the resource's path
        // must be /mcp too; any other path names a URL where nothing answers.
        if (!string.Equals(resource.AbsolutePath.TrimEnd('/'), HttpServerComposition.McpPath, StringComparison.Ordinal))
        {
            throw new ConfigurationException(
                $"Authentication:Resource must name this server's MCP endpoint, https://{{host}}{HttpServerComposition.McpPath}; "
                + $"it is '{config.Resource}', whose path is '{resource.AbsolutePath}'. MCP answers at "
                + $"{HttpServerComposition.McpPath}, and a client that connects to a resource URL where nothing answers "
                + "cannot connect.");
        }

        foreach (var (name, provider) in config.IdentityProviders)
        {
            var key = $"Authentication:IdentityProviders:{name}";

            if (!Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority) ||
                authority.Scheme != Uri.UriSchemeHttps)
            {
                throw new ConfigurationException(
                    $"{key}:Authority must be an absolute https URI; it is '{provider.Authority}'. "
                    + "Discovery and signing keys are fetched from it, so plaintext would put key "
                    + "material on the wire.");
            }

            if (string.IsNullOrWhiteSpace(provider.Issuer))
            {
                throw new ConfigurationException(
                    $"{key}:Issuer is required. It is the exact iss value tokens must carry, and it "
                    + "is pinned rather than read from the token.");
            }

            // contract-005 · G-12 (4) — the same rule as Authority: the metadata's authorization_servers
            // lists the Issuer, and clients are sent to it for its metadata and to sign in.
            if (!Uri.TryCreate(provider.Issuer, UriKind.Absolute, out var issuer) ||
                issuer.Scheme != Uri.UriSchemeHttps)
            {
                throw new ConfigurationException(
                    $"{key}:Issuer must be an absolute https URI; it is '{provider.Issuer}'. It is published as "
                    + "an authorization server, and clients are sent to it to find its metadata and to sign in, "
                    + "so plaintext would hand their credentials to anyone on the path.");
            }

            if (provider.Algorithms.Length == 0)
            {
                throw new ConfigurationException(
                    $"{key}:Algorithms must name at least one of {string.Join(", ", PermittedAlgorithms)}. "
                    + "An empty list would accept whatever the token proposes.");
            }

            var refused = provider.Algorithms
                .Where(a => !PermittedAlgorithms.Contains(a, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (refused.Length > 0)
            {
                throw new ConfigurationException(
                    $"{key}:Algorithms names {string.Join(", ", refused)}, which this server will not "
                    + $"accept. Permitted: {string.Join(", ", PermittedAlgorithms)}. Symmetric and "
                    + "unsigned algorithms are excluded because a resource server holding a "
                    + "verification secret could mint its own tokens with it.");
            }

            if (provider.ScopeCatalog.Length == 0)
            {
                throw new ConfigurationException(
                    $"{key}:ScopeCatalog must name the scopes this identity provider may assert. "
                    + "An empty catalog means no scope it issues can ever be honoured.");
            }

            var wildcards = provider.ScopeCatalog.Where(s => s.Contains('*', StringComparison.Ordinal)).ToArray();
            if (wildcards.Length > 0)
            {
                throw new ConfigurationException(
                    $"{key}:ScopeCatalog contains a wildcard ({string.Join(", ", wildcards)}). Scopes are "
                    + "matched exactly, so a wildcard grants nothing and hides what was intended.");
            }

            if (string.IsNullOrWhiteSpace(provider.ScopeClaim) ||
                string.IsNullOrWhiteSpace(provider.ClientIdClaim))
            {
                throw new ConfigurationException(
                    $"{key}:ScopeClaim and {key}:ClientIdClaim are required. They differ by identity "
                    + "provider — scope or scp; azp, cid, appid or client_id — and a wrong one reads every "
                    + "token as carrying no scopes, or refuses every token for want of a client.");
            }

            // contract-005 · G-12 (3) — the client claim is required of every token, so it must name a
            // claim that says which client called. A registered claim is present on every token this
            // server accepts or means something else, and the scope claim means the scopes: either
            // would switch the requirement off. Compared without case, because that is how a claim is
            // found on the principal (ClaimsIdentity.FindFirst): ClientIdClaim=SUB finds sub.
            var clientClaim = provider.ClientIdClaim.Trim();
            var notAClient =
                RegisteredClaims.Contains(clientClaim, StringComparer.OrdinalIgnoreCase)
                    ? "it is a registered JWT claim, present on every token or meaning something else"
                : string.Equals(clientClaim, provider.ScopeClaim.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? "it is this identity provider's ScopeClaim"
                : null;

            if (notAClient is not null)
            {
                throw new ConfigurationException(
                    $"{key}:ClientIdClaim is '{provider.ClientIdClaim}', which does not name the calling client: "
                    + $"{notAClient}, so requiring it would require nothing. Name the claim this identity provider "
                    + "puts the client in: azp (Keycloak, Entra ID v2), cid (Okta), appid (Entra ID v1) or client_id.");
            }
        }

        if (config.AdminIdentityProvider is { } admin)
        {
            if (!config.IdentityProviders.TryGetValue(admin, out var adminProvider))
            {
                throw new ConfigurationException(
                    $"Authentication:AdminIdentityProvider names '{admin}', which is not a configured "
                    + $"identity provider. Configured: {string.Join(", ", config.IdentityProviders.Keys)}.");
            }

            if (!adminProvider.ScopeCatalog.Contains("mcp:admin", StringComparer.Ordinal))
            {
                throw new ConfigurationException(
                    $"Authentication:AdminIdentityProvider names '{admin}', but its ScopeCatalog does not "
                    + "contain mcp:admin. The administrative plane would be unreachable.");
            }
        }
    }
}
