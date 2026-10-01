using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The network constant, and the one network a run creates from it.
///
/// contract-005 · G-4, G-16 — every container of a run sits on one user-defined network whose IPv4
/// subnet is <see cref="Subnet"/>. Docker's default networks fall in 172.16/12, which roadmap P2.7's
/// egress guard will refuse, so a fake upstream on one would be unreachable by design once that guard
/// lands. TEST-NET-2 lies outside P2.7's list as the roadmap states it today. Which ranges the guard
/// blocks is the egress contract's decision, not this constant's: if that decision moves, this is the
/// one line that moves with it, and no test-only relaxation of address checking is allowed instead.
///
/// Every address the harness uses is derived from the constant: the gateway is its first address,
/// the lower half holds the static addresses the front and the proxies are given (they must be
/// known before the server starts, because KnownProxies names them), and Docker hands out the
/// upper half to everything else, so a static address can never be taken by a dynamic one.
///
/// contract-005 · UC-6 — the pioneer's decision of 2026-09-29 (contract-005's revision of that date, "2. yes, cut it and
/// mock any calls"), as implemented: the network resolves no real name. Every container joins it one way
/// (<see cref="OnTheRunNetwork"/>), with its own loopback address as its only name server (<see cref="NameServer"/>) and no
/// search domain, so every call a server makes by name is answered by a fake of the run or fails at the name lookup.
/// contract-005 · G-17 round 2 (CON-R2-01) — that is all it holds: the network itself is not cut, and a container of the run
/// still reaches the internet by an address written as one. Names only or a real cut is put back to the pioneer
/// (contract-005's log, the correction of 2026-09-30); until then the harness claims no more than names.
/// </summary>
public static class E2ENetwork
{
    /// <summary>The run's IPv4 subnet: a slice of 198.51.100.0/24 (RFC 5737 TEST-NET-2).</summary>
    public const string Subnet = "198.51.100.0/26";

