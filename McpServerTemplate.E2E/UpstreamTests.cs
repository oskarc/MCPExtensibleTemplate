using System.Text.RegularExpressions;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-6 (G-7, G-8, G-11 · UC-6) — upstreams: every tool on the startup line is called once and seen at its
/// fake, and no record any fake keeps, anywhere in the run, carries the caller's token.
///
/// Seen at its fake: during the tool's call, the container the upstream registry gives the tool's provider host records a
/// request from this server under that host (<see cref="E2EEnvironment.UpstreamFor"/>, never assumed to be WireMock).
/// SmhiApiClient keeps the forecast model's reference time from every forecast it fetches, and get_forecast_model_info
/// answers from it, calling nothing, while it is fresh; so the forecast's information is called first, as the first SMHI
/// call on this class's fresh server, while that cache is cold. The token: every record of every upstream owner, whole — its
/// URL, every header, its body (<see cref="IUpstreamService.WholeRecordsAsync"/>) — holds neither the caller's token nor its
/// signature, nor any other JSON web token; that this server's calls were recorded, the first claim, is the witness.
///
/// contract-005 · G-17 round 2 (HAR-R2-02) — this server's calls: each owner's record is read from this server's address since
/// it started (<see cref="RequestSource"/>). contract-005 · G-17 round 2 (HAR-R2-06) — and the search's own positive control:
/// before it, every owner is sent one request of the test's own, a marker in a header and another as its body
/// (<see cref="IUpstreamService.SendWitnessAsync"/>), and its whole record must hold both. Nothing had ever seen the stand-in's
/// record hold a header or a body: a record that kept neither would have passed the search on its URL and host alone.
///
/// The class's server serves every built-in provider — JsonPlaceholder too, which Production leaves out, so write tools'
/// bodies are among what is searched — each bound to idp-a, which mints the caller's token with every scope it may. What it
/// serves is read from its own startup line (G-11); where each provider calls, and each tool's arguments, are the suite's
/// own tables (<see cref="ProviderCalls"/>).
///
/// The pioneer's decision of 2026-09-29 (contract-005's revision of that date, "2. yes, cut it and mock any calls") cut the
/// run's network off from real names (<see cref="E2ENetwork.NameServer"/>): every call a server of the run makes is answered
/// by a fake or fails at the name lookup. <see cref="UpstreamNamesTests"/> holds T-6's last clause, the name lookup.
/// </summary>
public sealed partial class UpstreamTests(UpstreamTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<UpstreamTests.Server>
{
    /// <summary>Every built-in provider, each bound to idp-a.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Set("Providers:Enabled:2", "JsonPlaceholder")
        .Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA)
        .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpA)
        .Set("Providers:JsonPlaceholder:IdentityProvider", E2EEnvironment.IdpA));

    private const string IssuerA = "idp-a.e2e.test";

    /// <summary>The tool the forecast's cache answers: called first, while the cache is cold.</summary>
    private const string ModelInfo = "get_forecast_model_info";

    private const string RefusedBeforeItsUpstream = "t6-the-first-tool-refused-before-its-upstream";
    private const string TokenSentToTheFake = "t6-the-callers-token-sent-to-the-fake";

    [Fact]
    [Sabotage(RefusedBeforeItsUpstream, SabotageActs.Inputs,
        "The first tool called, get_forecast_model_info, is given an argument its schema does not declare, so the frame refuses "
        + "it (extraneous-argument) before its upstream is called.")]
    [Sabotage(TokenSentToTheFake, SabotageActs.Inputs,
        "Before the records are read, the test sends one request of its own to the fake (https://wiremock.e2e.test/e2e-t6-passthrough) "
        + "carrying the caller's token in its Authorization header, as a server that passed the token through would.")]
    public async Task T6_every_tool_is_seen_at_its_fake_and_no_record_anywhere_in_the_run_carries_the_callers_token()
    {
        var environment = fixture.Environment;
        using var http = fixture.CreateClient();
        var server = await fixture.Server.RequestSourceAsync();

        // What the image serves, from its own startup line (G-11).
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        var tools = line.Entries(E2EEnvironment.Issuers.Entries.SelectMany(e => e.ScopeCatalog)).Where(e => e.Kind == "tool").ToList();

        // Positive controls: every built-in provider is served, and every tool on the line has its host and its arguments in
        // the suite's tables — a tool they do not name fails here, rather than going uncalled.
        Assert.True(
            ProviderCalls.Hosts.Keys.All(p => line.Providers.Contains(p, StringComparer.Ordinal)),
            $"the class's server does not serve every built-in provider ({string.Join(", ", ProviderCalls.Hosts.Keys)}): {line.Text}");
        var untabled = tools
            .Where(t => !ProviderCalls.Hosts.ContainsKey(t.Provider) || !ProviderCalls.Arguments.ContainsKey(t.Key))
            .Select(t => $"{t.Provider}/{t.Key}")
            .ToArray();
        Assert.True(untabled.Length == 0, $"these tools on the startup line have no host or no arguments in {nameof(ProviderCalls)}: {string.Join(", ", untabled)}.");
        Assert.Contains(tools, t => t.Key == ModelInfo);

        var token = await TestIssuerService.MintAsync(
            http, IssuerA, "valid", ServerUnderTest.Resource, E2EEnvironment.Issuers[E2EEnvironment.IdpA].ScopeCatalog);

        // The forecast's information first: the first SMHI call on this fresh server, while its cache is cold. Each call is then
        // seen, or not, at the owner of its provider's host, from this server.
        var unseen = new List<string>();
        foreach (var tool in tools.OrderBy(t => t.Key == ModelInfo ? 0 : 1))
        {
            var host = ProviderCalls.Hosts[tool.Provider];
            var owner = environment.UpstreamFor(host);
            var before = (await ProviderCalls.FromServerAsync(owner, http, server, host)).Count;

            var arguments = tool.Key == ModelInfo && Sabotage.Applies(RefusedBeforeItsUpstream) ? new { extra = 1 } : ProviderCalls.Arguments[tool.Key];
            var answer = McpRequests.TextOf(await ProviderCalls.CallAsync(http, token, tool.Key, arguments));
            var seen = await ProviderCalls.SeenSinceAsync(owner, http, server, host, before);
            output.WriteLine($"{tool.Key}: {seen} request(s) at {host}, answered by {owner.Container.Name.TrimStart('/')}; the tool answered '{answer}'");

            if (seen <= 0)
            {
                unseen.Add($"{tool.Key} (its provider's host {host}; it answered '{answer}')");
            }
        }

        Claim.True(
            unseen.Count == 0,
            $"these tools on the startup line were called, and their fake recorded no request from this server, {server}, during the "
            + $"call: {string.Join("; ", unseen)}.");

        // contract-005 · G-17 round 2 (HAR-R2-06) — the search's positive control: every owner is sent a request of the test's own,
        // a marker in a header and another as its body, before its records are read.
        var marker = $"e2e-t6-witness-{Guid.NewGuid():N}";
        var (inHeader, inBody) = ($"{marker}-header", $"{marker}-body");
        foreach (var service in environment.UpstreamServices.Values)
        {
            await service.SendWitnessAsync(http, inHeader, inBody);
        }

        if (Sabotage.Applies(TokenSentToTheFake))
        {
            using var passedThrough = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{WireMockService.Alias}/e2e-t6-passthrough"));
            passedThrough.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var answered = await http.SendAsync(passedThrough);
        }

        // Every record of every upstream owner in the run, whole.
        var (searched, carrying, blind) = await SearchAsync(environment, http, token, marker, inHeader, inBody);

        // Positive control: each owner's record holds a request's header and its body, so a token in either would be found.
        Assert.True(
            blind.Count == 0,
            $"a request sent with {UpstreamWitness.Header}: {inHeader} and the body {inBody} is not held whole in these owners' records, so a "
            + $"token passed on in a header or a body would not be found there: {string.Join("; ", blind)}.");

        output.WriteLine($"Searched {searched} records of {environment.UpstreamServices.Count} upstream owners for the caller's token and for any other.");
        Claim.True(
            carrying.Count == 0,
            $"a fake's record carries a token — a server passed a caller's credential on to an upstream: {string.Join("; ", carrying)}.");
    }

    /// <summary>
    /// Every record of every upstream owner in the run, whole: how many were searched, which carry the caller's token or any
    /// other, and — contract-005 · G-17 round 2 (HAR-R2-06) — which owners hold no record with both the witness's header marker
    /// (<paramref name="inHeader"/>) and its body marker (<paramref name="inBody"/>).
    /// </summary>
    private static async Task<(int Searched, List<string> Carrying, List<string> Blind)> SearchAsync(
        E2EEnvironment environment, HttpClient http, string token, string marker, string inHeader, string inBody)
    {
        var signature = token[(token.LastIndexOf('.') + 1)..];
        var carrying = new List<string>();
        var blind = new List<string>();
        var searched = 0;
        foreach (var (owner, service) in environment.UpstreamServices.OrderBy(u => u.Key.Name, StringComparer.Ordinal))
        {
            var records = await service.WholeRecordsAsync(http);
            if (!records.Any(r => r.Contains(inHeader, StringComparison.Ordinal) && r.Contains(inBody, StringComparison.Ordinal)))
            {
                blind.Add($"{owner.Name} ({records.Count(r => r.Contains(marker, StringComparison.Ordinal))} of its records hold the marker, none both parts)");
            }

            foreach (var record in records)
            {
                searched++;
                if (record.Contains(token, StringComparison.Ordinal) || record.Contains(signature, StringComparison.Ordinal))
                {
                    carrying.Add($"{owner.Name} holds the caller's token in {Excerpt(record, signature)}");
                }
                else if (JsonWebToken().IsMatch(record))
                {
                    carrying.Add($"{owner.Name} holds a token in {Excerpt(record, signature)}");
                }
            }
        }

        return (searched, carrying, blind);
    }

    /// <summary>A record, short, with every token in it replaced, and the caller's signature too, for a message the record keeps.</summary>
    private static string Excerpt(string record, string signature)
    {
        var redacted = SabotageRecorder.Redacted(record).Replace(signature, "[signature]", StringComparison.Ordinal);
        return redacted.Length <= 400 ? $"'{redacted}'" : $"'{redacted[..400]}…'";
    }

    /// <summary>A JSON web token: three base64url parts, the first a JSON object's ("eyJ").</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*")]
    private static partial Regex JsonWebToken();
}
