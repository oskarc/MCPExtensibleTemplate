using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The environment a run's tests share: the test PKI, the network, the three images (the server, the
/// test issuer and the test host), Keycloak, the test issuer, the upstream fake and Redis. Each test
/// class starts its own server into it.
///
/// contract-005 · UC-1 — the environment starts once per run. xUnit 2 has no assembly fixtures, so it
/// is a shared, lazily started instance: the first test class to need it starts it, the rest wait on
/// the same task, and a start that fails fails every class with the same named fault instead of
/// retrying. It is torn down when the assembly finishes (<see cref="E2ETestFramework"/>), which writes
/// the diagnostics bundle, removes the containers and the network, and deletes the run directory with
/// every key in it. contract-005 · G-17 round 2 (CON-R2-05) — a run stopped before that teardown (Ctrl+C,
/// or a process that exits some other way than through the assembly's end) removes itself on its way out,
/// as well as it can in the time it is given (<see cref="OnExit"/>); a run killed outright, which runs
/// nothing on its way out, is removed by the next run's start (<see cref="SweepDeadRunsAsync"/>).
///
/// contract-005 · G-16 — the four extension points the harness is built in, and later contracts add
/// through: <see cref="StartServerAsync"/> with a <see cref="SettingsDelta"/>, the
/// <see cref="UpstreamRegistry"/>, the <see cref="IssuerRegistry"/>, and <see cref="E2ENetwork.Subnet"/>.
///
/// contract-005 · UC-6 — the pioneer's decision of 2026-09-29 ("2. yes, cut it and mock any calls"): the run's network
/// resolves no real name (<see cref="E2ENetwork.NameServer"/>), so every call a server of the run makes is answered by a
/// fake the registries give out or fails at the name lookup. The start checks it of every container it started (phase
/// 'environment: no real names'), and T-6 of every container there when it runs. contract-005 · G-17 round 2 (HAR-R2-08) — and
/// every container the harness makes, the moment the engine starts it (<see cref="E2ENetwork.OnTheRunNetwork"/>).
/// </summary>
public sealed class E2EEnvironment : IAsyncDisposable
{
    /// <summary>The Keycloak identity provider's name in the server's configuration.</summary>
    public const string KeycloakIssuer = "keycloak";

    /// <summary>The test issuers' names in the server's configuration.</summary>
    public const string IdpA = "idp-a";

    /// <inheritdoc cref="IdpA"/>
    public const string IdpB = "idp-b";

    /// <summary>contract-005 · T-3 — the issuer no server is configured with (<see cref="IssuerRegistry.Stranger"/>).</summary>
    public const string Stranger = "stranger";

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
        string testHostImage,
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
        TestHostImage = testHostImage;
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

    /// <summary>
    /// The identity providers every server is configured with.
    ///
    /// contract-005 · G-12 (4) — idp-b's issuer is not its authority: it carries a trailing slash, as
    /// Auth0's and Entra v1's issuers do, while discovery and keys are fetched from the authority
    /// without one. A client is sent to the issuer (RFC 8414 §3.3 holds it to that exact string), so
    /// this is the provider on which the protected-resource metadata shows which of the two it lists.
    /// </summary>
    public static IssuerRegistry Issuers { get; } = new IssuerRegistry()
        .Register(new(KeycloakIssuer, new Uri(KeycloakService.Issuer), KeycloakService.Issuer, IssuerRegistry.Owner.Keycloak, "azp", KeycloakService.Scopes))
        .Register(new(IdpA, new Uri("https://idp-a.e2e.test"), "https://idp-a.e2e.test", IssuerRegistry.Owner.TestIssuer, "client_id", KeycloakService.Scopes))
        .Register(new(IdpB, new Uri("https://idp-b.e2e.test"), "https://idp-b.e2e.test/", IssuerRegistry.Owner.TestIssuer, "client_id", KeycloakService.Scopes))
        .WithStranger(new(Stranger, new Uri("https://stranger.e2e.test"), IssuerRegistry.Owner.TestIssuer));

    /// <summary>Every upstream host, WireMock's: what <see cref="Upstreams"/> hands on from; declared first, since it is read as Upstreams is made.</summary>
    private static readonly UpstreamRegistry OwnedByTheFake = new UpstreamRegistry()
        .Register("opendata-download-metfcst.smhi.se", WireMockService.Owner)
        .Register("opendata-download-metobs.smhi.se", WireMockService.Owner)
        .Register("jsonplaceholder.typicode.com", WireMockService.Owner)
        .Register(WireMockService.Alias, WireMockService.Owner);

    /// <summary>
    /// The upstream host names and the owner that answers each, for the whole run: registration is per
    /// run (see <see cref="UpstreamRegistry"/>). A later contract hands a host to another owner here.
    ///
    /// contract-005 · T-15 — and so does this one: SMHI's observations host is handed to a stand-in fake
    /// (<see cref="StandInUpstream"/>), which is not WireMock, in place of the fake — and in a run of T-15's sabotage
    /// (<see cref="StandInLeftOut"/>), the host is left to the fake. contract-005 · T-6 — T-15 is no longer the only test
    /// that reaches that host: T-6's (UpstreamTests, UpstreamNamesTests) call every observations tool, and each test reads the
    /// stand-in's record for its own server alone: contract-005 · G-17 round 2 (HAR-R2-02) — from its address since it started
    /// (<see cref="RequestSource"/>).
    /// </summary>
    public static UpstreamRegistry Upstreams { get; } = Sabotage.Choose(
        StandInLeftOut,
        OwnedByTheFake.Replace("opendata-download-metobs.smhi.se", StandInUpstream.Owner),
        OwnedByTheFake);

    /// <summary>contract-005 · T-12 (G-11) — T-15's sabotage, which acts here: the observations host left to the fake.</summary>
    internal const string StandInLeftOut = "t15-observations-host-left-to-the-fake";

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

