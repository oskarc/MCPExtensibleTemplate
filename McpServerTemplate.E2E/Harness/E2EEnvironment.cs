using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The environment a run's tests share: the test PKI, the network, the two images, Keycloak, the test
/// issuer, the upstream fake and Redis. Each test class starts its own server into it.
///
/// contract-005 · UC-1 — the environment starts once per run. xUnit 2 has no assembly fixtures, so it
/// is a shared, lazily started instance: the first test class to need it starts it, the rest wait on
/// the same task, and a start that fails fails every class with the same named fault instead of
/// retrying. It is torn down when the assembly finishes (<see cref="E2ETestFramework"/>), which writes
/// the diagnostics bundle, removes the containers and the network, and deletes the run directory with
/// every key in it. A run killed before that teardown is removed by the next run's start
/// (<see cref="SweepDeadRunsAsync"/>).
///
/// contract-005 · G-16 — the four extension points the harness is built in, and later contracts add
/// through: <see cref="StartServerAsync"/> with a <see cref="SettingsDelta"/>, the
/// <see cref="UpstreamRegistry"/>, the <see cref="IssuerRegistry"/>, and <see cref="E2ENetwork.Subnet"/>.
/// </summary>
public sealed class E2EEnvironment : IAsyncDisposable
{
    /// <summary>The Keycloak identity provider's name in the server's configuration.</summary>
    public const string KeycloakIssuer = "keycloak";

    /// <summary>The test issuers' names in the server's configuration.</summary>
    public const string IdpA = "idp-a";

    /// <inheritdoc cref="IdpA"/>
    public const string IdpB = "idp-b";

    private static readonly Lazy<Task<E2EEnvironment>> Shared = new(() => StartAsync(CancellationToken.None));

    private readonly List<IAsyncDisposable> _services;

    private E2EEnvironment(
        string runId,
        string repositoryRoot,
        IDockerClient docker,
        TestPki pki,
        INetwork network,
        string serverImage,
        string issuerImage,
        KeycloakService keycloak,
        TestIssuerService testIssuer,
        IReadOnlyDictionary<UpstreamOwner, IUpstreamService> upstreams,
        RedisService redis)
    {
        RunId = runId;
        RepositoryRoot = repositoryRoot;
        Docker = docker;
        Pki = pki;
        Network = network;
        ServerImage = serverImage;
        IssuerImage = issuerImage;
        Keycloak = keycloak;
        TestIssuer = testIssuer;
        UpstreamServices = upstreams;
        WireMock = (WireMockService)upstreams[WireMockService.Owner];
        Redis = redis;
        Names = upstreams.Values.Aggregate(keycloak.Names.Including(testIssuer.Names), (names, upstream) => names.Including(upstream.Names));
        _services = [keycloak, testIssuer, .. upstreams.Values, redis];
    }

    /// <summary>Every phase of the run, measured: the environment's, each server's and each test's.</summary>
    public static Timings Timings { get; } = new();

    /// <summary>The identity providers every server is configured with.</summary>
    public static IssuerRegistry Issuers { get; } = new IssuerRegistry()
        .Register(new(KeycloakIssuer, new Uri(KeycloakService.Issuer), KeycloakService.Issuer, IssuerRegistry.Owner.Keycloak, "azp", KeycloakService.Scopes))
        .Register(new(IdpA, new Uri("https://idp-a.e2e.test"), "https://idp-a.e2e.test", IssuerRegistry.Owner.TestIssuer, "client_id", KeycloakService.Scopes))
        .Register(new(IdpB, new Uri("https://idp-b.e2e.test"), "https://idp-b.e2e.test", IssuerRegistry.Owner.TestIssuer, "client_id", KeycloakService.Scopes));

    /// <summary>
    /// The upstream host names and the owner that answers each, for the whole run: registration is per
    /// run (see <see cref="UpstreamRegistry"/>). A later contract hands a host to another owner here.
    /// </summary>
    public static UpstreamRegistry Upstreams { get; } = new UpstreamRegistry()
        .Register("opendata-download-metfcst.smhi.se", WireMockService.Owner)
        .Register("opendata-download-metobs.smhi.se", WireMockService.Owner)
        .Register("jsonplaceholder.typicode.com", WireMockService.Owner)
        .Register(WireMockService.Alias, WireMockService.Owner);

