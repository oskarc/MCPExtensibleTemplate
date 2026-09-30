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
///
/// contract-005 · T-6 — the pioneer's decision of 2026-09-29 ("2. yes, cut it and mock any calls") and T-6's own clauses ask
/// three more things of it. Its record keeps each request whole — every header as it arrived, and its body — as WireMock's
/// journal does, so "no journal entry anywhere in the run contains the caller's token in any header, URL or body" holds for
/// this owner too (<see cref="WholeRecordsAsync"/>): nginx's own variables name headers one by one, so the record is written
/// by nginx's JavaScript module (njs, which the pinned image carries), from the request's raw headers and body. Its answer
/// reads as a station list with one active station, so an observations tool goes on to its own data request: SmhiObs keeps
/// a station list for hours, and an empty one would have the next tool of the same parameter ask nothing any fake could see.
/// And it closes every connection after its answer, so a server's next call to it looks its name up again: T-6 removes this
/// name from the network (<see cref="E2EEnvironment.WithoutNameAsync"/>), and a kept connection would still reach it.
/// </summary>
public sealed partial class StandInUpstream : IUpstreamService
{
    /// <summary>
    /// The answer to every request: a JSON document naming what gave it, which also reads as SMHI's station list with one
    /// active station (contract-005 · T-6).
    /// </summary>
    public const string Answer =
        """{"answeredBy":"e2e-stand-in","station":[{"id":1,"name":"e2e stand-in","latitude":59.33,"longitude":18.07,"active":true}]}""";

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
    public async Task<IReadOnlyList<UpstreamRequest>> RecordedAsync(HttpClient http, RequestSource from, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);

