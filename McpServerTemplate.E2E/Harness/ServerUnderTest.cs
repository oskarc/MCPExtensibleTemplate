using System.Formats.Tar;
using System.Globalization;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-8, G-10 — which image a server under test runs: the one that ships, built from the
/// repository's Dockerfile, or the test host, built from McpServerTemplate.E2EHost's. Both are
/// configured the same way, by the environment's base settings and a delta, and nothing else.
/// </summary>
public enum ServerBuild
{
    /// <summary>The shipped image (G-8).</summary>
    Shipped,

    /// <summary>The test host (G-10): the shipped composition, with test modules only it has.</summary>
    TestHost,
}

/// <summary>
/// One running server image, with the TLS front in front of it.
///
/// contract-005 · G-8 — the image built from the repository's Dockerfile, configured through its
/// environment only: the base settings below, then the test class's <see cref="SettingsDelta"/>.
///   Names:     contract-005 · the pioneer's decision of 2026-09-29 — the container resolves no real
///              name (<see cref="E2ENetwork.NameServer"/>), so a call to a host with no fake fails at
///              the name lookup and reaches nothing.
///   Trust:     SSL_CERT_FILE and SSL_CERT_DIR both name the run's test CA and nothing else, so a
///              server that did reach some other host would fail TLS there, and trust nothing else.
///   Transport: BindAddress 0.0.0.0, AllowedHosts mcp.e2e.test only, KnownProxies the front's
///              static address only.
///   Running:   stderr is kept by Docker and read back through the container's logs; the server is
///              ready when /readyz answers on its own published port — directly, not through the
///              front, so a broken front is not mistaken for a server that never started.
/// There is one per test class, so counts in Redis and at the fakes never leak between classes.
/// </summary>
public sealed partial class ServerUnderTest : IAsyncDisposable
{
    /// <summary>HttpTransport:Port — the product's default, and what the Dockerfile exposes.</summary>
    public const int Port = 3001;

    /// <summary>Authentication:Resource. Every issuer mints its audience from this.</summary>
    public const string Resource = "https://mcp.e2e.test/mcp";

    /// <summary>Where the run's CA is mounted in the server container, alone (G-3).</summary>
    public const string TrustMount = "/e2e/trust";

    /// <summary>The image's app user (G-1): whatever the server must read, this user must be able to.</summary>
    public const int AppUser = 1654;

    /// <summary>
    /// Where MCP answers: the resource's own URL, through the front. contract-005 · G-12 (1) — until the
    /// fix it answered at the root while the resource named /mcp.
    /// </summary>
    public static readonly Uri Endpoint = new(Resource);

    private readonly E2EEnvironment _environment;

    private ServerUnderTest(
        E2EEnvironment environment,
        string name,
        ServerBuild build,
        IContainer container,
        TlsFront front,
        IReadOnlyDictionary<string, string> settings)
    {
        _environment = environment;
        Name = name;
        Build = build;
        Container = container;
        Front = front;
        Settings = settings;
        Names = environment.Names.With(TlsFront.Host, TlsFront.Port, front.Published.Host, front.Published.Port);
    }

    /// <summary>The name this server was started under; also its network alias's suffix.</summary>
    public string Name { get; }

    /// <summary>Which image it runs.</summary>
    public ServerBuild Build { get; }

    public IContainer Container { get; }

    public TlsFront Front { get; }

    /// <summary>The environment variables the container was started with, after the delta.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }

    /// <summary>The environment's name map, with mcp.e2e.test sent to this server's front.</summary>
    public NameMap Names { get; }

    /// <summary>
    /// The server itself, on its published port, not through the front: plain http, from the test
    /// process. A request sent here comes from outside KnownProxies, so what it forwards is not trusted.
    /// </summary>
    public Uri DirectEndpoint => new($"http://{Container.Hostname}:{Container.GetMappedPublicPort(Port)}{Endpoint.AbsolutePath}");