    public string RunId { get; }

    public string RepositoryRoot { get; }

    /// <summary>Where this run's diagnostics bundle is written: TestResults/e2e/{run} under the repository.</summary>
    public string ResultsDirectory => Path.Combine(RepositoryRoot, "TestResults", "e2e", RunId);

    public IDockerClient Docker { get; }

    public TestPki Pki { get; }

    public INetwork Network { get; }

    /// <summary>The server image, tagged with the build context's hash.</summary>
    public string ServerImage { get; }

    public string IssuerImage { get; }

    public KeycloakService Keycloak { get; }

    public TestIssuerService TestIssuer { get; }

    /// <summary>The container each upstream owner started.</summary>
    public IReadOnlyDictionary<UpstreamOwner, IUpstreamService> UpstreamServices { get; }

    /// <summary>The WireMock fake (G-7): the owner of every provider host unless the registry says otherwise.</summary>
    public WireMockService WireMock { get; }

    public RedisService Redis { get; }

    /// <summary>The shared services' names. A server adds mcp.e2e.test, pointing at its own front.</summary>
    public NameMap Names { get; }

    /// <summary>The environment, started by whichever test class asks first.</summary>
    public static Task<E2EEnvironment> GetAsync() => Shared.Value;

    /// <summary>The container answering <paramref name="host"/> in this run, as the upstream registry gives it out.</summary>
    public IUpstreamService UpstreamFor(string host) =>
        Upstreams.Owners.TryGetValue(host, out var owner)
            ? UpstreamServices[owner]
            : throw new KeyNotFoundException($"No upstream owner is registered for '{host}'.");

    /// <summary>
    /// contract-005 · G-8 — the one entry point for a server under test: the environment's base
    /// settings with <paramref name="delta"/> on top, and nothing else.
    /// </summary>
    public Task<ServerUnderTest> StartServerAsync(string name, SettingsDelta delta, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(delta);
        return ServerUnderTest.StartAsync(this, name, delta, cancellationToken);
    }

    /// <summary>Called once, when the test assembly finishes.</summary>
    internal static async Task ShutdownAsync()
    {
        if (!Shared.IsValueCreated)
        {
            return;
        }

        // Waits for the start without rethrowing its fault: every test class has reported that
        // already, and a start that failed has removed what it created.
        var start = Shared.Value;
        await Task.WhenAny(start);
        if (start.IsCompletedSuccessfully)
        {
            await start.Result.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await WriteDiagnosticsAsync();

            foreach (var service in _services)
            {
                await service.DisposeAsync();
            }

            await Network.DisposeAsync();
        }
        finally
        {
            // contract-005 · G-3 — the keys go whatever else failed.
            Docker.Dispose();
            Pki.Dispose();
        }
    }

