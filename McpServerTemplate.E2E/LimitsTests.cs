using System.Globalization;
using System.Net;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-7 (G-8, G-9 · UC-7) — limits on the image.
///
/// Two servers of the shipped image share a Redis of this class's own, with Limits:PerPrincipalPerMinute set to
/// <see cref="PerPrincipalPerMinute"/>: below every tool's own per-caller limit (the lowest, get_monthly_climate's, is 10)
/// and below the per-address 60. One caller's requests are counted in Redis, so its count carries from one server to
/// the other and its next request is refused with caller-rate on a server that has itself served fewer than the limit.
/// Another caller, and the same subject arriving from the other identity provider, are other callers, and are served.
/// The per-address limit, 60 a minute, counts by the address the front forwards (G-9): one address gets 429 on its 61st
/// request while another, through the same front, gets 200. And a server whose Redis is stopped refuses every governed
/// request with limits-unavailable, rather than let it through uncounted.
///
/// Weather (Smhi) is bound to idp-a and observations (SmhiObs) to idp-b, so each issuer's callers have something to
/// call; what each server serves and what it counts in are read from its own startup line (G-11).
/// </summary>
public sealed class LimitsTests(LimitsTests.Servers fixture, ITestOutputHelper output)
    : IClassFixture<LimitsTests.Servers>
{
    /// <summary>
    /// Limits:PerPrincipalPerMinute for this class's servers, stated: below every tool's own limit (10 is the lowest) and
    /// below the per-address 60, so the caller's own limit is the one reached.
    /// </summary>
    public const int PerPrincipalPerMinute = 6;

    /// <summary>The per-address limit (HttpServerComposition, contract-005's magnitudes table).</summary>
    private const int PerAddressPerMinute = 60;

    private const string IssuerA = "idp-a.e2e.test";
    private const string IssuerB = "idp-b.e2e.test";

    /// <summary>A static resource of the weather provider: a governed request that reaches no upstream.</summary>
    private const string Resource = "smhi://coverage-area";

    /// <summary>A tool of the observations provider, called with a month it refuses itself: governed, and no upstream call.</summary>
    private const string ObservationsTool = "get_monthly_climate";

    private const string ServerBInTheEnvironmentsRedis = "t7-server-b-counts-in-the-environments-redis";
    private const string OtherCallerIsTheSame = "t7-other-caller-minted-for-the-same-subject";
    private const string SecondAsFirst = "t7-second-address-forwarded-as-the-first";
    private const string RedisLeftRunning = "t7-redis-left-running";

    /// <summary>The two servers, sharing a Redis of this class's own.</summary>
    public sealed class Servers : IAsyncLifetime
    {
        public E2EEnvironment Environment { get; private set; } = null!;

        public RedisService Redis { get; private set; } = null!;

        public ServerUnderTest A { get; private set; } = null!;

        public ServerUnderTest B { get; private set; } = null!;

        /// <summary>The client address the front forwards for this class's governed requests (G-9).</summary>
        public string ClientAddress { get; } = ClientAddresses.Next();

        /// <summary>A server's settings: this class's Redis and limit, and each provider bound to its issuer.</summary>
        public static SettingsDelta Delta(RedisService redis) => SettingsDelta.None
            .Set("Limits:Redis", redis.Endpoint)
            .Set("Limits:PerPrincipalPerMinute", PerPrincipalPerMinute.ToString(CultureInfo.InvariantCulture))
            .Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA)
            .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpB);

        public async Task InitializeAsync()
        {
            Environment = await E2EEnvironment.GetAsync();
            Redis = await Environment.StartRedisAsync("redis-limits.e2e.test");
            A = await Environment.StartServerAsync("limits-a", Delta(Redis));
            B = await Environment.StartServerAsync(
                "limits-b", Delta(Redis).Sabotaged(ServerBInTheEnvironmentsRedis, d => d.Set("Limits:Redis", RedisService.ConnectionString)));
        }

        public async Task DisposeAsync()
        {
            foreach (var server in new[] { A, B }.Where(s => s is not null))
            {
                await server.DisposeAsync();
            }

            if (Redis is not null)
            {
                await Redis.DisposeAsync();
            }
        }
    }

    /// <summary>Two claims, and a sabotage for each.</summary>
    [Fact]
    [Sabotage(ServerBInTheEnvironmentsRedis, SabotageActs.ContainerEnvironment,
        "Server B counts in the environment's own Redis (Limits:Redis=redis.e2e.test:6379) instead of this class's, so the two "
        + "servers count apart and the request after the limit is only server A's fourth.")]
    [Sabotage(OtherCallerIsTheSame, SabotageActs.Inputs,
        "The other caller's token is minted at idp-a for the exhausted caller's own subject, so it is the same caller, not another.")]
    public async Task T7_one_callers_count_carries_across_both_servers_while_other_callers_are_served()
    {
        using var viaA = fixture.A.CreateClient(fixture.ClientAddress);
        using var viaB = fixture.B.CreateClient(fixture.ClientAddress);

        // The image's own account (G-11): both servers count in Redis, and serve what the requests below name.
        foreach (var server in new[] { fixture.A, fixture.B })
        {
            var line = FrameLine.Parse(await server.StartupLineAsync());
            var entries = line.Entries(E2EEnvironment.Issuers.Entries.SelectMany(e => e.ScopeCatalog));
            Assert.Equal("Redis", line.Limits);
            Assert.Contains(new FrameLine.Entry("resource", "Smhi", Resource, "weather:read", null), entries);
            Assert.Contains(new FrameLine.Entry("tool", "SmhiObs", ObservationsTool, "observations:read", "Read"), entries);
        }

        var subject = $"e2e-limits-{Guid.NewGuid():N}";
        var caller = await TokenAsync(viaA, IssuerA, subject, "weather:read");

        // The caller's limit, spent on both servers in turn: A, B, A, B, … Each is served.
        var spent = new List<string>();
        for (var i = 0; i < PerPrincipalPerMinute; i++)
        {
            var (name, http) = i % 2 == 0 ? ("A", viaA) : ("B", viaB);
            var answer = await ReadAsync(http, caller);
            spent.Add($"{name}: {(McpRequests.ReturnsContent(answer) ? "served" : McpRequests.TextOf(answer))}");
        }

        // The next request, on A, which has itself served only half of the limit.
        var servedByA = (PerPrincipalPerMinute + 1) / 2;
        var next = await ReadAsync(viaA, caller);
        var refusal = McpRequests.TextOf(next);
        output.WriteLine($"Limit {PerPrincipalPerMinute}: {string.Join("; ", spent)}; then on A: {refusal}");

        Claim.True(
            spent.All(s => s.EndsWith(": served", StringComparison.Ordinal))
                && McpRequests.RuleOf(refusal) == "caller-rate" && !McpRequests.ReturnsContent(next),
            $"with Limits:PerPrincipalPerMinute={PerPrincipalPerMinute} and both servers counting in {fixture.Redis.Endpoint}, the caller's "
            + $"requests went [{string.Join("; ", spent)}], and its next, on server A — which had served {servedByA} of them — got "
            + $"'{(McpRequests.ReturnsContent(next) ? next.GetRawText() : refusal)}', not a caller-rate refusal: the count did not carry "
            + "from one server to the other.");

        // Other callers: another subject at the same issuer, and the same subject at the other issuer.
        var another = await ReadAsync(
            viaA, await TokenAsync(viaA, IssuerA, Sabotage.Choose(OtherCallerIsTheSame, $"e2e-limits-other-{Guid.NewGuid():N}", subject), "weather:read"));
        var sameSubjectOtherIssuer = await McpRequests.ExchangeAsync(viaA, McpRequests.Rpc(
            ServerUnderTest.Endpoint,
            await TokenAsync(viaA, IssuerB, subject, "observations:read"),
            "tools/call",
            new { name = ObservationsTool, arguments = new { latitude = 59.33, longitude = 18.07, month = 13 } }));
        output.WriteLine($"Another subject at {IssuerA}: {Describe(another)}; {subject} at {IssuerB}: {Describe(sameSubjectOtherIssuer)}");

        Claim.True(
            McpRequests.ReturnsContent(another) && McpRequests.RuleOf(McpRequests.TextOf(sameSubjectOtherIssuer)) is null,
            $"once {subject} at {IssuerA} was refused for its rate, another subject at {IssuerA} got {Describe(another)}, and {subject} "
            + $"at {IssuerB} got {Describe(sameSubjectOtherIssuer)}: each is another caller, with a count of its own, and is served.");
    }

    [Fact]
    [Sabotage(SecondAsFirst, SabotageActs.Inputs,
        "The second address's request is forwarded as the first address instead of a fresh one, so it shares the count the first has spent.")]
    public async Task T7_one_forwarded_address_gets_429_on_its_61st_request_while_another_gets_200()
    {
        var first = ClientAddresses.Next();
        var second = ClientAddresses.Next();
        using var asFirst = fixture.A.CreateClient(first);
        using var asSecond = fixture.A.CreateClient(Sabotage.Choose(SecondAsFirst, second, first));
        var healthz = new Uri($"https://{TlsFront.Host}/healthz");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i <= PerAddressPerMinute; i++)
        {
            using var response = await asFirst.GetAsync(healthz);
            statuses.Add(response.StatusCode);
        }

        using var other = await asSecond.GetAsync(healthz);
        output.WriteLine($"{first}: {statuses.Count(s => s == HttpStatusCode.OK)} of {PerAddressPerMinute} served, request {PerAddressPerMinute + 1} got "
            + $"{(int)statuses[PerAddressPerMinute]}; {second}, next: {(int)other.StatusCode}.");

        // Phase 1's note (UC-1 edge): with X-Forwarded-For lost, every request through the front counts as the front's own
        // address, which reads exactly like the product's 429. Each message names that cause.
        var early = statuses.Take(PerAddressPerMinute).Select((status, i) => (Status: status, Number: i + 1)).Where(s => s.Status != HttpStatusCode.OK).ToList();
        Claim.True(
            early.Count == 0 && statuses[PerAddressPerMinute] == HttpStatusCode.TooManyRequests && other.StatusCode == HttpStatusCode.OK,
            $"the forwarded address {first} got {(early.Count == 0 ? "200 on each of its first 60 requests" : $"{(int)early[0].Status} on its request {early[0].Number}")} "
            + $"and {(int)statuses[PerAddressPerMinute]} on its 61st; {second}, through the same front just after, got {(int)other.StatusCode}. "
            + "One address is throttled at 61 and another served only if the server counts by the X-Forwarded-For the front sets: if that "
            + "header was lost, every request shares the front's own count, a fresh address is refused for what others spent, and it reads "
            + "as the product's 429 (forwarded address not honoured).");
    }

    /// <summary>
    /// A server of this test's own, over a Redis of this test's own, so stopping it takes no other server's store. Every
    /// governed request — a resource read and a tool call — is refused with limits-unavailable once Redis is stopped.
    /// </summary>
    [Fact]
    [Sabotage(RedisLeftRunning, SabotageActs.ContainerEnvironment, "The test's Redis is left running instead of being stopped.")]
    public async Task T7_with_redis_stopped_requests_are_refused_limits_unavailable()
    {
        var environment = fixture.Environment;
        await using var redis = await environment.StartRedisAsync("redis-outage.e2e.test");
        await using var server = await environment.StartServerAsync("limits-outage", Servers.Delta(redis));
        using var http = server.CreateClient(ClientAddresses.Next());
        var token = await TokenAsync(http, IssuerA, $"e2e-outage-{Guid.NewGuid():N}", "weather:read");

        // Positive control: while Redis runs, the same request is served.
        var before = await ReadAsync(http, token);
        Assert.True(McpRequests.ReturnsContent(before), $"with Redis running, reading {Resource} got {Describe(before)}.");

        if (!Sabotage.Applies(RedisLeftRunning))
        {
            await redis.StopAsync();
        }

        var read = await ReadAsync(http, token);
        var call = await McpRequests.ExchangeAsync(http, McpRequests.Rpc(
            ServerUnderTest.Endpoint, token, "tools/call", new { name = "get_forecast", arguments = new { latitude = 59.33, longitude = 18.07 } }));
        output.WriteLine($"With {redis.Endpoint} stopped: resources/read got {Describe(read)}; tools/call got {Describe(call)}");

        Claim.True(
            new[] { read, call }.All(a => McpRequests.RuleOf(McpRequests.TextOf(a)) == "limits-unavailable" && !McpRequests.ReturnsContent(a)),
            $"with the server's Redis ({redis.Endpoint}) stopped, reading {Resource} got {Describe(read)} and calling get_forecast got "
            + $"{Describe(call)}: each must be refused with limits-unavailable and nothing returned, never let through uncounted.");
    }

    /// <summary>A token idp issues now to <paramref name="subject"/>.</summary>
    private static Task<string> TokenAsync(HttpClient http, string issuer, string subject, string scope) =>
        TestIssuerService.MintAsync(http, issuer, "valid", ServerUnderTest.Resource, [scope], new Dictionary<string, object> { ["subject"] = subject });

    private static Task<JsonElement> ReadAsync(HttpClient http, string token) =>
        McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, "resources/read", new { uri = Resource }));

    private static string Describe(JsonElement answer) =>
        McpRequests.ReturnsContent(answer) ? "served" : $"'{McpRequests.TextOf(answer)}'";
}
