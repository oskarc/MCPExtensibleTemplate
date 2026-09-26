using System.Collections.Immutable;
using Docker.DotNet;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// Which container answers for each upstream host name.
///
/// contract-005 · G-7, G-16 — an extension point. Each upstream host name belongs to exactly one
/// owner: two containers holding one network alias would be answered round-robin by Docker's
/// resolver, and a test would reach a different fake on each call. An owner's network aliases and its
/// certificate's names are both taken from here, so the registry is the only place an upstream name
/// is given out, and the environment starts every owner it names (<see cref="UpstreamOwner"/>). A
/// host registered to an owner that nothing then answers under is a fault of phase 'upstreams'
/// before any test runs, never a silent miss.
///
/// Registration is per run, not per test class. A network alias belongs to the run's network and a
/// certificate to the run's PKI, whose CA key is discarded once the leaves are signed, so the registry
/// is fixed before the environment starts (<see cref="E2EEnvironment.Upstreams"/>) and every server in
/// the run resolves a host to the same owner. A later contract puts something else in front of an
/// upstream by registering it there in place of the fake — T-15's stand-in for one provider host,
/// egress's fault proxy — and every test class in the run then reaches that owner for that host. A
/// test finds the container answering a host through <see cref="E2EEnvironment.UpstreamFor"/>, never
/// by assuming it is WireMock.
/// </summary>
public sealed class UpstreamRegistry
{
    private readonly ImmutableDictionary<string, UpstreamOwner> _owners;

    public UpstreamRegistry()
        : this(ImmutableDictionary<string, UpstreamOwner>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase))
    {
    }

    private UpstreamRegistry(ImmutableDictionary<string, UpstreamOwner> owners) => _owners = owners;

    /// <summary>Every registered host name, with the owner that answers for it.</summary>
    public IReadOnlyDictionary<string, UpstreamOwner> Owners => _owners;

    /// <summary>Every owner some host is registered to, each once, in a stable order.</summary>
    public IReadOnlyList<UpstreamOwner> DistinctOwners =>
        [.. _owners.Values.Distinct().OrderBy(o => o.Name, StringComparer.Ordinal)];

    /// <summary>Gives <paramref name="host"/> to <paramref name="owner"/>; a name already given out is refused.</summary>
    public UpstreamRegistry Register(string host, UpstreamOwner owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(owner);

        if (_owners.TryGetValue(host, out var current))
        {
            throw new InvalidOperationException(
                $"The upstream host '{host}' already belongs to '{current.Name}'. A name has exactly one owner; "
                + "use Replace to hand it to another.");
        }

        return new UpstreamRegistry(_owners.Add(host, owner));
    }

    /// <summary>Hands a registered <paramref name="host"/> to <paramref name="owner"/> instead.</summary>
    public UpstreamRegistry Replace(string host, UpstreamOwner owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(owner);

        if (!_owners.ContainsKey(host))
        {
            throw new InvalidOperationException($"The upstream host '{host}' is not registered, so it cannot be replaced.");
        }

        return new UpstreamRegistry(_owners.SetItem(host, owner));
    }

    /// <summary>The host names one owner answers for, in a stable order.</summary>
    public IReadOnlyList<string> HostsOf(UpstreamOwner owner) =>
        [.. _owners.Where(o => ReferenceEquals(o.Value, owner)).Select(o => o.Key).Order(StringComparer.Ordinal)];
}

/// <summary>
/// A kind of container that answers upstream host names: the WireMock fake today, a stand-in or a
/// fault proxy later.
///
/// contract-005 · G-16 — the owner knows the two things the environment needs of it: which names its
/// certificate must cover, and how to start its container on the run's network answering under the
/// host names the registry gives it. One instance is one container.
/// </summary>
public abstract class UpstreamOwner
{
    protected UpstreamOwner(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Its name: the leaf of the test PKI it serves with, and its log's name in the diagnostics bundle.</summary>
    public string Name { get; }

    /// <summary>The DNS names its PKI leaf must cover, given the host names the registry gives it.</summary>
    public virtual IReadOnlyList<string> CertificateNames(IReadOnlyList<string> hosts) => hosts;

    /// <summary>
    /// Starts its container on the run's network, answering under every one of <paramref name="hosts"/>
    /// with its leaf (<see cref="TestPki.LeafDirectory"/> of <see cref="Name"/>), and returns it once it
    /// answers.
    /// </summary>
    internal abstract Task<IUpstreamService> StartAsync(UpstreamStart start, IReadOnlyList<string> hosts, CancellationToken cancellationToken);

    public override string ToString() => Name;
}

/// <summary>What an <see cref="UpstreamOwner"/> is given to start its container with.</summary>
/// <param name="Docker">The engine, for what Testcontainers does not expose.</param>
/// <param name="Network">The run's network.</param>
/// <param name="Pki">The run's test PKI, holding the owner's leaf.</param>
/// <param name="Names">The name map so far, which the owner extends with its own names.</param>
/// <param name="RunId">The run, for the label on everything it creates.</param>
internal sealed record UpstreamStart(IDockerClient Docker, INetwork Network, TestPki Pki, NameMap Names, string RunId);

/// <summary>A running upstream container, as its owner started it.</summary>
public interface IUpstreamService : IAsyncDisposable
{
    public IContainer Container { get; }

    /// <summary>The name map entries for the host names it answers under.</summary>
    public NameMap Names { get; }
}
