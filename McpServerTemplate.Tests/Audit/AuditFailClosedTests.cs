using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Audit;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Tests.Frame;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-4 — record first; refuse when it cannot be written (roadmap I7, P3.6). The trail's sink is one the
/// test controls: it fails while told to, as a disk that cannot be written does, and it says what the tool's own counter
/// read at the moment a record was appended.
/// </summary>
[Collection(TestActTools.Collection)]
public sealed class AuditFailClosedTests
{
    private static readonly IReadOnlyList<IProviderModule> Modules = [new TestModule(), new AuditModule()];

    private static Task<StreamServer> ServerAsync(SwitchableSink sink, string? readWhenUnavailable = null, RequestGateTests.CapturedLog? log = null) =>
        StreamServer.StartAsync(
            Modules,
            configure: settings =>
            {
                if (readWhenUnavailable is not null)
                {
                    settings["Audit:ReadWhenUnavailable"] = readWhenUnavailable;
                }
            },
            logs: log,
            outsideTheFrame: services =>
            {
                services.RemoveAll<IAuditSink>();
                services.AddSingleton<IAuditSink>(sink);
            });

    private static string TextOf(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task T4_while_the_trail_cannot_be_written_nothing_is_served_by_default_not_a_read_and_not_a_list()
    {
        var sink = new SwitchableSink();
        await using var server = await ServerAsync(sink);
        var echoes = TestActTools.Echoes;
        var changes = AuditTools.Changes;

        sink.Failing = true;
        var read = await server.Client.CallToolAsync("test_echo", new Dictionary<string, object?> { ["text"] = "hi" });
        var write = await server.Client.CallToolAsync("audit_change", new Dictionary<string, object?> { ["value"] = "x" });
        var list = await Assert.ThrowsAsync<McpProtocolException>(async () => await server.Client.ListToolsAsync());
        var prompt = await Assert.ThrowsAsync<McpProtocolException>(async () => await server.Client.GetPromptAsync(AuditPrompts.Name, new Dictionary<string, object?> { ["topic"] = "t" }));

        Assert.Contains("audit-unavailable", TextOf(read), StringComparison.Ordinal);
        Assert.Contains("audit-unavailable", TextOf(write), StringComparison.Ordinal);
        Assert.Contains("audit-unavailable", list.Message, StringComparison.Ordinal);
        Assert.Contains("audit-unavailable", prompt.Message, StringComparison.Ordinal);
        Assert.Equal(echoes, TestActTools.Echoes);
        Assert.Equal(changes, AuditTools.Changes);
    }

    [Fact]
    public async Task T4_with_reads_allowed_a_read_is_served_while_a_write_is_still_refused_and_did_not_run()
    {
        var sink = new SwitchableSink();
        var log = new RequestGateTests.CapturedLog();
        await using var server = await ServerAsync(sink, readWhenUnavailable: "allow", log);
        var echoes = TestActTools.Echoes;
        var changes = AuditTools.Changes;

        sink.Failing = true;
        var read = await server.Client.CallToolAsync("test_echo", new Dictionary<string, object?> { ["text"] = "hi" });
        var tools = await server.Client.ListToolsAsync();
        var write = await server.Client.CallToolAsync("audit_change", new Dictionary<string, object?> { ["value"] = "x" });
        var irreversible = await server.Client.CallToolAsync("test_destroy", new Dictionary<string, object?> { ["target"] = "x" });

        Assert.NotEqual(true, read.IsError);
        Assert.NotEmpty(tools);
        Assert.Equal(echoes + 1, TestActTools.Echoes);

        Assert.True(write.IsError);
        Assert.Contains("audit-unavailable", TextOf(write), StringComparison.Ordinal);
        Assert.Contains("audit-unavailable", TextOf(irreversible), StringComparison.Ordinal);
        Assert.Equal(changes, AuditTools.Changes);

        // The ordinary log is the one place left to say it: that the trail is down, once, and each request it missed.
        Assert.Single(log.Lines, line => line.Contains("sys_monitor_disabled: the audit trail cannot be written", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("served unrecorded (Audit:ReadWhenUnavailable=allow)", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("audit record not written: event=mcp_tool_call", StringComparison.Ordinal) && line.Contains("target=test_echo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task T4_the_trail_comes_back_by_itself_and_the_first_thing_on_it_is_the_gap()
    {
        var sink = new SwitchableSink();
        await using var server = await ServerAsync(sink);
        var changes = AuditTools.Changes;

        sink.Failing = true;
        await server.Client.CallToolAsync("audit_change", new Dictionary<string, object?> { ["value"] = "lost" });
        sink.Failing = false;
        var before = sink.Records.Count;
        var served = await server.Client.CallToolAsync("audit_change", new Dictionary<string, object?> { ["value"] = "kept" });

        Assert.NotEqual(true, served.IsError);
        Assert.Equal(changes + 1, AuditTools.Changes);

        var added = sink.Records.Skip(before).ToArray();
        Assert.Equal([AuditEvents.SysMonitorDisabled, AuditEvents.McpToolCall], added.Select(r => r.Event.Name));

        // The chain carries on from the last record that was written: the lost ones took no number.
        Assert.Equal(sink.Records[before - 1].Hash, added[0].PrevHash);
        Assert.Equal(sink.Records[before - 1].Seq + 1, added[0].Seq);
        Assert.Equal(added[0].Hash, added[1].PrevHash);
    }

    [Fact]
    public async Task T4_the_admission_is_on_the_trail_before_the_tool_runs()
    {
        var sink = new SwitchableSink();
        await using var server = await ServerAsync(sink);
        var before = AuditTools.Changes;
        int? whenRecorded = null;
        sink.OnAppend = record =>
        {
            if (record.Event.Name == AuditEvents.McpToolCall && record.Event.Mcp?.Target == "audit_change")
            {
                whenRecorded = AuditTools.Changes;
            }
        };

        await server.Client.CallToolAsync("audit_change", new Dictionary<string, object?> { ["value"] = "x" });

        // At the moment its admission was appended the tool had not run; by the time the call returned it had, once.
        Assert.Equal(before, whenRecorded);
        Assert.Equal(before + 1, AuditTools.Changes);
    }

    [Fact]
    public async Task T4_a_server_whose_start_cannot_be_recorded_does_not_start()
    {
        var sink = new SwitchableSink { Failing = true };

        var refusal = await Assert.ThrowsAsync<ConfigurationException>(() => ServerAsync(sink));

        Assert.Contains("could not record the server's start", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(sink.Records);
    }
}
