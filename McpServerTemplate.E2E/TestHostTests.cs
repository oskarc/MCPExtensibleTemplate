using System.Net;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-8 (G-10, G-15 · UC-8) — the test host, in Production with Redis: its completion refusal, its
/// containment, and its frame. (Its confirmation round-trip is <see cref="TestHostConfirmationTests"/>.)
///
/// Completion: contract-003's T-3 asks that an unscoped completion be refused by the frame's own rule. On the shipped
/// image no provider offers completion and the SDK answers first; the test host's prompt provider does offer it, so a
/// caller holding the prompt's scope is given completions, and one without it is refused with not-permitted, no
/// result, and the matching refusal on stderr.
///
/// Containment: the test host serves only identity under .test, whatever its environment's name. Under Production and
/// under Staging it comes up with every identity under .test, and exits 78, naming the setting, when one identity
/// provider's Authority is not.
///
/// Same frame: its whole "Frame installed" line is the shipped image's, except that providers= adds the test modules
/// and the manifest adds their lines, and nothing else; both read limits=Redis.
/// </summary>
public sealed class TestHostTests(TestHostTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<TestHostTests.Server>
{
    /// <summary>The test host, as the environment and <see cref="TestHost.Delta"/> configure it.</summary>
    public sealed class Server() : ServerFixture(TestHost.Delta(), ServerBuild.TestHost);

    private const string IssuerA = "idp-a.e2e.test";

    /// <summary>
    /// Sabotage (G-11): mint the unscoped caller's token with demo:read as well. It is given the completions, and the
    /// claim's assertion goes red.
    /// </summary>
    [Fact]
    public async Task T8_an_unscoped_completion_is_refused_not_permitted_with_no_result_and_its_refusal_is_on_stderr()
    {
        using var http = fixture.CreateClient();

        // The prompt, its provider and its scope, from the test host's own startup line (G-11).
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        Assert.Contains($"prompt:{TestHost.CompletionsProvider}/{TestHost.Prompt}:{TestHost.PromptScope}", line.Manifest);

        var request = new { @ref = new { type = "ref/prompt", name = TestHost.Prompt }, argument = new { name = TestHost.Argument, value = "U" } };

        // Positive control: completion is offered, and a caller holding the prompt's scope is given it. The refusal
        // below is the frame's rule, not a server with nothing to answer completion with.
        var scoped = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, [TestHost.PromptScope]);
        var given = await McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, scoped, "completion/complete", request));
        output.WriteLine($"Holding {TestHost.PromptScope}: {given}");
        Assert.True(
            given.TryGetProperty("result", out var completed)
                && completed.GetProperty("completion").GetProperty("values").EnumerateArray().Any(),
            $"a caller holding {TestHost.PromptScope} was not given completions for {TestHost.Prompt}: {given}");

        var subject = $"e2e-unscoped-{Guid.NewGuid():N}";
        var unscoped = await TestIssuerService.MintAsync(
            http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read"], new Dictionary<string, object> { ["subject"] = subject });
        var before = (await fixture.Server.StderrLinesAsync("rule=not-permitted")).Count;
        var refused = await McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, unscoped, "completion/complete", request));
        var logged = await fixture.Server.StderrLinesAfterAsync("rule=not-permitted", before);
        output.WriteLine($"Without it: {refused}");
        output.WriteLine($"Logged: {string.Join(" | ", logged)}");

        Assert.True(
            McpRequests.RuleOf(McpRequests.TextOf(refused)) == "not-permitted" && !refused.TryGetProperty("result", out _),
            $"an unscoped completion of {TestHost.Prompt} answered {refused}, not a not-permitted refusal with no result.");
        var principal = $"principal={E2EEnvironment.IdpA}:{subject} target={TestHost.Prompt} ";
        Assert.True(
            logged.Any(l => l.Contains("authz_fail: rule=not-permitted", StringComparison.Ordinal) && l.Contains(principal, StringComparison.Ordinal)),
            $"the test host refused an unscoped completion, and its stderr has no matching refusal ('authz_fail: rule=not-permitted … {principal}…'): "
            + $"[{string.Join(" | ", logged)}].");
    }

    /// <summary>
    /// Sabotage (G-11): give the Authority a host under .test instead (https://idp-a.example.test). The test host
    /// comes up, and the claim's assertion goes red.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task T8_the_test_host_exits_78_given_an_identity_provider_outside_test(string environmentName)
    {
        var key = $"Authentication:IdentityProviders:{E2EEnvironment.IdpA}:Authority";
        var delta = TestHost.Delta().Set("ASPNETCORE_ENVIRONMENT", environmentName);
        if (environmentName != "Production")
        {
            // Only appsettings.Production.json names the providers to serve; any other environment names them here.
            delta = delta.Set("Providers:Enabled:0", "Smhi").Set("Providers:Enabled:1", "SmhiObs");
        }

        // Positive control: under this environment's name, with every identity under .test, the test host comes up.
        await using (var contained = await fixture.Environment.StartupAsync($"test-host-{environmentName.ToLowerInvariant()}-contained", delta, ServerBuild.TestHost))
        {
            output.WriteLine($"{environmentName}, contained: {contained.Describe()}");
            Assert.True(
                contained.Started && contained.Readyz == HttpStatusCode.OK,
                $"under {environmentName}, with every identity under .test, the test host did not come up: {contained.Describe()}");
        }

        const string outside = "https://idp-a.example.com";
        await using var refused = await fixture.Environment.StartupAsync(
            $"test-host-{environmentName.ToLowerInvariant()}-outside", delta.Set(key, outside), ServerBuild.TestHost);
        output.WriteLine($"{environmentName}, {key}={outside}: {refused.Describe()}");

        Assert.True(
            refused.ExitCode == 78 && refused.Refusal?.Contains($"{key} is '{outside}'", StringComparison.Ordinal) == true,
            $"under {environmentName}, the test host given {key}={outside}, a host not under .test: {refused.Describe()}");
    }

    /// <summary>
    /// Sabotage (G-11): the test host's delta also enables JsonPlaceholder (Providers:Enabled:4). Its providers= and
    /// manifest then add a provider that is not a test module, and the claim's assertion goes red.
    /// </summary>
    [Fact]
    public async Task T8_the_test_hosts_frame_line_is_the_shipped_images_but_for_the_test_modules()
    {
        var testHost = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        await using var shipped = await fixture.Environment.StartupAsync("test-host-shipped-frame", SettingsDelta.None);
        var image = FrameLine.Parse(await shipped.FrameLineAsync());
        output.WriteLine($"shipped:   {image.Text}");
        output.WriteLine($"test host: {testHost.Text}");

        // Self-check: each line is exactly its parts, so comparing the parts is comparing the whole lines.
        Assert.Equal(image.Text, Rebuilt(image.Limits, image.Providers, image.Filters, image.Manifest));
        Assert.Equal(testHost.Text, Rebuilt(testHost.Limits, testHost.Providers, testHost.Filters, testHost.Manifest));

        bool IsTestModules(string entry) => TestHost.Modules.Any(m => entry.Contains($":{m}/", StringComparison.Ordinal));
        var addedProviders = testHost.Providers.Except(image.Providers, StringComparer.Ordinal).ToArray();
        var addedEntries = testHost.Manifest.Except(image.Manifest, StringComparer.Ordinal).ToArray();
        var withoutTheModules = Rebuilt(
            testHost.Limits,
            [.. testHost.Providers.Where(p => !TestHost.Modules.Contains(p, StringComparer.Ordinal))],
            testHost.Filters,
            [.. testHost.Manifest.Where(e => !IsTestModules(e))]);

        Assert.True(
            image.Limits == "Redis" && testHost.Limits == "Redis"
                && addedProviders.SequenceEqual(TestHost.Modules)
                && addedEntries.Length > 0 && addedEntries.All(IsTestModules)
                && withoutTheModules == image.Text,
            $"the test host's frame line is not the shipped image's plus its test modules. limits: {image.Limits} and {testHost.Limits}; "
            + $"providers added: [{string.Join(", ", addedProviders)}] (expected [{string.Join(", ", TestHost.Modules)}]); manifest lines added: "
            + $"[{string.Join("; ", addedEntries)}]; manifest lines gone: [{string.Join("; ", image.Manifest.Except(testHost.Manifest, StringComparer.Ordinal))}]; "
            + $"filters {(image.Filters == testHost.Filters ? "equal" : $"differ: '{image.Filters}' and '{testHost.Filters}'")}.");
    }

    private static string Rebuilt(string limits, IEnumerable<string> providers, string filters, IEnumerable<string> manifest) =>
        $"Frame installed: limits={limits} providers={string.Join(",", providers)} :: {filters} | {string.Join(";", manifest)}";
}
