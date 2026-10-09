using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Audit;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Infrastructure.Identity;
using McpServerTemplate.Tests.Frame;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-1, T-2, T-15 — every decision the frame makes leaves exactly one record, carrying its rule.
///
/// One test per rule, named for it: <c>Rule_insufficient_scope</c> drives the rule <c>insufficient_scope</c> through the
/// SDK's own client and reads what the trail holds afterwards. <see cref="Every_rule_in_the_product_has_a_driver_here"/>
/// reads the rules out of the product's source and fails when one has no test of its name — so a rule added to the gate
/// without a record to show for it is caught here, not found by an investigator.
/// </summary>
[Collection(TestActTools.Collection)]
public sealed partial class AuditDecisionTests
{
    private static readonly IReadOnlyList<IProviderModule> Modules = [new TestModule(), new AuditModule()];

    /// <summary>
    /// Rules the product can write that no request through the frame reaches, each with why. A rule listed here is not
    /// driven; it is the test's standing admission, and the contract's untested line says the same.
    /// </summary>
    private static readonly Dictionary<string, string> NotDriven = new(StringComparer.Ordinal)
    {
        ["unknown-item"] =
            "A completion whose reference is neither a prompt nor a resource template: the SDK knows no third kind of "
            + "reference and rejects one before the frame's check, which Rule_malformed_request records.",
        ["unrecorded"] =
            "The audit step's own late record, for a governed request that was answered with no decision recorded: every "
            + "governed kind records its decision before it is answered, so no request reaches it. It is there so that a "
            + "kind governed later without a record cannot be answered silently.",
    };

    private static Task<StreamServer> ServerAsync(
        System.Security.Claims.ClaimsPrincipal? caller = null,
        bool anonymous = false,
        Action<Dictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? outsideTheFrame = null,
        IReadOnlyList<IProviderModule>? modules = null,
        bool canConfirm = false,
        bool confirms = true,
        AuthenticationConfig? identity = null,
        string? protocolVersion = null) =>
        StreamServer.StartAsync(
            modules ?? Modules,
            caller,
            anonymous,
            configure,
            services =>
            {
                // The SDK's one completion handler, installed beside the composition as the end-to-end test host does.
                services.Configure<McpServerOptions>(o => o.Handlers.CompleteHandler = AuditPrompts.CompleteAsync);
                outsideTheFrame?.Invoke(services);
            },
            identity,
            canConfirm: canConfirm,
            confirms: confirms,
            protocolVersion: protocolVersion);

    /// <summary>The frame composed with no transport and no host, as the startup checks see it.</summary>
    private static ServiceProvider Composed()
    {
        var settings = FrameHarness.Settings(Modules);
        settings["Confirmation:Key"] = Identity.InProcessServer.ConfirmationKey;
        return FrameHarness.Compose(Modules, settings);
    }

    /// <summary>Runs <paramref name="act"/> and returns the records it added to the trail.</summary>
    private static async Task<IReadOnlyList<JsonElement>> AddedAsync(StreamServer server, Func<Task> act)
    {
        var before = server.Records().Count;
        await act();
        return [.. server.Records().Skip(before)];
    }

    private static Task<CallToolResult> CallAsync(StreamServer server, string tool, params (string Name, object? Value)[] arguments) =>
        server.Client.CallToolAsync(tool, arguments.ToDictionary(a => a.Name, a => a.Value)).AsTask();

    /// <summary>One record, refused by <paramref name="rule"/>, with its event and level.</summary>
    private static JsonElement AssertRefused(IReadOnlyList<JsonElement> added, string rule, string @event, string level)
    {
        var record = Assert.Single(added);
        Assert.Equal("deny", AuditFiles.OutcomeOf(record));
        Assert.Equal(rule, AuditFiles.RuleOf(record));
        Assert.Equal(@event, AuditFiles.EventOf(record));
        Assert.Equal(level, record.GetProperty("level").GetString());
        return record;
    }

    // ── One test per rule ────────────────────────────────────────────────────

