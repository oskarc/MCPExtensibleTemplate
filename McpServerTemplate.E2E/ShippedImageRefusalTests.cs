using System.Globalization;
using System.Net;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-2 (G-8, G-11, G-15 · UC-2) — contract-003's refusals on the shipped image, in Production with
/// Redis.
///
/// contract-003's T-10 asks that a real Production process with Redis repeat its T-3 and T-5 refusals. This is that
/// process: the image built from the repository's Dockerfile, its startup line reading limits=Redis. Each refusal
/// names its own rule and returns nothing a caller could use — a hidden tool, another trust domain's resource, an
/// unscoped prompt, an extra argument, a wrong type, an over-long string and NaN — and the upstream fake, seen
/// recording the permitted call made first in the same test, records no request from this server for any of them.
/// contract-003's unscoped completion runs on the test host (T-8), the only image with a provider that offers
/// completion.
///
/// The class's server binds both weather providers to idp-a, which mints tokens with exactly the scopes a case needs;
/// the other trust domain's caller is a real Keycloak token. Every name comes from the image's own startup line (G-11).
/// </summary>
public sealed class ShippedImageRefusalTests(ShippedImageRefusalTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<ShippedImageRefusalTests.Server>
{
    /// <summary>Both weather providers bound to idp-a; Keycloak stays configured, trusted, and bound to nothing here.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA)
        .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpA));

    private const string IssuerA = "idp-a.e2e.test";
    private const string Tool = "get_forecast";
    private const string Resource = "smhi://coverage-area";
    private const string Prompt = "forecast_briefing";
    private const string ToolScope = "weather:read";
    private const string OtherScope = "observations:read";

    /// <summary>
    /// Sabotage (G-11): give the hidden-tool and unscoped-prompt callers' tokens weather:read as well. The tool then
    /// runs and the prompt answers, and the claim's assertion goes red on both, naming each.
    /// </summary>
    [Fact]
    public async Task T2_contract_003s_refusals_each_carry_their_rule_return_nothing_and_reach_no_upstream()
    {
        using var http = fixture.CreateClient();
        var server = await fixture.Server.NetworkAddressAsync();

        // The image's own account (G-11): Production with Redis, and the items the cases name, with their scopes.
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        var entries = line.Entries(E2EEnvironment.Issuers.Entries.SelectMany(e => e.ScopeCatalog));
        Assert.Equal("Redis", line.Limits);
        Assert.Contains(new FrameLine.Entry("tool", "Smhi", Tool, ToolScope, "Read"), entries);
        Assert.Contains(new FrameLine.Entry("resource", "Smhi", Resource, ToolScope, null), entries);
        Assert.Contains(new FrameLine.Entry("prompt", "Smhi", Prompt, ToolScope, null), entries);
        Assert.Contains(entries, e => e.Scope == OtherScope);

        var weather = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, [ToolScope]);
        var observationsOnly = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, [OtherScope]);
        var keycloak = await fixture.KeycloakTokenAsync();

        // The permitted call, and the witness seen active: the tool runs and its request reaches the fake from this server.
        var (latitude, longitude) = Coordinates();
        var permitted = await CallAsync(http, weather, new Dictionary<string, object> { ["latitude"] = latitude, ["longitude"] = longitude });
        var recorded = await FromServerAsync(http, server);
        var upstream = $"/lon/{longitude.ToString("F6", CultureInfo.InvariantCulture)}/lat/{latitude.ToString("F6", CultureInfo.InvariantCulture)}/";
        Assert.True(
            McpRequests.RuleOf(McpRequests.TextOf(permitted)) is null && recorded.Any(e => e.Path.Contains(upstream, StringComparison.Ordinal)),
            $"the permitted call did not reach the fake: it answered '{McpRequests.TextOf(permitted)}', and the fake recorded from this "
            + $"server [{string.Join(", ", recorded.Select(e => $"{e.Method} {e.Path}"))}].");

        // contract-003's T-3 and T-5, each with the rule it must be refused by. Each tool call has coordinates of its own.
        var cases = new (string Case, string Rule, Func<Task<JsonElement>> Send)[]
        {
            ("a hidden tool", "insufficient_scope", () => CallAsync(http, observationsOnly, Located())),
            ("another trust domain's resource", "not-permitted", () =>
                McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, keycloak, "resources/read", new { uri = Resource }))),
            ("an unscoped prompt", "not-permitted", () =>
                McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, observationsOnly, "prompts/get",
                    new { name = Prompt, arguments = new { latitude = "59.33", longitude = "18.07" } }))),
            ("an extra argument", "extraneous-argument", () => CallAsync(http, weather, Located(("extra", 1)))),
            ("latitude=true", "argument-schema", () => CallAsync(http, weather, Located(("latitude", true)))),
            ("a 513-character string", "argument-length", () => CallAsync(http, weather, Located(("latitude", new string('1', 513))))),
            ("\"NaN\"", "not-a-number", () => CallAsync(http, weather, Located(("latitude", "NaN")))),
        };

        var wrong = new List<string>();
        foreach (var (name, rule, send) in cases)
        {
            var answer = await send();
            var text = McpRequests.TextOf(answer);
            output.WriteLine($"{name}: {text}");

            if (McpRequests.RuleOf(text) != rule || McpRequests.ReturnsContent(answer))
            {
                wrong.Add($"{name} got '{(McpRequests.ReturnsContent(answer) ? answer.GetRawText() : text)}', not a refusal by rule {rule} with no content");
            }
        }

        var seen = recorded.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var reached = (await FromServerAsync(http, server)).Where(e => !seen.Contains(e.Id)).ToList();
        Assert.True(
            wrong.Count == 0 && reached.Count == 0,
            string.Join("; ", wrong)
            + (reached.Count == 0
                ? string.Empty
                : $"{(wrong.Count > 0 ? "; and " : string.Empty)}the fake recorded {reached.Count} request(s) from this server after the "
                    + $"permitted call: [{string.Join(", ", reached.Select(e => $"{e.Method} {e.Path}"))}]."));
    }

    /// <summary>A point within SMHI's area that no other call in the run uses.</summary>
    private static (double Latitude, double Longitude) Coordinates() =>
        (Math.Round(55 + (Random.Shared.NextDouble() * 10), 4), Math.Round(12 + (Random.Shared.NextDouble() * 10), 4));

    /// <summary>A forecast's arguments at a point of their own, with <paramref name="changes"/> set on top.</summary>
    private static Dictionary<string, object> Located(params (string Name, object Value)[] changes)
    {
        var (latitude, longitude) = Coordinates();
        var arguments = new Dictionary<string, object> { ["latitude"] = latitude, ["longitude"] = longitude };
        foreach (var (name, value) in changes)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private static Task<JsonElement> CallAsync(HttpClient http, string token, IReadOnlyDictionary<string, object> arguments) =>
        McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, "tools/call", new { name = Tool, arguments }));

    /// <summary>What the fake has recorded from <paramref name="server"/>.</summary>
    private static async Task<IReadOnlyList<WireMockService.JournalEntry>> FromServerAsync(HttpClient http, IPAddress server) =>
        [.. (await WireMockService.EntriesAsync(http)).Where(e => server.Equals(e.Client))];
}
