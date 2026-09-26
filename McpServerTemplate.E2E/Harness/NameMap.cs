using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The test process's view of the environment's names.
///
/// contract-005 · G-6 — the test reaches every service under the name the server uses for it:
/// https://idp-a.e2e.test, https://keycloak.e2e.test:8443, https://mcp.e2e.test. Those names exist
/// only inside the Docker network, so the test process maps each (name, port) to the port Docker
/// published on this machine, in the socket's connect step and nowhere else. The request itself is
/// untouched: its URL, its Host header and its TLS server name are the real names, and the
/// certificate is validated against those names and the run's test CA — and only that CA.
///
/// A name the map does not hold is refused before a socket opens, so nothing the test process sends
/// through a client made here can leave the environment. Plain http is refused too: every name in
/// the environment is served over TLS.
/// </summary>
public sealed class NameMap
{
    private readonly ImmutableDictionary<(string Host, int Port), DnsEndPoint> _entries;
    private readonly X509Certificate2 _ca;

    public NameMap(X509Certificate2 ca)
        : this(ca, ImmutableDictionary<(string Host, int Port), DnsEndPoint>.Empty)
    {
    }

    private NameMap(X509Certificate2 ca, ImmutableDictionary<(string Host, int Port), DnsEndPoint> entries)
    {
        _ca = ca;
        _entries = entries;
    }

    /// <summary>The names this map resolves, with where each one goes.</summary>
    public IReadOnlyDictionary<(string Host, int Port), DnsEndPoint> Entries => _entries;

    /// <summary>A map that also sends <paramref name="host"/>:<paramref name="port"/> to a published port.</summary>
    public NameMap With(string host, int port, string publishedHost, int publishedPort)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new NameMap(_ca, _entries.SetItem((host.ToLowerInvariant(), port), new DnsEndPoint(publishedHost, publishedPort)));
    }

    /// <summary>A map holding this map's entries and <paramref name="other"/>'s.</summary>
    public NameMap Including(NameMap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new NameMap(_ca, _entries.SetItems(other._entries));
    }

    /// <summary>
    /// An HTTP client whose every connection goes through this map.
    /// </summary>
    /// <param name="log">Records each connection and each request, for a test that asserts on routing.</param>
    /// <param name="clientAddress">
    /// The client address the TLS front forwards for requests to the front, through its test-only
    /// header (G-9): the front stands in for many clients behind one proxy.
    /// </param>
    public HttpClient CreateClient(NameMapLog? log = null, string? clientAddress = null)
    {
        var sockets = new SocketsHttpHandler
        {
            // A proxy on the developer's machine must not see, or break, the environment's traffic.
            UseProxy = false,
            // A redirect is a response a test may need to read (an authorization code arrives in one).
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context.DnsEndPoint, log, cancellationToken),
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = TrustOnlyTheTestCa(),
            },
        };

        var recording = new RecordingHandler(log, clientAddress) { InnerHandler = sockets };
        return new HttpClient(recording, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
    }

    private X509ChainPolicy TrustOnlyTheTestCa()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            // The test CA publishes no revocation list; on Windows a revocation check fails every chain.
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(_ca);
        return policy;
    }

    private async ValueTask<Stream> ConnectAsync(DnsEndPoint requested, NameMapLog? log, CancellationToken cancellationToken)
    {
        var key = (requested.Host.ToLowerInvariant(), requested.Port);
        if (!_entries.TryGetValue(key, out var target))
        {
            log?.Refuse(requested);
            throw new HttpRequestException(
                $"{requested.Host}:{requested.Port} is not in the end-to-end name map. The test process reaches "
                + "only the environment's own names, so nothing it sends can leave the environment.");
        }

        log?.Connect(requested, target);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class RecordingHandler(NameMapLog? log, string? clientAddress) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException(
                    $"{uri} is not https. Every name in the environment is served over TLS with the run's test CA.");
            }

            if (clientAddress is not null && uri.Host.Equals(TlsFront.Host, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Remove(TlsFront.ClientAddressHeader);
                request.Headers.Add(TlsFront.ClientAddressHeader, clientAddress);
            }

            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                log?.Request(request.Method, uri, (int)response.StatusCode);
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                log?.Request(request.Method, uri, 0);
                throw;
            }
        }
    }
}

/// <summary>What went through a <see cref="NameMap"/> client: each connection and each request.</summary>
public sealed class NameMapLog
{
    private readonly ConcurrentQueue<string> _connections = new();
    private readonly ConcurrentQueue<(HttpMethod Method, Uri Uri, int Status)> _requests = new();
    private readonly ConcurrentQueue<string> _refused = new();

    /// <summary>"host:port -> published endpoint", one per connection opened.</summary>
    public IReadOnlyList<string> Connections => [.. _connections];

    /// <summary>Every request sent, with its status (0 when no response came back).</summary>
    public IReadOnlyList<(HttpMethod Method, Uri Uri, int Status)> Requests => [.. _requests];

    /// <summary>Names a request asked for that the map does not hold.</summary>
    public IReadOnlyList<string> Refused => [.. _refused];

    internal void Connect(DnsEndPoint requested, DnsEndPoint target) =>
        _connections.Enqueue($"{requested.Host}:{requested.Port} -> {target.Host}:{target.Port}");

    internal void Request(HttpMethod method, Uri uri, int status) => _requests.Enqueue((method, uri, status));

    internal void Refuse(DnsEndPoint requested) => _refused.Enqueue($"{requested.Host}:{requested.Port}");

    public override string ToString() =>
        string.Join(Environment.NewLine, Requests.Select(r => $"  {r.Method} {r.Uri} -> {r.Status}"))
        + (Refused.Count > 0 ? $"{Environment.NewLine}  refused: {string.Join(", ", Refused)}" : string.Empty);
}
