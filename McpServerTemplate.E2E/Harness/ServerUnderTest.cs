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
/// One running server image, with the TLS front in front of it.
///
/// contract-005 · G-8 — the image built from the repository's Dockerfile, configured through its
/// environment only: the base settings below, then the test class's <see cref="SettingsDelta"/>.
///   Trust:     SSL_CERT_FILE and SSL_CERT_DIR both name the run's test CA and nothing else, so a
///              call to a host with no fake fails TLS instead of reaching the internet.
///   Transport: BindAddress 0.0.0.0, AllowedHosts mcp.e2e.test only, KnownProxies the front's
///              static address only.
///   Running:   stderr is kept by Docker and read back through the container's logs; the server is
///              ready when /readyz answers on its own published port — directly, not through the
///              front, so a broken front is not mistaken for a server that never started.
/// There is one per test class, so counts in Redis and at the fakes never leak between classes.
/// </summary>
public sealed class ServerUnderTest : IAsyncDisposable
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
    /// Where MCP answers today: the server root. The resource names /mcp and the endpoint is /; that
    /// disagreement is contract-005's first defect (G-12), fixed in a later phase, not here.
    /// </summary>
    public static readonly Uri Endpoint = new($"https://{TlsFront.Host}/");

    private readonly E2EEnvironment _environment;

    private ServerUnderTest(
        E2EEnvironment environment,
        string name,
        IContainer container,
        TlsFront front,
        IReadOnlyDictionary<string, string> settings)
    {
        _environment = environment;
        Name = name;
        Container = container;
        Front = front;
        Settings = settings;
        Names = environment.Names.With(TlsFront.Host, TlsFront.Port, front.Published.Host, front.Published.Port);
    }

    /// <summary>The name this server was started under; also its network alias's suffix.</summary>
    public string Name { get; }

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
    public Uri DirectEndpoint => new($"http://{Container.Hostname}:{Container.GetMappedPublicPort(Port)}/");

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
        E2EEnvironment environment, string name, SettingsDelta delta, CancellationToken cancellationToken)
    {
        var alias = $"server-{name}";
        var frontAddress = E2ENetwork.AllocateStatic();
        var settings = delta.ApplyTo(BaseSettings(E2EEnvironment.Issuers, frontAddress));

        var container = new ContainerBuilder(environment.ServerImage)
            .WithNetwork(environment.Network)
            .WithNetworkAliases(alias)
            .WithLabel(E2ENetwork.RunLabel, environment.RunId)
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
                await container.StartAsync(cancellationToken);
                await WaitUntilReadyAsync(environment.Docker, container, cancellationToken);
            });

            // contract-005 · UC-1 edge — the server's own start is left as it is: it can fail for the
            // product's reasons. What follows is the environment around it, and a failure there names
            // its phase however it surfaced.
            await E2EEnvironment.Timings.MeasureEnvironmentAsync($"{name}: trust self-check", () =>
                CheckTrustAsync(environment, container, cancellationToken));

            front = await E2EEnvironment.Timings.MeasureEnvironmentAsync($"{name}: front", () =>
                TlsFront.StartAsync(environment.Network, environment.Pki, frontAddress, alias, Port, environment.RunId, cancellationToken));

            var server = new ServerUnderTest(environment, name, container, front, settings);
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

    /// <summary>An HTTP client through this server's name map, forwarding <paramref name="clientAddress"/>.</summary>
    public HttpClient CreateClient(string clientAddress, NameMapLog? log = null) => Names.CreateClient(log, clientAddress);

    public async ValueTask DisposeAsync()
    {
        await WriteLogsAsync(_environment, Name, Container, Front);
        await Front.DisposeAsync();
        await Container.DisposeAsync();
    }

    private static async Task WaitUntilReadyAsync(Docker.DotNet.IDockerClient docker, IContainer container, CancellationToken cancellationToken)
    {
        // Directly on the published port, with the one Host the server allows: host filtering runs
        // before the health endpoints.
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var readyz = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(Port)}/readyz");
        var deadline = DateTime.UtcNow.AddMinutes(1);

        while (DateTime.UtcNow < deadline)
        {
            if (await DockerEngine.ExitCodeIfStoppedAsync(docker, container.Id, cancellationToken) is { } exitCode)
            {
                var (_, stderr) = await container.GetLogsAsync(ct: cancellationToken);
                throw new InvalidOperationException(
                    $"The server exited with code {exitCode} during startup. Its stderr:{Environment.NewLine}{stderr}");
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, readyz);
                request.Headers.Host = TlsFront.Host;
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
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
    private static async Task CheckTrustAsync(E2EEnvironment environment, IContainer container, CancellationToken cancellationToken)
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
    /// contract-005 · G-11 — forwarded headers ignored. An unauthenticated request through the front
    /// is challenged with a metadata URL the server builds from the request's scheme and Host; it is
    /// https only if the server honoured the front's X-Forwarded-Proto, which it does only when the
    /// front's address is the one KnownProxies names. No token is sent, so no issuer is consulted.
    /// When it is not https, the front's access-log line for the request says which side dropped the
    /// header: the front, which then did not send it, or the server, which did not trust it.
    /// </summary>
    private async Task CheckForwardedHeadersAsync(CancellationToken cancellationToken)
    {
        using var http = CreateClient(ClientAddresses.SelfCheck);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e-self-check","version":"1"}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new EnvironmentFaultException("front", $"CA trust missing: the front's certificate did not validate against the run's test CA ({ex.InnerException.Message}).", ex);
        }

        using (response)
        {
            var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                throw new EnvironmentFaultException(
                    "front",
                    $"an unauthenticated request through the front got {(int)response.StatusCode}, not 401 with a challenge. "
                    + "The front did not reach the server, or did not keep the Host header.");
            }

            if (!challenge.Contains($"resource_metadata=\"https://{TlsFront.Host}/", StringComparison.Ordinal))
            {
                // Which side dropped it, from the front's own record of what it sent upstream.
                var line = await Front.AccessLogLineAsync(ClientAddresses.SelfCheck, cancellationToken);
                string which;
                if (line is null)
                {
                    which = $"The front did not send it: its access log has no line forwarded for {ClientAddresses.SelfCheck}, so the request never passed through it.";
                }
                else if (!line.Contains(" forwarded-proto=https ", StringComparison.Ordinal) || line.Contains(" upstream=- ", StringComparison.Ordinal))
                {
                    which = $"The front did not send it: its access log line is '{line}'.";
                }
                else
                {
                    var knownProxies = Settings.Where(s => s.Key.StartsWith("HttpTransport__KnownProxies__", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value);
                    which = $"The front sent it (its access log line is '{line}'), so the server did not trust it: the front's address on the "
                        + $"run's network is {Front.Container.IpAddress} (allocated {Front.Address}), and the server's KnownProxies are [{string.Join(", ", knownProxies)}].";
                }

                throw new EnvironmentFaultException(
                    "front",
                    $"forwarded headers ignored: the server's challenge is '{challenge}', not an https URL on {TlsFront.Host}. "
                    + $"The front did not send X-Forwarded-Proto, or the server did not trust it. {which}");
            }
        }
    }

    private static async Task WriteLogsAsync(E2EEnvironment environment, string name, IContainer container, TlsFront? front)
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
