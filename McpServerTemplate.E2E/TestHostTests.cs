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
/// provider's Authority is not. contract-005 · G-17 round 2 (CON-R2-03) — and naming every place the value reached, each a
/// branch of its check (McpServerTemplate.E2EHost/Containment.cs) that a setting alone could reach: the Authority as the
/// setting, as its scheme was built, and the MetadataAddress derived from it; an Issuer as the setting, among the issuers its
/// scheme accepts, and among the authorization servers the metadata publishes; the Resource as the setting and as the
/// metadata publishes it. Only the settings' Authority was held: removing any other branch left every suite green.
///
/// Same frame: its whole "Frame installed" line is the shipped image's, except that providers= adds the test modules
/// and the manifest adds their lines, and nothing else; both read limits=Redis.
/// </summary>
public sealed class TestHostTests(TestHostTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<TestHostTests.Server>
{
    /// <summary>The test host, as the environment and <see cref="TestHost.Delta"/> configure it, but in a run of the sabotage that acts on it.</summary>
    public sealed class Server() : ServerFixture(
        TestHost.Delta().Sabotaged(AlsoServesJsonPlaceholder, d => d.Set("Providers:Enabled:4", "JsonPlaceholder")), ServerBuild.TestHost);

    private const string IssuerA = "idp-a.e2e.test";

    private const string UnscopedGivenTheScope = "t8-unscoped-caller-given-demo-read";
    private const string AuthorityUnderTest = "t8-authority-under-test";
    private const string IssuerOrResourceUnderTest = "t8-issuer-or-resource-under-test";
    private const string AlsoServesJsonPlaceholder = "t8-test-host-also-serves-jsonplaceholder";

    /// <summary>idp-a's bearer scheme, as the product names it (IdentityRegistration.SchemeFor).</summary>
    private static readonly string IdpAScheme = $"idp:{E2EEnvironment.IdpA}";

    [Fact]
    [Sabotage(UnscopedGivenTheScope, SabotageActs.Inputs,
        "The unscoped caller's token is minted with demo:read as well, the prompt's scope, so it may be given completions.")]
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
            http, IssuerA, "valid", ServerUnderTest.Resource, Sabotage.Choose<string[]>(UnscopedGivenTheScope, ["weather:read"], ["weather:read", TestHost.PromptScope]),
            new Dictionary<string, object> { ["subject"] = subject });
        var before = (await fixture.Server.StderrLinesAsync("rule=not-permitted")).Count;
        var refused = await McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, unscoped, "completion/complete", request));
        var logged = await fixture.Server.StderrLinesAfterAsync("rule=not-permitted", before);
        output.WriteLine($"Without it: {refused}");
        output.WriteLine($"Logged: {string.Join(" | ", logged)}");

        Claim.True(
            McpRequests.RuleOf(McpRequests.TextOf(refused)) == "not-permitted" && !refused.TryGetProperty("result", out _),
            $"an unscoped completion of {TestHost.Prompt} answered {refused}, not a not-permitted refusal with no result.");
        var principal = $"principal={E2EEnvironment.IdpA}:{subject} target={TestHost.Prompt} ";
        Claim.True(
            logged.Any(l => l.Contains("authz_fail: rule=not-permitted", StringComparison.Ordinal) && l.Contains(principal, StringComparison.Ordinal)),
            $"the test host refused an unscoped completion, and its stderr has no matching refusal ('authz_fail: rule=not-permitted … {principal}…'): "
            + $"[{string.Join(" | ", logged)}].");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [Sabotage(AuthorityUnderTest, SabotageActs.ContainerEnvironment,
        "The Authority the test host is given is https://idp-a.example.test, a host under .test, instead of https://idp-a.example.com.")]
    public async Task T8_the_test_host_exits_78_given_an_identity_provider_outside_test(string environmentName)
    {
        var key = $"Authentication:IdentityProviders:{E2EEnvironment.IdpA}:Authority";
        var delta = ContainmentDelta(environmentName);

        // Positive control: under this environment's name, with every identity under .test, the test host comes up.
        await using (var contained = await fixture.Environment.StartupAsync($"test-host-{environmentName.ToLowerInvariant()}-contained", delta, ServerBuild.TestHost))
        {
            output.WriteLine($"{environmentName}, contained: {contained.Describe()}");
            Assert.True(
                contained.Started && contained.Readyz == HttpStatusCode.OK,
                $"under {environmentName}, with every identity under .test, the test host did not come up: {contained.Describe()}");
        }

        var outside = Sabotage.Choose(AuthorityUnderTest, "https://idp-a.example.com", "https://idp-a.example.test");
        await using var refused = await fixture.Environment.StartupAsync(
            $"test-host-{environmentName.ToLowerInvariant()}-outside", delta.Set(key, outside), ServerBuild.TestHost);
        output.WriteLine($"{environmentName}, {key}={outside}: {refused.Describe()}");

        // contract-005 · G-17 round 2 (CON-R2-03) — named as the setting, and as idp-a's scheme was built: its Authority, and the
        // MetadataAddress the framework derives from it, where the scheme fetches its keys.
        string[] named = [$"{key} is '{outside}'", $"The {IdpAScheme} scheme's Authority is '{outside}'", $"The {IdpAScheme} scheme's MetadataAddress is '{outside}/"];
        Claim.True(
            refused.ExitCode == 78 && named.All(n => refused.Refusal?.Contains(n, StringComparison.Ordinal) == true),
            $"under {environmentName}, the test host given {key}={outside}, a host not under .test, was to exit 78 naming each of "
            + $"[{string.Join("; ", named)}]: {refused.Describe()}");
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-03) — the branches of the test host's containment an Authority does not reach: an
    /// Issuer outside .test, with idp-a's Authority kept under .test, is named as the setting, among the issuers idp-a's scheme
    /// accepts, and among the authorization servers the metadata publishes; a Resource outside .test, as the setting and as the
    /// metadata publishes it. Under Production and under Staging each; the Authority rows show the test host coming up under both
    /// names with every identity under .test, so a refusal here that names the value in every place it reached is that value's.
    /// </summary>
    [Theory]
    [InlineData("Issuer", "Production")]
    [InlineData("Issuer", "Staging")]
    [InlineData("Resource", "Production")]
    [InlineData("Resource", "Staging")]
    [Sabotage(IssuerOrResourceUnderTest, SabotageActs.ContainerEnvironment,
        "The row's Issuer or Resource the test host is given is a host under .test (https://idp-a.example.test, "
        + "https://mcp.example.test/mcp) instead of one outside it (https://idp-a.example.com, https://mcp.example.com/mcp).")]
    public async Task T8_the_test_host_exits_78_given_an_issuer_or_a_resource_outside_test(string setting, string environmentName)
    {
        var (key, outside) = setting switch
        {
            "Issuer" => ($"Authentication:IdentityProviders:{E2EEnvironment.IdpA}:Issuer",
                Sabotage.Choose(IssuerOrResourceUnderTest, "https://idp-a.example.com", "https://idp-a.example.test")),
            "Resource" => ("Authentication:Resource",
                Sabotage.Choose(IssuerOrResourceUnderTest, "https://mcp.example.com/mcp", "https://mcp.example.test/mcp")),
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Issuer or Resource"),
        };

        // Every place the value reached: the setting, and what the server was built with from it.
        string[] named = setting == "Issuer"
            ? [$"{key} is '{outside}'", $"An issuer the {IdpAScheme} scheme accepts is '{outside}'", $"An authorization server the metadata publishes is '{outside}'"]
            : [$"{key} is '{outside}'", $"The resource the metadata publishes is '{outside}'"];

        await using var refused = await fixture.Environment.StartupAsync(
            $"test-host-{environmentName.ToLowerInvariant()}-{setting.ToLowerInvariant()}-outside", ContainmentDelta(environmentName).Set(key, outside), ServerBuild.TestHost);
        output.WriteLine($"{environmentName}, {key}={outside}: {refused.Describe()}");

        Claim.True(
            refused.ExitCode == 78 && named.All(n => refused.Refusal?.Contains(n, StringComparison.Ordinal) == true),
            $"under {environmentName}, the test host given {key}={outside}, a host not under .test, was to exit 78 naming each of "
            + $"[{string.Join("; ", named)}]: {refused.Describe()}");
    }

    /// <summary>
    /// The test host's settings under <paramref name="environmentName"/>: <see cref="TestHost.Delta"/>, and, outside Production,
    /// the providers to serve, which only appsettings.Production.json names.
    /// </summary>
    private static SettingsDelta ContainmentDelta(string environmentName)
    {
        var delta = TestHost.Delta().Set("ASPNETCORE_ENVIRONMENT", environmentName);
        return environmentName == "Production" ? delta : delta.Set("Providers:Enabled:0", "Smhi").Set("Providers:Enabled:1", "SmhiObs");
    }

    [Fact]
    [Sabotage(AlsoServesJsonPlaceholder, SabotageActs.ContainerEnvironment,
        "The class's test host also enables JsonPlaceholder (Providers:Enabled:4), a built-in provider that is not a test module "
        + "and that the shipped image does not serve.")]
    public async Task T8_the_test_hosts_frame_line_is_the_shipped_images_but_for_the_test_modules()
    {
        var testHost = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        await using var shipped = await fixture.Environment.StartupAsync("test-host-shipped-frame", SettingsDelta.None);
        var image = FrameLine.Parse(await shipped.FrameLineAsync());
        output.WriteLine($"shipped:   {image.Text}");
        output.WriteLine($"test host: {testHost.Text}");

        // Self-check: each line is exactly its parts, so comparing the parts is comparing the whole lines.
        Assert.Equal(image.Text, Rebuilt(image.Limits, image.Providers, image.RequestKinds, image.Filters, image.Manifest));
        Assert.Equal(testHost.Text, Rebuilt(testHost.Limits, testHost.Providers, testHost.RequestKinds, testHost.Filters, testHost.Manifest));

        bool IsTestModules(string entry) => TestHost.Modules.Any(m => entry.Contains($":{m}/", StringComparison.Ordinal));
        var addedProviders = testHost.Providers.Except(image.Providers, StringComparer.Ordinal).ToArray();
        var addedEntries = testHost.Manifest.Except(image.Manifest, StringComparer.Ordinal).ToArray();
        var withoutTheModules = Rebuilt(
            testHost.Limits,
            [.. testHost.Providers.Where(p => !TestHost.Modules.Contains(p, StringComparer.Ordinal))],
            testHost.RequestKinds,
            testHost.Filters,
            [.. testHost.Manifest.Where(e => !IsTestModules(e))]);

        Claim.True(
            image.Limits == "Redis" && testHost.Limits == "Redis"
                && addedProviders.SequenceEqual(TestHost.Modules)
                && addedEntries.Length > 0 && addedEntries.All(IsTestModules)
                && withoutTheModules == image.Text,
            $"the test host's frame line is not the shipped image's plus its test modules. limits: {image.Limits} and {testHost.Limits}; "
            + $"providers added: [{string.Join(", ", addedProviders)}] (expected [{string.Join(", ", TestHost.Modules)}]); manifest lines added: "
            + $"[{string.Join("; ", addedEntries)}]; manifest lines gone: [{string.Join("; ", image.Manifest.Except(testHost.Manifest, StringComparer.Ordinal))}]; "
            + $"filters {(image.Filters == testHost.Filters ? "equal" : $"differ: '{image.Filters}' and '{testHost.Filters}'")}.");
    }

    /// <summary>
    /// A frame line from its parts. contract-005 · G-11 — with the governed request kinds after the providers, where the
    /// line names them.
    /// </summary>
    private static string Rebuilt(string limits, IEnumerable<string> providers, IReadOnlyList<string> requests, string filters, IEnumerable<string> manifest) =>
        $"Frame installed: limits={limits} providers={string.Join(",", providers)}"
        + (requests.Count > 0 ? $" requests={string.Join(",", requests)}" : string.Empty)
        + $" :: {filters} | {string.Join(";", manifest)}";
}
