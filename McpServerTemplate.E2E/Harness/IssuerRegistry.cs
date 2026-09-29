using System.Collections.Immutable;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The identity providers the environment offers, under the names the server's configuration uses.
///
/// contract-005 · G-16 — an extension point. The server's Authentication:IdentityProviders settings
/// are generated from this registry and from nothing else, and the test issuer container answers
/// under exactly the host names registered to it, so a later contract adds an identity provider by
/// registering it here — Phase 4's second Keycloak realm, say — and the server, the name map and the
/// certificates follow.
///
/// contract-005 · T-3 — it also holds strangers (<see cref="WithStranger"/>): issuers a container
/// answers as, under their own names and keys and counted like any other, that no server is
/// configured with. A stranger's token is a real token from an issuer nobody registered.
/// </summary>
public sealed class IssuerRegistry
{
    /// <summary>The containers that can serve an issuer.</summary>
    public enum Owner
    {
        /// <summary>The shared Keycloak (G-5).</summary>
        Keycloak,

        /// <summary>The test issuer container, which answers under each of its names (G-6).</summary>
        TestIssuer,
    }

    /// <summary>One identity provider as the server is told about it.</summary>
    /// <param name="Name">The key under Authentication:IdentityProviders.</param>
    /// <param name="Authority">Where discovery and signing keys are fetched.</param>
    /// <param name="Issuer">The iss value pinned for this provider.</param>
    /// <param name="ServedBy">The container that answers for it.</param>
    /// <param name="ClientIdClaim">The claim naming the calling application.</param>
    /// <param name="ScopeCatalog">The scopes it may assert.</param>
    public sealed record Entry(
        string Name,
        Uri Authority,
        string Issuer,
        Owner ServedBy,
        string ClientIdClaim,
        IReadOnlyList<string> ScopeCatalog)
    {
        /// <summary>The claim carrying scopes. Every issuer in the environment uses scope.</summary>
        public string ScopeClaim { get; init; } = "scope";

        /// <summary>The signing algorithms the server accepts from it.</summary>
        public IReadOnlyList<string> Algorithms { get; init; } = ["RS256"];

        /// <summary>The host name the server and the test reach it under.</summary>
        public string Host => Authority.Host;
    }

    /// <summary>
    /// contract-005 · T-3 — an issuer no server is configured with: its container answers as it at
    /// https://{host}, with a key of its own, and counts every request made to it, so "no key lookup at
    /// any issuer" includes the stranger's own.
    /// </summary>
    /// <param name="Name">What the tests call it.</param>
    /// <param name="Authority">https://{host}: where it answers, and the iss its tokens carry.</param>
    /// <param name="ServedBy">The container that answers for it.</param>
    public sealed record Stranger(string Name, Uri Authority, Owner ServedBy)
    {
        /// <summary>The iss value its tokens carry.</summary>
        public string Issuer => Authority.ToString().TrimEnd('/');

        /// <summary>The host name the test reaches it under; no server is told of it.</summary>
        public string Host => Authority.Host;
    }

    private readonly ImmutableList<Entry> _entries;
    private readonly ImmutableList<Stranger> _strangers;

    public IssuerRegistry()
        : this(ImmutableList<Entry>.Empty, ImmutableList<Stranger>.Empty)
    {
    }

    private IssuerRegistry(ImmutableList<Entry> entries, ImmutableList<Stranger> strangers)
    {
        _entries = entries;
        _strangers = strangers;
    }

    /// <summary>The identity providers every server is configured with.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>The issuers no server is configured with.</summary>
    public IReadOnlyList<Stranger> Strangers => _strangers;

    public Entry this[string name] =>
        _entries.FirstOrDefault(e => e.Name == name)
        ?? throw new KeyNotFoundException($"No identity provider named '{name}' is registered.");

    /// <summary>Adds an identity provider; a name already registered is refused, not replaced.</summary>
    public IssuerRegistry Register(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entries.Any(e => e.Name == entry.Name) || _strangers.Any(s => s.Name == entry.Name))
        {
            throw new InvalidOperationException($"An identity provider named '{entry.Name}' is already registered.");
        }

        return new IssuerRegistry(_entries.Add(entry), _strangers);
    }

    /// <summary>
    /// Adds an issuer no server is configured with. Its host may be no registered provider's: a
    /// stranger that answered under a trusted name would be that provider, not a stranger.
    /// </summary>
    public IssuerRegistry WithStranger(Stranger stranger)
    {
        ArgumentNullException.ThrowIfNull(stranger);
        if (_entries.Any(e => e.Name == stranger.Name || e.Host.Equals(stranger.Host, StringComparison.OrdinalIgnoreCase))
            || _strangers.Any(s => s.Name == stranger.Name || s.Host.Equals(stranger.Host, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{stranger.Name}' ({stranger.Host}) is already registered, as a provider or a stranger.");
        }

        return new IssuerRegistry(_entries, _strangers.Add(stranger));
    }

    /// <summary>The stranger named <paramref name="name"/>.</summary>
    public Stranger StrangerNamed(string name) =>
        _strangers.FirstOrDefault(s => s.Name == name)
        ?? throw new KeyNotFoundException($"No stranger named '{name}' is registered.");

    /// <summary>The host names a container answers issuers under, strangers' included.</summary>
    public IReadOnlyList<string> HostsServedBy(Owner owner) =>
        [.. _entries.Where(e => e.ServedBy == owner).Select(e => e.Host)
            .Concat(_strangers.Where(s => s.ServedBy == owner).Select(s => s.Host))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The issuer identifiers a container answers as, one per host, in registration order, strangers' last.</summary>
    public IReadOnlyList<string> IssuersServedBy(Owner owner) =>
        [.. _entries.Where(e => e.ServedBy == owner).DistinctBy(e => e.Host, StringComparer.OrdinalIgnoreCase).Select(e => e.Issuer)
            .Concat(_strangers.Where(s => s.ServedBy == owner).Select(s => s.Issuer))];

    /// <summary>The server's Authentication:IdentityProviders settings for every registered provider; strangers have none.</summary>
    public IEnumerable<KeyValuePair<string, string>> ToSettings()
    {
        foreach (var entry in _entries)
        {
            var prefix = $"Authentication:IdentityProviders:{entry.Name}";
            yield return new($"{prefix}:Authority", entry.Authority.ToString().TrimEnd('/'));
            yield return new($"{prefix}:Issuer", entry.Issuer);
            yield return new($"{prefix}:ScopeClaim", entry.ScopeClaim);
            yield return new($"{prefix}:ClientIdClaim", entry.ClientIdClaim);

            for (var i = 0; i < entry.Algorithms.Count; i++)
            {
                yield return new($"{prefix}:Algorithms:{i}", entry.Algorithms[i]);
            }

            for (var i = 0; i < entry.ScopeCatalog.Count; i++)
            {
                yield return new($"{prefix}:ScopeCatalog:{i}", entry.ScopeCatalog[i]);
            }
        }
    }
}
