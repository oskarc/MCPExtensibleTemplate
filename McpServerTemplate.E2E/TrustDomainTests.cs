using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-5 (G-5, G-6 · UC-5) — two trust domains on the image.
///
/// The class's server binds both weather providers to idp-a. A caller from idp-b — the other issuer, holding every
/// scope the catalog has — sees none of their tools, resources or prompts in any list, and calling, reading or
/// getting each one is refused in the same words as an item that does not exist, so a refusal does not tell a caller
/// from another trust domain that the item is there. What the bound providers serve is read from the image's own
/// startup line (G-11); an idp-a caller seeing every one of them listed is the witness that the lists list them.
/// </summary>
public sealed class TrustDomainTests(TrustDomainTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<TrustDomainTests.Server>
{
    /// <summary>Both weather providers bound to idp-a.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA)
        .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpA));

    /// <summary>The providers this class binds to idp-a.</summary>
    private static readonly string[] Bound = ["Smhi", "SmhiObs"];

    private const string IssuerA = "idp-a.e2e.test";
    private const string IssuerB = "idp-b.e2e.test";

    /// <summary>
    /// The tool row, stopped for the pioneer's decision (2026-09-29), not met and not changed: T-5 asks that a tool
    /// of the other trust domain be refused in the words for a tool that does not exist, and contract-002's T-6
    /// (ScopeAndBindingEnforcementTests.T6_a_caller_from_another_trust_domain_is_refused_by_a_different_rule), the
    /// roadmap's Phase 1 exit criterion (docs/06) and contract-003's G-4 hold that it is refused by its own rule,
    /// idp-binding. Seen red on the image: every bound tool answered 'authz_fail (rule: idp-binding). The tool '…'
    /// belongs to a provider bound to a different identity provider …', a tool that does not exist 'authz_fail (rule:
    /// not-permitted). You may not use the tool '…', or it does not exist.' Meeting one breaks the other.
    /// </summary>
    private const string ToolsStopped =
        "Stopped for the pioneer: contract-005 T-5 (the words for a tool that does not exist) conflicts with contract-002 T-6 "
        + "and the roadmap (rule idp-binding). Red recorded; resolve by decision, then remove this Skip.";

    /// <summary>
    /// Sabotage (G-11): mint the other-issuer caller's token at idp-a.e2e.test instead. It is then the bound
    /// providers' own caller, the lists show their items, and the claim's assertion goes red.
    /// </summary>
    [Fact]
    public async Task T5_a_caller_from_the_other_issuer_sees_none_of_the_bound_providers_items_in_the_lists()
    {
        using var http = fixture.CreateClient();
        var bound = await BoundEntriesAsync();
        var scopes = E2EEnvironment.Issuers[E2EEnvironment.IdpB].ScopeCatalog;

        // The witness: the bound providers' own caller sees every one of their items listed.
        var own = await ListedAsync(http, await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, scopes));
        var unlisted = bound.Where(e => !own.Contains((e.Kind, e.Key))).Select(e => $"{e.Kind} {e.Key}").ToArray();
        Assert.True(
            unlisted.Length == 0,
            $"an {IssuerA} caller holding every scope does not see these of the bound providers' items listed: {string.Join(", ", unlisted)}. "
            + $"The lists it got: {string.Join(", ", own.Select(i => $"{i.Kind} {i.Key}"))}.");

        var other = await ListedAsync(http, await TestIssuerService.MintAsync(http, IssuerB, "valid", ServerUnderTest.Resource, scopes));
        output.WriteLine($"An {IssuerB} caller holding [{string.Join(", ", scopes)}] was listed: [{string.Join(", ", other.Select(i => $"{i.Kind} {i.Key}"))}]");

        var seen = bound.Where(e => other.Contains((e.Kind, e.Key))).Select(e => $"{e.Kind} {e.Key}").ToArray();
        Assert.True(
            seen.Length == 0,
            $"a caller from {IssuerB}, the other issuer, sees the bound providers' {string.Join(", ", seen)} in the lists.");
    }

    /// <summary>
    /// Each of the bound providers' items, used by a caller from the other issuer, against an item of the same kind
    /// that does not exist, with each name replaced: the same words.
    ///
    /// Sabotage (G-11): mint the caller's token at idp-a.e2e.test instead. The items are then the caller's own: a
    /// resource is read, and a tool or a prompt goes on to its arguments, so the words are no longer those for an
    /// item that does not exist, and the claim's assertion goes red.
    /// </summary>
    [Theory]
    [InlineData("resource")]
    [InlineData("prompt")]
    [InlineData("tool", Skip = ToolsStopped)]
    public async Task T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist(string kind)
    {
        using var http = fixture.CreateClient();
        var items = (await BoundEntriesAsync()).Where(e => e.Kind == kind).ToArray();
        Assert.NotEmpty(items);

        var token = await TestIssuerService.MintAsync(
            http, IssuerB, "valid", ServerUnderTest.Resource, E2EEnvironment.Issuers[E2EEnvironment.IdpB].ScopeCatalog);

        var missing = kind == "resource" ? $"smhi://e2e-no-such-{Guid.NewGuid():N}" : $"e2e_no_such_{Guid.NewGuid():N}";
        var absent = await UseAsync(http, token, kind, missing);
        var absentWords = McpRequests.TextOf(absent);
        output.WriteLine($"{kind} that does not exist: {absentWords}");
        Assert.True(McpRequests.RuleOf(absentWords) is not null, $"a {kind} that does not exist was not refused by a rule: '{absentWords}'.");

        var different = new List<string>();
        foreach (var item in items)
        {
            var refused = await UseAsync(http, token, kind, item.Key);
            var words = McpRequests.TextOf(refused);
            output.WriteLine($"{kind} {item.Key}: {words}");

            var content = McpRequests.ReturnsContent(refused);
            if (content || words.Replace(item.Key, "{name}", StringComparison.Ordinal) != absentWords.Replace(missing, "{name}", StringComparison.Ordinal))
            {
                different.Add($"'{item.Key}' got '{(content ? refused.GetRawText() : words)}'");
            }
        }

        Assert.True(
            different.Count == 0,
            $"a caller from {IssuerB}, the other issuer, is refused the bound providers' {kind}s in other words than a {kind} that does not "
            + $"exist ('{absentWords}'): {string.Join("; ", different)}.");
    }

    /// <summary>The bound providers' tools, resources and prompts, as the image's startup line gives them.</summary>
    private async Task<IReadOnlyList<FrameLine.Entry>> BoundEntriesAsync()
    {
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        var scopes = E2EEnvironment.Issuers.Entries.SelectMany(e => e.ScopeCatalog);
        var bound = line.Entries(scopes).Where(e => Bound.Contains(e.Provider, StringComparer.Ordinal)).ToArray();
        Assert.True(
            Bound.All(p => line.Providers.Contains(p, StringComparer.Ordinal)) && bound.Length > 0,
            $"the image's startup line does not serve the providers this class binds ({string.Join(", ", Bound)}): {line.Text}");
        return bound;
    }

    /// <summary>What a caller holding <paramref name="token"/> is listed, of every kind.</summary>
    private static async Task<HashSet<(string Kind, string Key)>> ListedAsync(HttpClient http, string token)
    {
        var listed = new HashSet<(string Kind, string Key)>();
        foreach (var (method, kind, array, key) in new[]
        {
            ("tools/list", "tool", "tools", "name"),
            ("resources/list", "resource", "resources", "uri"),
            ("resources/templates/list", "resource", "resourceTemplates", "uriTemplate"),
            ("prompts/list", "prompt", "prompts", "name"),
        })
        {
            var message = await McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, method));
            if (!message.TryGetProperty("result", out var result) || !result.TryGetProperty(array, out var items))
            {
                throw new InvalidOperationException($"{method} answered with no {array}: {message}");
            }

            foreach (var item in items.EnumerateArray())
            {
                listed.Add((kind, item.GetProperty(key).GetString()!));
            }
        }

        return listed;
    }

    /// <summary>Calls, reads or gets one item, by its kind.</summary>
    private static Task<JsonElement> UseAsync(HttpClient http, string token, string kind, string key) =>
        McpRequests.ExchangeAsync(http, kind switch
        {
            "tool" => McpRequests.Rpc(ServerUnderTest.Endpoint, token, "tools/call", new { name = key, arguments = new { } }),
            "resource" => McpRequests.Rpc(ServerUnderTest.Endpoint, token, "resources/read", new { uri = key }),
            "prompt" => McpRequests.Rpc(ServerUnderTest.Endpoint, token, "prompts/get", new { name = key, arguments = new { } }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "tool, resource or prompt"),
        });
}
