using McpServerTemplate.Tests.Identity;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-003 · T-8 (G-8) — a caller's limit is one limit, however many instances serve them,
/// and a limit store that cannot answer is a refusal, never permission. Two in-process servers
/// share one Redis container of this test's own, which the last step stops.
/// </summary>
public class LimitsTests
{
    [Fact]
    public async Task T8_the_limit_carries_across_instances_belongs_to_one_caller_and_fails_closed()
    {
        var redis = await TestRedis.StartAsync();
        try
        {
            using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
            using var partner = new TestIdentityProvider("partner", "https://partner.example.com/");
            IReadOnlyList<McpServerTemplate.Infrastructure.Frame.IProviderModule> modules = [new TestModule()];
            void ThreePerMinute(Dictionary<string, string?> s) => s["Limits:PerPrincipalPerMinute"] = "3";

            await using var first = await InProcessServer.StartAsync([corp, partner], configure: ThreePerMinute, modules: modules, redis: redis.GetConnectionString());
            await using var second = await InProcessServer.StartAsync([corp, partner], configure: ThreePerMinute, modules: modules, redis: redis.GetConnectionString());

            var call = new { name = "test_echo", arguments = new { text = "hi" } };
            var alice = corp.MintToken(GateClient.Resource, subject: "alice", scopes: ["test:act"]);

            // Three requests on the first instance, the fourth on the second: one caller, one limit.
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal("hi", GateClient.TextOf(await GateClient.RpcAsync(first, alice, "tools/call", call)));
            }

            Assert.Contains("rule: caller-rate", GateClient.TextOf(await GateClient.RpcAsync(second, alice, "tools/call", call)), StringComparison.Ordinal);

            // Another caller is untouched.
            var bob = corp.MintToken(GateClient.Resource, subject: "bob", scopes: ["test:act"]);
            Assert.Equal("hi", GateClient.TextOf(await GateClient.RpcAsync(second, bob, "tools/call", call)));

            // The same subject from another identity provider is another caller. Its trust domain
            // serves none of these tools, so it is refused — but by the binding, not by the limit.
            var partnerAlice = partner.MintToken(GateClient.Resource, subject: "alice", scopes: ["test:act"]);
            var other = GateClient.TextOf(await GateClient.RpcAsync(second, partnerAlice, "tools/call", call));
            Assert.DoesNotContain("caller-rate", other, StringComparison.Ordinal);

            // With the store gone, requests are refused rather than let through unlimited.
            await redis.StopAsync();
            var carol = corp.MintToken(GateClient.Resource, subject: "carol", scopes: ["test:act"]);
            Assert.Contains("rule: limits-unavailable", GateClient.TextOf(await GateClient.RpcAsync(first, carol, "tools/call", call)), StringComparison.Ordinal);
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }
}
