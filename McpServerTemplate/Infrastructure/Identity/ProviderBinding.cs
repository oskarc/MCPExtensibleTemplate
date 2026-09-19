using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Which identity provider each provider answers to, and which provider each tool came from.
///
/// contract-002 · G-5 — two providers may share an identity provider; none may have none, and
/// none may have more than one. The binding is a deployment decision, so it lives in
/// configuration (<c>Providers:{Name}:IdentityProvider</c>), is required, and is validated at
/// startup rather than discovered when a call is refused.
///
/// The consequence this protects is not subtle: without it, a token from any identity provider
/// the server trusts reaches every tool the server exposes. A contractor's identity provider,
/// federated for one team's tools, would open the others.
/// </summary>
public sealed class ProviderBinding
{
    private readonly Dictionary<string, string> _identityProviderByProvider;
    private readonly Dictionary<string, string> _providerByTool;
    private readonly Dictionary<string, string> _scopeByTool;

    private ProviderBinding(
        Dictionary<string, string> identityProviderByProvider,
        Dictionary<string, string> providerByTool,
        Dictionary<string, string> scopeByTool)
    {
        _identityProviderByProvider = identityProviderByProvider;
        _providerByTool = providerByTool;
        _scopeByTool = scopeByTool;
    }

    /// <summary>Provider names that were bound, for diagnostics.</summary>
    public IReadOnlyCollection<string> Providers => _identityProviderByProvider.Keys;

    /// <summary>
    /// Builds the binding, refusing a configuration it cannot enforce.
    /// </summary>
    /// <param name="configuration">Configuration holding the <c>Providers</c> section.</param>
    /// <param name="identity">The validated identity configuration.</param>
    /// <param name="tools">The registered tools, whose metadata names their provider.</param>
    public static ProviderBinding Create(
        IConfiguration configuration,
        AuthenticationConfig identity,
        IEnumerable<McpServerTool> tools)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tools);

        var providerByTool = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopeByTool = new Dictionary<string, string>(StringComparer.Ordinal);
        var unattributed = new List<string>();
        var unscoped = new List<string>();

        foreach (var tool in tools)
        {
            var declared = tool.Metadata
                .OfType<McpProviderAttribute>()
                .Select(a => a.Name)
                .FirstOrDefault();

            if (declared is null)
            {
                unattributed.Add(tool.ProtocolTool.Name);
                continue;
            }

            providerByTool[tool.ProtocolTool.Name] = declared;

            var scope = tool.Metadata.OfType<McpScopeAttribute>().Select(a => a.Scope).FirstOrDefault();
            if (scope is null)
            {
                unscoped.Add(tool.ProtocolTool.Name);
                continue;
            }

            scopeByTool[tool.ProtocolTool.Name] = scope;
        }

        if (unscoped.Count > 0)
        {
            // Same reasoning as an unowned tool: a tool that requires no scope is reachable by any
            // token this server accepts, which is a permission nobody granted.
            throw new ConfigurationException(
                $"These tools declare no scope: {string.Join(", ", unscoped)}. Put "
                + "[McpScope(\"name:action\")] on the type that declares them. A tool requiring no "
                + "scope is reachable by every token this server accepts.");
        }

        if (unattributed.Count > 0)
        {
            // A tool nobody owns cannot be bound, and an unbindable tool is reachable by every
            // identity provider the server trusts. Refusing to start is the only honest answer.
            throw new ConfigurationException(
                $"These tools declare no provider: {string.Join(", ", unattributed)}. Put "
                + "[McpProvider(\"Name\")] on the type that declares them, so their identity-provider "
                + "binding can be enforced. A tool with no provider would be reachable from every "
                + "identity provider this server trusts.");
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in providerByTool.Values.Distinct(StringComparer.Ordinal))
        {
            var key = $"Providers:{provider}:IdentityProvider";
            var bound = configuration[key];

            if (string.IsNullOrWhiteSpace(bound))
            {
                throw new ConfigurationException(
                    $"{key} is required. Every provider answers to exactly one identity provider, "
                    + "and a provider with none would be reachable by tokens from all of them.");
            }

            if (!identity.IdentityProviders.ContainsKey(bound))
            {
                throw new ConfigurationException(
                    $"{key} names '{bound}', which is not a configured identity provider. "
                    + $"Configured: {string.Join(", ", identity.IdentityProviders.Keys)}.");
            }

            bindings[provider] = bound;
        }

        // Every scope a tool requires must be one its bound identity provider can actually issue,
        // or the tool is unreachable by anyone and the deployment does not know it.
        foreach (var (tool, scope) in scopeByTool)
        {
            var bound = bindings[providerByTool[tool]];
            var catalog = identity.IdentityProviders[bound].ScopeCatalog;

            if (!catalog.Contains(scope, StringComparer.Ordinal))
            {
                throw new ConfigurationException(
                    $"The tool '{tool}' requires scope '{scope}', which its identity provider "
                    + $"'{bound}' cannot issue. That provider's catalog is "
                    + $"{string.Join(", ", catalog)}. The tool would be unreachable by every caller.");
            }
        }

        return new ProviderBinding(bindings, providerByTool, scopeByTool);
    }

    /// <summary>
    /// Whether a principal from <paramref name="identityProvider"/> may see or call
    /// <paramref name="toolName"/>.
    ///
    /// An unknown tool returns false: the safe answer for something this binding cannot account
    /// for is no.
    /// </summary>
    public bool Allows(string toolName, string? identityProvider)
    {
        if (string.IsNullOrEmpty(identityProvider))
        {
            return false;
        }

        return _providerByTool.TryGetValue(toolName, out var provider) &&
               _identityProviderByProvider.TryGetValue(provider, out var bound) &&
               string.Equals(bound, identityProvider, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a principal holding <paramref name="scopes"/> may see or use <paramref name="toolName"/>.
    /// Both conditions must hold: the trust domain matches, and the scope is held.
    /// </summary>
    public bool Allows(string toolName, string? identityProvider, IReadOnlyCollection<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        return Allows(toolName, identityProvider) &&
               _scopeByTool.TryGetValue(toolName, out var required) &&
               scopes.Contains(required, StringComparer.Ordinal);
    }

    /// <summary>The scope a tool requires, or null if it is not a tool this server exposes.</summary>
    public string? ScopeOf(string toolName) =>
        _scopeByTool.TryGetValue(toolName, out var scope) ? scope : null;

    /// <summary>The provider a tool belongs to, or null if it is not a tool this server exposes.</summary>
    public string? ProviderOf(string toolName) =>
        _providerByTool.TryGetValue(toolName, out var provider) ? provider : null;

    /// <summary>The identity provider a provider answers to.</summary>
    public string? IdentityProviderOf(string provider) =>
        _identityProviderByProvider.TryGetValue(provider, out var bound) ? bound : null;
}
