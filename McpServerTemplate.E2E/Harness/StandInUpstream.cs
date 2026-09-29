using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · T-15 (G-16) — a stand-in fake: an upstream owner that is not WireMock, registered for a provider host
/// through the upstream registry in place of the fake, the way a later contract puts its own container in front of an
/// upstream (egress's fault proxy is the named example).
///
/// It is nginx, the image the TLS front already pins, answering over HTTPS with a leaf of the run's test CA under every
/// host name the registry gives it. Every request gets the same small JSON answer, and is recorded in its access log —
/// the address it came from, its method, its Host and its path — which is its journal: <see cref="RecordedAsync"/>
/// reads it back from the container's log, so nothing about it is taken on trust from WireMock's admin API.
/// </summary>
public sealed partial class StandInUpstream : IUpstreamService
{
    /// <summary>The answer to every request: a JSON document naming what gave it.</summary>
    public const string Answer = """{"answeredBy":"e2e-stand-in"}""";

    /// <summary>How long nginx may take to start its worker; it takes about a second.</summary>
    private static readonly TimeSpan ReadyWithin = TimeSpan.FromMinutes(1);

    private readonly IContainer _container;

    private StandInUpstream(IContainer container, NameMap names)
    {
        _container = container;
        Names = names;
    }

    /// <summary>The stand-in as an upstream owner: the registry gives it a host, and the environment starts it.</summary>
    public static UpstreamOwner Owner { get; } = new StandInOwner();

    public IContainer Container => _container;

    /// <summary>No entries of its own: the test reads its record from its log, and never calls it.</summary>
    public NameMap Names { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UpstreamRequest>> RecordedAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        // The access log follows a request a moment after it completes; a read that finds nothing looks again for up to
        // two seconds, so a request just answered is not missed.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            var (stdout, _) = await _container.GetLogsAsync(ct: cancellationToken);
            var recorded = stdout.Split('\n')
                .Select(line => RecordLine().Match(line))
                .Where(match => match.Success)
                .Select(match => new UpstreamRequest(
                    IPAddress.TryParse(match.Groups["client"].Value, out var client) ? client : null,
                    match.Groups["method"].Value,
                    match.Groups["host"].Value,
                    match.Groups["path"].Value))
                .ToList();

            if (recorded.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return recorded;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [GeneratedRegex(@"stand-in client=(?<client>\S+) method=(?<method>\S+) host=(?<host>\S+) path=(?<path>\S+) status=")]
    private static partial Regex RecordLine();

    private static string Configuration() => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        # contract-005 · T-15 — the stand-in upstream: one answer to every request, and a record of each.
        worker_processes 1;
        error_log /dev/stderr notice;
        pid /tmp/nginx.pid;

        events { worker_connections 64; }

        http {
            log_format standin 'stand-in client=$remote_addr method=$request_method host=$host path=$request_uri status=$status';
            access_log /dev/stdout standin;

            server {
                listen 443 ssl;
                server_name _;

                ssl_certificate     /e2e/tls/tls.crt;
                ssl_certificate_key /e2e/tls/tls.key;
                ssl_protocols       TLSv1.2 TLSv1.3;

                location / {
                    default_type application/json;
                    return 200 '{{Answer}}';
                }
            }
        }
        """);

    private sealed class StandInOwner() : UpstreamOwner("standin")
    {
        internal override async Task<IUpstreamService> StartAsync(UpstreamStart start, IReadOnlyList<string> hosts, CancellationToken cancellationToken)
        {
            var configuration = start.Pki.WriteFile("standin.conf", Configuration());
            var tls = start.Pki.LeafDirectory(Name);
            var container = new ContainerBuilder(TlsFront.Image)
                .WithNetwork(start.Network)
                .WithNetworkAliases([.. hosts])
                .WithLabel(E2ENetwork.RunLabel, start.RunId)
                .WithBindMount(configuration, "/etc/nginx/nginx.conf", AccessMode.ReadOnly)
                .WithBindMount(Path.Combine(tls, TestPki.CertificateFile), "/e2e/tls/tls.crt", AccessMode.ReadOnly)
                .WithBindMount(Path.Combine(tls, TestPki.KeyFile), "/e2e/tls/tls.key", AccessMode.ReadOnly)
                // contract-005 · UC-1 edge — a deadline of its own, like every other wait of the environment.
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("start worker process", wait => wait.WithTimeout(ReadyWithin)))
                .Build();

            try
            {
                await container.StartAsync(cancellationToken);
                return new StandInUpstream(container, start.Names);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string log;
                try
                {
                    var (stdout, stderr) = await container.GetLogsAsync(ct: CancellationToken.None);
                    log = stdout + stderr;
                }
                catch (Exception logFault) when (logFault is not OperationCanceledException)
                {
                    log = $"(not readable: {logFault.GetType().Name}: {logFault.Message})";
                }

                await container.DisposeAsync();
                throw new EnvironmentFaultException(Name, $"the stand-in upstream did not start ({ex.GetType().Name}: {ex.Message}). Its log: {log}", ex);
            }
        }
    }
}
