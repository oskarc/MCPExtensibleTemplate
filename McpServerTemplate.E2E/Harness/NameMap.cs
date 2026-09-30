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
    /// <param name="outermost">
    /// contract-005 · T-8 — a handler every request passes first, before the map sees it: how a test keeps
    /// a copy of what a client sent, to send it again. It cannot route around the map, which is beneath it.
    /// </param>
    public HttpClient CreateClient(NameMapLog? log = null, string? clientAddress = null, DelegatingHandler? outermost = null)
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
        HttpMessageHandler handler = recording;
        if (outermost is not null)
        {
            outermost.InnerHandler = recording;
            handler = outermost;
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
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

            var front = uri.Host.Equals(TlsFront.Host, StringComparison.OrdinalIgnoreCase);
            if (clientAddress is not null && front)
            {
                request.Headers.Remove(TlsFront.ClientAddressHeader);
                request.Headers.Add(TlsFront.ClientAddressHeader, clientAddress);
            }

            // contract-005 · G-17 round 3 (HAR-R3-01) — every request is logged however it ends: a response, with its status, or none
            // (status 0). Only a connection or TLS failure used to be logged, so a name that accepted the connection and then did not
            // answer — hung, paused, or slower than the client's own budget, which cancels the request — left no line, the name map's
            // log saw every name answered, and the SDK's failed OAuth flow was folded into a claim as the server's answer. And a
            // service of the environment answers only once its whole answer has come: its body is read here, so an answer that
            // stops part way is none. The front's answers are not read ahead: an MCP answer can be a stream the client reads as it
            // comes, and the front is the server's, whose answer a claim judges.
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
                if (!front)
                {
                    await response.Content.LoadIntoBufferAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                response?.Dispose();

                // A request to the front the client itself gave up on — cancelled as the client is disposed after a claim's failure,
                // with its event stream still open, or its budget for the server's answer spent — is recorded apart: the front is the
                // server's, and whether the server answered in time is a claim's to judge, never a name of the environment that gave
                // no answer.
                log?.Request(request.Method, uri, front && ex is OperationCanceledException ? NameMapLog.CancelledAtTheFront : 0);
                throw;
            }

            log?.Request(request.Method, uri, (int)response.StatusCode);
            return response;
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

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-01) — the status a request to the front is recorded with when the client itself cancelled
    /// it before its answer came: not 0, which is a name that gave no answer.
    /// </summary>
    public const int CancelledAtTheFront = -1;

    /// <summary>
    /// Every request sent, with its status: 0 when no whole answer came back — a connection or TLS failure, or a request cancelled
    /// or timed out before its answer had come (contract-005 · G-17 round 3, HAR-R3-01) — and <see cref="CancelledAtTheFront"/> for
    /// a request to the front the client itself cancelled.
    /// </summary>
    public IReadOnlyList<(HttpMethod Method, Uri Uri, int Status)> Requests => [.. _requests];

    /// <summary>Names a request asked for that the map does not hold.</summary>
    public IReadOnlyList<string> Refused => [.. _refused];

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-03) — every request that got no answer from a name the map holds: a connection or TLS
    /// failure at a container of the environment. A request to a name the map refused is not one: it went where it did, and
    /// where a request may go is a claim's to judge. contract-005 · G-17 round 3 (HAR-R3-01) — and one the name accepted and did
    /// not answer, or not in full, or not within the client's time: the SDK's own budget cancelling it counts, where it used to
    /// leave no line at all.
    /// </summary>
    public IReadOnlyList<(HttpMethod Method, Uri Uri)> Unanswered =>
        [.. Requests
            .Where(r => r.Status == 0 && !Refused.Contains($"{r.Uri.Host}:{r.Uri.Port}", StringComparer.OrdinalIgnoreCase))
            .Select(r => (r.Method, r.Uri))];

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-03) — before a test folds a failed OAuth flow into its claim: the flow's failure is the
    /// server's answer only if every request it sent to a name of the environment was answered. The MCP SDK's OAuth client
    /// (ModelContextProtocol.Core 2.2.0) catches a connection or TLS failure at the authorization server and throws an
    /// McpException of its own in its place — "Failed to find .well-known/openid-configuration or
    /// .well-known/oauth-authorization-server metadata …", with no inner exception — which <see cref="Claim.IsTheServersAnswer"/>
    /// takes for the server's answer; so a stopped or untrusted test issuer was reported as the standard client failing, its
    /// cause unnamed. Throws an <see cref="EnvironmentFaultException"/> of <paramref name="phase"/> naming each request no mapped
    /// name answered, with <paramref name="failure"/> inside it; returns when there is none. contract-005 · G-17 round 3
    /// (HAR-R3-01) — a name that did not answer in time is among them: the SDK swallows its own cancellation at the authorization
    /// server's metadata as it swallows a refused connection, and throws the same McpException.
    /// </summary>
    public void ThrowIfAMappedNameGaveNoAnswer(string phase, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var unanswered = Unanswered;
        if (unanswered.Count > 0)
        {
            throw new EnvironmentFaultException(
                phase,
                $"{string.Join("; ", unanswered.Select(r => $"{r.Method} {r.Uri.GetLeftPart(UriPartial.Path)}"))} got no answer from "
                + $"{string.Join(", ", unanswered.Select(r => $"{r.Uri.Host}:{r.Uri.Port}").Distinct(StringComparer.OrdinalIgnoreCase))}, a name "
                + "of the environment, or none within the client's time: a container that did not answer, answered only in part or too "
                + "slowly, or whose certificate did not validate against the run's test CA — not the server's answer. The client "
                + $"reported: {failure.GetType().Name}: {failure.Message.Split('\n')[0]}",
                failure);
        }
    }

    internal void Connect(DnsEndPoint requested, DnsEndPoint target) =>
        _connections.Enqueue($"{requested.Host}:{requested.Port} -> {target.Host}:{target.Port}");

    internal void Request(HttpMethod method, Uri uri, int status) => _requests.Enqueue((method, uri, status));

    internal void Refuse(DnsEndPoint requested) => _refused.Enqueue($"{requested.Host}:{requested.Port}");

    public override string ToString() =>
        string.Join(Environment.NewLine, Requests.Select(r => $"  {r.Method} {r.Uri} -> {r.Status}"))
        + (Refused.Count > 0 ? $"{Environment.NewLine}  refused: {string.Join(", ", Refused)}" : string.Empty);
}
