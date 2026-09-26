using System.Globalization;
using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

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
            ["SSL_CERT_FILE"] = "/e2e/trust/ca.pem",
            ["SSL_CERT_DIR"] = "/e2e/trust",

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
            // contract-005 · G-3 — the CA certificate alone, by read-only bind mount.
            .WithBindMount(environment.Pki.CaPath, "/e2e/trust/ca.pem", AccessMode.ReadOnly)
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

            await E2EEnvironment.Timings.MeasureAsync($"{name}: trust self-check", () =>
                CheckTrustAsync(environment, container, settings, cancellationToken));

            front = await E2EEnvironment.Timings.MeasureAsync($"{name}: front", () =>
                TlsFront.StartAsync(environment.Network, environment.Pki, frontAddress, alias, Port, environment.RunId, cancellationToken));

            var server = new ServerUnderTest(environment, name, container, front, settings);
            await E2EEnvironment.Timings.MeasureAsync($"{name}: forwarded-headers self-check", () =>
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
    /// contract-005 · G-11 — CA trust missing. The test CA must be inside the container, byte for
    /// byte, where SSL_CERT_FILE points. A bind mount Docker Desktop cannot share arrives as an empty
    /// path, and the server then fails every token with a key-lookup error that reads like a product
    /// fault. Checked without asking any issuer for anything, so no count moves.
    /// </summary>
    private static async Task CheckTrustAsync(
        E2EEnvironment environment, IContainer container, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        if (!settings.TryGetValue("SSL_CERT_FILE", out var trustFile) || trustFile != "/e2e/trust/ca.pem")
        {
            return; // a delta that changes trust on purpose is checked by its own test
        }

        byte[] inside;
        try
        {
            inside = await container.ReadFileAsync(trustFile, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EnvironmentFaultException("server trust", $"CA trust missing: {trustFile} could not be read from the server container ({ex.Message}).", ex);
        }

        var expected = await File.ReadAllBytesAsync(environment.Pki.CaPath, cancellationToken);
        if (!inside.AsSpan().SequenceEqual(expected))
        {
            throw new EnvironmentFaultException(
                "server trust",
                $"CA trust missing: {trustFile} in the server container is not the run's test CA ({inside.Length} bytes, "
                + $"expected {expected.Length}). Is {Path.GetDirectoryName(environment.Pki.CaPath)} shared with Docker?");
        }
    }

    /// <summary>
    /// contract-005 · G-11 — forwarded headers ignored. An unauthenticated request through the front
    /// is challenged with a metadata URL the server builds from the request's scheme and Host; it is
    /// https only if the server honoured the front's X-Forwarded-Proto, which it does only when the
    /// front's address is the one KnownProxies names. No token is sent, so no issuer is consulted.
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
                throw new EnvironmentFaultException(
                    "front",
                    $"forwarded headers ignored: the server's challenge is '{challenge}', not an https URL on {TlsFront.Host}. "
                    + $"It did not trust X-Forwarded-Proto from the front at {Front.Address}; its KnownProxies are "
                    + $"[{string.Join(", ", Settings.Where(s => s.Key.StartsWith("HttpTransport__KnownProxies__", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value))}].");
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
