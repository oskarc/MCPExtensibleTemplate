using System.Net;
using System.Text.Json;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-6 (G-4, G-5) — a caller sees only what their scopes allow, and cannot reach a
/// provider bound to another identity provider.
///
/// This is the test whose absence hid two defects. The enforcement filters were registered in
/// Program.cs and the harness built its own server without them, so the path was never executed:
/// the trust-domain binding refused every authenticated caller, because nothing wrote the idp
/// claim onto a validated token, and no scope check existed at all. Both passed every suite.
/// </summary>
public class ScopeAndBindingEnforcementTests
{
    private const string Resource = "https://mcp.example.com/mcp";

    private static async Task<string[]> ListToolsAsync(InProcessServer server, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", $"Bearer {token}");

        using var response = await server.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // The stateless transport answers either as JSON or as an event stream; take the payload
        // either way rather than depending on which.
        var json = body.Contains("data:", StringComparison.Ordinal)
            ? body[(body.IndexOf("data:", StringComparison.Ordinal) + 5)..].Trim()
            : body;

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tools", out var tools))
        {
            return [];
        }

        return [.. tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];
    }

    [Fact]
    public async Task T6_a_caller_sees_the_tools_its_scopes_allow_and_no_others()
    {
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var weatherOnly = corp.MintToken(Resource, scopes: ["weather:read"]);

        var listed = await ListToolsAsync(server, weatherOnly);

        Assert.NotEmpty(listed);
        Assert.Contains("get_forecast", listed);

        // Observations and the demo provider require scopes this token does not hold.
        Assert.DoesNotContain("get_recent_temperature", listed);
        Assert.DoesNotContain("get_blog_post", listed);
    }

    [Fact]
    public async Task T6_a_token_with_no_scopes_sees_an_empty_listing()
    {
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var listed = await ListToolsAsync(server, corp.MintToken(Resource));

        Assert.Empty(listed);
    }

    [Fact]
    public async Task T6_every_scope_together_sees_every_tool()
    {
        // The control. Without it, an empty listing above would be satisfied by a server that
        // lists nothing for anyone — which is exactly the defect this file was written after.
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var everything = corp.MintToken(
            Resource, scopes: ["weather:read", "observations:read", "demo:read", "demo:write"]);

        var listed = await ListToolsAsync(server, everything);

        Assert.Equal(13, listed.Length);
    }

    [Fact]
    public async Task T6_a_hidden_tool_is_refused_when_called_by_name()
    {
        // Hiding is discretion; refusing is enforcement. A caller can name a tool it was never
        // shown, so the call path is checked separately.
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        await using var server = await InProcessServer.StartAsync([corp]);

        var weatherOnly = corp.MintToken(Resource, scopes: ["weather:read"]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_blog_post","arguments":{"postId":1}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", $"Bearer {weatherOnly}");

        using var response = await server.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("insufficient_scope", body, StringComparison.Ordinal);
        Assert.Contains("demo:read", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T6_a_caller_from_another_trust_domain_is_refused_by_a_different_rule()
    {
        // Two identity providers, both trusted by this server; the demo provider is bound only to
        // corp. A partner token holding exactly the right scope must still be refused — and
        // refused as a binding violation, not as a missing scope, because the two call for
        // different action: one can be requested, the other cannot.
        using var corp = new TestIdentityProvider("corp", "https://login.corp.test/");
        using var partner = new TestIdentityProvider("partner", "https://login.partner.test/");

        await using var server = await InProcessServer.StartAsync([corp, partner]);

        var partnerToken = partner.MintToken(Resource, scopes: ["demo:read"]);

        var listed = await ListToolsAsync(server, partnerToken);
        Assert.DoesNotContain("get_blog_post", listed);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_blog_post","arguments":{"postId":1}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", $"Bearer {partnerToken}");

        using var response = await server.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("idp-binding", body, StringComparison.Ordinal);
    }
}
