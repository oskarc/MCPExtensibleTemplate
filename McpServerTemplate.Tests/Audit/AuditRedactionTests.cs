using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Tests.Frame;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-7, T-8, T-13 — what a record holds of what a caller sent: nothing secret, nothing that can forge a
/// line, the trace it belongs to; and the ordinary log, which holds no argument's value at all.
/// </summary>
public sealed class AuditRedactionTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";

    private static readonly IReadOnlyList<IProviderModule> Modules = [new TestModule(), new AuditModule()];

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The one record a call left, and the trail's whole text.</summary>
    private static async Task<(JsonElement Record, string File)> CallAsync(StreamServer server, string tool, Dictionary<string, object?> arguments)
    {
        var before = server.Records().Count;
        await server.Client.CallToolAsync(tool, arguments);
        var record = Assert.Single(server.Records().Skip(before));
        return (record, string.Join("\n", AuditFiles.In(server.AuditDirectory).SelectMany(AuditFiles.Lines)));
    }

    [Fact]
    public async Task T7_an_argument_the_policy_marks_sensitive_is_recorded_as_a_hash_tag_and_never_as_itself()
    {
        await using var server = await StreamServer.StartAsync(Modules);

        var (record, file) = await CallAsync(server, "audit_login", new() { ["password"] = "correct-horse-battery", ["note"] = "tuesday" });

        // Eight hex characters of the SHA-256 of the value as it was sent: its JSON text, quotes and all.
        Assert.Equal($"[redacted:sha256:{Sha256("\"correct-horse-battery\"")[..8]}]", record.GetProperty("args").GetProperty("password").GetString());
        Assert.Equal("tuesday", record.GetProperty("args").GetProperty("note").GetString());
        Assert.DoesNotContain("correct-horse-battery", file, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Jwt)]
    [InlineData("Bearer abcdef0123456789abcdef")]
    [InlineData("the key is AKIAIOSFODNN7EXAMPLE, keep it safe")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz012345")]
    public async Task T7_a_value_shaped_like_a_credential_is_replaced_whatever_argument_it_sits_in(string secret)
    {
        await using var server = await StreamServer.StartAsync(Modules);

        var (record, file) = await CallAsync(server, "audit_login", new() { ["password"] = "p", ["note"] = secret });

        Assert.Equal($"[redacted:sha256:{Sha256(secret)[..8]}]", record.GetProperty("args").GetProperty("note").GetString());
        Assert.DoesNotContain(secret, file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T7_a_credential_nested_in_arguments_the_tool_never_declared_is_replaced_in_the_refusals_record_too()
    {
        // Refused, for arguments the tool does not declare — and recorded, with what was sent: the refusal's record is where
        // a pasted token would otherwise stay.
        await using var server = await StreamServer.StartAsync(Modules);

        var (record, file) = await CallAsync(server, "test_echo", new()
        {
            ["text"] = "hi",
            ["extra"] = new Dictionary<string, object?>
            {
                ["token"] = Jwt,
                ["list"] = new object?[] { "plain", "AKIAIOSFODNN7EXAMPLE", new Dictionary<string, object?> { ["deep"] = "sk-abcdefghijklmnopqrstuvwxyz012345" } },
                ["count"] = 3,
            },
        });

        Assert.Equal("extraneous-argument", AuditFiles.RuleOf(record));
        var extra = record.GetProperty("args").GetProperty("extra");
        Assert.StartsWith("[redacted:sha256:", extra.GetProperty("token").GetString(), StringComparison.Ordinal);
        Assert.Equal("plain", extra.GetProperty("list")[0].GetString());
        Assert.StartsWith("[redacted:sha256:", extra.GetProperty("list")[1].GetString(), StringComparison.Ordinal);
        Assert.StartsWith("[redacted:sha256:", extra.GetProperty("list")[2].GetProperty("deep").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, extra.GetProperty("count").GetInt32());
        Assert.DoesNotContain("eyJ", file, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", file, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abc", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T7_a_long_string_is_cut_at_two_kilobytes_and_carries_the_hash_of_the_whole()
    {
        await using var server = await StreamServer.StartAsync(Modules);
        var note = string.Concat(Enumerable.Repeat("0123456789abcdef", 640));   // 10 240 bytes
        Assert.Equal(10_240, Encoding.UTF8.GetByteCount(note));

        var (record, _) = await CallAsync(server, "audit_login", new() { ["password"] = "p", ["note"] = note });

        var written = record.GetProperty("args").GetProperty("note").GetString()!;
        Assert.Equal($"{note[..2048]}…[truncated:10240 bytes:sha256:{Sha256(note)}]", written);
    }

    [Fact]
    public async Task T7_arguments_too_large_for_one_record_leave_their_size_and_hash_and_nothing_else()
    {
        await using var server = await StreamServer.StartAsync(Modules);
        var extra = Enumerable.Range(0, 20).ToDictionary(n => $"field{n}", n => (object?)new string((char)('a' + n), 1500));

        var (record, _) = await CallAsync(server, "test_echo", new(extra) { ["text"] = "hi" });

        var args = record.GetProperty("args");
        Assert.True(args.GetProperty("_truncated").GetBoolean());
        Assert.True(args.GetProperty("bytes").GetInt32() > 16 * 1024);
        Assert.Matches("^[0-9a-f]{64}$", args.GetProperty("sha256").GetString());
        Assert.Equal(3, args.EnumerateObject().Count());
    }

    [Fact]
    public async Task T7_a_line_break_or_an_escape_a_caller_sends_cannot_start_a_line_of_its_own()
    {
        await using var server = await StreamServer.StartAsync(Modules);
        const string Forged = "\r\n{\"datetime\":\"2026-01-01T00:00:00.000Z\",\"event\":\"authz_admin\"}\r\n\u001b[2J\u2028";

        var before = server.Records().Count;
        await server.Client.CallToolAsync("no_such" + Forged, new Dictionary<string, object?>());
        await server.Client.CallToolAsync("test_echo", new Dictionary<string, object?> { ["text"] = "hi", [Forged] = Forged });
        var records = server.Records();

        // As many lines as records, each one a record: nothing the caller sent became a line.
        var lines = AuditFiles.In(server.AuditDirectory).SelectMany(AuditFiles.Lines).ToArray();
        Assert.Equal(records.Count, lines.Length);
        Assert.Equal(before + 2, records.Count);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain('\u001b', line);
            Assert.DoesNotContain('\u2028', line);
            Assert.True(JsonDocument.Parse(line).RootElement.TryGetProperty("hash", out _));
        });
        Assert.DoesNotContain(records, record => AuditFiles.EventOf(record) == "authz_admin");

        // And what was sent is still there to read, as the caller sent it: the name the first call asked for.
        Assert.Contains(records.Skip(before), record => record.GetProperty("mcp").GetProperty("target").GetString()?.StartsWith("no_such", StringComparison.Ordinal) == true
            && AuditFiles.RuleOf(record) == "not-permitted");
    }

    [Fact]
    public void T7_a_sensitive_argument_the_tool_does_not_declare_stops_startup_naming_it()
    {
        var policies = TestModule.ActPolicies();
        policies["test_echo"] = policies["test_echo"] with { SensitiveArgs = ["text", "pasword"] };
        var modules = new IProviderModule[] { new TestModule(tools: policies) };
        var settings = FrameHarness.Settings(modules);
        settings["Confirmation:Key"] = Identity.InProcessServer.ConfirmationKey;

        var refusal = FrameHarness.Refusal(modules, settings);

        Assert.Contains("The tool 'test_echo' marks the argument 'pasword' as sensitive, and its input schema declares no such argument", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("'text' as sensitive", refusal, StringComparison.Ordinal);
    }

    // ── T-8: one trace through ───────────────────────────────────────────────

    [Fact]
    public async Task T8_the_record_and_the_code_inside_the_tool_are_under_one_trace_and_both_spans_are_opened()
    {
        var spans = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "McpServer",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await using var server = await StreamServer.StartAsync(Modules);
        var before = server.Records().Count;
        var result = await server.Client.CallToolAsync("audit_trace", new Dictionary<string, object?>());
        var record = Assert.Single(server.Records().Skip(before));

        // The tool asked which trace it was running under, and answered with the record's.
        var seenByTheTool = Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()).Text;
        var trace = record.GetProperty("trace_id").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", trace);
        Assert.Equal(trace, seenByTheTool);

        var mine = spans.Where(span => span.TraceId.ToHexString() == trace).ToArray();
        var request = Assert.Single(mine, span => span.OperationName == "mcp.request");
        var tool = Assert.Single(mine, span => span.OperationName == "mcp.tool");
        Assert.Equal(request.SpanId, tool.ParentSpanId);
        Assert.Equal("audit_trace", tool.GetTagItem("mcp.tool"));
    }

    [Fact]
    public async Task T8_with_nothing_listening_every_record_still_has_a_trace_and_the_tool_sees_it()
    {
        await using var server = await StreamServer.StartAsync(Modules);
        var before = server.Records().Count;

        var result = await server.Client.CallToolAsync("audit_trace", new Dictionary<string, object?>());

        var record = Assert.Single(server.Records().Skip(before));
        Assert.Equal(record.GetProperty("trace_id").GetString(), Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()).Text);
        Assert.All(server.Records().Skip(1), each => Assert.Matches("^[0-9a-f]{32}$", each.GetProperty("trace_id").GetString()));
    }

    // ── T-13: the ordinary log holds no argument's value ─────────────────────

    [Fact]
    public async Task T13_at_debug_the_ordinary_log_names_the_arguments_and_holds_none_of_their_values()
    {
        const string Marker = "MARKER-93f1c0de";
        var log = new RequestGateTests.CapturedLog();
        await using var server = await StreamServer.StartAsync(Modules, logs: log, logLevel: LogLevel.Debug);

        await server.Client.CallToolAsync("audit_login", new Dictionary<string, object?> { ["password"] = Marker, ["note"] = Marker + "-note" });

        Assert.Contains(log.Lines, line => line.Contains("Tool call: audit_login with args: [password, note]", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains(Marker, StringComparison.Ordinal));

        // The value that is not sensitive is on the audit trail, where it belongs; the one that is, is not anywhere.
        var record = server.RecordsOf("tools/call")[^1];
        Assert.Equal(Marker + "-note", record.GetProperty("args").GetProperty("note").GetString());
        Assert.DoesNotContain(Marker + "\"", string.Join("\n", AuditFiles.In(server.AuditDirectory).SelectMany(AuditFiles.Lines)), StringComparison.Ordinal);
    }
}