    private static async Task<E2EEnvironment> StartAsync(CancellationToken cancellationToken)
    {
        var runId = $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
        var root = FindRepositoryRoot();
        var created = new List<(string Name, IAsyncDisposable Resource)>();
        IDockerClient? docker = null;
        TestPki? pki = null;

        try
        {
            // contract-005 · UC-1 edge, T-1 — every phase of the start is measured through
            // MeasureEnvironmentAsync, so a failure names its phase and its cause however it surfaced.
            docker = await Timings.MeasureEnvironmentAsync("environment: docker available", () => DockerEngine.ConnectAsync(cancellationToken));

            // contract-005 · G-4, G-6 — Testcontainers' resource reaper is off. It published its own
            // port on every host interface, where anyone on the machine's network could ask it to
            // remove containers; nothing else of a run is published beyond loopback. Set before the
            // first resource is created, which is when Testcontainers would start it. What it did —
            // remove a run its process no longer tends — the sweep below does at the next start.
            TestcontainersSettings.ResourceReaperEnabled = false;

            await Timings.MeasureEnvironmentAsync("environment: sweep dead runs", () => SweepDeadRunsAsync(docker, root, runId, cancellationToken));

            pki = await Timings.MeasureEnvironmentAsync("environment: test pki", () => Task.FromResult(CreatePki(root, runId)));

            var contextHash = await Timings.MeasureEnvironmentAsync("environment: build context hash", () => Task.Run(() => Images.ContextHash(root), cancellationToken));
            var revision = await Timings.MeasureEnvironmentAsync("environment: checkout revision", () => Images.CheckoutRevisionAsync(root, cancellationToken));

            var network = await Timings.MeasureEnvironmentAsync("environment: network", () => E2ENetwork.CreateAsync(runId, docker, cancellationToken));
            created.Add(("network", network));

            var names = new NameMap(pki.Ca);
            var start = new UpstreamStart(docker, network, pki, names, runId);

            // Independent parts start together; the test issuer waits only for its own image. The
            // server image's build is the one phase whose failure can be the product's: a Dockerfile
            // that does not build says so in its own words (Images.BuildAsync), not as the environment.
            var serverImage = Timings.MeasureEnvironmentAsync(
                "image: server (Dockerfile)",
                () => Images.BuildAsync(Images.ServerRepository, "Dockerfile", root, contextHash, revision, cancellationToken),
                isProductFailure: ex => ex is InvalidOperationException { InnerException: ImageBuildFailedException });
            var issuerImage = Timings.MeasureEnvironmentAsync("image: test issuer", () =>
                Images.BuildAsync(Images.IssuerRepository, "McpServerTemplate.TestIssuer/Dockerfile", root, contextHash, revision, cancellationToken));
            var redis = Timings.MeasureEnvironmentAsync("container: redis", () => RedisService.StartAsync(network, runId, cancellationToken));
            var keycloak = Timings.MeasureEnvironmentAsync("container: keycloak", () =>
                KeycloakService.StartAsync(docker, network, pki, names, ServerUnderTest.Resource, ServerFixture.AllClientIds(), runId, cancellationToken));
            var testIssuer = Timings.MeasureEnvironmentAsync("container: test issuer", async () =>
                await TestIssuerService.StartAsync(docker, await issuerImage, network, pki, names, Issuers.HostsServedBy(IssuerRegistry.Owner.TestIssuer), runId, cancellationToken));

            // contract-005 · G-16 — every owner the upstream registry names is started, each with the
            // host names the registry gives it.
            var upstreams = Upstreams.DistinctOwners.ToDictionary(
                owner => owner,
                owner => Timings.MeasureEnvironmentAsync($"container: {owner.Name}", () =>
                    owner.StartAsync(start, Upstreams.HostsOf(owner), cancellationToken)));

            try
            {
                await Task.WhenAll([serverImage, issuerImage, redis, keycloak, testIssuer, .. upstreams.Values]);
            }
            finally
            {
                // Whatever did start is removed if anything else failed.
                if (redis.IsCompletedSuccessfully)
                {
                    created.Add(("redis", redis.Result));
                }

                if (keycloak.IsCompletedSuccessfully)
                {
                    created.Add(("keycloak", keycloak.Result));
                }

                foreach (var (owner, upstream) in upstreams.Where(u => u.Value.IsCompletedSuccessfully))
                {
                    created.Add((owner.Name, upstream.Result));
                }

                if (testIssuer.IsCompletedSuccessfully)
                {
                    created.Add(("test-issuer", testIssuer.Result));
                }
            }

            var started = upstreams.ToDictionary(u => u.Key, u => u.Value.Result);
            await Timings.MeasureEnvironmentAsync("environment: upstreams", () => CheckUpstreamsAsync(docker, network, pki, started, cancellationToken));

            // contract-005 · G-8 — before any test runs: both images carry this checkout's revision
            // and its build context's hash.
            await Timings.MeasureEnvironmentAsync("environment: revision labels", async () =>
            {
                await Images.VerifyRevisionAsync(docker, serverImage.Result, contextHash, revision, cancellationToken);
                await Images.VerifyRevisionAsync(docker, issuerImage.Result, contextHash, revision, cancellationToken);
            });

            var environment = new E2EEnvironment(
                runId, root, docker, pki, network, serverImage.Result, issuerImage.Result,
                keycloak.Result, testIssuer.Result, started, redis.Result);

            await Timings.MeasureEnvironmentAsync("environment: clock self-check", () => environment.CheckClockAsync(cancellationToken));
            return environment;
        }
        catch (Exception ex)
        {
            await WriteStartFailureAsync(root, runId, created, ex);

            created.Reverse();
            foreach (var (_, resource) in created)
            {
                await resource.DisposeAsync();
            }

            docker?.Dispose();
            pki?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A start that fails still leaves a diagnostics bundle: the fault, the timings up to it, and the
    /// log of every container that had started. A service that failed its own start put its log in
    /// the fault's message before removing its container.
    /// </summary>
    private static async Task WriteStartFailureAsync(string root, string runId, IEnumerable<(string Name, IAsyncDisposable Resource)> created, Exception fault)
    {
        var bundle = Path.Combine(root, "TestResults", "e2e", runId);
        try
        {
            Directory.CreateDirectory(bundle);
            await File.WriteAllTextAsync(Path.Combine(bundle, "start-failure.txt"), fault.ToString());
            Timings.WriteTo(Path.Combine(bundle, "timings.json"));

            foreach (var (name, resource) in created)
            {
                if (ContainerOf(resource) is { } container)
                {
                    var (stdout, stderr) = await container.GetLogsAsync();
                    await File.WriteAllTextAsync(Path.Combine(bundle, $"{name}.log"), stdout + stderr);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or DockerApiException or InvalidOperationException)
        {
            // The fault itself is what matters, and it is on its way to the test's output.
        }

        static DotNet.Testcontainers.Containers.IContainer? ContainerOf(IAsyncDisposable resource) => resource switch
        {
            KeycloakService keycloak => keycloak.Container,
            TestIssuerService issuer => issuer.Container,
            IUpstreamService upstream => upstream.Container,
            RedisService redis => redis.Container,
            _ => null,
        };
    }

    /// <summary>
    /// contract-005 · G-3 — the PKI covers every name the environment serves: the front, Keycloak,
    /// each test issuer name, and each upstream name. Written outside the repository, to a directory
    /// of this run's own, deleted when the run ends.
    /// </summary>
    private static TestPki CreatePki(string repositoryRoot, string runId)
    {
        RunDirectories.RemoveStale();

        var directory = RunDirectories.For(runId);
        var full = Path.GetFullPath(directory);
        if (full.StartsWith(Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new EnvironmentFaultException("pki", $"the temporary directory {full} lies inside the repository; key material must not.");
        }

        var leaves = new Dictionary<string, string[]>
        {
            ["front"] = [TlsFront.Host],
            ["keycloak"] = [KeycloakService.Host],
            ["issuer"] = [.. Issuers.HostsServedBy(IssuerRegistry.Owner.TestIssuer)],
        };

        // contract-005 · G-16 — each upstream owner's leaf covers the names the owner says it needs.
        foreach (var owner in Upstreams.DistinctOwners)
        {
            leaves.Add(owner.Name, [.. owner.CertificateNames(Upstreams.HostsOf(owner))]);
        }

        var pki = TestPki.Create(directory, leaves);

        RunDirectories.MarkOwned(directory);
        return pki;
    }

    /// <summary>
    /// contract-005 · G-3, T-13 — nothing a run creates outlives it by more than one run. With the
    /// resource reaper off, a run killed before its teardown leaves its containers and its network
    /// behind, and its network holds the subnet the next run needs. So before it creates anything, a
    /// run removes every container and network labelled with another run whose owning process is gone
    /// (<see cref="RunDirectories.IsLive"/>). A run whose process is alive — another run on this
    /// machine — is left alone: its network then holds the subnet, and this run stops with a fault
    /// naming it rather than pulling a live run's environment out from under it. What was removed is
    /// listed in this run's bundle as swept.txt.
    /// </summary>
    private static async Task<bool> SweepDeadRunsAsync(IDockerClient docker, string repositoryRoot, string runId, CancellationToken cancellationToken)
    {
        var labelled = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [E2ENetwork.RunLabel] = true },
        };

        bool Dead(IDictionary<string, string>? labels, out string run) =>
            (run = labels is not null && labels.TryGetValue(E2ENetwork.RunLabel, out var r) ? r : string.Empty) is { Length: > 0 }
            && run != runId
            && !RunDirectories.IsLive(run);

        var swept = new List<string>();
        foreach (var container in await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = labelled }, cancellationToken))
        {
            if (Dead(container.Labels, out var run))
            {
                await IgnoringGoneAsync(docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, cancellationToken));
                swept.Add($"container {container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.ID} ({container.Image}) of run {run}");
            }
        }

        // Networks after containers: a network is removed only once nothing is attached to it.
        foreach (var network in await docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = labelled }, cancellationToken))
        {
            if (Dead(network.Labels, out var run))
            {
                await IgnoringGoneAsync(docker.Networks.DeleteNetworkAsync(network.ID, cancellationToken));
                swept.Add($"network {network.Name} of run {run}");
            }
        }

        if (swept.Count > 0)
        {
            var bundle = Path.Combine(repositoryRoot, "TestResults", "e2e", runId);
            Directory.CreateDirectory(bundle);
            await File.WriteAllLinesAsync(Path.Combine(bundle, "swept.txt"), swept, cancellationToken);
        }

        return true;

        // Removed meanwhile by its own run's teardown: gone either way.
        static async Task IgnoringGoneAsync(Task removal)
        {
            try
            {
                await removal;
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
        }
    }

    /// <summary>
    /// contract-005 · G-7, G-16 — every host the upstream registry gives out is answered on the run's
    /// network by the container its owner started, and by no other, with a certificate of the run's
    /// PKI that names it. Read from the engine's own view of the network, not from what the owners
    /// were asked to do: a host whose owner then answers nothing under it — an owner nothing started
    /// for that name — would otherwise reach no fake and meet no refusal, and each call to it would
    /// fail as if the product had.
    /// </summary>
    private static async Task CheckUpstreamsAsync(
        IDockerClient docker, INetwork network, TestPki pki, Dictionary<UpstreamOwner, IUpstreamService> started, CancellationToken cancellationToken)
    {
        // Every alias on the run's network, with the containers (by name) that answer under it.
        var holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var attached = await docker.Networks.InspectNetworkAsync(network.Name, cancellationToken);
        foreach (var id in attached.Containers.Keys)
        {
            var container = await docker.Containers.InspectContainerAsync(id, cancellationToken);
            if (container.NetworkSettings?.Networks is not { } networks || !networks.TryGetValue(network.Name, out var endpoint))
            {
                continue;
            }

            foreach (var alias in endpoint.Aliases ?? [])
            {
                if (!holders.TryGetValue(alias, out var names))
                {
                    holders[alias] = names = [];
                }

                names.Add(container.Name.TrimStart('/'));
            }
        }

        var problems = new List<string>();
        if (!started.ContainsKey(WireMockService.Owner))
        {
            problems.Add($"no host is registered to {WireMockService.Owner.Name}, whose journal every test reads (G-7)");
        }

        foreach (var (host, owner) in Upstreams.Owners.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            var expected = started[owner].Container.Name.TrimStart('/');
            var answering = holders.GetValueOrDefault(host) ?? [];
            if (answering.Count == 0)
            {
                problems.Add($"'{host}' is registered to {owner.Name}, but no container on the run's network answers under it: nothing started it there");
            }
            else if (answering.Count > 1 || answering[0] != expected)
            {
                problems.Add($"'{host}' is registered to {owner.Name} ({expected}), but is answered by {string.Join(" and ", answering)}");
            }

            if (!pki.Leaves.TryGetValue(owner.Name, out var certified) || !certified.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"'{host}' is registered to {owner.Name}, but no certificate of the run's PKI for {owner.Name} names it");
            }
        }

        if (problems.Count > 0)
        {
            throw new EnvironmentFaultException(
                "upstreams",
                $"the upstream registry and the running environment disagree: {string.Join("; ", problems)}. "
                + "A call to such a host would reach no fake, and would fail as if the product had.");
        }
    }

    /// <summary>
    /// contract-005 · G-11 — clock skew. Tokens are judged by their times (a write needs one issued
    /// within five minutes, an irreversible call one within sixty seconds), and every container shares
    /// the Docker engine's clock, which on a laptop can fall behind across a sleep. More than a few
    /// seconds apart and a token-age test would fail for the clock's reason, so the run stops here.
    /// </summary>
    private async Task CheckClockAsync(CancellationToken cancellationToken)
    {
        using var http = Names.CreateClient();
        var host = TestIssuer.Hosts[0];
        var before = DateTimeOffset.UtcNow;
        var clock = await http.GetFromJsonAsync<JsonElement>(new Uri($"https://{host}/admin/clock"), cancellationToken);
        var after = DateTimeOffset.UtcNow;

        var engine = clock.GetProperty("utc").GetDateTimeOffset();
        var local = before + ((after - before) / 2);
        var skew = engine - local;
        if (Math.Abs(skew.TotalSeconds) > 5)
        {
            throw new EnvironmentFaultException(
                "clock",
                string.Create(CultureInfo.InvariantCulture, $"clock skew: the Docker engine's clock is {Math.Abs(skew.TotalSeconds):F1} s {(skew > TimeSpan.Zero ? "ahead of" : "behind")} this ")
                + "machine's. Restart Docker (or resync its VM's clock) and run again.");
        }
    }

    /// <summary>The bundle CI uploads when a run fails: every shared container's log, the fake's journal, the issuers' counts and the timings.</summary>
    private async Task WriteDiagnosticsAsync()
    {
        try
        {
            Directory.CreateDirectory(ResultsDirectory);
            foreach (var (name, container) in new[] { ("keycloak", Keycloak.Container), ("test-issuer", TestIssuer.Container), ("redis", Redis.Container) }
                .Concat(UpstreamServices.Select(u => (u.Key.Name, u.Value.Container))))
            {
                var (stdout, stderr) = await container.GetLogsAsync();
                await File.WriteAllTextAsync(Path.Combine(ResultsDirectory, $"{name}.log"), stdout + stderr);
            }

            using var http = Names.CreateClient();
            var journal = await WireMockService.JournalAsync(http);
            await File.WriteAllTextAsync(Path.Combine(ResultsDirectory, "wiremock-journal.json"), journal.ToString());
            var counts = await TestIssuerService.CountsAsync(http, TestIssuer.Hosts[0]);
            await File.WriteAllTextAsync(Path.Combine(ResultsDirectory, "issuer-counts.json"), JsonSerializer.Serialize(counts));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or DockerApiException or InvalidOperationException)
        {
            await File.WriteAllTextAsync(Path.Combine(ResultsDirectory, "diagnostics-error.txt"), ex.ToString());
        }

        Timings.WriteTo(Path.Combine(ResultsDirectory, "timings.json"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "McpServerTemplate.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new EnvironmentFaultException("repository", "the repository root (McpServerTemplate.sln) was not found above the test output directory.");
    }

    /// <summary>
    /// The run directories under the machine's temporary directory, one per run, each marked with the
    /// process that owns it. A run killed before its teardown leaves one behind; the next run removes
    /// every directory whose owner is gone, so key material never outlives a run by more than one.
    /// </summary>
    private static class RunDirectories
    {
        private const string Prefix = "mcp-e2e-";
        private const string OwnerFile = "owner.pid";

        public static string For(string runId) => Path.Combine(Path.GetTempPath(), Prefix + runId);

        /// <summary>
        /// Whether the run <paramref name="runId"/> is still tended: its directory is here and the
        /// process that owns it is alive. A run whose directory is gone has been torn down, or was
        /// removed as stale, and a run whose owner has exited will never tear itself down.
        /// </summary>
        public static bool IsLive(string runId)
        {
            var directory = For(runId);
            return Directory.Exists(directory) && OwnerIsAlive(directory);
        }

        public static void MarkOwned(string directory) =>
            File.WriteAllText(Path.Combine(directory, OwnerFile), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        public static void RemoveStale()
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
            {
                if (!OwnerIsAlive(directory))
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch (IOException)
                    {
                        // Still in use by something; the next run tries again.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Another user's run on a shared machine; not this run's to remove.
                    }
                }
            }
        }

        private static bool OwnerIsAlive(string directory)
        {
            var owner = Path.Combine(directory, OwnerFile);
            if (!File.Exists(owner) || !int.TryParse(File.ReadAllText(owner), CultureInfo.InvariantCulture, out var pid))
            {
                // Unmarked: a run that died between creating the directory and marking it, unless it is new.
                return Directory.GetCreationTimeUtc(directory) > DateTime.UtcNow.AddMinutes(-5);
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
