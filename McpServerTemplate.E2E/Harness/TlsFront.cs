using System.Globalization;
using System.Net;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The TLS front: nginx, the way docs/04 tells an operator to deploy the server.
///
/// contract-005 · G-9 — it holds a leaf of the run's test CA for mcp.e2e.test, terminates TLS, keeps
/// the Host header, sets X-Forwarded-Proto: https, and sets X-Forwarded-For to the client address the
/// test names in <see cref="ClientAddressHeader"/> — or, when the test names none, to the address the
/// connection really came from. That header is a declared harness device: one front standing for
/// many clients behind one proxy. The server trusts the forwarded address only because it arrives from
/// the front's static address, which is the server's only KnownProxies entry (G-8), and the header
/// itself is stripped before the request reaches the server.
///
/// Every client-facing test dials https://mcp.e2e.test through the front.
/// </summary>
public sealed class TlsFront : IAsyncDisposable
{
    /// <summary>nginx:1.30.5-alpine, by the digest of its multi-arch index (resolved 2026-09-26).</summary>
    public const string Image = "nginx:1.30.5-alpine@sha256:bf3201ab56f23e5954646379c775d511fc466e9f11376d9725361064ad07ed35";

    /// <summary>The name clients dial, and the only host the server allows.</summary>
    public const string Host = "mcp.e2e.test";

    public const int Port = 443;

    /// <summary>The test-only header naming the client address the front forwards.</summary>
    public const string ClientAddressHeader = "X-E2E-Client-Address";

    /// <summary>How long nginx may take to start its worker; it takes about a second.</summary>
    private static readonly TimeSpan ReadyWithin = TimeSpan.FromMinutes(1);

    private readonly IContainer _container;

    private TlsFront(IContainer container, IPAddress address)
    {
        _container = container;
        Address = address;
    }

    /// <summary>The front's static address on the run's network: the server's KnownProxies.</summary>
    public IPAddress Address { get; }

    public IContainer Container => _container;

    /// <summary>Where the test process reaches the front: its published port.</summary>
    public DnsEndPoint Published => new(_container.Hostname, _container.GetMappedPublicPort(Port));

    internal static async Task<TlsFront> StartAsync(
        INetwork network,
        TestPki pki,
        IPAddress address,
        string upstreamAlias,
        int upstreamPort,
        string runId,
        CancellationToken cancellationToken)
    {
        var configuration = pki.WriteFile($"front-{upstreamAlias}.conf", Configuration(upstreamAlias, upstreamPort));

        var tls = pki.LeafDirectory("front");
        var container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithLabel(E2ENetwork.RunLabel, runId)
            .WithPortBinding(Port, true)
            .WithLoopbackPortsOnly()
            .WithBindMount(configuration, "/etc/nginx/nginx.conf", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(tls, TestPki.CertificateFile), "/e2e/tls/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(tls, TestPki.KeyFile), "/e2e/tls/tls.key", AccessMode.ReadOnly)
            // contract-005 · G-4 — the static address, set on the endpoint Testcontainers creates for
            // the run's network.
            .WithCreateParameterModifier(parameters =>
                parameters.NetworkingConfig!.EndpointsConfig![network.Name].IPAMConfig =
                    new EndpointIPAMConfig { IPv4Address = address.ToString() })
            // contract-005 · UC-1 edge — a deadline of its own, like every other wait of the environment.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("start worker process", wait => wait.WithTimeout(ReadyWithin)))
            .Build();

        try
        {
            await container.StartAsync(cancellationToken);
            return new TlsFront(container, address);
        }
        catch (Exception ex)
        {
            // contract-005 · UC-1 edge — the fault names its cause and keeps the original. Its log is
            // read if it can be: a log that cannot be read must not replace the fault it would explain.
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                await container.DisposeAsync();
                throw;
            }

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
            throw new EnvironmentFaultException("front", $"the TLS front did not start ({ex.GetType().Name}: {ex.Message}). Its log: {log}", ex);
        }
    }

    /// <summary>
    /// The front's access-log line for the request it forwarded as <paramref name="forwardedFor"/>, or
    /// null when it has logged none within two seconds (nginx logs a request as it completes, and
    /// Docker's log follows a moment later). The line records what the front sent upstream: the
    /// X-Forwarded-For and X-Forwarded-Proto values, and the server address it proxied to.
    /// </summary>
    public async Task<string?> AccessLogLineAsync(string forwardedFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forwardedFor);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            var (stdout, _) = await _container.GetLogsAsync(ct: cancellationToken);
            var line = stdout.Split('\n').LastOrDefault(l => l.Contains($" forwarded-for={forwardedFor} ", StringComparison.Ordinal));
            if (line is not null || DateTime.UtcNow >= deadline)
            {
                return line?.Trim();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string Configuration(string upstreamAlias, int upstreamPort) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        # contract-005 · G-9 — the TLS front, generated for one server under test.
        worker_processes 1;
        error_log /dev/stderr notice;
        pid /tmp/nginx.pid;

        events { worker_connections 256; }

        http {
            # The client address a test names, or the peer's own address when it names none.
            map $http_x_e2e_client_address $e2e_client_address {
                ""      $remote_addr;
                default $http_x_e2e_client_address;
            }

            # What the front puts in X-Forwarded-Proto, held in one variable so the access log records
            # the value the front sent: the forwarded-headers self-check reads it to tell a front that
            # did not send the header from a server that did not trust it.
            map $scheme $e2e_forwarded_proto {
                default https;
            }

            log_format e2e '$remote_addr forwarded-for=$e2e_client_address forwarded-proto=$e2e_forwarded_proto upstream=$upstream_addr "$request" $status host=$http_host';
            access_log /dev/stdout e2e;

            server {
                listen {{Port}} ssl;
                server_name {{Host}};

                ssl_certificate     /e2e/tls/tls.crt;
                ssl_certificate_key /e2e/tls/tls.key;
                ssl_protocols       TLSv1.2 TLSv1.3;

                location / {
                    proxy_pass http://{{upstreamAlias}}:{{upstreamPort}};
                    proxy_http_version 1.1;

                    # The Host header as the client sent it: the server builds its challenge from it.
                    proxy_set_header Host $http_host;
                    proxy_set_header X-Forwarded-Proto $e2e_forwarded_proto;
                    # Set, not appended: one hop, one address.
                    proxy_set_header X-Forwarded-For $e2e_client_address;
                    # The harness device ends here; the server never sees it.
                    proxy_set_header X-E2E-Client-Address "";

                    # Streamable HTTP answers may be event streams.
                    proxy_buffering off;
                    proxy_read_timeout 300s;
                }
            }
        }
        """);
}