    /// <summary>
    /// The configuration every server in the environment starts from. The front's address is part of
    /// it because KnownProxies must name the front before the server exists.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BaseSettings(IssuerRegistry issuers, IPAddress front)
    {
        ArgumentNullException.ThrowIfNull(issuers);
        ArgumentNullException.ThrowIfNull(front);

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The environment the image ships for. appsettings.Production.json is loaded from /app.
            ["ASPNETCORE_ENVIRONMENT"] = "Production",

            // contract-005 · G-8 — trust the run's test CA and nothing else.
            ["SSL_CERT_FILE"] = $"{TrustMount}/{TestPki.CaFile}",
            ["SSL_CERT_DIR"] = TrustMount,

            ["Transport"] = "http",
            ["HttpTransport:BindAddress"] = "0.0.0.0",
            ["HttpTransport:Port"] = Port.ToString(CultureInfo.InvariantCulture),
            ["HttpTransport:AllowedHosts:0"] = TlsFront.Host,
            ["HttpTransport:KnownProxies:0"] = front.ToString(),

            ["Limits:Redis"] = RedisService.ConnectionString,

            ["Authentication:Resource"] = Resource,

            // Every provider answers to Keycloak unless a test class binds it elsewhere.
            ["Providers:Smhi:IdentityProvider"] = E2EEnvironment.KeycloakIssuer,
            ["Providers:SmhiObs:IdentityProvider"] = E2EEnvironment.KeycloakIssuer,
            ["Providers:JsonPlaceholder:IdentityProvider"] = E2EEnvironment.KeycloakIssuer,
        };

        foreach (var (key, value) in issuers.ToSettings())
        {
            settings[key] = value;
        }

