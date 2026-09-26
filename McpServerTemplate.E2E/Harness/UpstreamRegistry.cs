using System.Collections.Immutable;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// Which container answers for each upstream host name.
///
/// contract-005 · G-7, G-16 — an extension point. Each upstream host name belongs to exactly one
/// container: two containers holding one network alias would be answered round-robin by Docker's
/// resolver, and a test would reach a different fake on each call. A container's network aliases and
/// its certificate's names are both taken from here, so the registry is the only place an upstream
/// name is given out. A later contract puts something else in front of an upstream by registering it
/// in place of the fake — egress's fault proxy, for one.
/// </summary>
public sealed class UpstreamRegistry
{
    /// <summary>The WireMock fake (G-7).</summary>
    public const string WireMock = "wiremock";

    private readonly ImmutableDictionary<string, string> _owners;

    public UpstreamRegistry()
        : this(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase))
    {
    }

    private UpstreamRegistry(ImmutableDictionary<string, string> owners) => _owners = owners;

    /// <summary>Every registered host name, with the container that answers for it.</summary>
    public IReadOnlyDictionary<string, string> Owners => _owners;

    /// <summary>Gives <paramref name="host"/> to <paramref name="owner"/>; a name already given out is refused.</summary>
    public UpstreamRegistry Register(string host, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        if (_owners.TryGetValue(host, out var current))
        {
            throw new InvalidOperationException(
                $"The upstream host '{host}' already belongs to '{current}'. A name has exactly one owner; "
                + "use Replace to hand it to another.");
        }

        return new UpstreamRegistry(_owners.Add(host, owner));
    }

    /// <summary>Hands a registered <paramref name="host"/> to <paramref name="owner"/> instead.</summary>
    public UpstreamRegistry Replace(string host, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        if (!_owners.ContainsKey(host))
        {
            throw new InvalidOperationException($"The upstream host '{host}' is not registered, so it cannot be replaced.");
        }

        return new UpstreamRegistry(_owners.SetItem(host, owner));
    }

    /// <summary>The host names one container answers for, in a stable order.</summary>
    public IReadOnlyList<string> HostsOf(string owner) =>
        [.. _owners.Where(o => o.Value == owner).Select(o => o.Key).Order(StringComparer.Ordinal)];
}
