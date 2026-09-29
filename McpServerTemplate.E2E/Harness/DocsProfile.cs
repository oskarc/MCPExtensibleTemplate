using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-13 (UC-9) — the documented deployment, run as written.
///
/// docs/04's Docker Compose file — the production deployment it documents: ASPNETCORE_ENVIRONMENT=Production, the image's
/// own appsettings.Production.json and the environment the file sets, behind the file's own TLS-terminating proxy — is read
/// from the document itself, at the path the test is given, and run verbatim through one declared table
/// (<see cref="Table"/>). So is every file the compose file mounts that the document gives beside it: the proxy's
/// nginx.conf is the block the document shows under that name. The table has two kinds of row, each with its reason: host
/// names and addresses (the identity provider, the public host, the file's network, the proxy's fixed address on it, where
/// the file publishes on this machine, and — contract-005 · the pioneer's decision of 2026-09-29 — the name server every
/// service is given, which resolves no real name), and test trust (the proxy's certificate and key, which become the run's
/// test-CA leaf for the public host; SSL_CERT_FILE, SSL_CERT_DIR and the read-only mount of the run's CA in the server).
/// Nothing else about the file changes. Its image, mcp-server:latest, is this run's server image, tagged with that name
/// for the run and untagged after it (a tag it replaced is put back).
///
/// Each service runs as Compose runs it: its image, its environment, its read-only mounts, its fixed address and — contract-005
/// · G-17 round 2 — its restart policy, on the network under its service name, after the services it depends on; every image
/// it pulls is pinned by digest (CON-R2-04), and every container is checked as it starts (HAR-R2-08). The file's one network
/// is the run's (G-4), which the
/// network row follows; the containers carry the run's label, which no server reads. The deployment is reached the way the
/// file lets a client reach it and no other: through what it publishes on the host's 443, where https://${MCP_HOST}/mcp is
/// dialled — its own proxy — and it is ready when /readyz answers 200 there. It then serves as the document describes only if
/// its server takes forwarded headers from that proxy's address (<see cref="NotServing"/>).
///
/// contract-005 · G-17 round 1 (DOC-R1-01) — the profile used to start a TLS front of the harness's own in front of the
/// documented server, which the file did not contain and the table did not declare, and to reach the server on its own
/// published plain-http port. So it passed a file that had no proxy, published the server's plain-http port on every host
/// interface, and trusted the whole Docker range as proxies. The front is now the file's own proxy, run from the file, and a
/// file that publishes no front has none: a server that comes up behind nothing is reached on the port the file publishes
/// for it, over plain http, and T-9 fails there (<see cref="Front"/>).
///
/// It is not a server under test of the environment's base settings (<see cref="E2EEnvironment.StartServerAsync"/>): the
/// document is its configuration, and a setting the document does not name is not set.
/// </summary>
public sealed partial class DocsProfile : IAsyncDisposable
{
    /// <summary>
    /// The variable naming another copy of docs/04 to run instead of the repository's: how T-9 is shown red against the
    /// document as it stood at an earlier commit, without rewriting history. Refused in CI, where the profile is the
    /// repository's document and nothing else.
    /// </summary>
    public const string DocumentVariable = "MCP_E2E_DOCS04";

    /// <summary>The service the harness builds the image of: the server. Every other service's image is pulled as named.</summary>
    public const string ServerService = "mcp-server";

    /// <summary>The line in docs/04 the compose file follows.</summary>
    private const string ComposeMarker = "**Docker Compose**:";

    /// <summary>The kinds of row the table may hold (G-13).</summary>
    public const string HostOrAddress = "host name or address";

    /// <inheritdoc cref="HostOrAddress"/>
    public const string TestTrust = "test trust";

    /// <summary>contract-005 · G-17 round 1 (DOC-R1-01) — where docs/04's compose file mounts the proxy's certificate from.</summary>
    private const string ProxyCertificate = "./tls/tls.crt";

    /// <summary>contract-005 · G-17 round 1 (DOC-R1-01) — where docs/04's compose file mounts the proxy's key from.</summary>
    private const string ProxyKey = "./tls/tls.key";

    /// <summary>The run's test-CA leaf for the public host, <see cref="TlsFront.Host"/>, as E2EEnvironment makes it.</summary>
    private const string FrontLeaf = "front";

    /// <summary>Where the run's CA is mounted in the server container, as for every server of the run (G-8).</summary>
    private static readonly string TrustFile = $"{ServerUnderTest.TrustMount}/{TestPki.CaFile}";

    /// <summary>How long the documented deployment may take to answer /readyz, as a server under test may (G-8).</summary>
    private static readonly TimeSpan ReadyWithin = TimeSpan.FromMinutes(1);

    private readonly E2EEnvironment _environment;
    private readonly IReadOnlyList<(string Service, IContainer Container)> _services;
    private readonly RetaggedImage _image;

    private DocsProfile(
        E2EEnvironment environment,
        string document,
        IReadOnlyList<string> applied,
        IReadOnlyList<(string Service, IContainer Container)> services,
        RetaggedImage image,
        long? exitCode,
        string? notServing,
        string stderr,
        string? front,
        NameMap names)
    {
        _environment = environment;
        Document = document;
        Applied = applied;
        _services = services;
        _image = image;
        ExitCode = exitCode;
        NotServing = notServing;
        Stderr = stderr;
        Front = front;
        Names = names;
    }

    /// <summary>Where a row of the table acts.</summary>
    public enum Scope
    {
        /// <summary>
        /// On the compose file's text, and on each file the document gives beside it: every occurrence of what the
        /// document says.
        /// </summary>
        Text,

        /// <summary>A variable the file asks Compose for, by its name: the value Compose interpolates.</summary>
        Variable,

        /// <summary>The host side of every port a service publishes.</summary>
        PublishedPorts,

        /// <summary>The source of a read-only mount the file gives a service, which the document does not give beside it.</summary>
        Mount,

        /// <summary>Added to the server's service: an environment variable, or a mount.</summary>
        Server,

        /// <summary>contract-005 · the pioneer's decision of 2026-09-29 — added to every service the file defines.</summary>
        EveryService,
    }

    /// <summary>One declared row.</summary>
    /// <param name="Kind"><see cref="HostOrAddress"/> or <see cref="TestTrust"/>.</param>
    /// <param name="Where">Where it acts.</param>
    /// <param name="Document">What the document says, a variable's name, a mount's source, or what the run adds.</param>
    /// <param name="Run">What the run uses instead.</param>
    /// <param name="Reason">Why.</param>
    public sealed record Substitution(string Kind, Scope Where, string Document, string Run, string Reason)
    {
        /// <summary>The row as the run reports it.</summary>
        public override string ToString() => Where switch
        {
            Scope.Text => $"{Kind}: '{Document}' -> '{Run}' — {Reason}",
            Scope.Variable => $"{Kind}: variable {Document}={Run} — {Reason}",
            Scope.PublishedPorts => $"{Kind}: {Document} -> {Run} — {Reason}",
            Scope.Mount => $"{Kind}: mount {Document} -> {Run} — {Reason}",
            _ => $"{Kind}: added {Document} = {Run} — {Reason}",
        };
    }