    /// <summary>contract-005 · G-10 — the test host image, tagged with the build context's hash like the others.</summary>
    public string TestHostImage { get; }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-01) — the checkout's revision, as every image of the run was checked to carry before any
    /// test ran (<see cref="Images.CheckoutRevisionAsync"/>): what a test holds the image under test to.
    /// </summary>
    public string Revision { get; private init; } = string.Empty;

    /// <summary>contract-005 · G-17 round 3 (CON-R3-01) — the build context's hash, as every image of the run was checked to carry.</summary>
    public string ContextHash { get; private init; } = string.Empty;

    /// <summary>contract-005 · UC-6 — the run's one way in from the test process (<see cref="Harness.Door"/>).</summary>
    internal Door Door { get; private init; } = null!;

    /// <summary>
    /// contract-005 · UC-6 — the live witness taken at the environment's start: a TCP connect to a public address from a container
    /// of the run's network, and from the door, on an ordinary network (<see cref="Door.WitnessAsync"/>).
    /// </summary>
    public InternetWitness Internet { get; private init; } = null!;

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
    /// settings with <paramref name="delta"/> on top, and nothing else. contract-005 · G-10 — the same
    /// for the test host: <paramref name="build"/> chooses the image, never the configuration.
    /// </summary>
    public Task<ServerUnderTest> StartServerAsync(
        string name, SettingsDelta delta, ServerBuild build = ServerBuild.Shipped, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(delta);
        return ServerUnderTest.StartAsync(this, name, delta, build, cancellationToken);
    }

    /// <summary>
    /// contract-005 · G-8, T-11 — the same entry point for a server a test expects to refuse: the base
    /// settings with <paramref name="delta"/> on top, started only to see whether it exits (and how) or
    /// comes up. <paramref name="name"/> must be unique in the run; it names the server's alias and
    /// its diagnostics.
    /// </summary>
    public Task<StartupOutcome> StartupAsync(
        string name, SettingsDelta delta, ServerBuild build = ServerBuild.Shipped, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(delta);
        return ServerUnderTest.StartupAsync(this, name, delta, build, cancellationToken);
    }

    /// <summary>
    /// contract-005 · T-7 — a Redis of a test's own on the run's network, answering under <paramref name="host"/>, which
    /// a test points its servers at through their settings delta (Limits:Redis=<see cref="RedisService.Endpoint"/>). A
    /// test that stops its limit store stops this one; the environment's own is every other class's.
    /// </summary>
    public async Task<RedisService> StartRedisAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host.Equals(RedisService.Alias, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{host}' is the environment's own Redis; a test's own answers under another name.", nameof(host));
        }

        return await Timings.MeasureEnvironmentAsync($"container: redis {host}", () => RedisService.StartAsync(Docker, Network, RunId, host, cancellationToken));
    }

    /// <summary>
    /// contract-005 · G-13 — the documented deployment: the compose file of the docs/04 at
    /// <paramref name="documentPath"/>, run through the docs profile's declared table (<see cref="DocsProfile"/>) and
    /// nothing else. Not a server of the base settings: the document is its configuration.
    /// </summary>
    public Task<DocsProfile> StartDocsProfileAsync(string documentPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        return DocsProfile.StartAsync(this, documentPath, cancellationToken);
    }

    /// <summary>The image a server of <paramref name="build"/> runs.</summary>
    public string ImageOf(ServerBuild build) => build switch
    {
        ServerBuild.Shipped => ServerImage,
        ServerBuild.TestHost => TestHostImage,
        _ => throw new ArgumentOutOfRangeException(nameof(build), build, "No image is built for this."),
    };

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

            await Door.DisposeAsync();
            await Network.DisposeAsync();
        }
        finally
        {
            // contract-005 · G-3 — the keys go whatever else failed.
            Docker.Dispose();
            Pki.Dispose();

            // contract-005 · G-17 round 2 (CON-R2-05) — torn down: nothing is left for the way out.
            OnExit.Done();
        }
    }

    private static async Task<E2EEnvironment> StartAsync(CancellationToken cancellationToken)
    {
        // contract-005 · G-11 — a run the sabotage variable refuses (set in CI, or naming no sabotage) starts nothing.
        if (Sabotage.Refusal is { } refusal)
        {
            throw new EnvironmentFaultException("sabotage", refusal);
        }

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

            // contract-005 · G-17 round 2 (CON-R2-05) — from here the run creates things; stopped before its teardown, it removes
            // them on its way out, as well as it can.
            OnExit.Register(runId, root);

            pki = await Timings.MeasureEnvironmentAsync("environment: test pki", () => Task.FromResult(CreatePki(root, runId)));

            var contextHash = await Timings.MeasureEnvironmentAsync("environment: build context hash", () => Task.Run(() => Images.ContextHash(root), cancellationToken));
            var revision = await Timings.MeasureEnvironmentAsync("environment: checkout revision", () => Images.CheckoutRevisionAsync(root, cancellationToken));

            var network = await Timings.MeasureEnvironmentAsync("environment: network", () => E2ENetwork.CreateAsync(runId, docker, cancellationToken));
            created.Add(("network", network));

            // contract-005 · UC-6 — the network is internal and publishes nothing: the test process reaches it through the door.
            var door = await Timings.MeasureEnvironmentAsync("environment: door", () => E2ENetwork.OpenDoorAsync(runId, docker, network, pki, cancellationToken));
            created.Add(("door", door));

            var names = new NameMap(pki.Ca);
            var start = new UpstreamStart(docker, network, pki, names, runId);

            // Independent parts start together; the test issuer waits only for its own image. The
            // server image's build is the one phase whose failure can be the product's: a Dockerfile
            // that does not build says so in its own words (Images.BuildAsync), not as the environment.
            // contract-005 · G-17 round 3 (CON-R3-01) — each built by BuildKit, as operators build the server's.
            var serverImage = Timings.MeasureEnvironmentAsync(
                "image: server (Dockerfile)",
                () => Images.BuildAsync(docker, Images.ServerRepository, "Dockerfile", root, contextHash, revision, cancellationToken),
                isProductFailure: ex => ex is DockerfileBuildException);
            var issuerImage = Timings.MeasureEnvironmentAsync("image: test issuer", () =>
                Images.BuildAsync(docker, Images.IssuerRepository, "McpServerTemplate.TestIssuer/Dockerfile", root, contextHash, revision, cancellationToken));

            // contract-005 · G-10 — the test host, built like the others from the same context and tagged by its hash.
            var testHostImage = Timings.MeasureEnvironmentAsync("image: test host", () =>
                Images.BuildAsync(docker, Images.TestHostRepository, "McpServerTemplate.E2EHost/Dockerfile", root, contextHash, revision, cancellationToken));
            var redis = Timings.MeasureEnvironmentAsync("container: redis", () => RedisService.StartAsync(docker, network, runId, cancellationToken));
            var keycloak = Timings.MeasureEnvironmentAsync("container: keycloak", () =>
                KeycloakService.StartAsync(docker, network, pki, names, ServerUnderTest.Resource, ServerFixture.AllClientIds(), runId, cancellationToken));
            var testIssuer = Timings.MeasureEnvironmentAsync("container: test issuer", async () =>
                await TestIssuerService.StartAsync(docker, await issuerImage, network, pki, names, Issuers.IssuersServedBy(IssuerRegistry.Owner.TestIssuer), runId, cancellationToken));

            // contract-005 · G-16 — every owner the upstream registry names is started, each with the
            // host names the registry gives it.
            var upstreams = Upstreams.DistinctOwners.ToDictionary(
                owner => owner,
                owner => Timings.MeasureEnvironmentAsync($"container: {owner.Name}", () =>
                    owner.StartAsync(start, Upstreams.HostsOf(owner), cancellationToken)));

            try
            {
                await Task.WhenAll([serverImage, issuerImage, testHostImage, redis, keycloak, testIssuer, .. upstreams.Values]);
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

            // contract-005 · the pioneer's decision of 2026-09-29 ("2. yes, cut it and mock any calls") — before any test runs,
            // every container the environment started, as the engine records it, resolves no real name (E2ENetwork.NameServer).
            await Timings.MeasureEnvironmentAsync("environment: no real names", () => CheckNoRealNamesAsync(docker, network, cancellationToken));

            // contract-005 · UC-6 — "no internet access mock everything": the live witness, read by NetworkCutTests.
            var internet = await Timings.MeasureEnvironmentAsync("environment: no internet", () => door.WitnessAsync(redis.Result.Container, cancellationToken));

            // contract-005 · G-8 — before any test runs: every image carries this checkout's revision
            // and its build context's hash.
            await Timings.MeasureEnvironmentAsync("environment: revision labels", async () =>
            {
                await Images.VerifyRevisionAsync(docker, serverImage.Result, contextHash, revision, cancellationToken);
                await Images.VerifyRevisionAsync(docker, issuerImage.Result, contextHash, revision, cancellationToken);
                await Images.VerifyRevisionAsync(docker, testHostImage.Result, contextHash, revision, cancellationToken);
            });

            // contract-005 · G-17 round 1 (CON-R1-03) — the environment's client writes down every image name the run gives, in
            // the run's directory, so a run killed before its teardown has each put back by the next run's sweep.
            var environment = new E2EEnvironment(
                runId, root, new RecordingDockerClient(docker, pki.Directory), pki, network, serverImage.Result, issuerImage.Result,
                testHostImage.Result, keycloak.Result, testIssuer.Result, started, redis.Result)
            {
                Revision = revision,
                ContextHash = contextHash,
                Door = door,
                Internet = internet,
            };

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
            OnExit.Done();
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
    /// contract-005 · G-3, T-13 — nothing a run creates outlives it by more than one run. contract-005 · G-17 round 2 (CON-R2-05) —
    /// a run stopped before its teardown removes itself on its way out, best effort (<see cref="OnExit"/>); this sweep is the
    /// backstop, for one killed outright. With the
    /// resource reaper off, a run killed before its teardown leaves its containers and its network
    /// behind, and its network holds the subnet the next run needs. So before it creates anything, a
    /// run removes every container and network labelled with another run whose owning process is gone
    /// (<see cref="RunDirectories.IsLive"/>). A run whose process is alive — another run on this
    /// machine — is left alone: its network then holds the subnet, and this run stops with a fault
    /// naming it rather than pulling a live run's environment out from under it. What was removed is
    /// listed in this run's bundle as swept.txt.
    ///
    /// contract-005 · G-17 round 1 (CON-R1-03) — and it puts back every image name such a run gave an image, as the name stood
    /// before that run (<see cref="RecordingDockerClient.TakeBackAsync"/>): a run killed during the docs profile left
    /// mcp-server:latest, the name docs/04 deploys, on its own build. The run directory, which holds the record, is removed
    /// after this, with the rest of the dead run's key material (<see cref="RunDirectories.RemoveStale"/>).
    ///
    /// contract-005 · G-17 round 2 (CON-R2-06, FLA-R2-04) — only a run of this owner is judged: one whose owner label
    /// (<see cref="E2ENetwork.OwnerLabel"/>) names this machine, operating system, account and temporary directory, for only
    /// such a run's directory lies where this process looks for it. A run was taken for dead whenever its directory was not
    /// under this process's temporary directory, so a live run of a WSL distro on the same Docker Desktop, of another account
    /// on a shared engine, or of an IDE that sets its own TMP, had its containers and network removed mid-run. Every other run
    /// is left alone; its network then holds the subnet, and this run stops with a fault naming its owner. A run of an earlier
    /// harness, which carries no owner label, is removed only when its directory is here, marked, and its owner gone.
    /// contract-005 · G-17 round 3 (DOC-R3-01) — and every volume such a run labelled, after its containers.
    /// </summary>
    private static async Task<bool> SweepDeadRunsAsync(IDockerClient docker, string repositoryRoot, string runId, CancellationToken cancellationToken)
    {
        var labelled = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [E2ENetwork.RunLabel] = true },
        };

        bool Dead(IDictionary<string, string>? labels, out string run) => IsDeadRun(labels, runId, out run);

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

        // contract-005 · G-17 round 3 (DOC-R3-01) — and volumes, after the containers that held them: the docs profile runs docs/04's
        // named volume as the run's own, carrying the run's labels.
        foreach (var volume in (await docker.Volumes.ListAsync(new VolumesListParameters { Filters = labelled }, cancellationToken)).Volumes ?? [])
        {
            if (Dead(volume.Labels, out var run))
            {
                await IgnoringGoneAsync(docker.Volumes.RemoveAsync(volume.Name, force: true, cancellationToken));
                swept.Add($"volume {volume.Name} of run {run}");
            }
        }

        // The image names a dead run gave, as its directory records them.
        foreach (var directory in RunDirectories.Dead())
        {
            swept.AddRange(await RecordingDockerClient.TakeBackAsync(docker, directory, cancellationToken));
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
    /// contract-005 · G-17 round 2 (CON-R2-06, FLA-R2-04) — whether what carries <paramref name="labels"/> is a dead run's, which
    /// the run <paramref name="thisRun"/> removes at its start (<see cref="SweepDeadRunsAsync"/>): a run's, not this one's, and
    /// either this owner's (<see cref="E2ENetwork.OwnerLabel"/>) and no longer tended (<see cref="RunDirectories.IsLive"/>), or of
    /// no owner at all — an earlier harness's — and dead beyond doubt by its own directory here (<see cref="RunDirectories.ProvablyDead"/>).
    /// A run of any other owner is never dead to this one: its directory is not where this process looks.
    /// </summary>
    internal static bool IsDeadRun(IDictionary<string, string>? labels, string thisRun, out string run)
    {
        run = labels is not null && labels.TryGetValue(E2ENetwork.RunLabel, out var named) ? named : string.Empty;
        if (run.Length == 0 || run == thisRun)
        {
            return false;
        }

        return labels!.TryGetValue(E2ENetwork.OwnerLabel, out var owner)
            ? owner == E2ENetwork.ThisOwner && !RunDirectories.IsLive(run)
            : RunDirectories.ProvablyDead(run);
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
    /// contract-005 · UC-6 — the pioneer's decision of 2026-09-29 ("2. yes, cut it and mock any calls"): every container on the
    /// run's network was started resolving no real name — its own loopback as its only name server, and no search domain
    /// (<see cref="E2ENetwork.OnTheRunNetwork"/>) — read from the engine's own record of how each started, not from what the
    /// harness asked for. A container that could resolve a real name could reach the internet by it, and a call to a host with
    /// no fake would read, in every test after, as the product's own failure or success.
    /// </summary>
    private static async Task CheckNoRealNamesAsync(IDockerClient docker, INetwork network, CancellationToken cancellationToken)
    {
        var resolving = await E2ENetwork.ResolvingRealNamesAsync(docker, network.Name, cancellationToken);
        if (resolving.Count > 0)
        {
            throw new EnvironmentFaultException(
                "names",
                $"these containers on the run's network were started able to resolve real names: {string.Join("; ", resolving)}. Every "
                + $"container joins it through {nameof(E2ENetwork)}.{nameof(E2ENetwork.OnTheRunNetwork)}, with {E2ENetwork.NameServer} as its "
                + "only name server, no search domain, no host names written in and nothing mounted over its resolver's files, so that no "
                + "call of the run reaches the internet by name.");
        }
    }

    /// <summary>
    /// contract-005 · T-6 (G-7, G-16) — the registered upstream host names whose owner the test process does not reach (UC-6:
    /// through the run's door, <see cref="Harness.Door.Reaches"/>), in a stable order: the names <see cref="WithoutNameAsync"/> can
    /// take off the run's network and give back.
    /// </summary>
    internal Task<IReadOnlyList<string>> RemovableNamesAsync() => Task.FromResult<IReadOnlyList<string>>(
        [.. Upstreams.Owners
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .Where(o => !Door.Reaches(UpstreamServices[o.Value].Container.Id))
            .Select(o => o.Key)]);

    /// <summary>
    /// contract-005 · T-6 (G-7, G-16) — the run's network without the name <paramref name="host"/>: "with one fake's alias
    /// removed". The owner the upstream registry gives <paramref name="host"/> is taken off the network and put back on it under
    /// every other name the registry gives it, so no container of the run answers <paramref name="host"/> and a lookup of it
    /// fails (<see cref="E2ENetwork.NameServer"/>); disposing what is returned puts the owner back under every one of its
    /// names. Both are checked from the engine's own view of the network. Only an owner the test process does not reach can be
    /// (UC-6: through the run's door): it reaches WireMock's journal, which must not move under it. Every server of the run
    /// resolves the name through the one network, so a test that takes one away runs alone, after every other
    /// (UpstreamNamesTests' collection).
    /// </summary>
    internal async Task<IAsyncDisposable> WithoutNameAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (!Upstreams.Owners.TryGetValue(host, out var owner))
        {
            throw new KeyNotFoundException($"No upstream owner is registered for '{host}'.");
        }

        var id = UpstreamServices[owner].Container.Id;
        if (Door.Reaches(id))
        {
            throw new EnvironmentFaultException(
                "upstream names",
                $"'{host}' is answered by {owner.Name}, which the test process reaches through the run's door: its name is not taken off "
                + "the network and given back under it.");
        }

        var names = Upstreams.HostsOf(owner);
        await RejoinAsync(id, owner, [.. names.Where(n => !n.Equals(host, StringComparison.OrdinalIgnoreCase))], host, cancellationToken);
        return new NameGivenBack(() => RejoinAsync(id, owner, names, null, CancellationToken.None));
    }

    /// <summary>
    /// Takes the container <paramref name="id"/> off the run's network and puts it back answering <paramref name="names"/>, and
    /// not <paramref name="removed"/>, as the engine then records it; a fault of phase 'upstream names' otherwise.
    /// </summary>
    private async Task RejoinAsync(string id, UpstreamOwner owner, IReadOnlyList<string> names, string? removed, CancellationToken cancellationToken)
    {
        await Docker.Networks.DisconnectNetworkAsync(Network.Name, new NetworkDisconnectParameters { Container = id, Force = true }, cancellationToken);
        await Docker.Networks.ConnectNetworkAsync(
            Network.Name,
            new NetworkConnectParameters { Container = id, EndpointConfig = new EndpointSettings { Aliases = [.. names] } },
            cancellationToken);

        var inspected = await Docker.Containers.InspectContainerAsync(id, cancellationToken);
        IList<string> aliases = inspected.NetworkSettings?.Networks is { } networks && networks.TryGetValue(Network.Name, out var endpoint)
            ? endpoint.Aliases ?? []
            : [];
        var missing = names.Where(n => !aliases.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0 || (removed is not null && aliases.Contains(removed, StringComparer.OrdinalIgnoreCase)))
        {
            throw new EnvironmentFaultException(
                "upstream names",
                $"{owner.Name} rejoined the run's network {Network.Name} answering [{string.Join(", ", aliases)}], where it was to answer "
                + $"[{string.Join(", ", names)}]{(removed is null ? string.Empty : $" and not '{removed}'")}.");
        }
    }

    /// <summary>What <see cref="WithoutNameAsync"/> returns: disposing it gives the name back, once.</summary>
    private sealed class NameGivenBack(Func<Task> giveBack) : IAsyncDisposable
    {
        private int _given;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _given, 1) == 0)
            {
                await giveBack();
            }
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

            // contract-005 · G-17 round 3 (HAR-R3-04) — every server's record, written: the readers that return a record give one
            // server's alone.
            using var http = Names.CreateClient();
            await WireMockService.WriteJournalAsync(http, Path.Combine(ResultsDirectory, "wiremock-journal.json"));
            await TestIssuerService.WriteEveryServersCountsAsync(http, TestIssuer.Hosts[0], Path.Combine(ResultsDirectory, "issuer-counts.json"));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or DockerApiException or InvalidOperationException)
        {
            await File.WriteAllTextAsync(Path.Combine(ResultsDirectory, "diagnostics-error.txt"), ex.ToString());
        }

        Timings.WriteTo(Path.Combine(ResultsDirectory, "timings.json"));
    }

    /// <summary>The repository the suite runs from: the directory above the test output that holds McpServerTemplate.sln.</summary>
    internal static string FindRepositoryRoot()
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
    /// contract-005 · G-17 round 1 (FLA-R1-04) — who owns the run <paramref name="runId"/>, as its directory records it
    /// ("pid@start"), or null when its directory or its mark is gone: what a fault naming a live run says.
    /// </summary>
    internal static string? RunOwner(string runId)
    {
        try
        {
            return RunDirectories.OwnerOf(RunDirectories.For(runId));
        }
        catch (EnvironmentFaultException ex)
        {
            return $"unreadable: {ex.Cause}";
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-06) — this process, "pid@start", as its run directory's mark and the process label on
    /// everything its run creates name it (<see cref="E2ENetwork.ProcessLabel"/>).
    /// </summary>
    internal static string ThisProcess { get; } = RunDirectories.Owner(Environment.ProcessId);

    /// <summary>
    /// The run directories under the machine's temporary directory, one per run, each marked with the
    /// process that owns it. A run killed before its teardown leaves one behind; the next run removes
    /// every directory whose owner is gone, so key material never outlives a run by more than one.
    /// contract-005 · G-17 round 2 (CON-R2-05) — and a run stopped before its teardown, not killed outright, deletes its own on its
    /// way out (<see cref="OnExit"/>).
    ///
    /// contract-005 · G-17 round 1 (CON-R1-04, FLA-R1-04) — an owner is its process id and that process's start time, as
    /// the fast suite's TestRedis names a run ("pid@start"), so a dead run's id handed to another process is not taken for
    /// the run: judged by the id alone, such a run was live for as long as the other process lived — its key material kept,
    /// its network holding the subnet, and every later run told to wait for it.
    ///
    /// contract-005 · G-17 round 2 (FLA-R2-04) — the temporary directory is not always this account's alone: on Linux it is
    /// /tmp, which every account shares, and a run directory is its owner's only (mode 0700, TestPki). Another account's
    /// directory cannot be entered, so its mark read as missing, and an unmarked directory more than five minutes old was
    /// judged dead: its run's containers and network were removed mid-run. A directory this account cannot enter is now
    /// judged live (<see cref="CanEnter"/>), since what cannot be proven dead is never removed; and the sweep judges only runs
    /// whose owner label is this owner's (<see cref="E2ENetwork.OwnerLabel"/>).
    /// </summary>
    internal static class RunDirectories
    {
        private const string Prefix = "mcp-e2e-";
        private const string OwnerFile = "owner.pid";

        public static string For(string runId) => Path.Combine(Path.GetTempPath(), Prefix + runId);

        /// <summary>
        /// Whether the run <paramref name="runId"/> is still tended: its directory is here and the
        /// process that owns it is alive. A run whose directory is gone has been torn down, or was
        /// removed as stale, and a run whose owner has exited will never tear itself down. Asked only of
        /// a run of this owner (<see cref="E2ENetwork.ThisOwner"/>), whose directory, if it has one, is here.
        /// </summary>
        public static bool IsLive(string runId)
        {
            var directory = For(runId);
            return Directory.Exists(directory) && OwnerIsAlive(directory);
        }

        /// <summary>
        /// contract-005 · G-17 round 2 (CON-R2-06) — whether the run <paramref name="runId"/>, which names no owner (a run of an
        /// earlier harness), is dead beyond doubt: its directory is here, marked, and the process it names is gone. A directory
        /// that is not here may be the run's own in another temporary directory, and that run alive, so it is not.
        /// </summary>
        public static bool ProvablyDead(string runId)
        {
            var directory = For(runId);
            return Directory.Exists(directory) && CanEnter(directory) && OwnerOf(directory) is not null && !OwnerIsAlive(directory);
        }

        public static void MarkOwned(string directory) =>
            File.WriteAllText(Path.Combine(directory, OwnerFile), Owner(Environment.ProcessId));

        /// <summary>Every run directory whose owner is gone: the runs the next run's sweep takes back.</summary>
        public static IReadOnlyList<string> Dead() =>
            [.. Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*").Where(d => !OwnerIsAlive(d))];

        public static void RemoveStale()
        {
            foreach (var directory in Dead())
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

        /// <summary>The owner <paramref name="directory"/> is marked with, as written, or null when it has no mark.</summary>
        public static string? OwnerOf(string directory)
        {
            var owner = Path.Combine(directory, OwnerFile);
            try
            {
                return File.Exists(owner) ? File.ReadAllText(owner).Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new EnvironmentFaultException(
                    "environment: run directories", $"the owner of the run directory {directory} could not be read ({ex.GetType().Name}: {ex.Message}).", ex);
            }
        }

        /// <summary>"pid@start": a process by its id and its start, in whole seconds since the Unix epoch.</summary>
        public static string Owner(int processId)
        {
            using var process = Process.GetProcessById(processId);
            return string.Create(CultureInfo.InvariantCulture, $"{processId}@{new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds()}");
        }

        /// <summary>
        /// contract-005 · G-17 round 2 (FLA-R2-04) — whether this account can enter <paramref name="directory"/>: list what is in
        /// it. One it cannot is another account's (a run directory is its owner's alone, mode 0700 on Linux), and its mark
        /// cannot be read: File.Exists answers false for a file it may not see, which read as a directory with no mark. One that
        /// has gone meanwhile is entered as well as any, and judged as gone.
        /// </summary>
        public static bool CanEnter(string directory)
        {
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                entries.MoveNext();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
        }

        /// <summary>
        /// Whether the process that owns <paramref name="directory"/> is still running: its id names a process, and that
        /// process's start time matches the mark within two seconds, since a start time read from another process can differ
        /// by rounding. As TestRedis does, a process that exited while being asked is gone.
        ///
        /// Where TestRedis takes a process whose start time cannot be read to be alive — its runs are containers, which another
        /// account's run on the same engine may own — a run directory is always this account's own: it is made in this
        /// account's temporary directory, by a test process of this account. A process this account may not even query
        /// (access denied: the system's, a service's, another account's) is not that test process, so the id has been handed
        /// on and the run is dead. Taken for alive, as before, every later run failed at its sweep with an access-denied
        /// error that named no directory, for as long as that process lived.
        ///
        /// contract-005 · G-17 round 2 (FLA-R2-04) — "always this account's own" was not so where accounts share the temporary
        /// directory (/tmp on Linux): a directory this account cannot enter is another account's, and is judged live, whatever
        /// its age (<see cref="CanEnter"/>). <paramref name="now"/> is the moment an unmarked directory's age is judged at: now,
        /// except in the harness's self-test of this, which judges a directory it has just made as it would ten minutes on.
        /// </summary>
        public static bool OwnerIsAlive(string directory, DateTime? now = null)
        {
            if (!CanEnter(directory))
            {
                return true;
            }

            var owner = OwnerOf(directory);
            var parts = owner?.Split('@');
            if (parts is not [var id, var start]
                || !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                || !long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var started))
            {
                // Unmarked, or marked in a form no run of this harness writes (a process id alone, as before contract-005 ·
                // G-17 round 1): a run that died between creating the directory and marking it, unless it is new.
                return Directory.GetCreationTimeUtc(directory) > (now ?? DateTime.UtcNow).AddMinutes(-5);
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited
                    && Math.Abs(new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds() - started) <= 2;
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
            catch (InvalidOperationException)
            {
                return false; // exited while being asked
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // another account's process: not this account's test run
            }
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 4 (FLA-R4-03) — refuses, as an environment fault, to <paramref name="what"/> once the run's teardown
    /// on its way out has begun, so that nothing of the run is made while it is removed: checked as the harness creates a container
    /// of the run (<see cref="E2ENetwork.OnTheRunNetwork"/>) and gives an image a name (<see cref="RecordingDockerClient"/>).
    /// </summary>
    internal static void RefuseWhileStopping(string what)
    {
        if (OnExit.Stopping)
        {
            throw new EnvironmentFaultException("environment: stopping", $"the run is being taken down on its way out, so it does not {what}.");
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-05) — a run stopped before its teardown takes itself down on its way out, best effort.
    ///
    /// The resource reaper is off (it published its port on every interface), and the teardown runs only when the assembly
    /// finishes, so a run stopped by Ctrl+C, an IDE's cancel or a crash left Keycloak, the test issuer with its unauthenticated
    /// /admin/mint, WireMock's writable /__admin and every server that trusts that issuer running on 127.0.0.1, and its keys in
    /// its run directory, until another run started on the same machine and account — which might never happen. Now, on Ctrl+C,
    /// on the process's exit and on an unhandled exception that ends it, a run whose own teardown has not finished removes what it
    /// made, from the engine's own record
    /// of its label, within thirty seconds: every image name it gave put back as it was (its run directory records them), every
    /// container and network it labelled removed, then its run directory, with every key, deleted. What it did is written to its
    /// bundle as interrupted.txt. A run killed outright runs no handler at all; the next run's sweep stays the backstop for it.
    ///
    /// contract-005 · G-17 round 3 (FLA-R3-01) — it did not, in the case it was written for. It removed every container in one step,
    /// and that step stopped at the first container already gone — as they are, since the tests' own teardowns remove theirs while
    /// the process exits — after two or three; the network step then met a network still holding endpoints, and Keycloak, the test
    /// issuer with its /admin/mint and WireMock's /__admin were left running on loopback until the next run. Now the run directory,
    /// with every key, goes first, after the image names its record holds; then each container is removed on its own, one already
    /// gone or being removed counted gone, and the run's label is listed again after every pass until nothing carries it, since
    /// tests start and remove containers while the process exits; then its networks, tried again while they still hold an
    /// endpoint; then its volumes (<see cref="RunRemoval"/>, which HarnessSelfTests holds to this with an engine of its own); and
    /// whatever still carries the label when the time runs out is written to interrupted.txt as left.
    ///
    /// contract-005 · G-17 round 4 (FLA-R4-03) — tests kept creating containers while it ran, after its container step had found
    /// none. Now it first says it is stopping, and the harness refuses to create a container of the run or name an image after
    /// that (<see cref="RefuseWhileStopping"/>); and the container, network and volume steps are repeated as one pass until a
    /// pass lists nothing, so "left" is read after the last.
    /// </summary>
    private static class OnExit
    {
        private static readonly TimeSpan Within = TimeSpan.FromSeconds(30);

        private static string? _runId;
        private static string? _repositoryRoot;
        private static int _done;
        private static int _registered;
        private static int _stopping;

        /// <summary>contract-005 · G-17 round 4 (FLA-R4-03) — the teardown on the way out has begun: nothing of the run may be made after.</summary>
        public static bool Stopping => Volatile.Read(ref _stopping) == 1;

        /// <summary>From the moment the run <paramref name="runId"/> may create anything, until its own teardown is done.</summary>
        public static void Register(string runId, string repositoryRoot)
        {
            _runId = runId;
            _repositoryRoot = repositoryRoot;
            if (Interlocked.Exchange(ref _registered, 1) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => TearDown("the test process exited before the run's teardown");
                Console.CancelKeyPress += (_, _) => TearDown("Ctrl+C stopped the run before its teardown");

                // A crash ends the process without its exit event: an unhandled exception that ends it is a way out too.
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    if (e.IsTerminating)
                    {
                        TearDown("an unhandled exception ended the test process before the run's teardown");
                    }
                };
            }
        }

        /// <summary>The run's own teardown has finished — or a failed start has removed what it made: nothing is left to do on the way out.</summary>
        public static void Done() => Interlocked.Exchange(ref _done, 1);

        private static void TearDown(string why)
        {
            if (_runId is not { } run || _repositoryRoot is not { } root || Interlocked.Exchange(ref _done, 1) == 1)
            {
                return;
            }

            Interlocked.Exchange(ref _stopping, 1);
            try
            {
                Task.Run(() => RemoveAsync(run, root, why)).Wait(Within);
            }
            catch (AggregateException)
            {
                // Best effort, on the way out: what is left, the next run's sweep removes.
            }
        }

        /// <summary>Removes what the run <paramref name="run"/> made, each step on its own, so one that fails stops none after it.</summary>
        private static async Task RemoveAsync(string run, string repositoryRoot, string why)
        {
            // The engine's removals end in time for what remains to be listed and written within the thirty seconds.
            using var deadline = new CancellationTokenSource(Within - TimeSpan.FromSeconds(5));
            var cancellationToken = deadline.Token;
            var done = new List<string> { why };
            async Task StepAsync(string what, Func<Task> step)
            {
                try
                {
                    await step();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    done.Add($"{what}: not done ({ex.GetType().Name}: {ex.Message})");
                }
            }

            Task DeleteRunDirectory(string directory)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                    done.Add($"run directory {directory}, with every key in it");
                }

                return Task.CompletedTask;
            }

            using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig?.GetDockerClientBuilder().Build();
            var directory = RunDirectories.For(run);

            // The image names it gave, put back from its directory's record before the directory goes.
            if (docker is not null)
            {
                await StepAsync("image names", async () => done.AddRange(await RecordingDockerClient.TakeBackAsync(docker, directory, cancellationToken)));
            }

            // contract-005 · G-3 — the keys go whatever else failed. contract-005 · G-17 round 3 (FLA-R3-01) — and first, before the
            // engine's removals, which may take the rest of the time given: a key file a container still holds open is tried again
            // once the containers are gone.
            await StepAsync("run directory", () => DeleteRunDirectory(directory));

            if (docker is not null)
            {
                await StepAsync("containers, networks and volumes", async () =>
                    done.AddRange(await RunRemoval.RemoveAsync(RunEngine.Of(docker, run), cancellationToken)));
            }
            else
            {
                done.Add("containers, networks and volumes: not removed, Testcontainers found no Docker endpoint");
            }

            if (Directory.Exists(directory))
            {
                await StepAsync("run directory, again", () => DeleteRunDirectory(directory));
            }

            await StepAsync("bundle", async () =>
            {
                var bundle = Path.Combine(repositoryRoot, "TestResults", "e2e", run);
                Directory.CreateDirectory(bundle);
                await File.WriteAllLinesAsync(Path.Combine(bundle, "interrupted.txt"), done, CancellationToken.None);
            });
        }
    }
}

/// <summary>
/// contract-005 · G-17 round 3 (FLA-R3-01) — one thing a run labelled, as the engine lists it: its id, and the name a line of
/// interrupted.txt gives it.
/// </summary>
/// <param name="Id">What the engine removes it by.</param>
/// <param name="Name">What a line names it by.</param>
internal sealed record RunResource(string Id, string Name);

/// <summary>
/// contract-005 · G-17 round 3 (FLA-R3-01) — what removing a run asks of the engine, as functions: list what carries the run's
/// label, of each kind, and remove one of it. The engine's own (<see cref="Of"/>) for a run's way out; an engine of a test's own for
/// HarnessSelfTests, which holds <see cref="RunRemoval"/> to its word with no container at all.
/// </summary>
internal sealed record RunEngine(
    Func<CancellationToken, Task<IReadOnlyList<RunResource>>> Containers,
    Func<string, CancellationToken, Task> RemoveContainer,
    Func<CancellationToken, Task<IReadOnlyList<RunResource>>> Networks,
    Func<string, CancellationToken, Task> RemoveNetwork,
    Func<CancellationToken, Task<IReadOnlyList<RunResource>>> Volumes,
    Func<string, CancellationToken, Task> RemoveVolume)
{
    /// <summary>The engine <paramref name="docker"/> reaches, for everything that carries the run <paramref name="run"/>'s label.</summary>
    public static RunEngine Of(IDockerClient docker, string run)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var labelled = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [$"{E2ENetwork.RunLabel}={run}"] = true },
        };

        return new RunEngine(
            async cancellationToken => [.. (await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = labelled }, cancellationToken))
                .Select(c => new RunResource(c.ID, $"{c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID} ({c.Image})"))],
            (id, cancellationToken) => docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, cancellationToken),
            async cancellationToken => [.. (await docker.Networks.ListNetworksAsync(new NetworksListParameters { Filters = labelled }, cancellationToken))
                .Select(n => new RunResource(n.ID, n.Name))],
            (id, cancellationToken) => docker.Networks.DeleteNetworkAsync(id, cancellationToken),
            async cancellationToken => [.. ((await docker.Volumes.ListAsync(new VolumesListParameters { Filters = labelled }, cancellationToken)).Volumes ?? [])
                .Select(v => new RunResource(v.Name, v.Name))],
            (name, cancellationToken) => docker.Volumes.RemoveAsync(name, force: true, cancellationToken));
    }
}

