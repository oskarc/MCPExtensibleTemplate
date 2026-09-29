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

    /// <summary>The weather symbols' URI, which the SDK matches ignoring the case of its scheme and host, with an empty path read as /.</summary>
    private const string HiddenRespelled = "SMHI://WEATHER-SYMBOLS/";

    /// <summary>A URI no resource has, spelled the same way.</summary>
    private const string MissingRespelled = "SMHI://NO-SUCH-THING/";

    /// <summary>
    /// contract-005 · G-17 round 2 (SEC-R2-02) — the SDK matches a resource read ignoring the case of the URI's scheme and host
    /// and reading an empty path as /, and the refusal named the resource's own URI, the key it was matched to: a caller who
    /// respelled a hidden resource's URI was told it exists, and what it is, while a missing one was echoed as sent. The reply
    /// names the URI as the caller sent it, so the hidden one respelled gets the words a missing one spelled the same way gets,
    /// whether it is hidden by its scope or by its trust domain. The log keeps the key it was matched to, and the rule: for
    /// another trust domain's resource, idp-binding (the pioneer's decision 1 (A), as for a tool).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T3_a_hidden_resource_respelled_is_refused_in_the_words_a_missing_one_spelled_the_same_way_gets(bool otherTrustDomain)
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var partner = new TestIdentityProvider("partner", "https://partner.example.com/");
        using var log = new CapturedLog();
        await using var server = await InProcessServer.StartAsync([corp, partner], modules: Everything, logs: log);
        var caller = otherTrustDomain
            ? partner.MintToken(GateClient.Resource, scopes: ["weather:read", "observations:read"])
            : corp.MintToken(GateClient.Resource, scopes: ["observations:read"]);

        // Positive control: the respelling names the resource. Its owner, holding its scope, reads it by that spelling.
        var owner = await GateClient.RpcAsync(server, corp.MintToken(GateClient.Resource, scopes: ["weather:read"]), "resources/read", new { uri = HiddenRespelled });
        Assert.Contains("Weather Symbol Codes", owner.GetRawText(), StringComparison.Ordinal);

        var hidden = GateClient.TextOf(await GateClient.RpcAsync(server, caller, "resources/read", new { uri = HiddenRespelled }));
        var missing = GateClient.TextOf(await GateClient.RpcAsync(server, caller, "resources/read", new { uri = MissingRespelled }));

        Assert.Contains("rule: not-permitted", missing, StringComparison.Ordinal);
        Assert.Contains($"'{HiddenRespelled}'", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("smhi://weather-symbols", hidden, StringComparison.Ordinal);
        Assert.Equal(
            missing.Replace(MissingRespelled, "X", StringComparison.Ordinal),
            hidden.Replace(HiddenRespelled, "X", StringComparison.Ordinal));

        // The log names the key the read was matched to, and the rule that refused it.
        var rule = otherTrustDomain ? "idp-binding" : "not-permitted";
        Assert.Contains(
            log.Lines,
            l => l.Contains($"authz_fail: rule={rule} principal={(otherTrustDomain ? "partner" : "corp")}:", StringComparison.Ordinal)
                && l.Contains(" target=smhi://weather-symbols ", StringComparison.Ordinal));
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (SEC-R2-02) — a resource served under a URI template is matched to any URI the template
    /// describes, and the refusal named the template: any concrete URI exposed a hidden template, even spelled exactly. The
    /// reply names the URI sent, never the template, in the words a URI no resource has gets; the log names the template.
    /// </summary>
    [Fact]
    public async Task T3_a_hidden_templated_resource_read_by_a_uri_it_describes_is_refused_naming_that_uri_and_not_the_template()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var log = new CapturedLog();
        await using var server = await InProcessServer.StartAsync([corp], modules: [new TemplatedResourceModule()], logs: log);
        var caller = corp.MintToken(GateClient.Resource, scopes: ["weather:read"]);

        // Positive control: a caller holding its scope reads the item by that URI.
        var owner = await GateClient.RpcAsync(server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]), "resources/read", new { uri = "test://item/42" });
        Assert.Contains("item 42", owner.GetRawText(), StringComparison.Ordinal);

        var hidden = GateClient.TextOf(await GateClient.RpcAsync(server, caller, "resources/read", new { uri = "test://item/42" }));
        var missing = GateClient.TextOf(await GateClient.RpcAsync(server, caller, "resources/read", new { uri = "test://nothing/42" }));

        Assert.Contains("'test://item/42'", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("{id}", hidden, StringComparison.Ordinal);
        Assert.Equal(
            missing.Replace("test://nothing/42", "X", StringComparison.Ordinal),
            hidden.Replace("test://item/42", "X", StringComparison.Ordinal));
        Assert.Contains(
            log.Lines,
            l => l.Contains("authz_fail: rule=not-permitted", StringComparison.Ordinal)
                && l.Contains($" target={TestItemResources.Template} ", StringComparison.Ordinal));
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

    /// <summary>
    /// contract-005 · G-17 round 1, follow-up — a token that says it was issued later than now, past the clock skew its
    /// validation allows (30 seconds), has no age anyone can tell, and counted as fresh until its iat plus a freshness
    /// window, however long ago it was really issued. It is refused at authentication, on every request — a read among
    /// them, which no freshness gate ever saw — with the reason logged as authn_login_fail. Inside the skew it is served,
    /// as the validation treats it.
    /// </summary>
    [Fact]
    public async Task T6_a_token_that_says_it_was_issued_in_the_future_is_refused_at_authentication_even_for_a_read()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var log = new CapturedLog();
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything, logs: log);
        var call = new { name = "test_echo", arguments = new { text = "hi" } };

        // Positive control: inside the skew, the read is served.
        Assert.Equal("hi", GateClient.TextOf(await GateClient.RpcAsync(server, IssuedIn(corp, TimeSpan.FromSeconds(10)), "tools/call", call)));

        var (status, body) = await RawRpcAsync(server, IssuedIn(corp, TimeSpan.FromMinutes(10)), "tools/call", call);
        var logged = log.Lines.Where(l => l.Contains("authn_login_fail", StringComparison.Ordinal)).ToArray();

        Assert.True(
            status == System.Net.HttpStatusCode.Unauthorized,
            $"a read called with a token that says it was issued 10 minutes from now got {(int)status}: {body}");
        Assert.Contains(logged, l => l.Contains("issued", StringComparison.Ordinal) && l.Contains("from now", StringComparison.Ordinal));
    }

    /// <summary>
    /// contract-005 · G-17 round 1 — defence in depth: the write and irreversible gates refuse such a token by their own
    /// rule, token-age, should one ever reach them. Authentication's own check is taken out of the way by giving it a clock
    /// ten minutes ahead, by which the token was issued now; the gate keeps the server's own clock.
    /// </summary>
    [Theory]
    [InlineData("test_update")]
    [InlineData("test_destroy")]
    public async Task T6_a_token_that_says_it_was_issued_in_the_future_is_refused_for_its_age_by_the_gate_too(string tool)
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything, identityClock: new ShiftedClock(TimeSpan.FromMinutes(10)));
        var call = new { name = tool, arguments = tool == "test_update" ? (object)new { value = "v" } : new { target = "zeta" } };

        // Positive control: inside the skew the token passes the age gate — the write is served, and the irreversible call goes
        // on to ask for a confirmation, which this client cannot give.
        var inside = GateClient.TextOf(await GateClient.RpcAsync(server, IssuedIn(corp, TimeSpan.FromSeconds(10)), "tools/call", call));
        Assert.Contains(tool == "test_update" ? "set v" : "confirmation-unsupported", inside, StringComparison.Ordinal);

        var text = GateClient.TextOf(await GateClient.RpcAsync(server, IssuedIn(corp, TimeSpan.FromMinutes(10)), "tools/call", call));

        Assert.Contains("rule: token-age", text, StringComparison.Ordinal);
        Assert.Contains("from now", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token with test:act that says it was issued <paramref name="fromNow"/> from now, valid from a minute ago and for
    /// twenty, so the validation's own nbf and exp checks let it through: as a token with no nbf would pass.
    /// </summary>
    private static string IssuedIn(TestIdentityProvider idp, TimeSpan fromNow)
    {
        var now = DateTime.UtcNow;
        return idp.MintToken(GateClient.Resource, scopes: ["test:act"], issuedAt: now + fromNow, notBefore: now.AddMinutes(-1), expires: now.AddMinutes(20));
    }

    /// <summary>One JSON-RPC request, and the status and body it got, whatever they are: a refused token gets no JSON-RPC answer.</summary>
    private static async Task<(System.Net.HttpStatusCode Status, string Body)> RawRpcAsync(InProcessServer server, string token, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, InProcessServer.McpPath)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        using var response = await server.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A clock <paramref name="ahead"/> of the system's.</summary>
    private sealed class ShiftedClock(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }

    /// <summary>
    /// A log that keeps every line the server writes, for a test that reads what a refusal logged. contract-005 · T-5 — also
    /// contract-002's T-6 (ScopeAndBindingEnforcementTests), which reads the rule a refusal of another trust domain's tool logs.
    /// </summary>
    internal sealed class CapturedLog : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<string> lines) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category}: {formatter(state, exception)}");
        }
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