    /// <summary>
    /// The declared table (G-13): every way the run's compose file differs from the document's, of two kinds, each with
    /// its reason. The run reports which rows acted on the document it read (<see cref="Applied"/>).
    ///
    /// contract-005 · G-17 round 1 (DOC-R1-01) — the rows the documented deployment's own proxy and network need: the file's
    /// subnet and the proxy's fixed address on it, and the proxy's certificate and key. The row that mapped the whole Docker
    /// range, 172.16.0.0/12, onto the run's subnet is gone with the range from the document: the proxy is one address now.
    /// </summary>
    public static IReadOnlyList<Substitution> Table { get; } =
    [
        new(HostOrAddress, Scope.Text, "https://login.example.com/realms/corp", KeycloakService.Issuer,
            "The identity provider. The environment's real identity provider is Keycloak (G-5), whose realm is mcp and whose "
            + "issuer is pinned to this URL; the name the server gives it, corp, is kept."),
        new(HostOrAddress, Scope.Text, "mcp.example.com", TlsFront.Host,
            "The public host, where the document writes it out: the run's front name, which the run's test-CA leaf names (G-3) "
            + "and the run's clients dial (G-9)."),
        new(HostOrAddress, Scope.Variable, "MCP_HOST", TlsFront.Host,
            "The public host, where the file asks Compose for it, and stops without it: the allowed host and the resource's "
            + "host, the same name."),
        new(HostOrAddress, Scope.Text, "172.16.238.0/24", E2ENetwork.Subnet,
            "The file's own network, where the proxy, the server and Redis sit and which nothing publishes. A run has one "
            + "network, G-4's slice of TEST-NET-2, where the environment's identity provider sits too (G-5): the file's network "
            + "is run as that one, and each service joins it under its service name."),
        new(HostOrAddress, Scope.Text, "172.16.238.10", E2ENetwork.DocsProxy.ToString(),
            "The proxy's fixed address on that network, the one address the server takes forwarded headers from (its "
            + "KnownProxies): an address of the run's network's static half that the harness gives nothing else (G-4)."),
        new(HostOrAddress, Scope.PublishedPorts, "the host side of a published port", "127.0.0.1, at a port Docker chooses",
            "Where the file publishes a service on this machine: docs/04, the proxy's 443. Nothing a run starts is published "
            + "beyond loopback, and a fixed host port can be held by another process or need privileges; the container side is "
            + "unchanged, and the run's clients reach it as https://mcp.e2e.test through their name map (G-6)."),
        new(HostOrAddress, Scope.EveryService, "a name server", $"{E2ENetwork.NameServer}, the container's own loopback, and no search domain",
            "The run's network resolves no real name — the pioneer's decision of 2026-09-29 (contract-005 revision, \"2. yes, cut "
            + "it and mock any calls\"). The file sets no name server, so Compose would give each service the host's; every container "
            + "of the run is given its own loopback instead (E2ENetwork.NameServer), so the file's services find each other and the "
            + "run's by name, and any name no container of the run answers fails at the lookup rather than reaching the internet."),
        new(TestTrust, Scope.Mount, ProxyCertificate, "the certificate of the run's test-CA leaf for mcp.e2e.test",
            "The proxy's certificate. An operator's names their public host and chains to a CA the run's clients do not "
            + "trust; the run's leaf (G-3) names the run's front name and chains to the run's CA, which is all the run's "
            + "clients trust (G-6)."),
        new(TestTrust, Scope.Mount, ProxyKey, "that leaf's key",
            "The proxy's key, from the leaf's own directory, read-only: the key of the certificate above, and no other "
            + "service's (G-3)."),
        new(TestTrust, Scope.Server, "SSL_CERT_FILE", TrustFile,
            "The identity provider's certificate is a leaf of the run's test CA (G-3), which no image trusts. Trust is that CA "
            + "alone, as for every server of the run (G-8), so a host that is not the run's would fail TLS, were its name to resolve."),
        new(TestTrust, Scope.Server, "SSL_CERT_DIR", ServerUnderTest.TrustMount,
            "Both are set, or the runtime falls back to the image's own store for the other and trusts more than the run's CA."),
        new(TestTrust, Scope.Server, "a read-only mount", $"the run's CA at {TrustFile}",
            "The CA certificate alone, by read-only bind mount; no key of any kind (G-3)."),
    ];

    /// <summary>The document this run read.</summary>
    public string Document { get; }

    /// <summary>What the table changed in this document, row by row, and what it added: the run's account of itself.</summary>
    public IReadOnlyList<string> Applied { get; }

    /// <summary>The server's exit code, or null while it runs.</summary>
    public long? ExitCode { get; }

    /// <summary>
    /// Why the documented deployment is not serving as the document says, while its server runs: its proxy stopped, or
    /// answered /readyz with something else for as long as the server may take to start, or serves a certificate other than
    /// the one the file mounts for it, or is not a proxy the server takes forwarded headers from. Null when none of these.
    /// </summary>
    public string? NotServing { get; }

    /// <summary>Whether the deployment came up: its server runs, and /readyz answered 200 the way the file lets a client in.</summary>
    public bool Started => ExitCode is null && NotServing is null;

    /// <summary>The server's standard error as it stood when the deployment came up, or when it did not.</summary>
    public string Stderr { get; }

    /// <summary>
    /// The file's TLS front: the service it publishes on the host's 443, through which the test reaches the deployment — its
    /// proxy. Null when the file publishes nothing there, or publishes the server itself there: it then has no TLS front,
    /// and the profile reaches the server on the port the file publishes for it, over plain http.
    /// </summary>
    public string? Front { get; }

    /// <summary>The environment's name map, with mcp.e2e.test sent to the file's front when it has one.</summary>
    public NameMap Names { get; }

    private IContainer ServerContainer => _services.Single(s => s.Service == ServerService).Container;

    /// <summary>
    /// The docs/04 this run is to read: the repository's, or the copy <see cref="DocumentVariable"/> names. In CI the
    /// variable is refused, so a CI run proves the repository's document and no other.
    /// </summary>
    public static string DocumentPath(string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);

        var named = Environment.GetEnvironmentVariable(DocumentVariable);
        if (string.IsNullOrEmpty(named))
        {
            return Path.Combine(repositoryRoot, "docs", "04-CONFIGURATION.md");
        }

        if (Environment.GetEnvironmentVariable("CI") is not null)
        {
            throw new InvalidOperationException(
                $"{DocumentVariable} is set ('{named}') in CI. The docs profile there runs the repository's docs/04 and nothing else.");
        }

