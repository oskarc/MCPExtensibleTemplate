using System.Net;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The MCP SDK's own client as a standard OAuth client uses it: an endpoint URL, a client id, a
/// redirect URI and a browser to follow the authorization URL, and nothing else — no token, no extra
/// header. Where it goes after the first 401 is decided by what the server's challenge and metadata
/// tell it (contract-005 · UC-4).
/// </summary>
public static class StandardClient
{
    /// <summary>The redirect URI the client registers; nothing listens there, the browser reads the redirect.</summary>
    public static readonly Uri RedirectUri = new("http://localhost/callback");

    /// <summary>A transport to <paramref name="endpoint"/> through <paramref name="http"/>, which is also the browser's.</summary>
    public static HttpClientTransport Transport(HttpClient http, Uri endpoint, string clientId) =>
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

    /// <summary>
    /// What a person's browser would do at the authorization endpoint: follow the URL, and hand the
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
}
