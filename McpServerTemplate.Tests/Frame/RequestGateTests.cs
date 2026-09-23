using McpServerTemplate.Providers;
using McpServerTemplate.Tests.Identity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-003 · T-2, T-3, T-4, T-5, T-6, T-9 (G-4, G-5, G-6, G-7, G-9) — the request gate,
/// exercised through the SDK's own client against the server composed the shipped way.
/// </summary>
[Collection(TestActTools.Collection)]
public class RequestGateTests
{
    private static readonly IReadOnlyList<McpServerTemplate.Infrastructure.Frame.IProviderModule> Everything =
        [.. BuiltInProviders.Create(), new TestModule()];

    // ── T-2 (G-4): a caller sees what it may use, of every kind ──

    [Fact]
    public async Task T2_a_weather_only_token_lists_only_weather_tools_resources_and_prompts()
    {
        using var idp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([idp], modules: Everything);
        await using var client = await GateClient.ConnectAsync(server, idp.MintToken(GateClient.Resource, scopes: ["weather:read"]));

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToArray();
        var resources = (await client.ListResourcesAsync()).Select(r => r.Uri).Order().ToArray();
        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).ToArray();

        Assert.Equal(["get_current_weather", "get_forecast", "get_forecast_model_info"], tools);
        Assert.Equal(["smhi://coverage-area", "smhi://weather-symbols"], resources);
        Assert.Equal(["forecast_briefing"], prompts);
    }

    [Fact]
    public async Task T2_a_token_from_the_other_identity_provider_sees_none_of_them()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var partner = new TestIdentityProvider("partner", "https://partner.example.com/");
        await using var server = await InProcessServer.StartAsync([corp, partner], modules: Everything);

        // Same scope names, the other trust domain: scope names mean something only within theirs.
        await using var client = await GateClient.ConnectAsync(server, partner.MintToken(
            GateClient.Resource, scopes: ["weather:read", "observations:read", "demo:read", "demo:write", "test:act"]));

        Assert.Empty(await client.ListToolsAsync());
        Assert.Empty(await client.ListResourcesAsync());
        Assert.Empty(await client.ListPromptsAsync());
    }

    // ── T-3 (G-4): using something you may not use returns nothing ──

    [Fact]
    public async Task T3_each_kind_of_forbidden_use_is_refused_with_its_own_rule_and_no_content()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var partner = new TestIdentityProvider("partner", "https://partner.example.com/");
        await using var server = await InProcessServer.StartAsync([corp, partner], modules: Everything);
        var observationsOnly = corp.MintToken(GateClient.Resource, scopes: ["observations:read"]);
        var partnerToken = partner.MintToken(GateClient.Resource, scopes: ["weather:read"]);

        // A hidden tool, named directly.
        var hidden = await GateClient.RpcAsync(server, observationsOnly, "tools/call", new { name = "get_forecast", arguments = new { latitude = 59.3, longitude = 18.0 } });
        Assert.Contains("rule: insufficient_scope", GateClient.TextOf(hidden), StringComparison.Ordinal);
        Assert.DoesNotContain("°", GateClient.TextOf(hidden), StringComparison.Ordinal);

        // A resource of a provider bound to another identity provider.
        var resource = await GateClient.RpcAsync(server, partnerToken, "resources/read", new { uri = "smhi://coverage-area" });
        Assert.Contains("rule: not-permitted", GateClient.TextOf(resource), StringComparison.Ordinal);
        Assert.False(resource.TryGetProperty("result", out _));

        // A prompt without its scope.
        var prompt = await GateClient.RpcAsync(server, observationsOnly, "prompts/get", new { name = "forecast_briefing", arguments = new { location = "Umeå" } });
        Assert.Contains("rule: not-permitted", GateClient.TextOf(prompt), StringComparison.Ordinal);
        Assert.False(prompt.TryGetProperty("result", out _));

        // A completion for that prompt's arguments, which would disclose what it accepts. No
        // provider offers completion, so the SDK answers before the frame's completion check can
        // run: refused, and nothing returned — but by the SDK, not by the frame's rule. The frame's
        // check is installed (T-10 compares it) and is not exercised by a request until a provider
        // offers completion; the contract's untested line says so.
        var completion = await GateClient.RpcAsync(server, observationsOnly, "completion/complete",
            new { @ref = new { type = "ref/prompt", name = "forecast_briefing" }, argument = new { name = "location", value = "U" } });
        Assert.True(completion.TryGetProperty("error", out _));
        Assert.False(completion.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task T3_a_hidden_item_and_a_forbidden_one_are_refused_in_the_same_words()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);
        var token = corp.MintToken(GateClient.Resource, scopes: ["observations:read"]);

        var forbidden = GateClient.TextOf(await GateClient.RpcAsync(server, token, "resources/read", new { uri = "smhi://coverage-area" }));
        var nonexistent = GateClient.TextOf(await GateClient.RpcAsync(server, token, "resources/read", new { uri = "smhi://does-not-exist" }));

        // Only the name differs: the refusal does not confirm that a hidden item exists.
        Assert.Equal(forbidden.Replace("smhi://coverage-area", "X", StringComparison.Ordinal), nonexistent.Replace("smhi://does-not-exist", "X", StringComparison.Ordinal));
    }

    // ── T-4 (G-5): a request kind nobody governs goes no further ──

    [Theory]
    [InlineData("resources/subscribe")]
    [InlineData("logging/setLevel")]
    [InlineData("tasks/list")]
    [InlineData("x-invented/anything")]
    public async Task T4_an_ungoverned_request_kind_is_refused(string method)
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);

        var response = await GateClient.RpcAsync(server, corp.MintToken(GateClient.Resource, scopes: ["weather:read"]), method, new { uri = "smhi://coverage-area", level = "debug" });

        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("rule: request-kind", GateClient.TextOf(response), StringComparison.Ordinal);
    }

    // ── T-5 (G-6): arguments of the wrong shape never reach the tool ──

    [Theory]
    [InlineData("""{"text":"hi","extra":1}""", "extraneous-argument")]
    [InlineData("""{"text":42}""", "argument-schema")]
    [InlineData("""{"text":"this is far longer than twenty characters"}""", "argument-length")]
    [InlineData("""{}""", "argument-schema")]
    public async Task T5_arguments_of_the_wrong_shape_are_refused_before_the_tool_runs(string arguments, string rule)
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);

        var before = TestActTools.Echoes;
        var response = await GateClient.RpcAsync(server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]), "tools/call",
            new { name = "test_echo", arguments = System.Text.Json.JsonDocument.Parse(arguments).RootElement });

        Assert.Contains($"rule: {rule}", GateClient.TextOf(response), StringComparison.Ordinal);
        Assert.Equal(before, TestActTools.Echoes); // the tool never ran
    }

    [Fact]
    public async Task T5_a_latitude_that_is_not_a_number_never_reaches_the_upstream()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);

        // The SMHI base address is unreachable from here by design of this assertion: had the call
        // gone through, the tool would have reported an upstream failure, not this rule.
        var response = await GateClient.RpcAsync(server, corp.MintToken(GateClient.Resource, scopes: ["weather:read"]), "tools/call",
            new { name = "get_forecast", arguments = new { latitude = "NaN", longitude = 18.0 } });

        Assert.Contains("rule: not-a-number", GateClient.TextOf(response), StringComparison.Ordinal);
    }

    // ── T-6 (G-7): a write needs a recent token ──

    [Fact]
    public async Task T6_a_write_with_a_six_minute_old_token_is_refused_and_a_one_minute_old_one_is_allowed()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);

        var stale = corp.MintToken(GateClient.Resource, scopes: ["test:act"], issuedAt: DateTime.UtcNow.AddMinutes(-6));
        var fresh = corp.MintToken(GateClient.Resource, scopes: ["test:act"], issuedAt: DateTime.UtcNow.AddMinutes(-1));
        var call = new { name = "test_update", arguments = new { value = "v" } };

        Assert.Contains("rule: token-age", GateClient.TextOf(await GateClient.RpcAsync(server, stale, "tools/call", call)), StringComparison.Ordinal);
        Assert.Equal("set v", GateClient.TextOf(await GateClient.RpcAsync(server, fresh, "tools/call", call)));
    }

    [Fact]
    public async Task T6_a_read_is_not_aged()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);

        var old = corp.MintToken(GateClient.Resource, scopes: ["test:act"], issuedAt: DateTime.UtcNow.AddMinutes(-8));
        Assert.Equal("hi", GateClient.TextOf(await GateClient.RpcAsync(server, old, "tools/call", new { name = "test_echo", arguments = new { text = "hi" } })));
    }

    // ── T-9 (G-9): an answer over its cap is withheld ──

    [Fact]
    public async Task T9_an_answer_over_the_cap_is_refused_with_the_cap_named()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything);

        var text = GateClient.TextOf(await GateClient.RpcAsync(server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]), "tools/call", new { name = "test_flood", arguments = new { } }));

        Assert.Contains("rule: output-cap", text, StringComparison.Ordinal);
        Assert.Contains("1000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxxxxxxx", text, StringComparison.Ordinal);
    }

    // ── G-4, found during the build: the trust domain is the frame's to name ──

    [Fact]
    public async Task A_token_carrying_its_own_idp_claim_cannot_choose_its_trust_domain()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var partner = new TestIdentityProvider("partner", "https://partner.example.com/");
        await using var server = await InProcessServer.StartAsync([corp, partner], modules: Everything);

        // Entra ID issues an "idp" claim for guest users. A partner token naming corp gets nothing
        // of corp's: the frame names the trust domain from the scheme that validated the token.
        var token = partner.MintToken(GateClient.Resource, scopes: ["weather:read"],
            extraClaims: new Dictionary<string, object> { ["idp"] = "corp", ["mcp_scope"] = "weather:read" });

        await using var client = await GateClient.ConnectAsync(server, token);
        Assert.Empty(await client.ListToolsAsync());
        var refused = await Assert.ThrowsAnyAsync<McpException>(() => client.ReadResourceAsync("smhi://coverage-area").AsTask());
        Assert.Contains("not-permitted", refused.Message, StringComparison.Ordinal);
    }
}