/// <summary>
/// contract-005 · G-17 round 3 (FLA-R3-01) — how a run stopped before its teardown removes what it labelled, whatever is removed under
/// it meanwhile: each container on its own, one the engine says is gone (404) or already being removed (409) counted gone; the run's
/// label listed again after every pass, until nothing carries it or the time runs out, since the tests' own teardowns remove their
/// containers, and a test may start one, while the process exits; then its networks, tried again while they still hold an
/// endpoint; then its volumes, tried again while a container still holds one. Returns what it removed, a line each, and a last line
/// naming whatever still carries the label.
/// </summary>
internal static class RunRemoval
{
    /// <summary>Removes, through <paramref name="engine"/>, everything that carries the run's label, until <paramref name="cancellationToken"/> says the time is up.</summary>
    public static async Task<IReadOnlyList<string>> RemoveAsync(RunEngine engine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // contract-005 · G-17 round 4 (FLA-R4-03) — the whole pass again until one lists nothing: a test may create a container
        // while the networks or volumes are being removed.
        var said = new List<string>();
        var listedAny = true;
        while (listedAny && !cancellationToken.IsCancellationRequested)
        {
            var containers = await RemoveEveryAsync("container", engine.Containers, engine.RemoveContainer, [HttpStatusCode.NotFound, HttpStatusCode.Conflict], said, cancellationToken);
            var networks = await RemoveEveryAsync("network", engine.Networks, engine.RemoveNetwork, [HttpStatusCode.NotFound], said, cancellationToken);
            var volumes = await RemoveEveryAsync("volume", engine.Volumes, engine.RemoveVolume, [HttpStatusCode.NotFound], said, cancellationToken);
            listedAny = containers || networks || volumes;
        }

        // What is left, asked once more whatever the deadline did, briefly.
        using var last = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var left = new List<string>();
        foreach (var (kind, list) in new[] { ("container", engine.Containers), ("network", engine.Networks), ("volume", engine.Volumes) })
        {
            try
            {
                left.AddRange((await list(last.Token)).Select(r => $"{kind} {r.Name}"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                left.Add($"{kind}s: not listed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        said.Add(left.Count == 0 ? "left: nothing that carries the run's label" : $"left: {string.Join("; ", left)}");
        return said;
    }

    /// <summary>
    /// Removes every <paramref name="kind"/> <paramref name="list"/> gives, each on its own, listing again after each pass, until it
    /// gives none or the time is up. A removal the engine answers with one of <paramref name="gone"/> is of one gone, or going; any
    /// other failure leaves it to the next pass. Whether its first listing gave any.
    /// </summary>
    private static async Task<bool> RemoveEveryAsync(
        string kind,
        Func<CancellationToken, Task<IReadOnlyList<RunResource>>> list,
        Func<string, CancellationToken, Task> remove,
        HttpStatusCode[] gone,
        List<string> said,
        CancellationToken cancellationToken)
    {
        var listedAny = false;
        try
        {
            while (true)
            {
                var listed = await list(cancellationToken);
                if (listed.Count == 0)
                {
                    return listedAny;
                }

                listedAny = true;

                foreach (var resource in listed)
                {
                    try
                    {
                        await remove(resource.Id, cancellationToken);
                        said.Add($"{kind} {resource.Name}");
                    }
                    catch (DockerApiException ex) when (gone.Contains(ex.StatusCode))
                    {
                        // Gone, or going: the next listing says which.
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        // Still there — a network with an endpoint left, a volume a container still holds — and tried again.
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The time given is up; what is left is named after.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            said.Add($"{kind}s: not listed ({ex.GetType().Name}: {ex.Message})");
        }

        return false;
    }
}