        return settings;
    }

    internal static async Task<ServerUnderTest> StartAsync(
        E2EEnvironment environment, string name, SettingsDelta delta, ServerBuild build, CancellationToken cancellationToken)
    {
        var alias = $"server-{name}";
        var frontAddress = E2ENetwork.AllocateStatic();
        var settings = delta.ApplyTo(BaseSettings(E2EEnvironment.Issuers, frontAddress));

        var container = new ContainerBuilder(environment.ImageOf(build))
            .OnTheRunNetwork(environment.Network, environment.RunId)
            .WithNetworkAliases(alias)
            .WithPortBinding(Port, true)
            .WithLoopbackPortsOnly()
            // contract-005 · G-3 — the CA certificate alone, by read-only bind mount.
            .WithBindMount(environment.Pki.CaPath, $"{TrustMount}/{TestPki.CaFile}", AccessMode.ReadOnly)
            .WithEnvironment(settings)
            .Build();

        TlsFront? front = null;
        try
        {
            await E2EEnvironment.Timings.MeasureAsync($"{name}: server ready", async () =>
            {
                await container.StartServerAsync(environment.Docker, cancellationToken);
                await WaitUntilReadyAsync(environment.Docker, container, cancellationToken);
            });

            // contract-005 · UC-1 edge — the server's own start is left as it is: it can fail for the
            // product's reasons. What follows is the environment around it, and a failure there names
            // its phase however it surfaced.
            await E2EEnvironment.Timings.MeasureEnvironmentAsync($"{name}: trust self-check", () =>
                CheckTrustAsync(environment, container, cancellationToken));

            front = await E2EEnvironment.Timings.MeasureEnvironmentAsync($"{name}: front", () =>
                TlsFront.StartAsync(environment.Network, environment.Pki, frontAddress, alias, Port, environment.RunId, cancellationToken));

            var server = new ServerUnderTest(environment, name, build, container, front, settings);
            await E2EEnvironment.Timings.MeasureEnvironmentAsync($"{name}: forwarded-headers self-check", () =>
                server.CheckForwardedHeadersAsync(cancellationToken));

            return server;
        }
        catch
        {
            await WriteLogsAsync(environment, name, container, front);
            if (front is not null)
            {
                await front.DisposeAsync();
            }

            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>The server's standard error, as Docker captured it.</summary>
    public async Task<string> StderrAsync()
    {
        var (_, stderr) = await Container.GetLogsAsync();
        return stderr;
    }

    /// <summary>The server's "Frame installed:" line: what it serves, and which filters it installed.</summary>
    public async Task<string> StartupLineAsync()
    {
        var stderr = await StderrAsync();
        return stderr.Split('\n').FirstOrDefault(l => l.Contains("Frame installed:", StringComparison.Ordinal))?.Trim()
            ?? throw new InvalidOperationException("The server logged no 'Frame installed:' line on stderr.");
    }

    /// <summary>
    /// The latest line on the server's standard error that contains <paramref name="marker"/>, or null
    /// when none has appeared within two seconds (Docker's log follows the server a moment later).
    /// </summary>
    public async Task<string?> LatestStderrLineAsync(string marker)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            var line = (await StderrAsync()).Split('\n').LastOrDefault(l => l.Contains(marker, StringComparison.Ordinal))?.Trim();
            if (line is not null || DateTime.UtcNow >= deadline)
            {
                return line;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// contract-005 · T-3 — the lines on the server's standard error that contain <paramref name="marker"/>, after
    /// the first <paramref name="skip"/> of them: what it logged since a test last looked. Waits up to five seconds
    /// for at least one (Docker's log follows the server a moment later, and further under load); returns as soon
    /// as one is there, and empty when none has appeared.
    /// </summary>
    public async Task<IReadOnlyList<string>> StderrLinesAfterAsync(string marker, int skip)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var lines = await StderrLinesAsync(marker);
            if (lines.Count > skip || DateTime.UtcNow >= deadline)
            {
                return [.. lines.Skip(skip)];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Every line on the server's standard error that contains <paramref name="marker"/>, trimmed.</summary>
    public async Task<IReadOnlyList<string>> StderrLinesAsync(string marker)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        return [.. (await StderrAsync()).Split('\n').Where(l => l.Contains(marker, StringComparison.Ordinal)).Select(l => l.Trim())];
    }

    /// <summary>
    /// contract-005 · T-2, T-3, T-8 — this server's address on the run's network, as Docker gave it: where its
    /// requests to the issuers and the upstream fakes come from, so a witness's record can be read for this server
    /// alone while every server in the run shares the witness.
    /// </summary>
    public async Task<IPAddress> NetworkAddressAsync()
    {
        var inspected = await _environment.Docker.Containers.InspectContainerAsync(Container.Id);
        return inspected.NetworkSettings?.Networks is { } networks
            && networks.TryGetValue(_environment.Network.Name, out var endpoint)
            && IPAddress.TryParse(endpoint.IPAddress, out var address)
            ? address
            : throw new EnvironmentFaultException("server address", $"the server container {Name} has no address on the run's network {_environment.Network.Name}.");
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (FLA-R1-05) — when this server's container started, by the Docker engine's clock, which the
    /// test issuer's clock is (every container of the run shares the engine's kernel): the moment from which what a witness
    /// records at this server's address is this server's alone. Docker hands a freed address to the next container at once,
    /// so an earlier server at the same address may have left requests at a witness that counts by address; every one of
    /// them was made before that server stopped, and so before this one started.
    /// </summary>
    public async Task<DateTimeOffset> StartedAtAsync()
    {
        var inspected = await _environment.Docker.Containers.InspectContainerAsync(Container.Id);
        return EngineTime(inspected.State?.StartedAt)
            ?? throw new EnvironmentFaultException("server start time", $"the engine reports no start time for the server container {Name} ('{inspected.State?.StartedAt}').");
    }

    /// <summary>
    /// A time as the Docker engine writes one (RFC 3339, to the nanosecond), or null when there is none: .NET reads seven
    /// fractional digits, so the rest are cut, which moves it earlier by less than a tenth of a microsecond.
    /// </summary>
    internal static DateTimeOffset? EngineTime(string? written)
    {
        if (string.IsNullOrEmpty(written) || written.StartsWith("0001-01-01", StringComparison.Ordinal))
        {
            return null;
        }

        var cut = BeyondSevenFractionalDigits().Replace(written, "$1");
        return DateTimeOffset.TryParse(cut, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\.\d{7})\d+")]
    private static partial System.Text.RegularExpressions.Regex BeyondSevenFractionalDigits();

    /// <summary>An HTTP client through this server's name map, forwarding <paramref name="clientAddress"/>.</summary>
    public HttpClient CreateClient(string clientAddress, NameMapLog? log = null, DelegatingHandler? outermost = null) =>
        Names.CreateClient(log, clientAddress, outermost);

    public async ValueTask DisposeAsync()
    {
        await WriteLogsAsync(_environment, Name, Container, Front);
        await Front.DisposeAsync();
        await Container.DisposeAsync();
    }

    /// <summary>
    /// contract-005 · G-8, T-11 — a server started only to see whether it starts. The same image, the
    /// same base settings and the same delta as <see cref="StartAsync"/>, and no front and no
    /// self-checks: the outcome is the server's own, either an exit (its code and its stderr) or an
    /// answer on /readyz. A refusal at startup is the outcome a test asserts, so it is returned here
    /// rather than thrown. With no front, KnownProxies names an address no container holds
    /// (<see cref="E2ENetwork.NoFront"/>), so these servers take nothing from the static half.
    /// </summary>
    internal static async Task<StartupOutcome> StartupAsync(
        E2EEnvironment environment, string name, SettingsDelta delta, ServerBuild build, CancellationToken cancellationToken)
    {
        var settings = delta.ApplyTo(BaseSettings(E2EEnvironment.Issuers, E2ENetwork.NoFront));
        var container = new ContainerBuilder(environment.ImageOf(build))
            .OnTheRunNetwork(environment.Network, environment.RunId)
            .WithNetworkAliases($"server-{name}")
            .WithPortBinding(Port, true)
            .WithLoopbackPortsOnly()
            .WithBindMount(environment.Pki.CaPath, $"{TrustMount}/{TestPki.CaFile}", AccessMode.ReadOnly)
            .WithEnvironment(settings)
            .Build();

        try
        {
            // A 400 is an answer too: a server whose host filter refuses the name the harness probes
            // under has come up, and is refusing that name — which is an outcome, not a server that
            // never started.
            var (exitCode, answered) = await E2EEnvironment.Timings.MeasureAsync($"{name}: startup outcome", async () =>
            {
                await container.StartServerAsync(environment.Docker, cancellationToken);
                return await WaitForStartupAsync(environment.Docker, container, [HttpStatusCode.OK, HttpStatusCode.BadRequest], cancellationToken);
            });

            var (_, stderr) = await container.GetLogsAsync(ct: cancellationToken);
            return new StartupOutcome(environment, name, container, exitCode, answered, stderr);
        }
        catch
        {
            await WriteLogsAsync(environment, name, container, null);
            await container.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitUntilReadyAsync(Docker.DotNet.IDockerClient docker, IContainer container, CancellationToken cancellationToken)
    {
        if ((await WaitForStartupAsync(docker, container, [HttpStatusCode.OK], cancellationToken)).ExitCode is { } exitCode)
        {
            var (_, stderr) = await container.GetLogsAsync(ct: cancellationToken);
            throw new InvalidOperationException(
                $"The server exited with code {exitCode} during startup. Its stderr:{Environment.NewLine}{stderr}");
        }
    }

    /// <summary>
    /// The status /readyz answered on the server's published port, once it is one of
    /// <paramref name="answers"/>, or the server's exit code if it stopped first.
    /// </summary>
    internal static async Task<(long? ExitCode, HttpStatusCode? Answered)> WaitForStartupAsync(
        Docker.DotNet.IDockerClient docker, IContainer container, HttpStatusCode[] answers, CancellationToken cancellationToken)
    {
        // Directly on the published port, with the one Host the server allows: host filtering runs
        // before the health endpoints.
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        Uri? readyz = null;
        var deadline = DateTime.UtcNow.AddMinutes(1);

        while (DateTime.UtcNow < deadline)
        {
            if (await DockerEngine.ExitCodeIfStoppedAsync(docker, container.Id, cancellationToken) is { } exitCode)
            {
                return (exitCode, null);
            }

            // Asked of a container not seen to have stopped: one that has exited has no port mapped, and a refusal at once
            // is an outcome, not a missing port (seen 2026-09-29: "Exposed port 3001/tcp is not mapped" where three servers
            // had refused, exit 78, and Testcontainers' start had given up on them — DockerEngine.StartServerAsync).
            readyz ??= new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(Port)}/readyz");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, readyz);
                request.Headers.Host = TlsFront.Host;
                using var response = await http.SendAsync(request, cancellationToken);
                if (answers.Contains(response.StatusCode))
                {
                    return (null, response.StatusCode);
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A probe that timed out; try again.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        var (_, lastStderr) = await container.GetLogsAsync(ct: cancellationToken);
        throw new InvalidOperationException($"The server did not answer /readyz within a minute. Its stderr:{Environment.NewLine}{lastStderr}");
    }

    /// <summary>
    /// contract-005 · G-8, G-11, UC-1 edge — trust is the run's test CA and nothing else. Checked on the
    /// trust the container actually started with, as Docker records its environment, and never
    /// skipped. SSL_CERT_FILE and SSL_CERT_DIR must both be set: with neither, the server trusts the
    /// image's own store and not the run's CA, and every token fails with a key-lookup error that reads
    /// like a product fault; with one alone, the runtime falls back to the image's own location for the
    /// other, and a call to a host with no fake could pass TLS. Each must hold the run's CA, byte for
    /// byte, and nothing else, where the image's app user (1654) can read it: a bind mount Docker
    /// Desktop cannot share arrives as an empty path, and a CA only root can read is no trust at all to
    /// a server that is not root. Checked without asking any issuer for anything, so no count moves.
    ///
    /// contract-005 · G-2 — the test sees the server only through its network edges, its logs and its
    /// exit codes. A self-check may read back its own mounted inputs, as this one reads back the CA the
    /// harness mounted; it never reads the product's state.
    /// </summary>
    internal static async Task CheckTrustAsync(E2EEnvironment environment, IContainer container, CancellationToken cancellationToken)
    {
        var started = await environment.Docker.Containers.InspectContainerAsync(container.Id, cancellationToken);
        var variables = (started.Config?.Env ?? [])
            .Select(v => v.Split('=', 2))
            .Where(v => v.Length == 2 && v[1].Length > 0)
            .ToDictionary(v => v[0], v => v[1], StringComparer.Ordinal);
        variables.TryGetValue("SSL_CERT_FILE", out var file);
        variables.TryGetValue("SSL_CERT_DIR", out var directory);

        if (file is null && directory is null)
        {
            throw new EnvironmentFaultException(
                "server trust",
                "CA trust missing: the server container was started with neither SSL_CERT_FILE nor SSL_CERT_DIR, so it "
                + "trusts the image's own certificate store and not the run's test CA. Every token would fail its key "
                + "lookup at the identity provider, and read as a product failure.");
        }

        if (file is null || directory is null)
        {
            var (unset, set, setTo) = file is null ? ("SSL_CERT_FILE", "SSL_CERT_DIR", directory) : ("SSL_CERT_DIR", "SSL_CERT_FILE", file);
            throw new EnvironmentFaultException(
                "server trust",
                $"CA trust widened: the server container was started with {set}={setTo} but without {unset}, so for {unset} "
                + "the runtime falls back to the image's own location, and the server trusts more than the run's test CA. "
                + "A call to a host with no fake could pass TLS. Both must name the run's test CA and nothing else.");
        }

        var expected = await File.ReadAllBytesAsync(environment.Pki.CaPath, cancellationToken);
        await CheckTrustPathAsync(environment, container, "SSL_CERT_FILE", file, expected, cancellationToken);
        await CheckTrustPathAsync(environment, container, "SSL_CERT_DIR", directory, expected, cancellationToken);
    }

    /// <summary>
    /// Every file at <paramref name="path"/> in the container — the file, or each file in the
    /// directory — is the run's CA and readable by the app user; and there is at least one.
    /// </summary>
    private static async Task CheckTrustPathAsync(
        E2EEnvironment environment, IContainer container, string variable, string path, byte[] expected, CancellationToken cancellationToken)
    {
        var files = new List<(string Name, UnixFileMode Mode, int Uid, byte[] Content)>();
        try
        {
            // The archive, not the file alone: its entries carry the mode and owner the container sees.
            var archive = await environment.Docker.Containers.GetArchiveFromContainerAsync(
                container.Id, new ContainerPathStatParameters { Path = path }, statOnly: false, cancellationToken);
            using var stream = archive.Stream ?? throw new IOException("the engine returned no archive");
            using var reader = new TarReader(stream);
            while (await reader.GetNextEntryAsync(copyData: true, cancellationToken) is { } entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    using var content = new MemoryStream();
                    if (entry.DataStream is not null)
                    {
                        await entry.DataStream.CopyToAsync(content, cancellationToken);
                    }

                    files.Add((entry.Name, entry.Mode, entry.Uid, content.ToArray()));
                }
            }
        }
        catch (Exception ex) when (ex is DockerApiException or IOException or InvalidDataException)
        {
            throw new EnvironmentFaultException("server trust", $"CA trust missing: {variable}={path} could not be read from the server container ({ex.Message}).", ex);
        }

        if (files.Count == 0)
        {
            throw new EnvironmentFaultException("server trust", $"CA trust missing: {variable}={path} holds no file in the server container.");
        }

        foreach (var (name, mode, uid, content) in files)
        {
            if (!content.AsSpan().SequenceEqual(expected))
            {
                throw new EnvironmentFaultException(
                    "server trust",
                    $"CA trust missing: {variable}={path}: {name} in the server container is not the run's test CA ({content.Length} bytes, "
                    + $"expected {expected.Length}). "
                    + (path.StartsWith(TrustMount, StringComparison.Ordinal)
                        ? $"Is {Path.GetDirectoryName(environment.Pki.CaPath)} shared with Docker?"
                        : $"The harness mounts the CA at {TrustMount}/{TestPki.CaFile}; {variable} names something else."));
            }

            // contract-005 · G-1 — the server runs as 1654: the CA is trust only if that user can read it.
            if (!mode.HasFlag(UnixFileMode.OtherRead) && !(uid == AppUser && mode.HasFlag(UnixFileMode.UserRead)))
            {
                throw new EnvironmentFaultException(
                    "server trust",
                    $"CA trust missing: {variable}={path}: {name} is the run's test CA, but user {AppUser}, which the server runs as, "
                    + $"cannot read it (mode {Convert.ToString((int)mode, 8)}, owner {uid}).");
            }
        }
    }

    /// <summary>
    /// contract-005 · G-11, UC-1 edge — forwarded headers, the environment's part. The server trusts X-Forwarded-Proto and
    /// X-Forwarded-For only from a proxy it was started trusting, and the front must send them to it; both are the
    /// environment's, and both are checked here from the environment's own records, never from the server's answer. First,
    /// the address Docker gave the front on the run's network is one the server container was started trusting, among its
    /// HttpTransport:KnownProxies or inside its HttpTransport:KnownNetworks, as Docker records its environment. Then a request
    /// sent through the front — a GET of the resource's metadata document, whose answer is not judged here — went upstream to
    /// this server's address, with forwarded-proto=https, by the front's own access-log line.
    ///
    /// contract-005 · G-17 round 1 (HAR-R1-05) — what the server does with the headers it is sent is the product's, and is read
    /// only by the claims. This check used to send T-3's own request (no token, through the front) and require the product's
    /// 401 with an https metadata URL, so a server that stopped challenging, or stopped honouring the forwarded scheme, failed
    /// every class here as an environment fault that said the product was fine, and T-3's claim could never be the assertion
    /// that went red. T-3 (TokenRefusalTests.T3_no_token_is_challenged_with_the_metadata_on_the_front) and T-11 (1) hold the
    /// challenge and its scheme; T-1 holds the forwarded address.
    /// </summary>
    private async Task CheckForwardedHeadersAsync(CancellationToken cancellationToken)
    {
        var docker = _environment.Docker;
        var network = _environment.Network.Name;
        var front = await AddressOnRunNetworkAsync(docker, network, Front.Container.Id, cancellationToken)
            ?? throw new EnvironmentFaultException("front", $"the front of {Name} has no address on the run's network {network}.");
        var server = await AddressOnRunNetworkAsync(docker, network, Container.Id, cancellationToken)
            ?? throw new EnvironmentFaultException("front", $"the server container {Name} has no address on the run's network {network}.");

        // The front is a proxy the server was started trusting.
        var started = await docker.Containers.InspectContainerAsync(Container.Id, cancellationToken);
        var trusted = (started.Config?.Env ?? [])
            .Select(v => v.Split('=', 2))
            .Where(v => v.Length == 2 && TrustedProxySetting().IsMatch(v[0]))
            .Select(v => (Name: v[0], Value: v[1]))
            .ToList();
        if (!trusted.Any(t => Trusts(t.Name, t.Value, front)))
        {
            throw new EnvironmentFaultException(
                "front",
                $"forwarded headers ignored: the front's address on the run's network is {front} (allocated {Front.Address}), and the "
                + $"server container was started trusting [{string.Join(", ", trusted.Select(t => $"{t.Name}={t.Value}"))}] as proxies, so "
                + "it would take nothing the front forwards.");
        }

        // The front sends the headers to this server.
        using var http = CreateClient(ClientAddresses.SelfCheck);
        HttpStatusCode answered;
        try
        {
            using var response = await http.GetAsync(MetadataDocument, cancellationToken);
            answered = response.StatusCode;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new EnvironmentFaultException("front", $"CA trust missing: the front's certificate did not validate against the run's test CA ({ex.InnerException.Message}).", ex);
        }

        var line = await Front.AccessLogLineAsync(ClientAddresses.SelfCheck, cancellationToken);
        var fault = line switch
        {
            null => $"the front's access log has no line forwarded for {ClientAddresses.SelfCheck}: a GET of {MetadataDocument} "
                + $"(answered {(int)answered}) never passed through it.",
            _ when !line.Contains(" forwarded-proto=https ", StringComparison.Ordinal) =>
                $"the front did not send X-Forwarded-Proto: https to the server: its access log line is '{line}'.",
            _ when !line.Contains($" upstream={server}:{Port} ", StringComparison.Ordinal) =>
                $"the front did not send the request to this server, at {server}:{Port}: its access log line is '{line}'.",
            _ when answered is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
                $"the front got no answer from the server at {server}:{Port} ({(int)answered}): its access log line is '{line}'.",
            _ => null,
        };

        if (fault is not null)
        {
            throw new EnvironmentFaultException("front", $"forwarded headers not delivered: {fault}");
        }
    }

    /// <summary>contract-005 · G-17 round 1 (HAR-R1-05) — the resource's metadata document, through the front: what the forwarded-headers self-check sends.</summary>
    private static readonly Uri MetadataDocument = new($"https://{TlsFront.Host}/.well-known/oauth-protected-resource/mcp");

    /// <summary>
    /// Whether the setting <paramref name="name"/>=<paramref name="value"/> (a KnownProxies or KnownNetworks entry) names
    /// <paramref name="address"/>. contract-005 · G-17 round 1 (DOC-R1-01) — the docs profile asks it of the documented proxy.
    /// </summary>
    internal static bool Trusts(string name, string value, IPAddress address) =>
        name.Contains("KnownNetworks", StringComparison.OrdinalIgnoreCase)
            ? IPNetwork.TryParse(value.Trim(), out var trusted) && trusted.Contains(address)
            : IPAddress.TryParse(value.Trim(), out var proxy) && proxy.Equals(address);

    /// <summary>A container's address on the run's network <paramref name="network"/>, as the engine gave it, or null when it has none.</summary>
    internal static async Task<IPAddress?> AddressOnRunNetworkAsync(
        Docker.DotNet.IDockerClient docker, string network, string containerId, CancellationToken cancellationToken)
    {
        var inspected = await docker.Containers.InspectContainerAsync(containerId, cancellationToken);
        return inspected.NetworkSettings?.Networks is { } networks
            && networks.TryGetValue(network, out var endpoint)
            && IPAddress.TryParse(endpoint.IPAddress, out var address)
            ? address
            : null;
    }

    /// <summary>The environment variables that name the proxies a server trusts forwarded headers from.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^HttpTransport__Known(Proxies|Networks)__\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    internal static partial System.Text.RegularExpressions.Regex TrustedProxySetting();

    internal static async Task WriteLogsAsync(E2EEnvironment environment, string name, IContainer container, TlsFront? front)
    {
        try
        {
            var directory = Path.Combine(environment.ResultsDirectory, name);
            Directory.CreateDirectory(directory);

            var (stdout, stderr) = await container.GetLogsAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "server.stdout.log"), stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, "server.stderr.log"), stderr);

            if (front is not null)
            {
                var (frontOut, frontErr) = await front.Container.GetLogsAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, "front.log"), frontOut + frontErr);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Docker.DotNet.DockerApiException)
        {
            // Diagnostics are best effort; the failure they would explain is already on its way.
        }
    }
}