        return Path.GetFullPath(named);
    }

    /// <summary>The server's standard error as it stands now.</summary>
    public async Task<string> StderrAsync()
    {
        var (_, stderr) = await ServerContainer.GetLogsAsync();
        return stderr;
    }

    /// <summary>The server's "Frame installed:" line.</summary>
    public async Task<string> StartupLineAsync() =>
        (await StderrAsync()).Split('\n').FirstOrDefault(l => l.Contains("Frame installed:", StringComparison.Ordinal))?.Trim()
            ?? throw new InvalidOperationException("The documented server logged no 'Frame installed:' line on stderr.");

    /// <summary>
    /// An HTTP client through this profile's name map: https://mcp.e2e.test reaches the file's own proxy, which forwards the
    /// connection's own address, as it would an operator's client's.
    /// </summary>
    public HttpClient CreateClient() => Names.CreateClient();

    /// <summary>What happened, in one line, for a failure message.</summary>
    public string Describe() => (ExitCode, NotServing, Front) switch
    {
        ({ } exitCode, _, _) => $"the documented server ({Document}) exited with code {exitCode}; "
            + (StartupOutcome.RefusalIn(Stderr) ?? "its stderr names no refusal: " + Stderr.Trim()),
        (null, { } why, _) => $"the documented server ({Document}) runs, but {why}",
        (null, null, { } front) => $"the documented deployment ({Document}) started, and /readyz answered 200 through its proxy, "
            + $"'{front}', under Host {TlsFront.Host}",
        _ => $"the documented server ({Document}) started, and /readyz answered 200 on the port the file publishes for it, over "
            + $"plain http: the file publishes no proxy on the host's {TlsFront.Port}, so nothing terminates TLS in front of it",
    };

    public async ValueTask DisposeAsync() => await TearDownAsync(_environment, Document, Applied, _services, _image);

    /// <summary>
    /// The diagnostics bundle's docs-profile directory — the table as it acted, and each service's log, the proxy's among
    /// them — then every container removed and the image's name put back. The same whether the profile came up or its start
    /// failed.
    /// </summary>
    private static async Task TearDownAsync(
        E2EEnvironment environment,
        string document,
        IReadOnlyList<string> applied,
        IReadOnlyList<(string Service, IContainer Container)> services,
        RetaggedImage image)
    {
        try
        {
            var directory = Path.Combine(environment.ResultsDirectory, "docs-profile");
            Directory.CreateDirectory(directory);
            await File.WriteAllLinesAsync(Path.Combine(directory, "table.txt"), [$"document: {document}", .. applied]);
            foreach (var (service, container) in services)
            {
                var (stdout, stderr) = await container.GetLogsAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, $"{service}.log"), stdout + stderr);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DockerApiException)
        {
            // Diagnostics are best effort; the failure they would explain is already on its way.
        }

        try
        {
            foreach (var (_, container) in services.Reverse())
            {
                await container.DisposeAsync();
            }
        }
        finally
        {
            await image.RestoreAsync();
        }
    }

    /// <summary>Reads <paramref name="documentPath"/>'s compose file, runs it through the table, and starts it.</summary>
    internal static async Task<DocsProfile> StartAsync(E2EEnvironment environment, string documentPath, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(documentPath, cancellationToken);
        var (compose, applied) = Substituted(ComposeFileIn(text, documentPath), string.Empty);

        var variables = Table.Where(r => r.Where == Scope.Variable).ToDictionary(r => r.Document, StringComparer.Ordinal);
        string Interpolated(string value) => Compose.Interpolate(value, variables, applied);

        var project = Compose.Parse(compose);
        var services = project.Services
            .Select(s => s with
            {
                Image = Interpolated(s.Image),
                Environment = [.. s.Environment.Select(Interpolated)],
                Ports = [.. s.Ports.Select(Interpolated)],
                Volumes = [.. s.Volumes.Select(Interpolated)],
                Networks = [.. s.Networks.Select(n => (n.Network, n.Address is null ? null : Interpolated(n.Address)))],
                Restart = s.Restart is null ? null : Interpolated(s.Restart),
            })
            .ToList();
        var networks = project.Networks.Select(n => n with { Subnet = n.Subnet is null ? null : Interpolated(n.Subnet) }).ToList();

        var server = services.FirstOrDefault(s => s.Name == ServerService)
            ?? throw new InvalidOperationException(
                $"The compose file in {documentPath} has no '{ServerService}' service: nothing in it runs the server's image.");

        // contract-005 · G-17 round 2 (CON-R2-04) — before anything is pulled: every image the documented deployment pulls is pinned
        // by the digest of what it names, as every image of this repository is (T-13; docs/04 says so of its own). The server's is
        // not pulled: it is this run's build, given the file's name for the run. The profile pulled every other as named, and
        // T-13's check reads the harness's own image references, never this file's, so an edit to a tag would have been pulled
        // and run in every end-to-end job with every check green.
        var unpinned = services.Where(s => s.Name != ServerService && !PinnedByDigest().IsMatch(s.Image)).ToList();
        if (unpinned.Count > 0)
        {
            throw new EnvironmentFaultException(
                "docs profile: images",
                $"the compose file in {documentPath} pulls {string.Join(" and ", unpinned.Select(s => $"'{s.Image}' for the '{s.Name}' service"))}, "
                + "not pinned by digest (image@sha256:<64 hex digits>): a tag can be moved on its registry, and the deployment the "
                + "profile runs would change with no line of the document changing. Pin each by the digest of its multi-arch index, "
                + "as every image of this repository is.");
        }

        if (RunNetworkFor(networks, services) is { } network)
        {
            applied.Add($"network '{network}': the run's network, {environment.Network.Name} ({E2ENetwork.Subnet}), which each service joins under its service name (G-4)");
        }

        // The way in: what the file publishes on the host's 443, where https://${MCP_HOST}/mcp is dialled — unless that is
        // the server itself, which serves plain http and is no TLS front. Without a front, the server's own published port.
        var front = WayIn(services) is { } way && way.Service != ServerService ? way : ((string Service, int Port)?)null;
        var direct = front is null ? ServerPort(server) : null;
        if (front is null && direct is null)
        {
            throw new InvalidOperationException(
                $"The compose file in {documentPath} publishes nothing on the host's {TlsFront.Port}, where https://${{MCP_HOST}}/mcp "
                + "is dialled, and does not publish the server either: nothing reaches the deployment, and the docs profile cannot see it serve.");
        }

        // Every file a service mounts, before anything starts: the document's own, or a declared row's in its place.
        var mounts = services.ToDictionary(
            s => s.Name,
            s => s.Volumes.Select(v => MountOf(environment, text, s.Name, v, applied)).ToList(),
            StringComparer.Ordinal);

        // "For that run the image is tagged with the name the compose file uses" (G-13).
        var image = await RetaggedImage.TagAsync(environment.Docker, environment.ServerImage, server.Image, cancellationToken);
        applied.Add($"image: '{server.Image}' is this run's server image ({environment.ServerImage}), tagged with that name for the run");

        var started = new List<(string Service, IContainer Container)>();
        try
        {
            foreach (var service in InStartOrder(services))
            {
                started.Add((service.Name, await StartServiceAsync(environment, service, mounts[service.Name], applied, cancellationToken)));
            }

            var container = started.Single(s => s.Service == ServerService).Container;
            (string Service, IContainer Container, int Port)? proxy = front is { } f
                ? (f.Service, started.Single(s => s.Service == f.Service).Container, f.Port)
                : null;

            var (exitCode, notServing) = await E2EEnvironment.Timings.MeasureAsync("docs profile: deployment ready", () =>
                WaitForDeploymentAsync(environment, container, proxy, direct, cancellationToken));

            var names = environment.Names;
            if (exitCode is null)
            {
                await E2EEnvironment.Timings.MeasureEnvironmentAsync("docs profile: trust self-check", () =>
                    ServerUnderTest.CheckTrustAsync(environment, container, cancellationToken));

                if (notServing is null && proxy is { } p)
                {
                    names = NamesThrough(environment.Names, p.Container, p.Port);
                    notServing = await ProxyNotTrustedAsync(environment, p.Service, p.Container, container, cancellationToken);
                }
            }

            var (_, stderr) = await container.GetLogsAsync(ct: cancellationToken);
            return new DocsProfile(environment, documentPath, applied, started, image, exitCode, notServing, stderr, front?.Service, names);
        }
        catch
        {
            await TearDownAsync(environment, documentPath, applied, started, image);
            throw;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with every Text row applied, and the rows that acted, each with how many times, labelled with
    /// <paramref name="where"/> when the text is a file the document gives beside the compose file.
    /// </summary>
    private static (string Text, List<string> Acted) Substituted(string text, string where)
    {
        var acted = new List<string>();
        foreach (var row in Table.Where(r => r.Where == Scope.Text))
        {
            var count = Regex.Count(text, Regex.Escape(row.Document));
            if (count > 0)
            {
                text = text.Replace(row.Document, row.Run, StringComparison.Ordinal);
                acted.Add($"{where}{row} ({count}x)");
            }
        }

        return (text, acted);
    }

    /// <summary>
    /// One service, as Compose runs it: its image, its environment, its read-only mounts and the address the file fixes for
    /// it, on the network under its own name. The server also gets the trust rows; every published port the address row.
    /// </summary>
    private static async Task<IContainer> StartServiceAsync(
        E2EEnvironment environment,
        Compose.Service service,
        IReadOnlyList<(string Source, string Target)> mounts,
        List<string> applied,
        CancellationToken cancellationToken)
    {
        // contract-005 · the pioneer's decision of 2026-09-29 — on the run's network the one way, which resolves no real name: a
        // row of the table (Scope.EveryService), reported for each service.
        var builder = E2ENetwork.OnTheRunNetwork(service.Image, environment.Docker, environment.Network, environment.RunId)
            .WithNetworkAliases(service.Name);
        applied.Add($"'{service.Name}': {Table.Single(r => r.Where == Scope.EveryService)}");

        foreach (var entry in service.Environment)
        {
            var equals = entry.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new InvalidOperationException(
                    $"The '{service.Name}' service sets '{entry}', which takes its value from the shell Compose runs in; the docs "
                    + "profile runs the document's values and no shell's.");
            }

            builder = builder.WithEnvironment(entry[..equals], entry[(equals + 1)..]);
        }

        foreach (var mapping in service.Ports)
        {
            builder = builder.WithPortBinding(PortOf(mapping, service.Name), assignRandomHostPort: true);
            applied.Add($"'{service.Name}' publishes '{mapping}': {Table.Single(r => r.Where == Scope.PublishedPorts)}");
        }

        builder = builder.WithLoopbackPortsOnly();

        foreach (var (source, target) in mounts)
        {
            builder = builder.WithBindMount(source, target, AccessMode.ReadOnly);
        }

        // contract-005 · G-17 round 1 (DOC-R1-01) — the address the file fixes for it on its network, which the address row
        // has put on the run's (RunNetworkFor checked it), set on the endpoint Testcontainers creates for that network.
        if (service.Networks.Select(n => n.Address).FirstOrDefault(a => a is not null) is { } address)
        {
            builder = builder.WithCreateParameterModifier(parameters =>
                parameters.NetworkingConfig!.EndpointsConfig![environment.Network.Name].IPAMConfig =
                    new EndpointIPAMConfig { IPv4Address = address });
        }

        // contract-005 · G-17 round 2 — the service's restart policy, as the file writes it: the engine starts the container again
        // as Compose would have it start the service. Not a row of the table: nothing about it is the run's.
        if (service.Restart is { } restart)
        {
            var policy = Compose.RestartPolicyOf(restart, service.Name);
            builder = builder.WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new HostConfig();
                parameters.HostConfig.RestartPolicy = policy;
            });
        }

        if (service.Name == ServerService)
        {
            // The trust rows (TestTrust): the two variables, and the CA they name, mounted read-only.
            builder = builder
                .WithEnvironment("SSL_CERT_FILE", TrustFile)
                .WithEnvironment("SSL_CERT_DIR", ServerUnderTest.TrustMount)
                .WithBindMount(environment.Pki.CaPath, TrustFile, AccessMode.ReadOnly);
            applied.AddRange(Table.Where(r => r.Where == Scope.Server).Select(r => $"'{service.Name}': {r}"));
        }

        var container = builder.Build();

        // Any service of the file may stop at once — the server refusing its configuration, or the proxy the server it can
        // no longer name — and its exit is then the outcome the wait below reads, not a harness failure.
        await E2EEnvironment.Timings.MeasureAsync(
            $"docs profile: {service.Name}",
            () => container.StartServerAsync(environment.Docker, cancellationToken));
        return container;
    }

    /// <summary>
    /// What the started deployment did: the server's exit code if it stopped; while it runs, why the deployment is not serving
    /// (see <see cref="NotServing"/>), or neither once /readyz answered 200 the way the file lets a client in — through its
    /// proxy (<paramref name="front"/>), over TLS against the run's CA, or, where the file publishes no proxy, on the port it
    /// publishes for the server (<paramref name="direct"/>), over plain http under Host mcp.e2e.test. Each container's state is
    /// asked of the engine before each probe, the server's first: a proxy that stops because its server did is the server's
    /// outcome.
    /// </summary>
    private static async Task<(long? ExitCode, string? NotServing)> WaitForDeploymentAsync(
        E2EEnvironment environment,
        IContainer server,
        (string Service, IContainer Container, int Port)? front,
        int? direct,
        CancellationToken cancellationToken)
    {
        var docker = environment.Docker;
        HttpClient? http = null;
        Uri? readyz = null;
        string? proxyAnswered = null;
        var last = "nothing answered";
        var deadline = DateTime.UtcNow.Add(ReadyWithin);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (await DockerEngine.ExitCodeIfStoppedAsync(docker, server.Id, cancellationToken) is { } exitCode)
                {
                    return (exitCode, null);
                }

                if (front is { } watched && await DockerEngine.ExitCodeIfStoppedAsync(docker, watched.Container.Id, cancellationToken) is { } stopped)
                {
                    var (stdout, stderr) = await watched.Container.GetLogsAsync(ct: cancellationToken);
                    return (null, $"its proxy, '{watched.Service}', exited with code {stopped}. The proxy's log: {(stdout + stderr).Trim()}");
                }

                // Asked of containers not seen to have stopped: one that has exited has no port mapped.
                if (http is null)
                {
                    (http, readyz) = front is { } through
                        ? (NamesThrough(environment.Names, through.Container, through.Port).CreateClient(), new Uri($"https://{TlsFront.Host}/readyz"))
                        : (new HttpClient(new SocketsHttpHandler { UseProxy = false }), new Uri($"http://{server.Hostname}:{server.GetMappedPublicPort(direct!.Value)}/readyz"));
                    http.Timeout = TimeSpan.FromSeconds(5);
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, readyz);
                    if (front is null)
                    {
                        // Directly on the published port, with the one Host the server allows: host filtering runs first.
                        request.Headers.Host = TlsFront.Host;
                    }

                    using var response = await http.SendAsync(request, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return (null, null);
                    }

                    last = $"{(int)response.StatusCode} {response.StatusCode}";
                    proxyAnswered = front is null ? null : $"answered /readyz with {last}, and never with the server's 200";
                }
                catch (HttpRequestException ex) when (front is not null && ex.InnerException is AuthenticationException tls)
                {
                    // The certificate the proxy serves is the document's choice (its nginx.conf names it): one the run's CA did
                    // not sign for mcp.e2e.test is not the one the file mounts for it, which the certificate rows replace.
                    last = tls.Message;
                    proxyAnswered = "serves a certificate that is not the one the file mounts for it: it did not validate against "
                        + $"the run's test CA for {TlsFront.Host} ({tls.Message})";
                }
                catch (HttpRequestException ex)
                {
                    // Not listening yet.
                    last = ex.Message;
                    proxyAnswered = null;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A probe that timed out; try again.
                    last = "a probe timed out";
                    proxyAnswered = null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        finally
        {
            http?.Dispose();
        }

        // A proxy that still answered, when a server has had as long as it may take to start, with something other than the
        // server's ready — another status, or a certificate the run's CA did not sign — is the file's proxy not serving its
        // server. Nothing answering at all names no cause, and is not the document's to carry.
        if (front is { } answering && proxyAnswered is { } why)
        {
            return (null, $"its proxy, '{answering.Service}', {why} ({ReadyWithin.TotalSeconds:F0} s after it started).");
        }

        var (_, lastStderr) = await server.GetLogsAsync(ct: cancellationToken);
        throw new InvalidOperationException(
            $"The documented deployment did not answer /readyz within {ReadyWithin.TotalSeconds:F0} s "
            + (front is { } unanswered ? $"through its proxy, '{unanswered.Service}'" : "on the port the file publishes for the server")
            + $" (last: {last}). The server's stderr:{Environment.NewLine}{lastStderr}");
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (DOC-R1-01) — whether the documented server takes forwarded headers from the documented
    /// proxy: the proxy's address on the run's network, as the engine gave it, is one the server was started trusting, among
    /// its HttpTransport:KnownProxies or inside its :KnownNetworks, as the engine records its environment. Both are the file's,
    /// through the table. A file whose two disagreed would still start and list tools, while the server took every client's
    /// address and scheme to be the proxy's own, over plain http: that is not the deployment the document describes. Null
    /// when it does; otherwise why not.
    /// </summary>
    private static async Task<string?> ProxyNotTrustedAsync(
        E2EEnvironment environment, string service, IContainer proxy, IContainer server, CancellationToken cancellationToken)
    {
        var docker = environment.Docker;
        var address = await ServerUnderTest.AddressOnRunNetworkAsync(docker, environment.Network.Name, proxy.Id, cancellationToken)
            ?? throw new EnvironmentFaultException("docs profile: proxy", $"the proxy '{service}' has no address on the run's network {environment.Network.Name}.");

        var started = await docker.Containers.InspectContainerAsync(server.Id, cancellationToken);
        var trusted = (started.Config?.Env ?? [])
            .Select(v => v.Split('=', 2))
            .Where(v => v.Length == 2 && ServerUnderTest.TrustedProxySetting().IsMatch(v[0]))
            .Select(v => (Name: v[0], Value: v[1]))
            .ToList();

        return trusted.Any(t => ServerUnderTest.Trusts(t.Name, t.Value, address))
            ? null
            : $"it does not take forwarded headers from its proxy: '{service}' is at {address} on the run's network, and the server "
                + $"was started trusting [{string.Join(", ", trusted.Select(t => $"{t.Name}={t.Value}"))}] as proxies, so it would take "
                + "every client's address and scheme to be the proxy's own.";
    }

    /// <summary>The environment's name map, with mcp.e2e.test:443 sent to what <paramref name="front"/> publishes of <paramref name="port"/>.</summary>
    private static NameMap NamesThrough(NameMap names, IContainer front, int port) =>
        names.With(TlsFront.Host, TlsFront.Port, front.Hostname, front.GetMappedPublicPort(port));

    /// <summary>contract-005 · G-17 round 2 (CON-R2-04) — an image reference pinned by digest: it ends in @sha256: and 64 hex digits.</summary>
    [GeneratedRegex("@sha256:[0-9a-f]{64}$")]
    private static partial Regex PinnedByDigest();

    /// <summary>
    /// contract-005 · G-4, G-17 round 1 (DOC-R1-01) — the file's networks, as the run runs them: a run has one network, so the
    /// file may define one, whose subnet — when it names one — the network row has made the run's, and on which every
    /// address the file fixes is one a row has put on the run's network. No network at all is Compose's default one, which
    /// the run's stands for as well. Returns the file's network's name, or null when it defines none; anything else is
    /// refused, naming it, since the run could not keep what the file keeps apart.
    /// </summary>
    private static string? RunNetworkFor(List<Compose.Network> networks, IReadOnlyList<Compose.Service> services)
    {
        if (networks.Count > 1)
        {
            throw new InvalidOperationException(
                $"The compose file defines {networks.Count} networks ({string.Join(", ", networks.Select(n => n.Name))}); a run has one "
                + "(G-4), which the docs profile runs the file's one network as, and could not keep them apart.");
        }

        var network = networks.SingleOrDefault();
        if (network is { Subnet: { } subnet } && subnet != E2ENetwork.Subnet)
        {
            throw new InvalidOperationException(
                $"The compose file's network '{network.Name}' is {subnet}, which no row of the table maps onto the run's network "
                + $"({E2ENetwork.Subnet}, G-4).");
        }

        foreach (var service in services)
        {
            foreach (var (joined, address) in service.Networks)
            {
                if (address is null)
                {
                    continue;
                }

                if (network?.Subnet is null)
                {
                    throw new InvalidOperationException(
                        $"The '{service.Name}' service is fixed at {address} on '{joined}', a network that names no subnet: Compose "
                        + "fixes an address only on a network with one.");
                }

                if (address != E2ENetwork.DocsProxy.ToString())
                {
                    throw new InvalidOperationException(
                        $"The '{service.Name}' service is fixed at {address} on '{joined}', which no row of the table puts on the run's "
                        + "network: an address the harness hands out, or none of the run's, could not be given it (G-4).");
                }
            }
        }

        return network?.Name;
    }

    /// <summary>
    /// The services in the order Compose starts them: each after the services it depends on, and otherwise in the file's
    /// order. A dependency the file does not define, or a cycle, is refused: Compose would start neither.
    /// </summary>
    private static List<Compose.Service> InStartOrder(IReadOnlyList<Compose.Service> services)
    {
        var ordered = new List<Compose.Service>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Visit(Compose.Service service)
        {
            if (ordered.Any(s => s.Name == service.Name))
            {
                return;
            }

            if (!visiting.Add(service.Name))
            {
                throw new InvalidOperationException($"The compose file's services depend on each other in a cycle through '{service.Name}'.");
            }

            foreach (var dependency in service.DependsOn)
            {
                Visit(services.FirstOrDefault(s => s.Name == dependency)
                    ?? throw new InvalidOperationException($"The '{service.Name}' service depends on '{dependency}', which the compose file does not define."));
            }

            visiting.Remove(service.Name);
            ordered.Add(service);
        }

        foreach (var service in services)
        {
            Visit(service);
        }

        return ordered;
    }

    /// <summary>
    /// The service the file publishes on the host's 443, where https://${MCP_HOST}/mcp is dialled, with the container port it
    /// publishes there; or null when it publishes nothing there.
    /// </summary>
    private static (string Service, int Port)? WayIn(IReadOnlyList<Compose.Service> services)
    {
        var ways = services
            .SelectMany(s => s.Ports.Where(p => HostPortOf(p, s.Name) == TlsFront.Port).Select(p => (Service: s.Name, Port: PortOf(p, s.Name))))
            .ToList();
        return ways.Count switch
        {
            0 => null,
            1 => ways[0],
            _ => throw new InvalidOperationException(
                $"The compose file publishes the host's {TlsFront.Port} {ways.Count} times ({string.Join(", ", ways.Select(w => w.Service))}); "
                + "a host port is published once."),
        };
    }

    /// <summary>The container side of the one port the file publishes for the server, or null when it publishes none.</summary>
    private static int? ServerPort(Compose.Service server) => server.Ports.Count switch
    {
        0 => null,
        1 => PortOf(server.Ports[0], server.Name),
        _ => throw new InvalidOperationException(
            $"The '{server.Name}' service publishes {server.Ports.Count} ports; the docs profile reaches it on one, and the document names which only by publishing one."),
    };

    /// <summary>The container port of a Compose port mapping, "HOST:CONTAINER" or "CONTAINER".</summary>
    private static int PortOf(string mapping, string service)
    {
        var container = mapping[(mapping.LastIndexOf(':') + 1)..];
        return int.TryParse(container, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new InvalidOperationException($"The '{service}' service publishes '{mapping}', whose container port the docs profile cannot read.");
    }

    /// <summary>The host port of a Compose port mapping, "HOST:CONTAINER" or "IP:HOST:CONTAINER", or null for "CONTAINER" alone.</summary>
    private static int? HostPortOf(string mapping, string service)
    {
        var parts = mapping.Split(':');
        if (parts.Length == 1)
        {
            return null;
        }

        return int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new InvalidOperationException($"The '{service}' service publishes '{mapping}', whose host port the docs profile cannot read.");
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (DOC-R1-01) — one read-only mount of the file, as the run makes it: the source and the
    /// container path. A file the document gives beside the compose file is that file, as written, with the Text rows
    /// applied; a file it does not give is a row's, in its place (the proxy's certificate and key); anything else — a named
    /// volume, a path outside the compose file's directory, a mount the container may write — is refused, naming it.
    /// </summary>
    private static (string Source, string Target) MountOf(E2EEnvironment environment, string document, string service, string volume, List<string> applied)
    {
        var parts = volume.Split(':');
        if (parts is not [var source, var target, "ro"] || !source.StartsWith("./", StringComparison.Ordinal) || source.Length <= 2
            || source.Contains("..", StringComparison.Ordinal) || !target.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"The '{service}' service mounts '{volume}', which the docs profile does not run: it runs a read-only mount of a file "
                + "beside the compose file, ./NAME:/PATH:ro, and refuses anything else rather than run it otherwise.");
        }

        if (Table.FirstOrDefault(r => r.Where == Scope.Mount && r.Document == source) is { } row)
        {
            applied.Add($"'{service}': {row}");
            var leaf = environment.Pki.LeafDirectory(FrontLeaf);
            return (Path.Combine(leaf, row.Document == ProxyKey ? TestPki.KeyFile : TestPki.CertificateFile), target);
        }

        var name = source[2..];
        var given = FileBesideComposeIn(document, name)
            ?? throw new InvalidOperationException(
                $"The '{service}' service mounts '{source}', which the document does not give (no fenced block after a line that opens "
                + $"with **{name}**, below the compose file) and no row of the table declares.");

        var (file, rows) = Substituted(given, $"'{service}' mounts '{source}': ");
        applied.Add($"'{service}' mounts '{source}': the document's own, the block after **{name}**, as written");
        applied.AddRange(rows);
        return (environment.Pki.WriteFile($"docs04-{Path.GetFileName(name)}", file), target);
    }

    /// <summary>The first YAML block after the Docker Compose heading: the document's compose file.</summary>
    private static string ComposeFileIn(string document, string path)
    {
        var marker = document.IndexOf(ComposeMarker, StringComparison.Ordinal);
        var fence = marker < 0 ? -1 : document.IndexOf("```yaml", marker, StringComparison.Ordinal);
        var body = fence < 0 ? -1 : document.IndexOf('\n', fence) + 1;
        var end = body <= 0 ? -1 : document.IndexOf("```", body, StringComparison.Ordinal);
        return end < 0
            ? throw new InvalidOperationException($"{path} has no compose file: no ```yaml block after '{ComposeMarker}'.")
            : document[body..end];
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (DOC-R1-01) — the file <paramref name="name"/> as the document gives it beside the compose
    /// file: the first fenced block after the first line, below the compose file, that opens with **NAME** (docs/04:
    /// **nginx.conf**); or null when the document gives no such file.
    /// </summary>
    private static string? FileBesideComposeIn(string document, string name)
    {
        var below = document.IndexOf(ComposeMarker, StringComparison.Ordinal);
        if (below < 0)
        {
            return null;
        }

        var heading = Regex.Match(document[below..], $@"^\*\*{Regex.Escape(name)}\*\*", RegexOptions.Multiline);
        if (!heading.Success)
        {
            return null;
        }

        var fence = document.IndexOf("```", below + heading.Index, StringComparison.Ordinal);
        var body = fence < 0 ? -1 : document.IndexOf('\n', fence) + 1;
        var end = body <= 0 ? -1 : document.IndexOf("```", body, StringComparison.Ordinal);
        return end < 0 ? null : document[body..end].Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// The subset of Compose the document uses, read strictly: services, each with an image and the lists environment,
    /// ports, volumes, depends_on and networks — networks as a list, or as a mapping that may fix the service's ipv4_address
    /// — and networks, each with at most one ipam config subnet. Anything else is refused, naming it, rather than run as
    /// something other than the document says.
    ///
    /// contract-005 · G-17 round 1 (DOC-R1-01) — volumes, depends_on, networks and the fixed address are the documented
    /// deployment's: a proxy that terminates TLS with a mounted configuration, certificate and key, the server and Redis, on
    /// the file's own network, where the proxy has the one address the server takes forwarded headers from.
    ///
    /// contract-005 · G-17 round 2 — and restart, once a service: the documented deployment restarts each of its services unless
    /// it is stopped. Read as Compose reads it (no, always, on-failure, on-failure:N, unless-stopped) and run as written, as the
    /// engine's restart policy of the service's container; a server that refuses its configuration then exits and is started
    /// again, which every wait for an exit reads as that exit (<see cref="DockerEngine.ExitCodeIfStoppedAsync"/>).
    /// </summary>
    internal static partial class Compose
    {
        /// <summary>One service of the file.</summary>
        internal sealed record Service(
            string Name,
            string Image,
            IReadOnlyList<string> Environment,
            IReadOnlyList<string> Ports,
            IReadOnlyList<string> Volumes,
            IReadOnlyList<string> DependsOn,
            IReadOnlyList<(string Network, string? Address)> Networks,
            string? Restart);

        /// <summary>One network the file defines, with its subnet when it names one.</summary>
        internal sealed record Network(string Name, string? Subnet);

        /// <summary>The file: its services and the networks it defines.</summary>
        internal sealed record Project(IReadOnlyList<Service> Services, IReadOnlyList<Network> Networks);

        /// <summary>The lists a service may give, one entry a line.</summary>
        private static readonly string[] Lists = ["environment", "ports", "volumes", "depends_on", "networks"];

        internal static Project Parse(string yaml)
        {
            var services = new List<ServiceLines>();
            var networks = new List<NetworkLines>();
            var sections = new HashSet<string>(StringComparer.Ordinal);
            string? section = null;
            string? list = null;
            var fixable = false;
            var ipam = 0;

            foreach (var raw in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = raw.TrimEnd();
                var text = line.TrimStart();
                if (text.Length == 0 || text.StartsWith('#'))
                {
                    continue;
                }

                var indent = line.Length - text.Length;
                var property = KeyValue().Match(text);
                var (key, value) = property.Success ? (property.Groups["key"].Value, property.Groups["value"].Value) : (string.Empty, string.Empty);

                // A fixed address is read only on the line after a network the service joins as a mapping entry.
                var mayFix = fixable;
                fixable = false;
                switch (indent)
                {
                    case 0 when key is "services" or "networks" && value.Length == 0 && sections.Add(key):
                        section = key;
                        list = null;
                        break;

                    case 2 when section == "services" && property.Success && value.Length == 0:
                        if (services.Any(s => s.Name == key))
                        {
                            throw new InvalidOperationException($"The compose file defines the service '{key}' twice.");
                        }

                        services.Add(new ServiceLines(key));
                        list = null;
                        break;

                    case 2 when section == "networks" && property.Success && value is "" or "{}":
                        if (networks.Any(n => n.Name == key))
                        {
                            throw new InvalidOperationException($"The compose file defines the network '{key}' twice.");
                        }

                        networks.Add(new NetworkLines(key));
                        ipam = 0;
                        break;

                    case 4 when section == "services" && services.Count > 0 && property.Success:
                        list = null;
                        if (key == "image" && value.Length > 0 && services[^1].Image is null)
                        {
                            services[^1].Image = Scalar(value);
                        }
                        else if (key == "restart" && value.Length > 0 && services[^1].Restart is null)
                        {
                            // contract-005 · G-17 round 2 — the service's restart policy, as Compose reads it; checked here, run as written.
                            services[^1].Restart = Scalar(value);
                            _ = RestartPolicyOf(services[^1].Restart!, services[^1].Name);
                        }
                        else if (Lists.Contains(key) && value.Length == 0 && services[^1].Given.Add(key))
                        {
                            list = key;
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"The compose file gives '{services[^1].Name}' '{text}', which the docs profile does not run: it runs "
                                + "image, restart and the lists environment, ports, volumes, depends_on and networks, once each, and "
                                + "refuses anything else rather than ignore it.");
                        }

                        break;

                    case 4 when section == "networks" && networks.Count > 0 && text == "ipam:" && ipam == 0:
                        ipam = 1;
                        break;

                    case 6 when section == "services" && list is not null && text.StartsWith("- ", StringComparison.Ordinal)
                        && !(list == "networks" && services[^1].NetworksAsMapping):
                        services[^1].Add(list, Scalar(text[2..]));
                        break;

                    case 6 when section == "services" && list == "networks" && property.Success && value is "" or "{}"
                        && (services[^1].NetworksAsMapping || services[^1].Networks.Count == 0):
                        services[^1].NetworksAsMapping = true;
                        services[^1].Networks.Add((key, null));
                        fixable = value.Length == 0;
                        break;

                    case 8 when section == "services" && mayFix && key == "ipv4_address" && value.Length > 0:
                        services[^1].Networks[^1] = (services[^1].Networks[^1].Network, Scalar(value));
                        break;

                    case 6 when section == "networks" && ipam == 1 && text == "config:":
                        ipam = 2;
                        break;

                    case 8 when section == "networks" && ipam == 2 && SubnetEntry().Match(text) is { Success: true } entry && networks[^1].Subnet is null:
                        networks[^1].Subnet = Scalar(entry.Groups["subnet"].Value);
                        break;

                    default:
                        throw new InvalidOperationException($"The compose file's line '{line}' is not one the docs profile reads.");
                }
            }

            if (services.Count == 0)
            {
                throw new InvalidOperationException("The compose file names no services.");
            }

            foreach (var service in services)
            {
                if (service.Networks.FirstOrDefault(n => !networks.Any(d => d.Name == n.Network)) is { Network: { } joined })
                {
                    throw new InvalidOperationException(
                        $"The '{service.Name}' service joins the network '{joined}', which the compose file does not define.");
                }
            }

            return new Project(
                [.. services.Select(s => new Service(
                    s.Name,
                    s.Image ?? throw new InvalidOperationException($"The compose file's '{s.Name}' service names no image."),
                    s.Environment,
                    s.Ports,
                    s.Volumes,
                    s.DependsOn,
                    s.Networks,
                    s.Restart))],
                [.. networks.Select(n => new Network(n.Name, n.Subnet))]);
        }

        /// <summary>
        /// contract-005 · G-17 round 2 — a Compose restart value as the engine's restart policy: no, always, unless-stopped,
        /// on-failure, or on-failure:N with N retries. Any other is refused, naming the service, as Compose would refuse it.
        /// </summary>
        internal static RestartPolicy RestartPolicyOf(string value, string service)
        {
            ArgumentNullException.ThrowIfNull(value);

            var parts = value.Split(':');
            return (parts[0], parts.Length) switch
            {
                ("no", 1) => new RestartPolicy { Name = RestartPolicyKind.No },
                ("always", 1) => new RestartPolicy { Name = RestartPolicyKind.Always },
                ("unless-stopped", 1) => new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                ("on-failure", 1) => new RestartPolicy { Name = RestartPolicyKind.OnFailure },
                ("on-failure", 2) when long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var retries) && retries > 0 =>
                    new RestartPolicy { Name = RestartPolicyKind.OnFailure, MaximumRetryCount = retries },
                _ => throw new InvalidOperationException(
                    $"The '{service}' service restarts '{value}', which is no restart policy Compose runs: no, always, on-failure, "
                    + "on-failure:N or unless-stopped."),
            };
        }

        /// <summary>
        /// Compose's interpolation: $$ is a dollar; ${NAME}, ${NAME:-default}, ${NAME-default}, ${NAME:?error},
        /// ${NAME?error}, ${NAME:+other} and ${NAME+other} as Compose reads them, and $NAME. A variable the table does not
        /// set is refused rather than read as empty, and a required one Compose would stop on stops the run, with its words.
        /// </summary>
        internal static string Interpolate(string value, IReadOnlyDictionary<string, Substitution> rows, List<string> applied)
        {
            var variables = rows.ToDictionary(r => r.Key, r => r.Value.Run, StringComparer.Ordinal);
            var result = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '$' || i + 1 >= value.Length)
                {
                    result.Append(value[i]);
                    continue;
                }

                if (value[i + 1] == '$')
                {
                    result.Append('$');
                    i++;
                    continue;
                }

                string name;
                string? operation = null;
                var argument = string.Empty;
                if (value[i + 1] == '{')
                {
                    var close = value.IndexOf('}', i + 2);
                    var inner = close < 0 ? null : value[(i + 2)..close];
                    if (inner is null || inner.Contains("${", StringComparison.Ordinal) || Braced().Match(inner) is not { Success: true } braced)
                    {
                        throw new InvalidOperationException($"'{value}' holds an interpolation the docs profile does not read.");
                    }

                    name = braced.Groups["name"].Value;
                    operation = braced.Groups["op"].Success ? braced.Groups["op"].Value : null;
                    argument = braced.Groups["arg"].Value;
                    i = close;
                }
                else if (char.IsAsciiLetter(value[i + 1]) || value[i + 1] == '_')
                {
                    var end = i + 1;
                    while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] == '_'))
                    {
                        end++;
                    }

                    name = value[(i + 1)..end];
                    i = end - 1;
                }
                else
                {
                    result.Append('$');
                    continue;
                }

                var set = variables.TryGetValue(name, out var variable);
                var empty = !set || variable!.Length == 0;
                result.Append(operation switch
                {
                    null when set => variable,
                    ":-" => empty ? argument : variable,
                    "-" => set ? variable : argument,
                    ":?" when !empty => variable,
                    "?" when set => variable,
                    ":?" or "?" => throw new InvalidOperationException($"Compose would stop on '{value}': {argument}"),
                    ":+" => empty ? string.Empty : argument,
                    "+" => set ? argument : string.Empty,
                    _ => throw new InvalidOperationException(
                        $"The compose file uses ${{{name}}}, which the docs profile's table does not set: Compose would read it as empty."),
                });

                if (set && !applied.Contains(rows[name].ToString()))
                {
                    applied.Add(rows[name].ToString());
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// A YAML scalar as it stands in a line: quoted, with its quotes taken off, or plain, up to a comment. A plain
        /// scalar holding ": " would be a mapping, not a string, so it is refused rather than misread.
        /// </summary>
        private static string Scalar(string text)
        {
            text = text.Trim();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                return text[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            }

            if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
            {
                return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            var comment = text.IndexOf(" #", StringComparison.Ordinal);
            var plain = (comment < 0 ? text : text[..comment]).TrimEnd();
            return plain.Contains(": ", StringComparison.Ordinal) || plain.EndsWith(':')
                ? throw new InvalidOperationException($"'{plain}' is a mapping in YAML, not a string; the docs profile does not read it as one.")
                : plain;
        }

        [GeneratedRegex(@"^(?<key>[A-Za-z_][A-Za-z0-9_-]*):(?:\s+(?<value>.*))?$")]
        private static partial Regex KeyValue();

        [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>:\?|\?|:-|-|:\+|\+)(?<arg>.*))?$")]
        private static partial Regex Braced();

        /// <summary>contract-005 · G-17 round 1 (DOC-R1-01) — a network's one ipam config entry: its subnet.</summary>
        [GeneratedRegex(@"^- subnet:\s+(?<subnet>\S.*)$")]
        private static partial Regex SubnetEntry();

        /// <summary>A service as its lines are read.</summary>
        private sealed class ServiceLines(string name)
        {
            public string Name { get; } = name;

            public string? Image { get; set; }

            /// <summary>contract-005 · G-17 round 2 — its restart policy, as written; given once.</summary>
            public string? Restart { get; set; }

            /// <summary>The lists given so far: each may be given once.</summary>
            public HashSet<string> Given { get; } = new(StringComparer.Ordinal);

            public List<string> Environment { get; } = [];

            public List<string> Ports { get; } = [];

            public List<string> Volumes { get; } = [];

            public List<string> DependsOn { get; } = [];

            public List<(string Network, string? Address)> Networks { get; } = [];

            /// <summary>Whether its networks are a mapping, which may fix an address, rather than a list: Compose takes one form.</summary>
            public bool NetworksAsMapping { get; set; }

            public void Add(string list, string entry)
            {
                switch (list)
                {
                    case "environment":
                        Environment.Add(entry);
                        break;
                    case "ports":
                        Ports.Add(entry);
                        break;
                    case "volumes":
                        Volumes.Add(entry);
                        break;
                    case "depends_on":
                        DependsOn.Add(entry);
                        break;
                    default:
                        Networks.Add((entry, null));
                        break;
                }
            }
        }

        /// <summary>A network of the file as its lines are read.</summary>
        private sealed class NetworkLines(string name)
        {
            public string Name { get; } = name;

            public string? Subnet { get; set; }
        }
    }

    /// <summary>
    /// The name the compose file gives the server's image, pointed at this run's build for the run, and put back after it:
    /// removed when it named nothing before, or pointed again at what it named, so a developer's own image of that name
    /// is theirs again once the run ends.
    /// </summary>
    private sealed class RetaggedImage
    {
        private readonly IDockerClient _docker;
        private readonly string _name;
        private readonly string? _before;

        private RetaggedImage(IDockerClient docker, string name, string? before)
        {
            _docker = docker;
            _name = name;
            _before = before;
        }

        public static async Task<RetaggedImage> TagAsync(IDockerClient docker, string built, string name, CancellationToken cancellationToken)
        {
            var slash = name.LastIndexOf('/');
            var colon = name.LastIndexOf(':');
            var (repository, tag) = colon > slash ? (name[..colon], name[(colon + 1)..]) : (name, "latest");
            if (name.Contains('@', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The compose file names the server's image by digest ('{name}'); a digest cannot be given to this run's build.");
            }

            string? before = null;
            try
            {
                before = (await docker.Images.InspectImageAsync(name, cancellationToken)).ID;
            }
            catch (DockerImageNotFoundException)
            {
                // Nothing of that name on this machine: the run's tag is removed after it.
            }

            await docker.Images.TagImageAsync(built, new ImageTagParameters { RepositoryName = repository, Tag = tag }, cancellationToken);
            return new RetaggedImage(docker, $"{repository}:{tag}", before);
        }

        public async Task RestoreAsync()
        {
            try
            {
                if (_before is null)
                {
                    await _docker.Images.DeleteImageAsync(_name, new ImageDeleteParameters { NoPrune = true });
                }
                else
                {
                    var colon = _name.LastIndexOf(':');
                    await _docker.Images.TagImageAsync(_before, new ImageTagParameters { RepositoryName = _name[..colon], Tag = _name[(colon + 1)..] });
                }
            }
            catch (DockerApiException)
            {
                // Best effort, as every teardown step: the tag is a name, and the run's result does not depend on it.
            }
        }
    }
}
