using System.Net;
using System.Text.Json;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-3 (G-2) and T-11 (G-11) — what the metadata document says, and that no
/// request depends on having been preceded by another.
/// </summary>
public class MetadataAndStatelessTests
{
    private const string Resource = "https://mcp.example.com/mcp";

    [Fact]
    public async Task T3_the_metadata_document_describes_this_server()
    {
        // A client with no token finds this document from the challenge, and everything it needs
        // to get a token has to be in it. A document that is served but wrong is worse than one
        // that is missing: the client goes somewhere, and it is the wrong somewhere.
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        using var partner = new TestIdentityProvider("partner", "https://login.partner.test/");
        await using var server = await InProcessServer.StartAsync([corp, partner]);

        using var response = await server.Client.GetAsync("/.well-known/oauth-protected-resource");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(Resource, root.GetProperty("resource").GetString());

        // One resource, several authorization servers: every configured identity provider is
        // listed, because a client may hold a token from any of them.
        var servers = root.GetProperty("authorization_servers")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(corp.Authority, servers);
        Assert.Contains(partner.Authority, servers);

        var scopes = root.GetProperty("scopes_supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("weather:read", scopes);
        Assert.Contains("observations:read", scopes);

        // Nothing invented: the document reports what was configured, not a fixed list.
        Assert.DoesNotContain("mcp:admin", scopes);
    }

    [Fact]
    public async Task T3_the_metadata_document_needs_no_credential()
    {
        // It is the thing a caller reads *because* it has no credential. Requiring one would
        // close the only door out of the 401.
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        using var response = await server.Client.GetAsync("/.well-known/oauth-protected-resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task T11_two_calls_on_different_connections_both_succeed()
    {
        // Stateless streamable HTTP: no session affinity, no Mcp-Session-Id. If the second call
        // depended on the first having been made on the same connection, no second instance of
        // this server could serve a client that started against the first.
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var token = corp.MintToken(Resource);

        using var first = await server.PostAsync(token);
        Assert.NotEqual(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.False(first.Headers.Contains("Mcp-Session-Id"), "the server issued a session id");

        // A second client entirely, sharing nothing with the first.
        using var separate = new HttpClient { BaseAddress = server.Client.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"second","version":"1"}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", $"Bearer {token}");

        using var second = await separate.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(first.StatusCode, second.StatusCode);
    }
}
