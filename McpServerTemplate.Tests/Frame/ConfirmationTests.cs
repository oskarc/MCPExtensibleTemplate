using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
using McpServerTemplate.Tests.Identity;
using ModelContextProtocol.Client;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-003 · T-7 (G-7) — an irreversible tool runs once per confirmation, only with the
/// arguments confirmed, only within 120 seconds, only for a confirmation this server signed, and
/// never for a client that cannot be asked. The real client confirms once; the retry it sends is
/// captured, and then replayed, altered and aged by hand.
/// </summary>
public class ConfirmationTests
{
    private static readonly IReadOnlyList<IProviderModule> Everything = [.. BuiltInProviders.Create(), new TestModule()];

    /// <summary>Records every request body the client sends.</summary>
    private sealed class Recorder : DelegatingHandler
    {
        public List<string> Bodies { get; } = [];

        public List<Dictionary<string, string>> Headers { get; } = [];

        public Recorder() : base(new HttpClientHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
                Headers.Add(request.Headers
                    .Where(h => h.Key.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(McpClient Client, Recorder Recorder)> ConnectRecordingAsync(InProcessServer server, string token)
    {
        var recorder = new Recorder();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = server.Address,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            new HttpClient(recorder),
            ownsHttpClient: true);
        var options = new McpClientOptions();
        options.Handlers.ElicitationHandler = (_, _) => ValueTask.FromResult(new ModelContextProtocol.Protocol.ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) },
        });
        return (await McpClient.CreateAsync(transport, options), recorder);
    }

    /// <summary>The confirmed retry — the tools/call that carries the signed state back — and its protocol headers.</summary>
    private static (JsonObject Body, Dictionary<string, string> Headers) ConfirmedRetry(Recorder recorder)
    {
        var index = recorder.Bodies.FindLastIndex(b =>
            JsonNode.Parse(b)!.AsObject() is var o && (string?)o["method"] == "tools/call" && o["params"]?["requestState"] is not null);
        return (JsonNode.Parse(recorder.Bodies[index])!.AsObject(), recorder.Headers[index]);
    }

    private static string TextOf(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

    private static Dictionary<string, string> _headers = [];

    private static async Task<string> SendAsync(InProcessServer server, string token, JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", $"Bearer {token}");
        foreach (var (name, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await server.Client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task T7_the_whole_round_trip_runs_the_tool_once_and_nothing_else_runs_it_again()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);
        var token = corp.MintToken(GateClient.Resource, scopes: ["test:act"]);

        // 1. A client that can confirm: asked, confirms, the tool runs once.
        var (client, recorder) = await ConnectRecordingAsync(server, token);
        await using (client)
        {
            var before = TestActTools.Runs;
            var result = await client.CallToolAsync("test_destroy", new Dictionary<string, object?> { ["target"] = "alpha" });
            var text = TextOf(result);
            Assert.True(text.Contains("destroyed alpha", StringComparison.Ordinal), text);
            Assert.Equal(before + 1, TestActTools.Runs);
        }

        (var retry, _headers) = ConfirmedRetry(recorder);
        var runs = TestActTools.Runs;

        // 2. The same confirmation, sent again inside its 120 seconds: refused.
        Assert.Contains("confirmation-replayed", await SendAsync(server, token, retry.DeepClone().AsObject()), StringComparison.Ordinal);

        // 3. The confirmation, for different arguments: refused.
        var otherArguments = retry.DeepClone().AsObject();
        otherArguments["params"]!["arguments"]!["target"] = "beta";
        Assert.Contains("different arguments", await SendAsync(server, token, otherArguments), StringComparison.Ordinal);

        // 4. The confirmation, altered: refused.
        var altered = retry.DeepClone().AsObject();
        var state = (string)altered["params"]!["requestState"]!;
        altered["params"]!["requestState"] = state[..^2] + (state[^2] == 'A' ? "B" : "A") + state[^1];
        Assert.Contains("not issued by this server, or has been altered", await SendAsync(server, token, altered), StringComparison.Ordinal);

        // 5. A confirmation signed with this server's key 121 seconds ago: refused.
        var caller = new Caller("corp", "user-1", new HashSet<string>(), DateTimeOffset.UtcNow);
        var aged = new ConfirmationService(Convert.FromBase64String(InProcessServer.ConfirmationKey), new FixedClock(DateTimeOffset.UtcNow.AddSeconds(-121)))
            .Issue(caller, "test_destroy", new Dictionary<string, JsonElement> { ["target"] = JsonSerializer.SerializeToElement("alpha") });
        var old = retry.DeepClone().AsObject();
        old["params"]!["requestState"] = aged;
        Assert.Contains("valid for 120", await SendAsync(server, token, old), StringComparison.Ordinal);

        // 6. Another caller presenting it: refused.
        var otherCaller = corp.MintToken(GateClient.Resource, subject: "user-2", scopes: ["test:act"]);
        Assert.Contains("another caller", await SendAsync(server, otherCaller, retry.DeepClone().AsObject()), StringComparison.Ordinal);

        // Through all of that, the tool ran exactly once.
        Assert.Equal(runs, TestActTools.Runs);
    }

    [Fact]
    public async Task T7_a_declined_confirmation_does_not_run_the_tool()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);
        await using var client = await GateClient.ConnectAsync(server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]), canConfirm: true, confirms: false);

        var before = TestActTools.Runs;
        var refused = await client.CallToolAsync("test_destroy", new Dictionary<string, object?> { ["target"] = "gamma" });
        Assert.True(refused.IsError);
        Assert.Contains("declined or not answered", TextOf(refused), StringComparison.Ordinal);
        Assert.Equal(before, TestActTools.Runs);
    }

    [Fact]
    public async Task T7_a_client_that_cannot_confirm_is_refused_and_told_why()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);

        // Raw JSON-RPC declaring an older protocol: a client that does not do input requests.
        var before = TestActTools.Runs;
        var response = await GateClient.RpcAsync(server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]), "tools/call",
            new { name = "test_destroy", arguments = new { target = "delta" } });

        Assert.Contains("confirmation-unsupported", GateClient.TextOf(response), StringComparison.Ordinal);
        Assert.Equal(before, TestActTools.Runs);
    }

    [Fact]
    public async Task T7_an_irreversible_call_needs_a_token_under_a_minute_old()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);
        await using var client = await GateClient.ConnectAsync(server,
            corp.MintToken(GateClient.Resource, scopes: ["test:act"], issuedAt: DateTime.UtcNow.AddSeconds(-90)), canConfirm: true);

        var refused = await client.CallToolAsync("test_destroy", new Dictionary<string, object?> { ["target"] = "epsilon" });
        Assert.True(refused.IsError);
        Assert.Contains("token-age", TextOf(refused), StringComparison.Ordinal);
    }
}