    /// <summary>A label on everything a run creates, so a run's leftovers can be found by hand.</summary>
    public const string RunLabel = "org.mcp-server-template.e2e.run";

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-06, FLA-R2-04) — a label on everything a run creates beside the run's own, naming who
    /// owns it: the machine, the operating system, the account and the temporary directory its process ran with
    /// (<see cref="ThisOwner"/>). A run's liveness is read from its run directory, which lies in its owner's temporary directory,
    /// so only a run of this owner can be judged at all: the next run's sweep removes a dead run of this owner, and leaves every
    /// other alone. It judged every run by this process's temporary directory, so a live run of another — a WSL run on the same
    /// Docker Desktop, another account on a shared engine — had its containers and network removed from under it mid-run.
    /// </summary>
    public const string OwnerLabel = "org.mcp-server-template.e2e.owner";

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-06) — a label naming the process that owns the run, "pid@start", as its run directory
    /// records it: what a fault naming a run it leaves alone says of it.
    /// </summary>
    public const string ProcessLabel = "org.mcp-server-template.e2e.process";

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-06, FLA-R2-04) — this process's owner, as <see cref="OwnerLabel"/> carries it: the
    /// machine, the operating system's family, the account and the temporary directory.
    /// </summary>
    public static string ThisOwner { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"machine={Environment.MachineName}; os={OperatingSystemFamily()}; account={Environment.UserDomainName}\\{Environment.UserName}; temp={Path.GetFullPath(Path.GetTempPath())}");

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-06) — the labels on everything the run <paramref name="runId"/> creates: the run, its
    /// owner and its owner's process.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RunLabels(string runId) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RunLabel] = runId,
        [OwnerLabel] = ThisOwner,
        [ProcessLabel] = E2EEnvironment.ThisProcess,
    };

    /// <summary>The operating system's family, which does not change with its updates: an owner's, as <see cref="ThisOwner"/> names it.</summary>
    private static string OperatingSystemFamily() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other";

    /// <summary>
    /// contract-005 · UC-6 — the pioneer's decision of 2026-09-29 ("2. yes, cut it and mock any calls"): the one name server
    /// every container of a run is given, its own loopback address. Docker's embedded DNS (127.0.0.11 in the container)
    /// answers the run network's own names — every alias the upstream and issuer registries give out, every service and
    /// server — itself, and forwards any other name to the container's name servers; a name server given as a container's
    /// own (--dns) is reached from the container's own network namespace ("--dns=127.0.0.1 refers to the container's own
    /// loopback address", Docker's networking guide; moby's resolvconf marks only the host's own resolvers for the host's
    /// namespace). Nothing listens on port 53 there, so the forward is refused at once, the embedded DNS answers SERVFAIL
    /// (moby, libnetwork/resolver.go), and the lookup fails without a packet leaving the container: a host no fake of the run
    /// answers fails at the name lookup, and a real name never reaches a real address. The published ports the test process
    /// reaches every service through are untouched.
    ///
    /// Why this and not a stricter cut, on Docker Desktop and on the runner alike: an internal network (--internal, and the
    /// isolated gateway mode, which requires it) publishes no port at all (moby#36174, moby#27441), and the test process
    /// reaches the front, the issuers, Keycloak and the fake's journal only through ports published on loopback. The routed
    /// gateway mode, or a network without masquerading, sends a container's packets out with its own address and relies on
    /// the network beyond to drop them, which a Linux runner's does and Docker Desktop's user-space networking does not
    /// promise. A firewall rule (DOCKER-USER) needs root on the host and has no equivalent on Docker Desktop. An unroutable
    /// name server would cut the same names, but each lookup would wait out the embedded DNS's four-second forward, and
    /// would rely on nothing routing or answering that address. What this does not cut is a connection to an address
    /// written as one: nothing the suite configures is — a provider's BaseUrl must name its policy's host, an identity
    /// provider's Authority names a host — so no call reaches the internet by name. contract-005 · G-17 round 2 (CON-R2-01) — the
    /// narrowing of "cut it" to names was the agent's, not the pioneer's; whether a connection by address must be cut as well is
    /// the pioneer's to decide, and this cuts names only.
    /// </summary>
    public const string NameServer = "127.0.0.1";

    /// <summary>
    /// contract-005 · the pioneer's decision of 2026-09-29 — no search domain: Docker's "." for none (moby's resolvconf,
    /// OverrideSearch). Without it a container keeps the host's own search domains, which are tried first for a name with
    /// fewer dots than the host's ndots; the forward of that try fails SERVFAIL, and musl's resolver (the Alpine images:
    /// nginx, Redis, WireMock) stops at a SERVFAIL instead of trying the name itself, so a front could not find its server by
    /// its alias.
    /// </summary>
    public const string NoSearchDomain = ".";

    private static readonly IPAddress Base = IPAddress.Parse(Subnet.Split('/')[0]);
    private static readonly int PrefixLength = int.Parse(Subnet.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture);
    private static readonly int Size = 1 << (32 - PrefixLength);

    // The first static address handed out; below it the gateway and a few spare addresses.
    private static int _nextStatic = 9;

    /// <summary>The network's gateway: the subnet's first address.</summary>
    public static IPAddress Gateway => Offset(1);

    /// <summary>
    /// A spare address below the first static one, never handed out: no container on the network
    /// holds it. A server started only to see whether it starts has no front, so its KnownProxies
    /// names this, and it trusts no container as a proxy. contract-005 · T-11 — each such server
    /// used to take a static address of its own, and the rows T-11 holds outnumber the static half.
    /// </summary>
    public static IPAddress NoFront => Offset(9);

    /// <summary>
    /// contract-005 · G-17 round 1 (DOC-R1-01) — the static address the docs profile gives the documented proxy: docs/04's
    /// compose file fixes its proxy's address on the file's own network, and its server's KnownProxies names that address, so
    /// the run's copy of both must be known before either starts (<see cref="DocsProfile.Table"/>). Below the first static
    /// address handed out, like <see cref="NoFront"/>, and never handed out: one docs profile runs in a run.
    /// </summary>
    public static IPAddress DocsProxy => Offset(8);

    /// <summary>The upper half of the subnet, from which Docker assigns addresses on its own.</summary>
    public static string DynamicRange => $"{Offset(Size / 2)}/{PrefixLength + 1}";

    /// <summary>
    /// How many containers the dynamic range can hold at once: its addresses less the subnet's broadcast address, its last.
    ///
    /// contract-005 · G-17 round 1 (FLA-R1-02) — 31 for 198.51.100.0/26. What a run holds at once is the five shared services
    /// (Keycloak, the test issuer, the upstream fake, the stand-in and Redis) and, for each test class running, its server and
    /// what its test starts beside it: two at most for most classes, five for LimitsTests (two servers, a Redis of its own,
    /// and, in its outage test, another of each). Each front has a static address, and does not count. xUnit ran as many
    /// classes at once as the machine has cores, 32 on the development machine, where the range was seen full; the E2E
    /// project's xunit.runner.json caps it at 4 — the runner's own core count, the one configuration CI's budget was measured
    /// on — so the most a run holds at once is 5 + 5 + 2 + 2 + 2 = 16 of 31, whatever the machine.
    /// </summary>
    public static int DynamicAddresses => (Size / 2) - 1;

    /// <summary>
    /// contract-005 · G-17 round 1 (FLA-R1-02) — Docker's refusal to start a container because the run's network has no
    /// address left to give it, as the environment fault it is, naming the pool and the phase it met; null when
    /// <paramref name="exception"/> is anything else. Met as it was, it reached a test as the engine's own error, in a server's
    /// phase, where a failure reads as the product's.
    /// </summary>
    internal static EnvironmentFaultException? AddressPoolExhausted(string phase, Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is DockerApiException docker && docker.Message.Contains("no available IPv4 addresses", StringComparison.OrdinalIgnoreCase))
            {
                return new EnvironmentFaultException(
                    phase,
                    $"address pool exhausted: the run's network has no address left to give this container. Its dynamic range, "
                    + $"{DynamicRange}, holds {DynamicAddresses} containers at once; more were running (the shared services, and each "
                    + "test class's server and what its test starts). McpServerTemplate.E2E/xunit.runner.json bounds how many classes "
                    + $"run at once, and {nameof(E2ENetwork)}.{nameof(DynamicAddresses)} says what that allows. The engine said: {docker.Message}",
                    exception);
            }
        }

        return null;
    }

    /// <summary>
    /// A static address in the lower half, unique for this run. The front is given one before the
    /// server starts, so the server's KnownProxies can name it.
    /// </summary>
    public static IPAddress AllocateStatic()
    {
        var offset = Interlocked.Increment(ref _nextStatic);
        if (offset >= Size / 2)
        {
            throw new InvalidOperationException(
                $"The static half of {Subnet} is used up ({offset - 10} addresses handed out this run).");
        }

        return Offset(offset);
    }

    /// <summary>
    /// contract-005 · G-4, UC-6 — the one way a container of the run is made: of <paramref name="image"/>, on
    /// <paramref name="network"/>, carrying the run's labels (<see cref="RunLabels"/>), with <see cref="NameServer"/> as its only
    /// name server and no search domain (<see cref="NoSearchDomain"/>) — the pioneer's decision of 2026-09-29, "cut it and mock
    /// any calls", as implemented (names). HarnessSelfTests fails on a file of the suite that makes a container builder, joins a
    /// container to a network, or sets a startup callback any other way.
    ///
    /// contract-005 · G-17 round 2 (HAR-R2-08) — and checked as it starts: the moment the engine has started it, the container's
    /// own record of how it was created — its name servers, search domains and written-in host names — is read back, and a
    /// container that could resolve a real name fails its start as an environment fault naming it (<see cref="CouldResolveRealNames"/>).
    /// What the engine gave a container used to be read only for the shared services, at the environment's start, and in T-6's
    /// last class; every other class's server and front, T-7's Redis, the docs profile's services and the startup-outcome
    /// servers were held by the suite's source alone, which a later create-parameter modifier, or ExtraHosts, would pass.
    /// contract-005 · G-17 round 3 (HAR-R3-05) — and the check reads more than that record: nothing may be mounted over a file its
    /// resolver reads, and the resolv.conf and hosts file it actually has are read from it and held to what the engine writes
    /// (<see cref="ResolverNotTheRunsAsync"/>).
    /// </summary>
    internal static ContainerBuilder OnTheRunNetwork(string image, IDockerClient docker, INetwork network, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return new ContainerBuilder(image)
            .WithNetwork(network)
            .WithLabel(RunLabels(runId))
            .WithCreateParameterModifier(parameters =>
            {
                // contract-005 · G-17 round 4 (FLA-R4-03) — run as the engine is asked to create it: none once the run is stopping.
                E2EEnvironment.RefuseWhileStopping($"create a container of {image}");
                parameters.HostConfig ??= new HostConfig();
                parameters.HostConfig.DNS = [NameServer];
                parameters.HostConfig.DNSSearch = [NoSearchDomain];
            })
            .WithStartupCallback((container, cancellationToken) => CheckStartedResolvingNoRealNameAsync(docker, container.Id, cancellationToken));
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-08) — the container <paramref name="containerId"/>, as the engine records how it was
    /// created, resolves no real name; an environment fault naming it and what it was given otherwise. Run the moment each
    /// container of the run starts (<see cref="OnTheRunNetwork"/>), and again for a server whose start is let stop at once
    /// (<see cref="DockerEngine.StartServerAsync"/>).
    /// </summary>
    internal static async Task CheckStartedResolvingNoRealNameAsync(IDockerClient docker, string containerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var container = await docker.Containers.InspectContainerAsync(containerId, cancellationToken);
        if ((CouldResolveRealNames(container) ?? await ResolverNotTheRunsAsync(docker, container, cancellationToken)) is { } why)
        {
            throw new EnvironmentFaultException(
                "names",
                $"the container {container.Name.TrimStart('/')} ({container.Config?.Image}) was started able to resolve real names: {why}. "
                + $"Every container of the run is made through {nameof(E2ENetwork)}.{nameof(OnTheRunNetwork)}, with {NameServer} as its only "
                + "name server, no search domain, no host names written in and nothing mounted over its resolver's files, so that no "
                + "call of the run reaches the internet by name.");
        }
    }

    /// <summary>
    /// contract-005 · UC-6, G-17 round 2 (HAR-R2-08) — why a container, as the engine records how it was created, could resolve a
    /// real name, or null when it could not: a name server other than its own loopback alone, a search domain, or a host name
    /// written into its hosts file (ExtraHosts), which is answered before any name server is asked. contract-005 · G-17 round 3
    /// (HAR-R3-05) — or a mount over a file its resolver reads (<see cref="MountedOverResolverFiles"/>): with a hosts file or a
    /// resolv.conf of its own bound in, a container resolved real names while its record showed the name server, search domains
    /// and written-in names this accepts.
    /// </summary>
    internal static string? CouldResolveRealNames(ContainerInspectResponse container)
    {
        ArgumentNullException.ThrowIfNull(container);

        var servers = container.HostConfig?.DNS ?? [];
        var search = container.HostConfig?.DNSSearch ?? [];
        var written = container.HostConfig?.ExtraHosts ?? [];
        var mounted = MountedOverResolverFiles(container);
        return servers.SequenceEqual([NameServer], StringComparer.Ordinal)
            && search.SequenceEqual([NoSearchDomain], StringComparer.Ordinal)
            && written.Count == 0
            && mounted.Count == 0
            ? null
            : $"name servers [{string.Join(", ", servers)}], search domains [{string.Join(", ", search)}], host names written in "
                + $"[{string.Join(", ", written)}], mounts over its resolver's files [{string.Join(", ", mounted)}]";
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-05) — the files a container's resolver reads, whatever image it runs: its name servers and
    /// search domains (resolv.conf), the names answered before any name server is asked (hosts), and the order glibc asks its
    /// sources in (nsswitch.conf). The engine writes the first two into every container it starts on the run's network.
    /// </summary>
    internal static readonly string[] ResolverFiles = ["/etc/resolv.conf", "/etc/hosts", "/etc/nsswitch.conf"];

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-05) — the resolver file a mount at <paramref name="target"/> lies over — the file itself, or
    /// a directory above it — or null when it lies over none. The path is read as the engine reads it: separators doubled, "." and
    /// ".." resolved, a trailing separator dropped.
    /// </summary>
    internal static string? ResolverFileUnder(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var parts = new List<string>();
        foreach (var part in target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        var clean = "/" + string.Join('/', parts);
        return ResolverFiles.FirstOrDefault(f => f == clean || clean == "/" || f.StartsWith(clean + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-05) — every mount of <paramref name="container"/>, as the engine records it, that lies over a
    /// resolver file (<see cref="ResolverFileUnder"/>): each mount point it lists (a file or a directory bound in, a volume), each
    /// mount it was created with, and each tmpfs.
    /// </summary>
    internal static IReadOnlyList<string> MountedOverResolverFiles(ContainerInspectResponse container)
    {
        ArgumentNullException.ThrowIfNull(container);

        IEnumerable<string?> targets = [
            .. (container.Mounts ?? []).Select(m => m.Destination),
            .. (container.HostConfig?.Mounts ?? []).Select(m => m.Target),
            .. container.HostConfig?.Tmpfs?.Keys ?? []];
        return [.. targets
            .OfType<string>()
            .Where(t => ResolverFileUnder(t) is not null)
            .Select(t => $"{t} (over {ResolverFileUnder(t)})")
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-05) — the name server Docker's embedded DNS answers on, in every container on a user-defined
    /// network: the one a container of the run's resolv.conf may name.
    /// </summary>
    internal const string EmbeddedDns = "127.0.0.11";

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-05) — why the resolver <paramref name="container"/> actually has is not the run's, or null
    /// when it is: its /etc/resolv.conf and /etc/hosts, read through Docker's archive API as the CA-trust check reads files, held
    /// to what the engine writes for a container of the run (<see cref="ResolvConfNotTheRuns"/>, <see cref="HostsNotTheRuns"/>).
    /// The engine's record of how the container was started (<see cref="CouldResolveRealNames"/>) is not the resolver it uses: a
    /// file mounted or written over either one resolved real names with that record unchanged. A container that has stopped, or
    /// gone, resolves nothing more, and is not one.
    /// </summary>
    internal static async Task<string?> ResolverNotTheRunsAsync(IDockerClient docker, ContainerInspectResponse container, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(container);

        string? resolv;
        string? hosts;
        try
        {
            resolv = await ContainerArchive.ReadTextAsync(docker, container.ID, "/etc/resolv.conf", cancellationToken);
            hosts = await ContainerArchive.ReadTextAsync(docker, container.ID, "/etc/hosts", cancellationToken);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            resolv = hosts = null;
        }

        if (resolv is null || hosts is null)
        {
            // Not there to read: a container that has stopped or gone meanwhile resolves nothing more; one still running without
            // the files the engine writes for every container is not one of the run's as the run makes them.
            try
            {
                var now = await docker.Containers.InspectContainerAsync(container.ID, cancellationToken);
                if (now.State is not { Running: true })
                {
                    return null;
                }
            }
            catch (DockerContainerNotFoundException)
            {
                return null;
            }

            return $"its {(resolv is null ? "/etc/resolv.conf" : "/etc/hosts")} could not be read through the engine's archive, where the engine "
                + "writes one into every container it starts on the run's network";
        }

        var why = new[] { ResolvConfNotTheRuns(resolv), HostsNotTheRuns(hosts) }.OfType<string>().ToArray();
        return why.Length == 0 ? null : string.Join("; ", why);
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-05) — why <paramref name="text"/>, a resolv.conf, is not one the engine writes for a
    /// container of the run, or null when it is: at least one name server, every one of them the embedded DNS
    /// (<see cref="EmbeddedDns"/>), which answers the run's names and forwards any other to <see cref="NameServer"/>, where nothing
    /// answers; and no search or domain line but ".", which is none.
    /// </summary>
    internal static string? ResolvConfNotTheRuns(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var servers = new List<string>();
        var domains = new List<string>();
        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            switch (words[0])
            {
                case "nameserver":
                    servers.Add(words.Length > 1 ? words[1] : string.Empty);
                    break;
                case "search" or "domain":
                    domains.AddRange(words.Skip(1));
                    break;
            }
        }

        return servers.Count > 0 && servers.All(s => s == EmbeddedDns) && domains.All(d => d == NoSearchDomain)
            ? null
            : $"its /etc/resolv.conf names the name servers [{string.Join(", ", servers)}] and the search domains [{string.Join(", ", domains)}], "
                + $"where the engine writes the embedded DNS, {EmbeddedDns}, alone and no search domain";
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-05) — why <paramref name="text"/>, a hosts file, answers a real name, or null when it
    /// answers none: every name in it is one no real resolver gives out — a single label (localhost, the ip6- names, the
    /// container's own host name, which the engine writes) or a name under a suffix reserved for local or test use (.test,
    /// .localhost, .invalid, .internal, .local, .home.arpa). A real name written in, as a mounted hosts file wrote
    /// opendata-download-metobs.smhi.se, is answered before any name server is asked.
    /// </summary>
    internal static string? HostsNotTheRuns(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] local = [".test", ".localhost", ".invalid", ".internal", ".local", ".home.arpa"];
        var real = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(l => (l.IndexOf('#', StringComparison.Ordinal) is var hash and >= 0 ? l[..hash] : l).Trim())
            .Where(l => l.Length > 0)
            .SelectMany(l => l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Skip(1))
            .Select(n => n.TrimEnd('.'))
            .Where(n => n.Contains('.', StringComparison.Ordinal) && !local.Any(s => n.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return real.Length == 0 ? null : $"its /etc/hosts answers real names [{string.Join(", ", real)}]";
    }

    /// <summary>
    /// contract-005 · UC-6 — the pioneer's decision of 2026-09-29: each container attached to <paramref name="network"/> that was
    /// not started resolving no real name, as the engine records how it started — its name servers, search domains and, since
    /// G-17 round 2 (HAR-R2-08), host names written in — named with what it was given (<see cref="CouldResolveRealNames"/>).
    /// Empty when every one was started with <see cref="NameServer"/> alone, no search domain and no host name written in. A
    /// container removed while it is asked has left the network, and is not one. contract-005 · G-17 round 3 (HAR-R3-05) — and
    /// nothing mounted over its resolver's files, and the resolver it actually has, read from it, the run's
    /// (<see cref="ResolverNotTheRunsAsync"/>): T-6's "every container on the run's network resolves no real name" read the record
    /// of how each started, and a hosts file mounted in left that record as this accepts it.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ResolvingRealNamesAsync(IDockerClient docker, string network, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var attached = await docker.Networks.InspectNetworkAsync(network, cancellationToken);
        var found = new List<string>();
        foreach (var id in attached.Containers?.Keys ?? [])
        {
            ContainerInspectResponse container;
            try
            {
                container = await docker.Containers.InspectContainerAsync(id, cancellationToken);
            }
            catch (DockerContainerNotFoundException)
            {
                continue;
            }

            if ((CouldResolveRealNames(container) ?? await ResolverNotTheRunsAsync(docker, container, cancellationToken)) is { } why)
            {
                found.Add($"{container.Name.TrimStart('/')} ({why})");
            }
        }

        return found;
    }

    /// <summary>
    /// Creates the run's network. A subnet another network already holds is an environment fault,
    /// named with the network that holds it. A killed run's network is removed before this, by the
    /// start's sweep of dead runs, so the holder is a run still alive on this machine or a network that
    /// is not a run's at all. contract-005 · G-17 round 2 (CON-R2-06, FLA-R2-04) — or a run of another owner
    /// (<see cref="OwnerLabel"/>), which the sweep cannot judge and leaves alone: the fault names that owner.
    /// </summary>
    internal static async Task<INetwork> CreateAsync(string runId, IDockerClient docker, CancellationToken cancellationToken)
    {
        var network = new NetworkBuilder()
            .WithName($"mcp-e2e-{runId}")
            .WithLabel(RunLabels(runId))
            .WithCreateParameterModifier(parameters =>
            {
                parameters.EnableIPv6 = false;
                parameters.IPAM = new IPAM
                {
                    Config =
                    [
                        new IPAMConfig
                        {
                            Subnet = Subnet,
                            IPRange = DynamicRange,
                            Gateway = Gateway.ToString(),
                        },
                    ],
                };
            })
            .Build();

        try
        {
            await network.CreateAsync(cancellationToken);
            return network;
        }
        catch (DockerApiException ex)
        {
            var holder = await HolderOfSubnetAsync(docker, cancellationToken);
            throw new EnvironmentFaultException(
                "network",
                holder switch
                {
                    null => $"the run's network on {Subnet} could not be created: {ex.Message}",
                    { Labels: { } labels } when labels.TryGetValue(RunLabel, out var run)
                        && labels.TryGetValue(OwnerLabel, out var owner) && owner == ThisOwner =>
                        $"subnet taken: {Subnet} is held by network '{holder.Name}' of run {run}, whose owner "
                        + $"({E2EEnvironment.RunOwner(run) ?? "unrecorded"}, process id @ start time) is still running on this machine (a dead "
                        + "run's would have been swept). Wait for it to finish; two runs cannot share the subnet.",
                    { Labels: { } labels } when labels.TryGetValue(RunLabel, out var run) =>
                        $"subnet taken: {Subnet} is held by network '{holder.Name}' of run {run}, "
                        + (labels.TryGetValue(OwnerLabel, out var other)
                            ? $"owned by {other} (process {(labels.TryGetValue(ProcessLabel, out var process) ? process : "unrecorded")})"
                            : "which names no owner (a run of an earlier harness, whose run directory is not here)")
                        + $": not this owner's ({ThisOwner}), so this run cannot tell whether it is alive, and leaves it alone rather than "
                        + "pull a live run's environment out from under it. If it is still running, wait for it to finish; if it is not, "
                        + $"remove it by hand: docker rm -f $(docker ps -aq --filter label={RunLabel}={run}), then docker network rm {holder.Name}.",
                    _ => $"subnet taken: {Subnet} overlaps network '{holder.Name}', which is not a run's. Remove it or move it "
                        + "off the subnet (docker network rm " + holder.Name + ").",
                },
                ex);
        }
    }

    private static async Task<NetworkResponse?> HolderOfSubnetAsync(IDockerClient docker, CancellationToken cancellationToken)
    {
        var networks = await docker.Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken);
        return networks.FirstOrDefault(n => n.IPAM?.Config?.Any(c => Overlaps(c.Subnet)) == true);
    }

    private static bool Overlaps(string? cidr)
    {
        if (cidr is null || !cidr.Contains('/', StringComparison.Ordinal) ||
            !IPNetwork.TryParse(cidr, out var other) || other.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var ours = IPNetwork.Parse(Subnet);
        return ours.Contains(other.BaseAddress) || other.Contains(ours.BaseAddress);
    }

    private static IPAddress Offset(int offset)
    {
        var bytes = Base.GetAddressBytes();
        var value = ((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]) + (uint)offset;
        return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }
}
