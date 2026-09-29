namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Reads the identity configuration and refuses to start on anything it cannot honour.
///
/// contract-002 · G-8 — an operator who misconfigures identity gets exit 78 and the name of the
/// setting, not a stack trace and not a server that starts and rejects every request for a
/// reason nobody can see. Each check below is a way a deployment can be wrong while looking
/// plausible, so each names the setting and what it needs.
///
/// contract-005 · G-17 round 1 — a refusal that names a URL writes it without its user information or its query
/// (<see cref="LogSafe.Url"/>): user information is refused because it is a credential written into
/// a URL, a query can carry one too, and the refusal wrote them into the log the server stops with.
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

    /// <summary>
    /// contract-005 · G-12 (3) — the claims identity providers put the calling client in, and the only
    /// values ClientIdClaim may take: azp (Keycloak, Entra ID v2), cid (Okta), appid (Entra ID v1) and
    /// client_id (RFC 9068's JWT access token profile), each checked against the vendor's own token
    /// reference when the setting was first applied.
    /// </summary>
    private static readonly string[] ClientClaims = ["azp", "cid", "appid", "client_id"];

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
                + $"OAuth resource, for example https://mcp.example.com/mcp. It is '{LogSafe.Url(config.Resource)}'. "
                + "Every identity provider must issue tokens whose audience is exactly this value.");
        }

        // contract-005 · G-12 (1) — the resource is the URL a client connects to, and a standard client
        // refuses metadata whose resource is not that URL. MCP answers at /mcp, so the resource's path
        // must be /mcp too; any other path names a URL where nothing answers.
        //
        // Held as written, not as parsed: the string is what the metadata publishes and what every
        // token's audience must equal, character for character. Parsed, /./mcp and /x/../mcp are /mcp,
        // /%6Dcp is decoded, \ becomes /, and a query, a fragment and user information are set aside —
        // each of those bound, and the metadata published https://host/./mcp. None may appear, and the
        // path as written is /mcp with at most one trailing slash.
        var notAsClientsCarryIt =
            QueryFragmentOrUserInfo(config.Resource, resource)
            ?? (config.Resource.Contains('\\', StringComparison.Ordinal) ? "a backslash"
                : config.Resource.Contains('%', StringComparison.Ordinal) ? "percent-encoding"
                : WrittenPath(config.Resource).Split('/').Any(segment => segment is "." or "..") ? "a dot-segment"
                : null);

        if (notAsClientsCarryIt is not null)
        {
            throw new ConfigurationException(
                $"Authentication:Resource must be written as clients connect to it, https://{{host}}{HttpServerComposition.McpPath}, "
                + "with no query, fragment, user information, backslash, percent-encoding or dot-segment; it is "
                + $"'{LogSafe.Url(config.Resource)}', which carries {notAsClientsCarryIt}. It is published as written, and every token's "
                + "audience must equal it character for character, so a spelling that parses to the same URL is still "
                + "another resource.");
        }

        var path = WrittenPath(config.Resource);
        if (!string.Equals(path, HttpServerComposition.McpPath, StringComparison.Ordinal)
            && !string.Equals(path, HttpServerComposition.McpPath + "/", StringComparison.Ordinal))
        {
            throw new ConfigurationException(
                $"Authentication:Resource must name this server's MCP endpoint, https://{{host}}{HttpServerComposition.McpPath}; "
                + $"it is '{LogSafe.Url(config.Resource)}', whose path is '{path}'. MCP answers at {HttpServerComposition.McpPath} "
                + "(with one trailing slash at most), and a client that connects to a resource URL where nothing "
                + "answers cannot connect.");
        }

        foreach (var (name, provider) in config.IdentityProviders)
        {
            var key = $"Authentication:IdentityProviders:{name}";

            if (!Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority) ||
                authority.Scheme != Uri.UriSchemeHttps)
            {
                throw new ConfigurationException(
                    $"{key}:Authority must be an absolute https URI; it is '{LogSafe.Url(provider.Authority)}'. "
                    + "Discovery and signing keys are fetched from it, so plaintext would put key "
                    + "material on the wire.");
            }

            // contract-005 · G-12 (4) — discovery and keys are fetched from paths appended to the Authority,
            // so a query or a fragment would swallow them, and user information is a credential written into a
            // URL.
            if (QueryFragmentOrUserInfo(provider.Authority, authority) is { } authorityPart)
            {
                throw new ConfigurationException(
                    $"{key}:Authority must not carry {authorityPart}; it is '{LogSafe.Url(provider.Authority)}'. Discovery and signing "
                    + "keys are fetched from paths appended to it, which a query or a fragment would swallow, and user "
                    + "information would be a credential written into a URL.");
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
                    $"{key}:Issuer must be an absolute https URI; it is '{LogSafe.Url(provider.Issuer)}'. It is published as "
                    + "an authorization server, and clients are sent to it to find its metadata and to sign in, "
                    + "so plaintext would hand their credentials to anyone on the path.");
            }

            // contract-005 · G-12 (4) — an issuer identifier is an https URL with no query or fragment (RFC 8414
            // §2), and this one is published as written; user information in it would publish a credential.
            if (QueryFragmentOrUserInfo(provider.Issuer, issuer) is { } issuerPart)
            {
                throw new ConfigurationException(
                    $"{key}:Issuer must not carry {issuerPart}; it is '{LogSafe.Url(provider.Issuer)}'. An issuer identifier is an https "
                    + "URL with no query or fragment (RFC 8414, section 2), and this one is published as written, as an "
                    + "authorization server clients are sent to; user information in it would publish a credential.");
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
            // would switch the requirement off. So would any claim one provider or another puts on every
            // token — typ, ver, tid, acr, sid, auth_time, nonce, amr — and those passed a check that named
            // only the first two kinds, so ClientIdClaim is now one of the four names providers use for the
            // client, and nothing else. Compared exactly, as the claim is found on a validated token: the
            // bearer handler's identity matches claim names by case, so AZP would find no azp and refuse
            // every token. Trimmed, as it is looked up (IdentityRegistration).
            var clientClaim = provider.ClientIdClaim.Trim();
            var notAClient =
                RegisteredClaims.Contains(clientClaim, StringComparer.Ordinal)
                    ? "it is a registered JWT claim, present on every token or meaning something else, so requiring it "
                        + "would require nothing"
                : string.Equals(clientClaim, provider.ScopeClaim.Trim(), StringComparison.Ordinal)
                    ? "it is this identity provider's ScopeClaim, so requiring it would require nothing"
                : !ClientClaims.Contains(clientClaim, StringComparer.Ordinal)
                    ? "it is not one of the claims identity providers put the client in, and a claim some provider puts "
                        + "on every token (typ, ver, tid and the like) would require nothing, while any other name is on "
                        + "no token at all"
                : null;

            if (notAClient is not null)
            {
                throw new ConfigurationException(
                    $"{key}:ClientIdClaim is '{provider.ClientIdClaim}', which does not name the calling client: "
                    + $"{notAClient}. It must be exactly one of azp (Keycloak, Entra ID v2), cid (Okta), appid (Entra ID v1) "
                    + "or client_id (RFC 9068), whichever this identity provider puts the client in.");
            }
        }

        // contract-005 · G-17 round 1 — a token goes to the identity provider whose Issuer its iss names, the first that
        // does (IdentityRegistration, compared ordinally, as here), so a second provider with the same Issuer authenticates
        // no caller, and every provider bound to it refuses every one. A setting the server would never act on is refused;
        // the metadata listed the issuer twice besides.
        foreach (var shared in config.IdentityProviders
            .GroupBy(p => p.Value.Issuer, StringComparer.Ordinal)
            .Where(g => g.Count() > 1))
        {
            var names = shared.Select(p => p.Key).ToArray();
            throw new ConfigurationException(
                $"{Listed([.. names.Select(n => $"Authentication:IdentityProviders:{n}:Issuer")])} are {(names.Length == 2 ? "both" : "all")} "
                + $"'{LogSafe.Url(shared.Key)}'. A token goes to the identity provider whose Issuer its iss "
                + $"names, the first that does: '{names[0]}'. So {Listed([.. names[1..].Select(n => $"'{n}'")])} would authenticate no "
                + $"caller, and every provider bound to {(names.Length == 2 ? "it" : "them")} would refuse every one. Give each "
                + "identity provider the issuer its own tokens carry, or remove the one not meant.");
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

    /// <summary><paramref name="items"/> as a sentence lists them: "a", "a and b", "a, b and c".</summary>
    private static string Listed(string[] items) =>
        items.Length == 1 ? items[0] : $"{string.Join(", ", items[..^1])} and {items[^1]}";

    private static readonly char[] EndOfAuthority = ['/', '?', '#', '\\'];

    private static readonly char[] QueryOrFragment = ['?', '#'];

    /// <summary>
    /// What an absolute URI carries, as written, that a URL this server publishes or fetches from may
    /// not: a query, a fragment or user information. Null when it carries none. Read from the string as
    /// well as the parsed URI, because the string is what is published and fetched.
    /// </summary>
    private static string? QueryFragmentOrUserInfo(string written, Uri parsed) =>
        written.Contains('?', StringComparison.Ordinal) ? "a query"
        : written.Contains('#', StringComparison.Ordinal) ? "a fragment"
        : parsed.UserInfo.Length > 0 || WrittenAuthority(written).Contains('@', StringComparison.Ordinal) ? "user information"
        : null;

    /// <summary>The authority of an absolute URI as written: from after :// to the first / ? # or \.</summary>
    private static string WrittenAuthority(string written)
    {
        var start = written.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += 3;
        var end = written.IndexOfAny(EndOfAuthority, start);
        return end < 0 ? written[start..] : written[start..end];
    }

    /// <summary>
    /// The path of an absolute URI as written: from the first / after the authority, up to a query or a
    /// fragment. Empty when it has none, or when it is not written scheme://authority at all.
    /// </summary>
    private static string WrittenPath(string written)
    {
        var scheme = written.IndexOf("://", StringComparison.Ordinal);
        var start = scheme < 0 ? -1 : written.IndexOf('/', scheme + 3);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = written.IndexOfAny(QueryOrFragment, start);
        return end < 0 ? written[start..] : written[start..end];
    }
}
