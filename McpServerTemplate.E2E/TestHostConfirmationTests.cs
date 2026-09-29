using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpServerTemplate.E2E.Harness;
using Microsoft.IdentityModel.JsonWebTokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-8 (G-10, G-15 · UC-8) — the test host's confirmation round-trip, in Production with Redis.
///
/// A client that can confirm calls the test host's irreversible tool, is asked, confirms, and the tool runs: the fake
/// records one run. The confirmed retry the client sent is kept, and sent again five ways, each on a token issued
/// just before the call — so no refusal is the token's age — and each is refused for its own reason: replayed; for
/// other arguments; altered; presented by another caller; and a confirmation asked for and never used, sent once it
/// has waited out its 120 seconds. Through all of it the witness, outside the host's process, shows exactly one run.
///
/// Its own class, and so its own server and its own place in the run: the 120 seconds are waited out beside every
/// other class, not after them.
/// </summary>
public sealed class TestHostConfirmationTests(TestHostConfirmationTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<TestHostConfirmationTests.Server>
{
    /// <summary>The test host, as the environment and <see cref="TestHost.Delta"/> configure it.</summary>
    public sealed class Server() : ServerFixture(TestHost.Delta(), ServerBuild.TestHost);

    private const string IssuerA = "idp-a.e2e.test";

    /// <summary>How long a confirmation is valid (contract-003 · T-7).</summary>
    private static readonly TimeSpan Validity = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Sabotage (G-11): wait 100 seconds instead of 121 before the last attempt. The unused confirmation is then
    /// still valid, the tool runs a second time, and the claim's assertion goes red: two runs, and that attempt not
    /// refused as expired.
    /// </summary>
    [Fact]
    public async Task T8_five_tampering_attempts_are_each_refused_for_their_own_reason_and_the_tool_ran_once()
    {
        var target = $"e2e-{Guid.NewGuid():N}";
        var caller = $"e2e-confirmer-{Guid.NewGuid():N}";
        var recorder = new RequestRecorder();
        using var recorded = fixture.CreateClient(outermost: recorder);
        using var http = fixture.CreateClient();
        var server = await fixture.Server.NetworkAddressAsync();

        // The tool is the test host's, and irreversible, on its own startup line (G-11).
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        Assert.Contains($"tool:{TestHost.IrreversibleProvider}/{TestHost.Tool}:{TestHost.ToolScope}:Irreversible", line.Manifest);

        // The round-trip, through the SDK's own client: asked, confirmed, run once. The witness is seen active.
        var first = await FreshTokenAsync(http, caller);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = ServerUnderTest.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {first}" },
            },
            recorded,
            loggerFactory: null,
            ownsHttpClient: false);
        var options = new McpClientOptions();
        options.Handlers.ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) },
        });

        await using (var client = await McpClient.CreateAsync(transport, options))
        {
            var result = await client.CallToolAsync(TestHost.Tool, new Dictionary<string, object?> { ["target"] = target });
            var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            output.WriteLine($"The confirmed call: {text}");
            Assert.True(result.IsError is not true && text.Contains($"acted on {target}", StringComparison.Ordinal), $"the confirmed call did not run the tool: '{text}'.");
        }

        var runs = await RunsAsync(http, server);
        Assert.True(runs.Count == 1, $"the confirmed call ran the tool, and the witness shows {runs.Count} runs: [{string.Join(", ", runs)}].");

        var (retry, headers) = recorder.ConfirmedRetry();

        // A confirmation asked for and never answered: the retry without its state and answer is a first call again.
        var asked = await McpRequests.ExchangeAsync(http, McpRequests.Send(ServerUnderTest.Endpoint, await FreshTokenAsync(http, caller), WithoutConfirmation(retry), headers));
        var askedAt = await EngineClockAsync(http);
        var unused = RequestStateIn(asked)
            ?? throw new InvalidOperationException($"the test host did not ask for a confirmation: {asked}");

        var attempts = new List<(string Attempt, string Rule, string Reason, JsonObject Body, string Subject)>
        {
            ("a replay", "confirmation-replayed", "has already been used", retry, caller),
            ("other arguments", "confirmation", "given for different arguments", With(retry, body => body["params"]!["arguments"]!["target"] = $"{target}-other"), caller),
            ("an altered confirmation", "confirmation", "not issued by this server, or has been altered", With(retry, body => body["params"]!["requestState"] = Altered((string)body["params"]!["requestState"]!)), caller),
            ("another caller", "confirmation", "issued to another caller or for another tool", retry, $"e2e-other-{Guid.NewGuid():N}"),
        };

        var wrong = new List<string>();
        foreach (var (attempt, rule, reason, body, subject) in attempts)
        {
            wrong.AddRange(await AttemptAsync(http, attempt, rule, reason, body, subject, headers));
        }

        // The expired one: the unused confirmation, once it has waited out its 120 seconds by the server's clock (the
        // engine's, which every container shares), sent with a token issued now.
        var waited = await WaitOutAsync(http, askedAt, Validity + TimeSpan.FromSeconds(1));
        output.WriteLine($"Waited until the unused confirmation was {waited.TotalSeconds:0} s old.");
        wrong.AddRange(await AttemptAsync(
            http, "an expired one", "confirmation", $"it is valid for {Validity.TotalSeconds:0}", With(retry, body => body["params"]!["requestState"] = unused), caller, headers));

        runs = await RunsAsync(http, server);
        Assert.True(
            wrong.Count == 0 && runs.Count == 1,
            string.Join("; ", wrong) + $"{(wrong.Count > 0 ? "; " : string.Empty)}the witness shows {runs.Count} run(s): [{string.Join(", ", runs)}], not exactly one.");
    }

    /// <summary>
    /// One attempt: the body sent under a token issued for <paramref name="subject"/> just before the call. Returns what
    /// was wrong with how it was refused, or nothing when it was refused by <paramref name="rule"/> for <paramref name="reason"/>.
    /// </summary>
    private async Task<IEnumerable<string>> AttemptAsync(
        HttpClient http, string attempt, string rule, string reason, JsonObject body, string subject, IReadOnlyDictionary<string, string> headers)
    {
        var token = await FreshTokenAsync(http, subject);
        var age = DateTimeOffset.UtcNow - new JsonWebToken(token).IssuedAt;
        var answer = await McpRequests.ExchangeAsync(http, McpRequests.Send(ServerUnderTest.Endpoint, token, body, headers));
        var text = McpRequests.TextOf(answer);
        output.WriteLine($"{attempt} (token issued {age.TotalSeconds:0.0} s before): {text}");

        if (age >= TimeSpan.FromSeconds(60))
        {
            // A self-check, not the claim: a token this old is refused for its age, which would hide the attempt's reason.
            throw new InvalidOperationException($"{attempt}: the token was issued {age.TotalSeconds:0} s before the call, not under 60 s.");
        }

        var content = McpRequests.ReturnsContent(answer);
        return McpRequests.RuleOf(text) == rule && text.Contains(reason, StringComparison.Ordinal) && !content
            ? []
            : [$"{attempt} got '{(content ? answer.GetRawText() : text)}', not a refusal by rule {rule} saying '{reason}', with no content"];
    }

    /// <summary>A token idp-a issues now to <paramref name="subject"/>, with the tool's scope.</summary>
    private static Task<string> FreshTokenAsync(HttpClient http, string subject) =>
        TestIssuerService.MintAsync(
            http, IssuerA, "valid", ServerUnderTest.Resource, [TestHost.ToolScope], new Dictionary<string, object> { ["subject"] = subject });

    /// <summary>The runs the witness recorded from <paramref name="server"/>: each report's marker, which no other run has.</summary>
    private static async Task<IReadOnlyList<string>> RunsAsync(HttpClient http, System.Net.IPAddress server) =>
        [.. (await WireMockService.EntriesAsync(http))
            .Where(e => server.Equals(e.Client) && e.Path.StartsWith(TestHost.WitnessPath, StringComparison.Ordinal))
            .Select(e => e.Path[TestHost.WitnessPath.Length..])
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The engine's clock, which the server's is: the test issuer reads it for the run's clock self-check.</summary>
    private static async Task<DateTimeOffset> EngineClockAsync(HttpClient http)
    {
        var clock = await http.GetFromJsonAsync<JsonElement>(new Uri($"https://{IssuerA}/admin/clock"));
        return clock.GetProperty("utc").GetDateTimeOffset();
    }

    /// <summary>Waits until <paramref name="span"/> has passed since <paramref name="since"/> on the engine's clock; returns how long it has.</summary>
    private static async Task<TimeSpan> WaitOutAsync(HttpClient http, DateTimeOffset since, TimeSpan span)
    {
        while (true)
        {
            var elapsed = await EngineClockAsync(http) - since;
            if (elapsed >= span)
            {
                return elapsed;
            }

            await Task.Delay(span - elapsed + TimeSpan.FromMilliseconds(250));
        }
    }

    private static JsonObject With(JsonObject body, Action<JsonObject> change)
    {
        var copy = body.DeepClone().AsObject();
        change(copy);
        return copy;
    }

    private static JsonObject WithoutConfirmation(JsonObject retry) => With(retry, body =>
    {
        var parameters = body["params"]!.AsObject();
        parameters.Remove("requestState");
        parameters.Remove("inputResponses");
    });

    /// <summary>The state with one character of its signature changed, as the fast suite alters it (contract-003 · T-7).</summary>
    private static string Altered(string state) => state[..^2] + (state[^2] == 'A' ? "B" : "A") + state[^1];

    /// <summary>The requestState an input-required answer carries, wherever in its result it sits.</summary>
    private static string? RequestStateIn(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Select(p => p.Name == "requestState" && p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : RequestStateIn(p.Value))
            .FirstOrDefault(s => s is not null),
        JsonValueKind.Array => element.EnumerateArray().Select(RequestStateIn).FirstOrDefault(s => s is not null),
        _ => null,
    };

    /// <summary>
    /// Keeps every request body the client sends, with its protocol headers (the Mcp- ones), so the confirmed retry
    /// can be sent again: the request that carries a requestState back to the server.
    /// </summary>
    private sealed class RequestRecorder : DelegatingHandler
    {
        private readonly List<(JsonObject Body, Dictionary<string, string> Headers)> _sent = [];

        public (JsonObject Body, IReadOnlyDictionary<string, string> Headers) ConfirmedRetry()
        {
            lock (_sent)
            {
                var retry = _sent.LastOrDefault(s => (string?)s.Body["method"] == "tools/call" && s.Body["params"]?["requestState"] is not null);
                return retry.Body is null
                    ? throw new InvalidOperationException($"the client sent no confirmed retry; it sent: {string.Join(" | ", _sent.Select(s => s.Body.ToJsonString()))}")
                    : (retry.Body, retry.Headers);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null && JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) is JsonObject body)
            {
                var headers = request.Headers
                    .Where(h => h.Key.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
                lock (_sent)
                {
                    _sent.Add((body, headers));
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