    [Fact]
    public async Task Rule_principal()
    {
        await using var server = await ServerAsync(anonymous: true);
        var echoes = TestActTools.Echoes;

        var added = await AddedAsync(server, () => CallAsync(server, "test_echo", ("text", "hi")));

        var record = AssertRefused(added, "principal", "authz_fail", "CRITICAL");
        Assert.Equal(JsonValueKind.Null, record.GetProperty("principal").ValueKind);
        Assert.Equal(echoes, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_not_permitted()
    {
        await using var server = await ServerAsync();

        var added = await AddedAsync(server, () => CallAsync(server, "no_such_tool"));

        var record = AssertRefused(added, "not-permitted", "authz_fail", "CRITICAL");
        Assert.Equal("no_such_tool", record.GetProperty("mcp").GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("mcp").GetProperty("provider").ValueKind);
    }

    [Fact]
    public async Task Rule_idp_binding()
    {
        // The providers answer to corp; the caller comes from partner. The caller is told what a missing tool is told,
        // and the trail keeps the rule that refused it, and whose tool it was.
        await using var server = await ServerAsync(
            caller: StreamServer.Caller(identityProvider: "partner"), identity: StreamServer.TwoIdentityProviders());
        var echoes = TestActTools.Echoes;

        CallToolResult? reply = null;
        var added = await AddedAsync(server, async () => reply = await CallAsync(server, "test_echo", ("text", "hi")));

        var record = AssertRefused(added, "idp-binding", "authz_fail", "CRITICAL");
        Assert.Equal("TestAct", record.GetProperty("mcp").GetProperty("provider").GetString());
        Assert.Equal("partner", record.GetProperty("principal").GetProperty("idp").GetString());
        Assert.Contains("not-permitted", string.Join(" ", reply!.Content.OfType<TextContentBlock>().Select(c => c.Text)), StringComparison.Ordinal);
        Assert.Equal(echoes, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_insufficient_scope()
    {
        await using var server = await ServerAsync(caller: StreamServer.Caller(scopes: ["weather:read"]));
        var echoes = TestActTools.Echoes;

        var added = await AddedAsync(server, () => CallAsync(server, "test_echo", ("text", "hi")));

        var record = AssertRefused(added, "insufficient_scope", "authz_fail", "CRITICAL");
        Assert.Equal("Read", record.GetProperty("mcp").GetProperty("risk").GetString());
        Assert.Equal(echoes, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_token_age()
    {
        await using var server = await ServerAsync(caller: StreamServer.Caller(issuedAt: DateTimeOffset.UtcNow.AddMinutes(-6)));
        var changes = AuditTools.Changes;

        var added = await AddedAsync(server, () => CallAsync(server, "audit_change", ("value", "x")));

        var record = AssertRefused(added, "token-age", "authz_fail", "CRITICAL");
        Assert.Equal("Write", record.GetProperty("mcp").GetProperty("risk").GetString());
        Assert.Equal(changes, AuditTools.Changes);
    }

    [Fact]
    public async Task Rule_confirmation_unsupported()
    {
        // A client with no way to be asked to confirm: a stateless HTTP request on a protocol version that has no input
        // round-trip. The irreversible tool is refused, not run and not asked about. Over HTTP, with a real token, because
        // only there does the server know it cannot ask; the check comes before any limit is counted, so no Redis is needed.
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await Identity.InProcessServer.StartAsync([corp], modules: [new TestModule()], redis: NoRedis.ConnectionString);
        var runs = TestActTools.Runs;
        var before = AuditFiles.Records(server.AuditDirectory).Count;

        var reply = await GateClient.RpcAsync(
            server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]), "tools/call", new { name = "test_destroy", arguments = new { target = "x" } });

        Assert.Contains("confirmation-unsupported", GateClient.TextOf(reply), StringComparison.Ordinal);
        AssertRefused([.. AuditFiles.Records(server.AuditDirectory).Skip(before)], "confirmation-unsupported", "authz_fail", "CRITICAL");
        Assert.Equal(runs, TestActTools.Runs);
    }

    [Fact]
    public async Task Rule_confirmation()
    {
        // Asked, and declined: two requests, two records — the question, then the refusal — and nothing ran.
        await using var server = await ServerAsync(canConfirm: true, confirms: false);
        var runs = TestActTools.Runs;

        var added = await AddedAsync(server, () => CallAsync(server, "test_destroy", ("target", "x")));

        Assert.Equal(2, added.Count);
        Assert.Equal("mcp_confirmation_required", AuditFiles.EventOf(added[0]));
        Assert.Equal("challenge", AuditFiles.OutcomeOf(added[0]));
        Assert.Equal("confirmation", AuditFiles.RuleOf(added[0]));
        Assert.Equal("INFO", added[0].GetProperty("level").GetString());
        AssertRefused([added[1]], "confirmation", "authz_fail", "CRITICAL");
        Assert.Equal(runs, TestActTools.Runs);
    }

    [Fact]
    public async Task Rule_confirmation_replayed()
    {
        // One confirmation, presented twice: it runs the tool once. The first is admitted as a confirmed call, the second
        // is refused — and the tool's own counter says it ran once.
        var caller = StreamServer.Caller();
        await using var server = await ServerAsync(caller: caller);
        var runs = TestActTools.Runs;

        var arguments = new Dictionary<string, JsonElement> { ["target"] = JsonSerializer.SerializeToElement("y") };
        var state = server.Services.GetRequiredService<ConfirmationService>()
            .Issue(Caller.From(caller, TimeProvider.System)!, "test_destroy", arguments);
        var confirmed = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "replay",
            method = "tools/call",
            @params = new
            {
                name = "test_destroy",
                arguments = new { target = "y" },
                requestState = state,
                inputResponses = new Dictionary<string, object>
                {
                    [ConfirmationService.InputKey] = new { action = "accept", content = new Dictionary<string, bool> { [ConfirmationService.InputKey] = true } },
                },
            },
        });

        var before = server.Records().Count;
        await server.InjectAsync(confirmed);
        await server.WaitForRecordsAsync(before + 1);
        await server.InjectAsync(confirmed);
        var added = (await server.WaitForRecordsAsync(before + 2)).Skip(before).ToArray();

        Assert.Equal("mcp_tool_confirmed", AuditFiles.EventOf(added[0]));
        Assert.Equal("allow", AuditFiles.OutcomeOf(added[0]));
        AssertRefused([added[1]], "confirmation-replayed", "authz_fail", "CRITICAL");
        Assert.Equal(runs + 1, TestActTools.Runs);
    }

    [Fact]
    public async Task Rule_caller_rate()
    {
        // One request a minute for this caller: the server/discover the client connects with has used it.
        await using var server = await ServerAsync(configure: settings => settings["Limits:PerPrincipalPerMinute"] = "1");
        var echoes = TestActTools.Echoes;

        await CallAsync(server, "test_echo", ("text", "one"));
        var added = await AddedAsync(server, () => CallAsync(server, "test_echo", ("text", "two")));

        AssertRefused(added, "caller-rate", "excess_rate_limit_exceeded", "WARN");
        Assert.Equal(echoes + 1, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_tool_rate()
    {
        var policies = TestModule.ActPolicies();
        policies["test_echo"] = policies["test_echo"] with { PerPrincipalPerMinute = 1 };
        await using var server = await ServerAsync(modules: [new TestModule(tools: policies)]);
        var echoes = TestActTools.Echoes;

        await CallAsync(server, "test_echo", ("text", "one"));
        var added = await AddedAsync(server, () => CallAsync(server, "test_echo", ("text", "two")));

        AssertRefused(added, "tool-rate", "excess_rate_limit_exceeded", "WARN");
        Assert.Equal(echoes + 1, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_limits_unavailable()
    {
        await using var server = await ServerAsync(outsideTheFrame: services =>
        {
            services.RemoveAll<ILimitStore>();
            services.AddSingleton<ILimitStore>(new UnreachableLimits());
        });
        var echoes = TestActTools.Echoes;

        var added = await AddedAsync(server, () => CallAsync(server, "test_echo", ("text", "hi")));

        AssertRefused(added, "limits-unavailable", "sys_availability", "WARN");
        Assert.Equal(echoes, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_extraneous_argument()
    {
        await using var server = await ServerAsync();
        var echoes = TestActTools.Echoes;

        var added = await AddedAsync(server, () => CallAsync(server, "test_echo", ("text", "hi"), ("admin", true)));

        var record = AssertRefused(added, "extraneous-argument", "malicious_extraneous", "CRITICAL");
        Assert.True(record.GetProperty("args").GetProperty("admin").GetBoolean());
        Assert.Equal(echoes, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_argument_length()
    {
        await using var server = await ServerAsync();
        var echoes = TestActTools.Echoes;

        var added = await AddedAsync(server, () => CallAsync(server, "test_echo", ("text", new string('x', 21))));

        AssertRefused(added, "argument-length", "input_validation_fail", "WARN");
        Assert.Equal(echoes, TestActTools.Echoes);
    }

    [Fact]
    public async Task Rule_not_a_number()
    {
        await using var server = await ServerAsync();

        var added = await AddedAsync(server, () => CallAsync(server, "audit_measure", ("value", "NaN")));

        AssertRefused(added, "not-a-number", "input_validation_fail", "WARN");
    }

    [Fact]
    public async Task Rule_argument_schema()
    {
        await using var server = await ServerAsync();

        var added = await AddedAsync(server, () => CallAsync(server, "audit_measure", ("value", true)));

        AssertRefused(added, "argument-schema", "input_validation_fail", "WARN");
    }

    [Fact]
    public async Task Rule_output_cap()
    {
        // The one refusal made after the tool ran: admitted, run, and its answer withheld. Two decisions, two records.
        await using var server = await ServerAsync();

        var added = await AddedAsync(server, () => CallAsync(server, "test_flood"));

        Assert.Equal(2, added.Count);
        Assert.Equal("mcp_tool_call", AuditFiles.EventOf(added[0]));
        Assert.Equal("allow", AuditFiles.OutcomeOf(added[0]));
        AssertRefused([added[1]], "output-cap", "mcp_tool_denied", "WARN");
    }

    [Fact]
    public async Task Rule_request_kind()
    {
        await using var server = await ServerAsync();

        var added = await AddedAsync(server, () => server.RawAsync("frobnicate/now", new { level = "debug" }));

        var record = AssertRefused(added, "request-kind", "authz_fail", "CRITICAL");
        Assert.Equal("frobnicate/now", record.GetProperty("mcp").GetProperty("method").GetString());
    }

    [Fact]
    public async Task Rule_malformed_request()
    {
        // Turned away by the SDK inside the frame's checks, before any of them could decide: a governed kind whose
        // parameters cannot be read. Still one record.
        await using var server = await ServerAsync();

        var before = server.Records().Count;
        await server.InjectAsync("""{"jsonrpc":"2.0","id":"m1","method":"resources/read","params":{}}""");
        await server.InjectAsync("""{"jsonrpc":"2.0","id":"m2","method":"completion/complete","params":{"ref":{"type":"ref/other","name":"x"},"argument":{"name":"a","value":"b"}}}""");
        var added = (await server.WaitForRecordsAsync(before + 2)).Skip(before).ToArray();

        Assert.Equal(2, added.Length);
        foreach (var record in added)
        {
            AssertRefused([record], "malformed-request", "input_validation_fail", "WARN");
        }

        Assert.Equal(["completion/complete", "resources/read"], added.Select(r => r.GetProperty("mcp").GetProperty("method").GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Rule_rejected_by_protocol()
    {
        // Turned away by the SDK before the frame saw it at all: a method the negotiated protocol version does not have.
        // The audit step never meets it; the way out records it, in the SDK's own words.
        await using var server = await ServerAsync();

        string? answer = null;
        var added = await AddedAsync(server, async () => answer = await server.RawAsync("ping"));

        var record = AssertRefused(added, "rejected-by-protocol", "input_validation_fail", "WARN");
        Assert.Contains("ping", answer, StringComparison.Ordinal);
        Assert.Contains("ping", record.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("alice", record.GetProperty("principal").GetProperty("sub").GetString());
    }

    [Fact]
    public async Task Rule_audit_unavailable()
    {
        // The rule whose own record cannot be written when it applies. What shows it: the refusal, the tool that did not
        // run, the ordinary log saying the trail is down, and — once the trail is back — the gap, on the trail itself.
        var sink = new SwitchableSink();
        var log = new RequestGateTests.CapturedLog();
        await using var server = await StreamServer.StartAsync(
            Modules,
            logs: log,
            outsideTheFrame: services =>
            {
                services.RemoveAll<IAuditSink>();
                services.AddSingleton<IAuditSink>(sink);
            });
        var changes = AuditTools.Changes;

        sink.Failing = true;
        var refused = await CallAsync(server, "audit_change", ("value", "x"));

        Assert.True(refused.IsError);
        Assert.Contains("sys_monitor_disabled (rule: audit-unavailable)", string.Join(" ", refused.Content.OfType<TextContentBlock>().Select(c => c.Text)), StringComparison.Ordinal);
        Assert.Equal(changes, AuditTools.Changes);
        Assert.Contains(log.Lines, line => line.Contains("sys_monitor_disabled: the audit trail cannot be written", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("sys_monitor_disabled: rule=audit-unavailable principal=corp:alice target=audit_change", StringComparison.Ordinal));

        sink.Failing = false;
        var before = sink.Records.Count;
        var served = await CallAsync(server, "audit_change", ("value", "y"));

        Assert.NotEqual(true, served.IsError);
        var added = sink.Records.Skip(before).ToArray();
        Assert.Equal(["sys_monitor_disabled", "mcp_tool_call"], added.Select(r => r.Event.Name));
        Assert.Equal("2", added[0].Event.Detail!["missed"]);
        Assert.Equal(changes + 1, AuditTools.Changes);
    }

    // ── Every rule has a driver ──────────────────────────────────────────────

    [Fact]
    public void Every_rule_in_the_product_has_a_driver_here()
    {
        var source = Path.Combine(RepositoryRoot(), "McpServerTemplate");
        var rules = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => RuleInSource().Matches(File.ReadAllText(file)).Select(match => match.Groups["rule"].Value))
            .ToHashSet(StringComparer.Ordinal);

        // The scan finds something, and finds what it is known to hold: a pattern that matched nothing would pass anything.
        Assert.Superset(new HashSet<string> { "insufficient_scope", "idp-binding", "request-kind", "audit-unavailable", "output-cap", "malformed-request" }, rules);

        var drivers = typeof(AuditDecisionTests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name.StartsWith("Rule_", StringComparison.Ordinal) && method.GetCustomAttribute<FactAttribute>() is not null)
            .Select(method => method.Name["Rule_".Length..])
            .ToHashSet(StringComparer.Ordinal);

        var undriven = rules.Where(rule => !drivers.Contains(rule.Replace('-', '_')) && !NotDriven.ContainsKey(rule)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            undriven.Length == 0,
            $"The product can refuse or record by these rules, and no test here drives them: {string.Join(", ", undriven)}. "
            + "Add a test named Rule_<rule> that drives each and reads its record, or say in NotDriven why none can.");

        // And nothing is excused that the product no longer writes.
        Assert.DoesNotContain(NotDriven.Keys, rule => !rules.Contains(rule));
    }

    /// <summary>
    /// A rule as the product writes one: the first argument of a refusal, or the rule beside an outcome in a record the
    /// frame writes itself.
    /// </summary>
    [GeneratedRegex("""(?:Refusal\.(?:Of|Concealed)\(\s*|AuditOutcome\.(?:Allow|Deny|Challenge),\s*(?:rule:\s*)?)"(?<rule>[a-z_-]+)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex RuleInSource();

    // ── T-2: N requests, N records ───────────────────────────────────────────

    [Fact]
    public async Task T2_every_request_of_every_kind_leaves_exactly_one_record_naming_its_method()
    {
        await using var server = await ServerAsync();

        // What connecting cost is on the trail already: the server's start, then one record per message the client sent.
        var connected = server.Records();
        Assert.Equal("sys_startup", AuditFiles.EventOf(connected[0]));
        Assert.All(connected.Skip(1), record => Assert.Equal("allow", AuditFiles.OutcomeOf(record)));

        var steps = new (string Method, Func<Task> Act, int Records)[]
        {
            ("tools/list", async () => await server.Client.ListToolsAsync(), 1),
            ("tools/call", () => CallAsync(server, "test_echo", ("text", "hi")), 1),
            ("resources/list", async () => await server.Client.ListResourcesAsync(), 1),
            ("resources/templates/list", async () => await server.Client.ListResourceTemplatesAsync(), 1),
            ("resources/read", async () => await server.Client.ReadResourceAsync(AuditResources.Uri), 1),
            ("prompts/list", async () => await server.Client.ListPromptsAsync(), 1),
            ("prompts/get", async () => await server.Client.GetPromptAsync(AuditPrompts.Name, new Dictionary<string, object?> { ["topic"] = "t" }), 1),
            ("completion/complete", async () => await server.Client.CompleteAsync(new PromptReference { Name = AuditPrompts.Name }, "topic", "a"), 1),
            ("frobnicate/now", () => server.RawAsync("frobnicate/now"), 1),
            ("tools/call", () => CallAsync(server, "test_flood"), 2),
        };

        var expected = connected.Count;
        foreach (var (method, act, records) in steps)
        {
            var added = await AddedAsync(server, act);
            Assert.True(added.Count == records, $"{method} left {added.Count} records, and {records} were expected: {string.Join(" | ", added.Select(r => r.GetProperty("event").GetString()))}");
            Assert.All(added, record => Assert.Equal(method, record.GetProperty("mcp").GetProperty("method").GetString()));
            expected += records;
        }

        // The kinds the SDK's client has no call for, as lines of their own: a method the protocol version lacks, a
        // notification the frame governs, one it does not, a reply nobody asked for, and a request it cannot read.
        string[] lines =
        [
            """{"jsonrpc":"2.0","id":"p1","method":"ping"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"none"}}""",
            """{"jsonrpc":"2.0","method":"notifications/unknown"}""",
            """{"jsonrpc":"2.0","id":"r1","result":{}}""",
            """{"jsonrpc":"2.0","id":"m1","method":"tools/call","params":{"name":5}}""",
        ];
        foreach (var line in lines)
        {
            await server.InjectAsync(line);
            expected++;
            await server.WaitForRecordsAsync(expected);
        }

        await Task.Delay(200);
        var all = server.Records();
        Assert.Equal(expected, all.Count);

        // One chain, numbered without a gap from the start to here.
        Assert.Equal(Enumerable.Range(1, all.Count).Select(n => (long)n), all.Select(r => r.GetProperty("seq").GetInt64()));
        Assert.Equal(AuditVerifier.Intact, AuditVerifier.Verify(AuditFiles.In(server.AuditDirectory)).Code);
    }

    // ── T-15: the order is held at startup ───────────────────────────────────

    [Fact]
    public void T15_startup_refuses_when_the_audit_step_is_not_first()
    {
        using var provider = Composed();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.Filters.Message.IncomingFilters.Insert(0, next => next);

        var message = Assert.Throws<ConfigurationException>(() => FrameIntegrity.VerifyInstalled(
            options, provider.GetRequiredService<FrameManifest>(), Modules)).Message;

        Assert.Contains("the frame's audit step is not the first incoming message filter", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T15_startup_refuses_when_the_request_kind_gate_is_not_straight_behind_it()
    {
        using var provider = Composed();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var incoming = options.Filters.Message.IncomingFilters;
        (incoming[0], incoming[1]) = (incoming[1], incoming[0]);

        var message = Assert.Throws<ConfigurationException>(() => FrameIntegrity.VerifyInstalled(
            options, provider.GetRequiredService<FrameManifest>(), Modules)).Message;

        Assert.Contains("the frame's audit step is not the first incoming message filter", message, StringComparison.Ordinal);
        Assert.Contains("the frame's request-kind gate is not the second incoming message filter", message, StringComparison.Ordinal);
    }

    [Fact]
    public void T15_the_frame_as_composed_has_the_audit_step_first_the_gate_second_and_the_way_out_installed()
    {
        using var provider = Composed();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var manifest = provider.GetRequiredService<FrameManifest>();

        Assert.Same(manifest.Audit, options.Filters.Message.IncomingFilters[0]);
        Assert.Same(manifest.Gate, options.Filters.Message.IncomingFilters[1]);
        Assert.Equal(2, options.Filters.Message.IncomingFilters.Count);
        Assert.Single(options.Filters.Message.OutgoingFilters);
        Assert.True(manifest.Owns(options.Filters.Message.OutgoingFilters[0]));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpServerTemplate.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root from the test output directory");
        return dir!.FullName;
    }

    /// <summary>A limit store that cannot be reached, as Redis is when it is down.</summary>
    private sealed class UnreachableLimits : ILimitStore
    {
        public string Description => "unreachable (a test's)";

        public Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken) =>
            throw new TimeoutException("the limit store did not answer");

        public Task<bool> TryClaimOnceAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            throw new TimeoutException("the limit store did not answer");
    }
}
