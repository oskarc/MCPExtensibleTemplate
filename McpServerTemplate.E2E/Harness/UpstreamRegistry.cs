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

    /// <summary>
    /// contract-005 · G-16, T-15 — every request it has recorded, whatever kind of container it is: how a test reads what
    /// reached the container answering a host (<see cref="E2EEnvironment.UpstreamFor"/>) without assuming it is WireMock.
    /// contract-005 · G-17 round 2 (HAR-R2-02) — each with when it was recorded, which a test reads one server's requests by,
    /// through <see cref="ProviderCalls.FromServerAsync"/> and <see cref="RequestSource"/>, and never by address alone.
    /// contract-005 · G-17 round 3 (HAR-R3-04) — the requests <paramref name="from"/> made, and no others: every owner's record was
    /// returned whole, and a caller could filter it by an address alone.
    /// </summary>
    /// <param name="http">A client made from the environment's name map, for an owner whose record is read over HTTPS.</param>
    /// <param name="from">The server whose requests are read: from its address, since it started.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<IReadOnlyList<UpstreamRequest>> RecordedAsync(HttpClient http, RequestSource from, CancellationToken cancellationToken = default);

    /// <summary>
    /// contract-005 · T-6 — every request it has recorded, whole, one text each: its URL, every header it arrived with, and its
    /// body. What T-6 searches for a caller's token, "in any header, URL or body"; an owner whose record kept less would hide a
    /// token passed through in what it left out, so an owner keeps the whole request or is not one.
    /// </summary>
    /// <param name="http">A client made from the environment's name map, for an owner whose record is read over HTTPS.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<IReadOnlyList<string>> WholeRecordsAsync(HttpClient http, CancellationToken cancellationToken = default);

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-06) — sends this owner one request of the test's own, carrying
    /// <paramref name="header"/> as the value of the header <see cref="UpstreamWitness.Header"/> and <paramref name="body"/> as
    /// its body, to <see cref="UpstreamWitness.Path"/>: T-6's positive control, which requires <see cref="WholeRecordsAsync"/>
    /// to hold both before it searches every record for a token. A record that kept no header, or no body, would find no token
    /// there however many a server had passed on, and nothing had ever seen the stand-in's record hold either. Every owner can
    /// be sent one, whether the test process reaches it (the fake, through its published port) or not (the stand-in, from
    /// inside its own container).
    /// </summary>
    /// <param name="http">A client made from the environment's name map, for an owner the test process reaches over HTTPS.</param>
    /// <param name="header">The header's value, a marker no other request carries.</param>
    /// <param name="body">The body, another such marker.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task SendWitnessAsync(HttpClient http, string header, string body, CancellationToken cancellationToken = default);
}

/// <summary>contract-005 · G-17 round 2 (HAR-R2-06) — the request T-6 sends every upstream owner before its search (<see cref="IUpstreamService.SendWitnessAsync"/>).</summary>
public static class UpstreamWitness
{
    /// <summary>The header its marker travels in.</summary>
    public const string Header = "X-E2E-Witness";

    /// <summary>The path it is sent to: one no provider asks for.</summary>
    public const string Path = "/e2e-t6-witness";
}

/// <summary>One request an upstream container recorded.</summary>
/// <param name="Client">The address it came from: on the run's network, the server that made it.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Host">The host name it was sent to, as its Host header named it.</param>
/// <param name="Path">The path, with its query.</param>
/// <param name="At">
/// contract-005 · G-17 round 2 (HAR-R2-02) — when the owner recorded it, by the Docker engine's clock, which every container
/// of the run shares: the fake's own time for the request, the stand-in's log line's time. What a read bounds by a server's
/// start (<see cref="RequestSource"/>), since Docker hands a freed address to the next container at once.
/// </param>
public sealed record UpstreamRequest(System.Net.IPAddress? Client, string Method, string Host, string Path, DateTimeOffset At);
