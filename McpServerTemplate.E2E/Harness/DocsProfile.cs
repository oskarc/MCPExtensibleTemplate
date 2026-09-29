using System.Globalization;
using System.Net;
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
/// docs/04's Docker Compose file — the production configuration it deploys: ASPNETCORE_ENVIRONMENT=Production, the
/// image's own appsettings.Production.json, and the environment the file sets — is read from the document itself, at
/// the path the test is given, and run verbatim through one declared table (<see cref="Table"/>). The table has two
/// kinds of row, each with its reason: host names and addresses (the identity provider, the resource, the proxy, and
/// where the server is published), and test-trust additions (SSL_CERT_FILE, SSL_CERT_DIR and the read-only mount of the
/// run's CA). Nothing else about the file changes. Its image, mcp-server:latest, is this run's server image, tagged with
/// that name for the run and untagged after it (a tag it replaced is put back).
///
/// Each service runs as Compose runs it: its image, its environment, on the network under its service name. The network
/// is the run's (G-4), which the proxy row follows; the containers carry the run's label, which no server reads. The
/// server is ready when /readyz answers on its published port (G-8), and the TLS front (G-9) is the proxy the document
/// tells an operator to put in front of it.
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

    /// <summary>Where the run's CA is mounted in the server container, as for every server of the run (G-8).</summary>
    private static readonly string TrustFile = $"{ServerUnderTest.TrustMount}/{TestPki.CaFile}";

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
        string stderr,
        TlsFront? front)
    {
        _environment = environment;
        Document = document;
        Applied = applied;
        _services = services;
        _image = image;
        ExitCode = exitCode;
        Stderr = stderr;
        Front = front;
        Names = front is null ? environment.Names : environment.Names.With(TlsFront.Host, TlsFront.Port, front.Published.Host, front.Published.Port);
    }

    /// <summary>Where a row of the table acts.</summary>
    public enum Scope
    {
        /// <summary>On the compose file's text: every occurrence of what the document says.</summary>
        Text,

        /// <summary>A variable the file asks Compose for, by its name: the value Compose interpolates.</summary>
        Variable,

        /// <summary>The host side of every port a service publishes.</summary>
        PublishedPorts,

        /// <summary>Added to the server's service: an environment variable, or a mount.</summary>
        Server,
    }

    /// <summary>One declared row.</summary>
    /// <param name="Kind"><see cref="HostOrAddress"/> or <see cref="TestTrust"/>.</param>
    /// <param name="Where">Where it acts.</param>
    /// <param name="Document">What the document says, a variable's name, or what the run adds.</param>
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
            _ => $"{Kind}: added {Document} = {Run} — {Reason}",
        };
    }

    /// <summary>
    /// The declared table (G-13): every way the run's compose file differs from the document's, of two kinds, each with
    /// its reason. The run reports which rows acted on the document it read (<see cref="Applied"/>).
    /// </summary>
    public static IReadOnlyList<Substitution> Table { get; } =
    [
        new(HostOrAddress, Scope.Text, "https://login.example.com/realms/corp", KeycloakService.Issuer,
            "The identity provider. The environment's real identity provider is Keycloak (G-5), whose realm is mcp and whose "
            + "issuer is pinned to this URL; the name the server gives it, corp, is kept."),
        new(HostOrAddress, Scope.Text, "mcp.example.com", TlsFront.Host,
            "The resource's host, where the document writes it out: the name the TLS front serves with a leaf of the run's CA (G-9)."),
        new(HostOrAddress, Scope.Variable, "MCP_HOST", TlsFront.Host,
            "The resource's host, where the file asks Compose for it, and stops without it: the same name."),
        new(HostOrAddress, Scope.Text, "172.16.0.0/12", E2ENetwork.Subnet,
            "The proxy. The documented network is the range Docker gives Compose's own network, where the proxy that terminates "
            + "TLS sits; a run has one network, G-4's slice of TEST-NET-2, where the front sits (G-9) and the services join it."),
        new(HostOrAddress, Scope.PublishedPorts, "the host side of a published port", "127.0.0.1, at a port Docker chooses",
            "Where the server is published on this machine. Nothing a run starts is published beyond loopback, and a fixed host "
            + "port can be held by another process; the server's own port, the container side, is unchanged."),
        new(TestTrust, Scope.Server, "SSL_CERT_FILE", TrustFile,
            "The identity provider's certificate is a leaf of the run's test CA (G-3), which no image trusts. Trust is that CA "
            + "alone, as for every server of the run (G-8), so a call to a host with no fake fails TLS instead of reaching the internet."),
        new(TestTrust, Scope.Server, "SSL_CERT_DIR", ServerUnderTest.TrustMount,
            "Both are set, or the runtime falls back to the image's own store for the other and trusts more than the run's CA."),
        new(TestTrust, Scope.Server, "a read-only mount", $"the run's CA at {TrustFile}",
            "The CA certificate alone, by read-only bind mount; no key of any kind (G-3)."),
    ];

    /// <summary>The document this run read.</summary>
    public string Document { get; }

    /// <summary>What the table changed in this document, row by row, and what it added: the run's account of itself.</summary>
    public IReadOnlyList<string> Applied { get; }

    /// <summary>The server's exit code, or null when it came up and answered /readyz.</summary>
    public long? ExitCode { get; }

    /// <summary>Whether the server came up.</summary>
    public bool Started => ExitCode is null;

    /// <summary>The server's standard error as it stood when it came up or exited.</summary>
    public string Stderr { get; }

    /// <summary>The TLS front in front of the server, when it came up.</summary>
    public TlsFront? Front { get; }

    /// <summary>The environment's name map, with mcp.e2e.test sent to this profile's front.</summary>
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

    /// <summary>An HTTP client through this profile's name map, forwarding <paramref name="clientAddress"/> through the front.</summary>
    public HttpClient CreateClient(string clientAddress) => Names.CreateClient(clientAddress: clientAddress);

    /// <summary>What happened, in one line, for a failure message.</summary>
    public string Describe() =>
        Started
            ? $"the documented server ({Document}) started, and /readyz answered 200 under Host {TlsFront.Host}"
            : $"the documented server ({Document}) exited with code {ExitCode}; "
                + (StartupOutcome.RefusalIn(Stderr) ?? "its stderr names no refusal: " + Stderr.Trim());

    public async ValueTask DisposeAsync() => await TearDownAsync(_environment, Document, Applied, _services, Front, _image);

    /// <summary>
    /// The diagnostics bundle's docs-profile directory — the table as it acted, each service's log and the front's — then
    /// every container removed and the image's name put back. The same whether the profile came up or its start failed.
    /// </summary>
    private static async Task TearDownAsync(
        E2EEnvironment environment,
        string document,
        IReadOnlyList<string> applied,
        IReadOnlyList<(string Service, IContainer Container)> services,
        TlsFront? front,
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

            if (front is not null)
            {
                var (frontOut, frontErr) = await front.Container.GetLogsAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, "front.log"), frontOut + frontErr);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DockerApiException)
        {
            // Diagnostics are best effort; the failure they would explain is already on its way.
        }

        try
        {
            if (front is not null)
            {
                await front.DisposeAsync();
            }

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
        var compose = ComposeFileIn(text, documentPath);

        var applied = new List<string>();
        foreach (var row in Table.Where(r => r.Where == Scope.Text))
        {
            var count = Regex.Matches(compose, Regex.Escape(row.Document)).Count;
            if (count > 0)
            {
                compose = compose.Replace(row.Document, row.Run, StringComparison.Ordinal);
                applied.Add($"{row} ({count}x)");
            }
        }

        var variables = Table.Where(r => r.Where == Scope.Variable).ToDictionary(r => r.Document, StringComparer.Ordinal);
        var services = Compose.Parse(compose)
            .Select(s => s with
            {
                Image = Compose.Interpolate(s.Image, variables, applied),
                Environment = [.. s.Environment.Select(e => Compose.Interpolate(e, variables, applied))],
                Ports = [.. s.Ports.Select(p => Compose.Interpolate(p, variables, applied))],
            })
            .ToList();

        var server = services.FirstOrDefault(s => s.Name == ServerService)
            ?? throw new InvalidOperationException(
                $"The compose file in {documentPath} has no '{ServerService}' service: nothing in it runs the server's image.");

        // "For that run the image is tagged with the name the compose file uses" (G-13).
        var image = await RetaggedImage.TagAsync(environment.Docker, environment.ServerImage, server.Image, cancellationToken);
        applied.Add($"image: '{server.Image}' is this run's server image ({environment.ServerImage}), tagged with that name for the run");

        var started = new List<(string Service, IContainer Container)>();
        TlsFront? front = null;
        try
        {
            foreach (var service in services)
            {
                started.Add((service.Name, await StartServiceAsync(environment, service, service == server, applied, cancellationToken)));
            }

            var container = started.Single(s => s.Service == ServerService).Container;
            var (exitCode, _) = await E2EEnvironment.Timings.MeasureAsync("docs profile: server ready", () =>
                ServerUnderTest.WaitForStartupAsync(environment.Docker, container, [HttpStatusCode.OK], cancellationToken));

            if (exitCode is null)
            {
                await E2EEnvironment.Timings.MeasureEnvironmentAsync("docs profile: trust self-check", () =>
                    ServerUnderTest.CheckTrustAsync(environment, container, cancellationToken));

                var port = ContainerPort(server);
                front = await E2EEnvironment.Timings.MeasureEnvironmentAsync("docs profile: front", () =>
                    TlsFront.StartAsync(environment.Network, environment.Pki, E2ENetwork.AllocateStatic(), ServerService, port, environment.RunId, cancellationToken));
            }

            var (_, stderr) = await container.GetLogsAsync(ct: cancellationToken);
            return new DocsProfile(environment, documentPath, applied, started, image, exitCode, stderr, front);
        }
        catch
        {
            await TearDownAsync(environment, documentPath, applied, started, front, image);
            throw;
        }
    }

    /// <summary>
    /// One service, as Compose runs it: its image, its environment, on the network under its own name. The server also
    /// gets the trust rows; every published port the address row.
    /// </summary>
    private static async Task<IContainer> StartServiceAsync(
        E2EEnvironment environment, Compose.Service service, bool isServer, List<string> applied, CancellationToken cancellationToken)
    {
        var builder = new ContainerBuilder(service.Image)
            .WithNetwork(environment.Network)
            .WithNetworkAliases(service.Name)
            .WithLabel(E2ENetwork.RunLabel, environment.RunId);

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

        if (isServer)
        {
            // The trust rows (TestTrust): the two variables, and the CA they name, mounted read-only.
            builder = builder
                .WithEnvironment("SSL_CERT_FILE", TrustFile)
                .WithEnvironment("SSL_CERT_DIR", ServerUnderTest.TrustMount)
                .WithBindMount(environment.Pki.CaPath, TrustFile, AccessMode.ReadOnly);
            applied.AddRange(Table.Where(r => r.Where == Scope.Server).Select(r => $"'{service.Name}': {r}"));
        }

        var container = builder.Build();
        await E2EEnvironment.Timings.MeasureAsync($"docs profile: {service.Name}", () => container.StartAsync(cancellationToken));
        return container;
    }

    /// <summary>The container side of the server's one published port: where the front sends what it proxies.</summary>
    private static int ContainerPort(Compose.Service server) =>
        server.Ports.Count == 1
            ? PortOf(server.Ports[0], server.Name)
            : throw new InvalidOperationException(
                $"The '{server.Name}' service publishes {server.Ports.Count} ports; the front proxies to one, and the document names which only by publishing one.");

    /// <summary>The container port of a Compose port mapping, "HOST:CONTAINER" or "CONTAINER".</summary>
    private static int PortOf(string mapping, string service)
    {
        var container = mapping[(mapping.LastIndexOf(':') + 1)..];
        return int.TryParse(container, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : throw new InvalidOperationException($"The '{service}' service publishes '{mapping}', whose container port the docs profile cannot read.");
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
    /// The subset of Compose the document uses, read strictly: services, each with an image, an environment list and a
    /// ports list. Anything else is refused, naming it, rather than run as something other than the document says.
    /// </summary>
    internal static partial class Compose
    {
        /// <summary>One service of the file.</summary>
        internal sealed record Service(string Name, string Image, IReadOnlyList<string> Environment, IReadOnlyList<string> Ports);

        internal static IReadOnlyList<Service> Parse(string yaml)
        {
            var services = new List<(string Name, string? Image, List<string> Environment, List<string> Ports)>();
            string? list = null;
            var inServices = false;

            foreach (var raw in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = raw.TrimEnd();
                var text = line.TrimStart();
                if (text.Length == 0 || text.StartsWith('#'))
                {
                    continue;
                }

                var indent = line.Length - text.Length;
                switch (indent)
                {
                    case 0 when text == "services:":
                        inServices = true;
                        break;

                    case 2 when inServices && text.EndsWith(':') && !text.Contains(' ', StringComparison.Ordinal):
                        services.Add((text[..^1], null, [], []));
                        list = null;
                        break;

                    case 4 when services.Count > 0 && KeyValue().Match(text) is { Success: true } property:
                        var (key, value) = (property.Groups["key"].Value, property.Groups["value"].Value);
                        list = null;
                        switch (key)
                        {
                            case "image" when value.Length > 0:
                                services[^1] = services[^1] with { Image = Scalar(value) };
                                break;
                            case "environment" or "ports" when value.Length == 0:
                                list = key;
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"The compose file gives '{services[^1].Name}' '{text}', which the docs profile does not run: it runs "
                                    + "image, an environment list and a ports list, and refuses anything else rather than ignore it.");
                        }

                        break;

                    case 6 when list is not null && text.StartsWith("- ", StringComparison.Ordinal):
                        (list == "environment" ? services[^1].Environment : services[^1].Ports).Add(Scalar(text[2..]));
                        break;

                    default:
                        throw new InvalidOperationException($"The compose file's line '{line}' is not one the docs profile reads.");
                }
            }

            return services.Count == 0
                ? throw new InvalidOperationException("The compose file names no services.")
                : [.. services.Select(s => new Service(
                    s.Name,
                    s.Image ?? throw new InvalidOperationException($"The compose file's '{s.Name}' service names no image."),
                    s.Environment,
                    s.Ports))];
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
