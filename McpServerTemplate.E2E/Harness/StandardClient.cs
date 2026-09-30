using System.Net;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The MCP SDK's own client as a standard OAuth client uses it: an endpoint URL, a client id, a
/// redirect URI and a browser to follow the authorization URL, and nothing else — no token, no extra
/// header. Where it goes after the first 401 is decided by what the server's challenge and metadata
/// tell it (contract-005 · UC-4).
///
/// contract-005 · G-17 round 3 (HAR-R3-04) — and the one way the suite runs it (<see cref="RunAsync"/>), which reads the name
/// map's log itself before it hands back any failure: a test made the client, and was held to reading the log, by a check of its
/// source that saw one spelling in top-level files. Nothing else in the suite makes the SDK's OAuth options (HarnessSelfTests
/// reads what every file compiles to), and a failure reaches a test only once it is the server's answer.
/// </summary>
public static class StandardClient
{
    /// <summary>The redirect URI the client registers; nothing listens there, the browser reads the redirect.</summary>
    public static readonly Uri RedirectUri = new("http://localhost/callback");

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-01, HAR-R3-04) — the SDK's OAuth client, given <paramref name="endpoint"/> and
    /// <paramref name="clientId"/>, connected through a client <paramref name="client"/> makes over a name map log of this flow's
    /// own, and handed to <paramref name="use"/>. Returns the flow: that log, and its failure when it failed with the server's
    /// answer (<see cref="Claim.IsTheServersAnswer"/>) or went to a name the map refused, which is a claim's to judge. A failure
    /// in which a name of the environment gave no answer, or none in time, is thrown as that environment fault
    /// (<see cref="NameMapLog.ThrowIfAMappedNameGaveNoAnswer"/>, named <paramref name="phase"/>); any other failure propagates as
    /// what it is.
    ///
    /// The SDK runs its discover probe, and the OAuth flow a 401 starts inside it, under its own budget, DiscoverProbeTimeout (5 s
    /// in ModelContextProtocol.Core 2.2.0, read from the SDK by G-17 round 3's second reader), and swallows the cancellation it
    /// causes: a healthy issuer answering after six seconds failed the flow as if the server had. The budget here is the client's
    /// own InitializationTimeout, whatever the SDK sets it to, so the probe is given no less time than the connection.
    /// </summary>
    public static async Task<OAuthFlow> RunAsync(
        Func<NameMapLog, HttpClient> client,
        Uri endpoint,
        string clientId,
        string phase,
        Func<McpClient, Task> use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(use);

        var log = new NameMapLog();
        using var http = client(log);
        var options = new McpClientOptions();
        options.DiscoverProbeTimeout = options.InitializationTimeout;
        try
        {
            await using var connected = await McpClient.CreateAsync(Transport(http, endpoint, clientId), options, cancellationToken: cancellationToken);
            await use(connected);
            return new OAuthFlow(log, null);
        }
        catch (Exception ex) when (Claim.IsTheServersAnswer(ex) || log.Refused.Count > 0)
        {
            log.ThrowIfAMappedNameGaveNoAnswer(phase, ex);
            return new OAuthFlow(log, ex);
        }
    }

    /// <summary>
    /// What the browser does at the authorization endpoint: follow the URL, and hand the
    /// redirect's code, state and iss back to the client. Through the name map like everything else.
    /// </summary>
    public static async Task<AuthorizationResult?> AuthorizeAsync(HttpClient http, Uri authorizationUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var response = await http.GetAsync(authorizationUri, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location is not { } location)
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
    }

    /// <summary>A transport to <paramref name="endpoint"/> through <paramref name="http"/>, which is also the browser's.</summary>
    private static HttpClientTransport Transport(HttpClient http, Uri endpoint, string clientId) =>
        new(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                OAuth = new ClientOAuthOptions
                {
                    ClientId = clientId,
                    RedirectUri = RedirectUri,
                    AuthorizationCallbackHandler = (context, cancellationToken) => AuthorizeAsync(http, context.AuthorizationUri, cancellationToken),
                },
            },
            http,
            loggerFactory: null,
            ownsHttpClient: false);
}

/// <summary>
/// contract-005 · G-17 round 3 (HAR-R3-04) — one run of the SDK's OAuth client (<see cref="StandardClient.RunAsync"/>): what went
/// through the name map, and how the flow failed, when it failed with the server's answer.
/// </summary>
/// <param name="Log">Every connection and request the flow made, and every name the map refused it.</param>
/// <param name="Failure">The failure, once the log has shown every name of the environment answered; null when the flow completed.</param>
public sealed record OAuthFlow(NameMapLog Log, Exception? Failure);
