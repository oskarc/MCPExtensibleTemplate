using System.Diagnostics;
using System.Text;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-5, T-6 — the chain: a file the trail wrote verifies; a changed, removed, moved or cut line is named
/// at its line; the chain carries on across midnight; old files go.
/// </summary>
public sealed class AuditChainTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mcp-audit-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AuditTrail Trail(TimeProvider clock, string instance = "0a1b2c3d", int retentionDays = 400)
    {
        var settings = new AuditSettings(_directory, ReadWhenUnavailable: false, retentionDays, "mcp-server", Otlp: null);
        var identity = new AuditIdentity("test-host", instance);
        return new AuditTrail([new FileAuditSink(settings, identity)], settings, identity, clock, NullLogger<AuditTrail>.Instance);
    }

    private static AuditEvent Event(int n) => new(AuditEvents.McpToolCall, $"record {n}")
    {
        Mcp = new AuditMcp("tools/call", "Test", "tool", $"tool_{n}", "Read"),
        Decision = new AuditDecision(AuditOutcome.Allow),
    };

    /// <summary>Writes <paramref name="count"/> records and returns the one file they are in.</summary>
    private string Written(int count)
    {
        using (var trail = Trail(new ManualClock(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero))))
        {
            for (var n = 1; n <= count; n++)
            {
                Assert.True(trail.Record(Event(n)));
            }
        }

        return Assert.Single(AuditFiles.In(_directory));
    }

    [Fact]
    public void T5_a_file_the_trail_wrote_is_intact_from_its_first_record_to_its_last()
    {
        var file = Written(5);

        var result = AuditVerifier.Verify([file]);

        Assert.Equal(AuditVerifier.Intact, result.Code);
        Assert.Equal(5, result.Records);
        Assert.Equal(new string('0', 64), result.FirstPrevHash);
        Assert.Equal((1, 5), (result.FirstSeq, result.LastSeq));
        Assert.Equal(JsonDocument.Parse(File.ReadLines(file).Last()).RootElement.GetProperty("hash").GetString(), result.LastHash);
        Assert.Matches(@"^audit-20261009-test-host-0a1b2c3d\.jsonl$", Path.GetFileName(file));
    }

    public static TheoryData<string, int> Tampering => new()
    {
        { "one byte changed in line 3", 3 },
        { "the hash itself changed in line 3", 3 },
        { "line 3 removed", 3 },
        { "lines 2 and 3 swapped", 2 },
        { "a line added after line 2", 3 },
        { "the last line cut short", 5 },
        { "the last line's end removed", 5 },
    };

    [Theory]
    [MemberData(nameof(Tampering))]
    public void T5_tampering_is_named_at_its_line(string what, int line)
    {
        var file = Written(5);
        var lines = File.ReadAllLines(file).ToList();
        string text;
        switch (what)
        {
            case "one byte changed in line 3":
                lines[2] = lines[2].Replace("record 3", "record 9", StringComparison.Ordinal);
                text = string.Join("\n", lines) + "\n";
                break;
            case "the hash itself changed in line 3":
                lines[2] = lines[2][..^3] + (lines[2][^3] == 'a' ? 'b' : 'a') + lines[2][^2..];
                text = string.Join("\n", lines) + "\n";
                break;
            case "line 3 removed":
                lines.RemoveAt(2);
                text = string.Join("\n", lines) + "\n";
                break;
            case "lines 2 and 3 swapped":
                (lines[1], lines[2]) = (lines[2], lines[1]);
                text = string.Join("\n", lines) + "\n";
                break;
            case "a line added after line 2":
                lines.Insert(2, lines[1]);
                text = string.Join("\n", lines) + "\n";
                break;
            case "the last line cut short":
                text = string.Join("\n", lines)[..^40];
                break;
            default:
                text = string.Join("\n", lines);
                break;
        }

        File.WriteAllText(file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = AuditVerifier.Verify([file]);

        Assert.Equal(AuditVerifier.Broken, result.Code);
        Assert.Equal(line, result.Line);
        Assert.Equal(file, result.File);
        Assert.Equal(line - 1, result.Records);
        output.WriteLine($"{what}: {result.Reason}");
    }

    [Fact]
    public void T5_a_file_that_is_not_there_is_unreadable_not_intact()
    {
        var result = AuditVerifier.Verify([Path.Combine(_directory, "audit-20261009-nobody-00000000.jsonl")]);

        Assert.Equal(AuditVerifier.Unreadable, result.Code);
    }

    [Fact]
    public void T5_the_command_says_what_it_found_and_returns_its_code()
    {
        var file = Written(3);
        var intact = new StringWriter();
        var nothing = new StringWriter();

        Assert.Equal(AuditVerifier.Intact, AuditVerifier.Run([file], intact));
        Assert.Equal(AuditVerifier.Usage, AuditVerifier.Run([], nothing));

        Assert.Contains("intact: 3 records in 1 file(s), numbered 1 to 3", intact.ToString(), StringComparison.Ordinal);
        Assert.Contains("usage: --verify-audit", nothing.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void T6_the_chain_carries_on_across_midnight_into_the_next_days_file()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 9, 23, 59, 59, TimeSpan.Zero));
        using (var trail = Trail(clock))
        {
            Assert.True(trail.Record(Event(1)));
            Assert.True(trail.Record(Event(2)));
            clock.Now = clock.Now.AddSeconds(2);
            Assert.True(trail.Record(Event(3)));
        }

        var files = AuditFiles.In(_directory);
        Assert.Equal(2, files.Count);
        Assert.Contains("-20261009-", files[0], StringComparison.Ordinal);
        Assert.Contains("-20261010-", files[1], StringComparison.Ordinal);

        var lastOfDayOne = JsonDocument.Parse(File.ReadLines(files[0]).Last()).RootElement;
        var firstOfDayTwo = JsonDocument.Parse(File.ReadLines(files[1]).First()).RootElement;
        Assert.Equal(lastOfDayOne.GetProperty("hash").GetString(), firstOfDayTwo.GetProperty("prev_hash").GetString());
        Assert.Equal(3, firstOfDayTwo.GetProperty("seq").GetInt64());

        // The two in order are one chain; the second alone is intact too, and says where it continues from.
        Assert.Equal(AuditVerifier.Intact, AuditVerifier.Verify(files).Code);
        var alone = AuditVerifier.Verify([files[1]]);
        Assert.Equal(AuditVerifier.Intact, alone.Code);
        Assert.Equal(lastOfDayOne.GetProperty("hash").GetString(), alone.FirstPrevHash);

        // Out of order they are not a chain.
        Assert.Equal(AuditVerifier.Broken, AuditVerifier.Verify([files[1], files[0]]).Code);
    }

    [Fact]
    public void T6_files_older_than_the_retention_are_removed_when_a_file_is_opened_and_nothing_else_is()
    {
        Directory.CreateDirectory(_directory);
        var expired = Path.Combine(_directory, "audit-20260930-other-host-aaaaaaaa.jsonl");   // 9 days before: past 7
        var kept = Path.Combine(_directory, "audit-20261003-other-host-bbbbbbbb.jsonl");      // 6 days before: within 7
        var notOurs = Path.Combine(_directory, "notes-20200101.txt");
        var notDated = Path.Combine(_directory, "audit-undated.jsonl");
        foreach (var file in new[] { expired, kept, notOurs, notDated })
        {
            File.WriteAllText(file, "x");
        }

        using (var trail = Trail(new ManualClock(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)), retentionDays: 7))
        {
            Assert.True(trail.Record(Event(1)));
        }

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(notOurs));
        Assert.True(File.Exists(notDated));
    }

    [Fact]
    public void Two_instances_write_two_files_and_each_is_a_chain_of_its_own()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        using (var one = Trail(clock, instance: "11111111"))
        using (var two = Trail(clock, instance: "22222222"))
        {
            for (var n = 1; n <= 3; n++)
            {
                Assert.True(one.Record(Event(n)));
                Assert.True(two.Record(Event(n)));
            }
        }

        var files = AuditFiles.In(_directory);
        Assert.Equal(2, files.Count);
        Assert.All(files, file => Assert.Equal((AuditVerifier.Intact, 3L), (AuditVerifier.Verify([file]).Code, AuditVerifier.Verify([file]).Records)));
    }

    [Fact]
    public void A_file_can_be_read_and_checked_while_the_trail_still_holds_it_open()
    {
        using var trail = Trail(new ManualClock(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(trail.Record(Event(1)));
        Assert.True(trail.Record(Event(2)));

        var file = Assert.Single(AuditFiles.In(_directory));
        Assert.Equal(2, AuditFiles.Lines(file).Count);
        Assert.Equal(AuditVerifier.Intact, AuditVerifier.Verify([file]).Code);
    }

    /// <summary>
    /// Not a pass or a fail: the one measured figure behind the contract's bet, written where a run shows it. Each append
    /// is flushed to disk before it returns, and every request waits for one.
    /// </summary>
    [Fact]
    public void Measured_what_one_flushed_append_costs_on_this_machine()
    {
        using var trail = Trail(TimeProvider.System);
        Assert.True(trail.Record(Event(0)));

        const int Count = 300;
        var watch = Stopwatch.StartNew();
        for (var n = 1; n <= Count; n++)
        {
            Assert.True(trail.Record(Event(n)));
        }

        watch.Stop();
        output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MEASURED: {Count} flushed appends in {watch.ElapsedMilliseconds} ms, {watch.Elapsed.TotalMilliseconds / Count:0.000} ms each"));
    }
}

/// <summary>A clock a test sets.</summary>
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
