using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Audit;
using McpServerTemplate.Tests.Frame;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-11 — the copy that leaves the machine. The collector is a handler in this process (the tests reach no
/// network): it keeps what it is sent, and fails, refuses or stalls when a test tells it to.
/// </summary>
public sealed class AuditExportTests : IDisposable
{
    private static readonly Uri Endpoint = new("https://collector.example.com:4318/v1/logs");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mcp-audit-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private (AuditTrail Trail, OtlpAuditSink Copy) Trail(Collector collector, RequestGateTests.CapturedLog? log = null)
    {
        var settings = new AuditSettings(_directory, ReadWhenUnavailable: false, 400, "mcp-server", Otlp: null);
        var identity = new AuditIdentity("test-host", "0a1b2c3d");
        var copy = new OtlpAuditSink(
            new AuditOtlpSettings(Endpoint, new Dictionary<string, string> { ["Authorization"] = "Bearer collector-key" }),
            identity,
            "mcp-server",
            log?.CreateLogger("export") ?? NullLogger.Instance,
            collector,
            flushInterval: TimeSpan.FromMilliseconds(20));
        return (new AuditTrail([new FileAuditSink(settings, identity), copy], settings, identity, TimeProvider.System, NullLogger<AuditTrail>.Instance), copy);
    }

    private static AuditEvent Event(int n) => new(n % 2 == 0 ? AuditEvents.McpToolCall : AuditEvents.AuthzFail, $"record {n}")
    {
        TraceId = "0af7651916cd43dd8448eb211c80319c",
        Mcp = new AuditMcp("tools/call", "Test", "tool", $"tool_{n}", "Read"),
        Decision = new AuditDecision(n % 2 == 0 ? AuditOutcome.Allow : AuditOutcome.Deny, n % 2 == 0 ? null : "insufficient_scope"),
    };

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"after ten seconds, {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task T11_each_record_reaches_the_collector_as_the_same_line_the_file_holds()
    {
        var collector = new Collector();
        var (trail, _) = Trail(collector);
        using (trail)
        {
            for (var n = 1; n <= 5; n++)
            {
                Assert.True(trail.Record(Event(n)));
            }

            await UntilAsync(() => collector.LogRecords().Count == 5, $"the collector holds {collector.LogRecords().Count} of 5 records");
        }

        var lines = AuditFiles.Lines(Assert.Single(AuditFiles.In(_directory)));
        var received = collector.LogRecords().OrderBy(r => long.Parse(Attribute(r, "audit.seq", "intValue"), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(lines, received.Select(r => r.GetProperty("body").GetProperty("stringValue").GetString()!));

        // What a collector needs to check its copy against the file, and to see a gap: the hashes and the running number.
        for (var i = 0; i < received.Length; i++)
        {
            using var line = JsonDocument.Parse(lines[i]);
            Assert.Equal(line.RootElement.GetProperty("hash").GetString(), Attribute(received[i], "audit.hash", "stringValue"));
            Assert.Equal(line.RootElement.GetProperty("prev_hash").GetString(), Attribute(received[i], "audit.prev_hash", "stringValue"));
            Assert.Equal((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Attribute(received[i], "audit.seq", "intValue"));
            Assert.Equal("0af7651916cd43dd8448eb211c80319c", received[i].GetProperty("traceId").GetString());
        }

        Assert.Equal(("authz_fail", "CRITICAL", 21), (Attribute(received[0], "event.name", "stringValue"), received[0].GetProperty("severityText").GetString(), received[0].GetProperty("severityNumber").GetInt32()));
        Assert.Equal(("mcp_tool_call", "INFO", 9), (Attribute(received[1], "event.name", "stringValue"), received[1].GetProperty("severityText").GetString(), received[1].GetProperty("severityNumber").GetInt32()));

        // Sent where it was told to, as OTLP's JSON, with the header it was given, naming this application and instance.
        Assert.All(collector.Requests, request =>
        {
            Assert.Equal(Endpoint, request.Uri);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal("Bearer collector-key", request.Authorization);
        });
        using var export = JsonDocument.Parse(collector.Requests.First().Body);
        var resource = export.RootElement.GetProperty("resourceLogs")[0].GetProperty("resource").GetProperty("attributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("key").GetString()!, a => a.GetProperty("value").GetProperty("stringValue").GetString());
        Assert.Equal(("mcp-server", "0a1b2c3d", "test-host"), (resource["service.name"], resource["service.instance.id"], resource["host.name"]));
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("refusing")]
    public async Task T11_a_failing_collector_refuses_nothing_delays_nothing_and_is_reported(string how)
    {
        var collector = new Collector { Unreachable = how == "unreachable", Status = how == "refusing" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK };
        var log = new RequestGateTests.CapturedLog();
        var (trail, copy) = Trail(collector, log);
        using (trail)
        {
            for (var n = 1; n <= 20; n++)
            {
                Assert.True(trail.Record(Event(n)));
            }

            await UntilAsync(() => copy.Dropped == 20, $"{copy.Dropped} of 20 undelivered records were counted");
        }

        // The file is the record, and it is whole.
        var file = Assert.Single(AuditFiles.In(_directory));
        Assert.Equal((AuditVerifier.Intact, 20L), (AuditVerifier.Verify([file]).Code, AuditVerifier.Verify([file]).Records));

        // Said on the ordinary log, once, naming the collector and not its key.
        var report = Assert.Single(log.Lines, line => line.Contains("The audit copy to https://collector.example.com:4318 is not being delivered", StringComparison.Ordinal));
        Assert.Contains("Every record is in the audit file", report, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Lines, line => line.Contains("collector-key", StringComparison.Ordinal));
    }

    [Fact]
    public void T11_a_collector_that_does_not_answer_holds_no_record_back()
    {
        var collector = new Collector { Stall = TimeSpan.FromSeconds(30) };
        var (trail, _) = Trail(collector);
        var watch = Stopwatch.StartNew();
        using (trail)
        {
            for (var n = 1; n <= 50; n++)
            {
                Assert.True(trail.Record(Event(n)));
            }

            watch.Stop();
            Assert.Equal(50, AuditFiles.Lines(Assert.Single(AuditFiles.In(_directory))).Count);
        }

        // Fifty flushed appends, and no wait on a collector that would have held each for thirty seconds.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"fifty records took {watch.Elapsed.TotalSeconds:0.0} s with a stalled collector");
    }

    private static string Attribute(JsonElement logRecord, string key, string kind) =>
        logRecord.GetProperty("attributes").EnumerateArray().Single(a => a.GetProperty("key").GetString() == key).GetProperty("value").GetProperty(kind).GetString()!;

    /// <summary>A collector in this process.</summary>
    private sealed class Collector : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Uri, string ContentType, string? Authorization, string Body)> Requests { get; } = new();

        public bool Unreachable { get; init; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public TimeSpan Stall { get; init; }

        public IReadOnlyList<JsonElement> LogRecords() =>
            [.. Requests.SelectMany(request =>
            {
                using var document = JsonDocument.Parse(request.Body);
                return document.RootElement.GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords").EnumerateArray().Select(r => r.Clone()).ToArray();
            })];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Stall > TimeSpan.Zero)
            {
                await Task.Delay(Stall, cancellationToken);
            }

            if (Unreachable)
            {
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            }

            if (Status == HttpStatusCode.OK)
            {
                Requests.Enqueue((
                    request.RequestUri!,
                    request.Content!.Headers.ContentType!.MediaType!,
                    request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null,
                    await request.Content.ReadAsStringAsync(cancellationToken)));
            }

            return new HttpResponseMessage(Status);
        }
    }
}