        // contract-005 · G-17 round 3 (HAR-R3-04) — the requests from made, and no others; read whole first, as before, so the wait
        // below is for the record to hold anything at all, not for this server's first request.
        return [.. (await EveryRequestAsync(cancellationToken)).Where(from.Made)];
    }

    /// <summary>Every request the stand-in recorded, every server's: for <see cref="RecordedAsync"/>, which a <see cref="RequestSource"/> bounds.</summary>
    private async Task<IReadOnlyList<UpstreamRequest>> EveryRequestAsync(CancellationToken cancellationToken)
    {
        // The access log follows a request a moment after it completes; a read that finds nothing looks again for up to
        // two seconds, so a request just answered is not missed.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            // contract-005 · G-17 round 2 (HAR-R2-02) — with the engine's own time for each line, which is when the request was
            // recorded: a record is one server's only from its address since it started (RequestSource), and a line whose time
            // cannot be read is a fault of the record, named, never a request left out.
            var (stdout, _) = await _container.GetLogsAsync(timestampsEnabled: true, ct: cancellationToken);
            var recorded = stdout.Split('\n')
                .Select(line => RecordLine().Match(line))
                .Where(match => match.Success)
                .Select(match => new UpstreamRequest(
                    IPAddress.TryParse(match.Groups["client"].Value, out var client) ? client : null,
                    match.Groups["method"].Value,
                    match.Groups["host"].Value,
                    match.Groups["path"].Value,
                    ServerUnderTest.EngineTime(match.Groups["at"].Value)
                        ?? throw new InvalidOperationException(
                            $"The stand-in's record line '{match.Value}' carries no time the engine wrote, so it cannot be told from an "
                            + "earlier server's at the same address.")))
                .ToList();

            if (recorded.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return recorded;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    /// <summary>
    /// contract-005 · T-6 — each request it recorded, whole: its access-log line, which holds the request line's path and query,
    /// its Host, and — as njs wrote them — every header as it arrived and the body (JSON, escaped as nginx escapes a log line).
    /// </summary>
    public async Task<IReadOnlyList<string>> WholeRecordsAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        var (stdout, _) = await _container.GetLogsAsync(timestampsEnabled: true, ct: cancellationToken);
        return [.. stdout.Split('\n').Where(line => RecordLine().IsMatch(line)).Select(line => line.Trim())];
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-06) — sent from inside the stand-in's own container, since it publishes no port the test
    /// process could reach (<see cref="E2EEnvironment.WithoutNameAsync"/> takes a name only from an owner that publishes none): a
    /// POST by the image's own wget to its witness door, a listener on the container's loopback that answers as every other of its
    /// requests is answered and records the request the same way, whole. Nothing on the run's network can reach that door.
    /// </summary>
    public async Task SendWitnessAsync(HttpClient http, string header, string body, CancellationToken cancellationToken = default)
    {
        var sent = await _container.ExecAsync(
            ["wget", "-q", "-O", "/dev/null", "--header", $"{UpstreamWitness.Header}: {header}", "--post-data", body, $"http://127.0.0.1:{WitnessPort}{UpstreamWitness.Path}"],
            cancellationToken);
        if (sent.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The stand-in's witness request, sent from inside its container with wget, exited {sent.ExitCode}: {sent.Stdout}{sent.Stderr}");
        }

        // nginx writes the record once it has answered, and the engine's log follows a moment later: wait up to three seconds for
        // the request to be recorded, as every read of the record does, so the record T-6 reads next holds it if it ever will.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !(await WholeRecordsAsync(http, cancellationToken)).Any(r => r.Contains(header, StringComparison.Ordinal)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// A record line of the access log, as the engine's log gives it back, with its time first (RFC 3339, to the nanosecond):
    /// contract-005 · G-17 round 2 (HAR-R2-02) — the time is part of the record.
    /// </summary>
    [GeneratedRegex(@"^(?<at>\d{4}-\d{2}-\d{2}T\S+Z) stand-in client=(?<client>\S+) method=(?<method>\S+) host=(?<host>\S+) path=(?<path>\S+) status=")]
    private static partial Regex RecordLine();

    /// <summary>Where the stand-in's script is mounted, beside its configuration.</summary>
    private const string ScriptPath = "/etc/nginx/standin.js";

    /// <summary>contract-005 · G-17 round 2 (HAR-R2-06) — the witness door's port, on the container's loopback alone.</summary>
    private const int WitnessPort = 8080;

    private static string Configuration() => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        # contract-005 · T-15 — the stand-in upstream: one answer to every request, and a record of each.
        # contract-005 · T-6 — the record is whole (every header, the body), written by njs, which the pinned image carries.
        load_module modules/ngx_http_js_module.so;

        worker_processes 1;
        error_log /dev/stderr notice;
        pid /tmp/nginx.pid;

        events { worker_connections 64; }

        http {
            js_import standin from {{ScriptPath}};
            js_var $e2e_request "-";

            log_format standin escape=json 'stand-in client=$remote_addr method=$request_method host=$host path=$request_uri status=$status request=$e2e_request';
            access_log /dev/stdout standin;

            # contract-005 · T-6 — no connection outlives its answer, so a server's next call looks the name up again.
            keepalive_timeout 0;

            # contract-005 · T-6 — every body it accepts is held in memory, where njs reads it for the record.
            client_max_body_size 8m;
            client_body_buffer_size 8m;
            client_body_in_single_buffer on;

            server {
                listen 443 ssl;
                server_name _;

                ssl_certificate     /e2e/tls/tls.crt;
                ssl_certificate_key /e2e/tls/tls.key;
                ssl_protocols       TLSv1.2 TLSv1.3;

                location / {
                    js_content standin.answer;
                }
            }

            # contract-005 · G-17 round 2 (HAR-R2-06) — the witness door: on the container's loopback alone, where nothing on the
            # run's network reaches, answered and recorded as every request above is. T-6 sends one request through it, from
            # inside this container, and requires the record to hold its header and its body before it searches for a token.
            server {
                listen 127.0.0.1:{{WitnessPort}};

                location / {
                    js_content standin.answer;
                }
            }
        }
        """);

    /// <summary>
    /// contract-005 · T-6 — the stand-in's content handler: it writes the request, whole, into the variable the access log
    /// records (its raw headers as they arrived, and its body), then gives <see cref="Answer"/>.
    /// </summary>
    private static string Script() => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        // contract-005, T-6 and T-15: the stand-in's one answer, and its record of each request whole.
        const ANSWER = {{System.Text.Json.JsonSerializer.Serialize(Answer)}};

        function answer(r) {
            r.variables.e2e_request = JSON.stringify({ headers: r.rawHeadersIn, body: r.requestText || '' });
            r.headersOut['Content-Type'] = 'application/json';
            r.return(200, ANSWER);
        }

        export default { answer };
        """);

    private sealed class StandInOwner() : UpstreamOwner("standin")
    {
        internal override async Task<IUpstreamService> StartAsync(UpstreamStart start, IReadOnlyList<string> hosts, CancellationToken cancellationToken)
        {
            var configuration = start.Pki.WriteFile("standin.conf", Configuration());
            var script = start.Pki.WriteFile("standin.js", Script());
            var tls = start.Pki.LeafDirectory(Name);
            var container = E2ENetwork.OnTheRunNetwork(TlsFront.Image, start.Docker, start.Network, start.RunId)
                .WithNetworkAliases([.. hosts])
                .WithBindMount(configuration, "/etc/nginx/nginx.conf", AccessMode.ReadOnly)
                .WithBindMount(script, ScriptPath, AccessMode.ReadOnly)
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
