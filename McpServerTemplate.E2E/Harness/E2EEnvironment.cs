using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Docker.DotNet;
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
/// every key in it.
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
        WireMockService wireMock,
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
        WireMock = wireMock;
        Redis = redis;
        Names = keycloak.Names.Including(testIssuer.Names).Including(wireMock.Names);
        _services = [keycloak, testIssuer, wireMock, redis];
    }

    /// <summary>Every phase of the run, measured: the environment's, each server's and each test's.</summary>
    public static Timings Timings { get; } = new();

    /// <summary>The identity providers every server is configured with.</summary>
    public static IssuerRegistry Issuers { get; } = new IssuerRegistry()
        .Register(new(KeycloakIssuer, new Uri(KeycloakService.Issuer), KeycloakService.Issuer, IssuerRegistry.Owner.Keycloak, "azp", KeycloakService.Scopes))
        .Register(new(IdpA, new Uri("https://idp-a.e2e.test"), "https://idp-a.e2e.test", IssuerRegistry.Owner.TestIssuer, "client_id", KeycloakService.Scopes))
        .Register(new(IdpB, new Uri("https://idp-b.e2e.test"), "https://idp-b.e2e.test", IssuerRegistry.Owner.TestIssuer, "client_id", KeycloakService.Scopes));

    /// <summary>The upstream host names and the container that answers each.</summary>
    public static UpstreamRegistry Upstreams { get; } = new UpstreamRegistry()
        .Register("opendata-download-metfcst.smhi.se", UpstreamRegistry.WireMock)
        .Register("opendata-download-metobs.smhi.se", UpstreamRegistry.WireMock)
        .Register("jsonplaceholder.typicode.com", UpstreamRegistry.WireMock)
        .Register(WireMockService.Alias, UpstreamRegistry.WireMock);

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

    public WireMockService WireMock { get; }

    public RedisService Redis { get; }

    /// <summary>The shared services' names. A server adds mcp.e2e.test, pointing at its own front.</summary>
    public NameMap Names { get; }

    /// <summary>The environment, started by whichever test class asks first.</summary>
    public static Task<E2EEnvironment> GetAsync() => Shared.Value;

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
        var created = new List<IAsyncDisposable>();
        IDockerClient? docker = null;
        TestPki? pki = null;

        try
        {
            docker = await Timings.MeasureAsync("environment: docker available", () => DockerEngine.ConnectAsync(cancellationToken));

            // Set before the first resource is created, which is when Testcontainers starts it.
            TestcontainersSettings.ResourceReaperImage = new DockerImage(Images.ResourceReaper);

            pki = await Timings.MeasureAsync("environment: test pki", () => Task.FromResult(CreatePki(root, runId)));

            var contextHash = await Timings.MeasureAsync("environment: build context hash", () => Task.Run(() => Images.ContextHash(root), cancellationToken));
            var revision = await Timings.MeasureAsync("environment: checkout revision", () => Images.CheckoutRevisionAsync(root, cancellationToken));

            var network = await Timings.MeasureAsync("environment: network", () => E2ENetwork.CreateAsync(runId, docker, cancellationToken));
            created.Add(network);

            var names = new NameMap(pki.Ca);

            // Independent parts start together; the test issuer waits only for its own image.
            var serverImage = Timings.MeasureAsync("image: server (Dockerfile)", () =>
                Images.BuildAsync(Images.ServerRepository, "Dockerfile", root, contextHash, revision, cancellationToken));
            var issuerImage = Timings.MeasureAsync("image: test issuer", () =>
                Images.BuildAsync(Images.IssuerRepository, "McpServerTemplate.TestIssuer/Dockerfile", root, contextHash, revision, cancellationToken));
            var redis = Timings.MeasureAsync("container: redis", () => RedisService.StartAsync(network, runId, cancellationToken));
            var keycloak = Timings.MeasureAsync("container: keycloak", () =>
                KeycloakService.StartAsync(docker, network, pki, names, ServerUnderTest.Resource, ServerFixture.AllClientIds(), runId, cancellationToken));
            var wireMock = Timings.MeasureAsync("container: wiremock", () =>
                WireMockService.StartAsync(docker, network, pki, names, Upstreams.HostsOf(UpstreamRegistry.WireMock), runId, cancellationToken));
            var testIssuer = Timings.MeasureAsync("container: test issuer", async () =>
                await TestIssuerService.StartAsync(docker, await issuerImage, network, pki, names, Issuers.HostsServedBy(IssuerRegistry.Owner.TestIssuer), runId, cancellationToken));

            try
            {
                await Task.WhenAll(serverImage, issuerImage, redis, keycloak, wireMock, testIssuer);
            }
            finally
            {
                // Whatever did start is removed if anything else failed.
                if (redis.IsCompletedSuccessfully)
                {
                    created.Add(redis.Result);
                }

                if (keycloak.IsCompletedSuccessfully)
                {
                    created.Add(keycloak.Result);
                }

                if (wireMock.IsCompletedSuccessfully)
                {
                    created.Add(wireMock.Result);
                }

                if (testIssuer.IsCompletedSuccessfully)
                {
                    created.Add(testIssuer.Result);
                }
            }

            // contract-005 · G-8 — before any test runs: both images carry this checkout's revision
            // and its build context's hash.
            await Timings.MeasureAsync("environment: revision labels", async () =>
            {
                await Images.VerifyRevisionAsync(docker, serverImage.Result, contextHash, revision, cancellationToken);
                await Images.VerifyRevisionAsync(docker, issuerImage.Result, contextHash, revision, cancellationToken);
            });

            var environment = new E2EEnvironment(
                runId, root, docker, pki, network, serverImage.Result, issuerImage.Result,
                keycloak.Result, testIssuer.Result, wireMock.Result, redis.Result);

            await Timings.MeasureAsync("environment: clock self-check", () => environment.CheckClockAsync(cancellationToken));
            return environment;
        }
        catch (Exception ex)
        {
            await WriteStartFailureAsync(root, runId, created, ex);

            created.Reverse();
            foreach (var resource in created)
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
    private static async Task WriteStartFailureAsync(string root, string runId, IEnumerable<IAsyncDisposable> created, Exception fault)
    {
        var bundle = Path.Combine(root, "TestResults", "e2e", runId);
        try
        {
            Directory.CreateDirectory(bundle);
            await File.WriteAllTextAsync(Path.Combine(bundle, "start-failure.txt"), fault.ToString());
            Timings.WriteTo(Path.Combine(bundle, "timings.json"));

            foreach (var (name, container) in created.Select(Describe).OfType<(string, DotNet.Testcontainers.Containers.IContainer)>())
            {
                var (stdout, stderr) = await container.GetLogsAsync();
                await File.WriteAllTextAsync(Path.Combine(bundle, $"{name}.log"), stdout + stderr);
            }
        }
        catch (Exception ex) when (ex is IOException or DockerApiException or InvalidOperationException)
        {
            // The fault itself is what matters, and it is on its way to the test's output.
        }

        static (string, DotNet.Testcontainers.Containers.IContainer)? Describe(IAsyncDisposable resource) => resource switch
        {
            KeycloakService keycloak => ("keycloak", keycloak.Container),
            TestIssuerService issuer => ("test-issuer", issuer.Container),
            WireMockService wireMock => ("wiremock", wireMock.Container),
            RedisService redis => ("redis", redis.Container),
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

        var pki = TestPki.Create(directory, new Dictionary<string, string[]>
        {
            ["front"] = [TlsFront.Host],
            ["keycloak"] = [KeycloakService.Host],
            ["issuer"] = [.. Issuers.HostsServedBy(IssuerRegistry.Owner.TestIssuer)],
            ["wiremock"] = [.. Upstreams.HostsOf(UpstreamRegistry.WireMock)],
        });

        RunDirectories.MarkOwned(directory);
        return pki;
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
            foreach (var (name, container) in new[]
            {
                ("keycloak", Keycloak.Container), ("test-issuer", TestIssuer.Container),
                ("wiremock", WireMock.Container), ("redis", Redis.Container),
            })
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
