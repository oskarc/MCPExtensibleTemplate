using System.Globalization;
using System.Text.Json.Nodes;
using McpServerTemplate.Infrastructure;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-005 · G-17 round 2 (SEC-R2-03) — every line the server's sinks write is its own: whatever a caller put into an
/// event — in a property the SDK names it by, in the text of a template, in an exception's message, however deep in a value —
/// the event is written as the lines its output template makes, one for the message and one for an exception, and nothing a
/// caller wrote breaks a line, forges the next, rewrites a terminal or shows the line other than it is. Nothing is cut: what
/// was there still shows, escaped (<see cref="LogSafe.Escaped"/>).
/// </summary>
public sealed class LineSafeLoggingTests : IDisposable
{
    /// <summary>A log line as the finding forged it, at a time no real line carries.</summary>
    internal const string Forged = "[99:99:99 WRN] McpServerTemplate.Identity : authn_login_fail: idp:corp refused a token for /mcp — FORGED LINE (principal=corp:alice)";

    /// <summary>
    /// What a caller wrote, as the finding sent it: a line break and the forged line after it, a terminal's clear-screen and
    /// cursor-home, a direction override, and the Unicode line separator.
    /// </summary>
    internal static readonly string Hostile = $"x\r\n{Forged}\u001b[2J\u001b[1;1H\u202Egnp.exe\u2028";

    private readonly string _directory = Directory.CreateTempSubdirectory("mcp-line-safe-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>The output templates the shipped sinks write with, each entry of Serilog:WriteTo in appsettings.Production.json.</summary>
    internal static IReadOnlyList<string> ShippedTemplates()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.Production.json")))!;
        var templates = settings["Serilog"]!["WriteTo"]!.AsArray()
            .Select(sink => sink!["Args"]?["outputTemplate"]?.GetValue<string>())
            .OfType<string>()
            .ToArray();
        Assert.True(templates.Length > 0, "appsettings.Production.json names no output template: nothing to hold the lines to.");
        return templates;
    }

    /// <summary>
    /// <paramref name="logEvent"/>, as each shipped template writes it, is the lines the template makes — one for the message,
    /// and one for an exception — with no character in it that breaks or disguises a line but the template's own line ends,
    /// and none of them the forged line.
    /// </summary>
    internal static void AssertWrittenAsItsOwnLines(LogEvent logEvent, string what)
    {
        foreach (var template in ShippedTemplates())
        {
            var written = Render(logEvent, template).Replace(Environment.NewLine, "\n", StringComparison.Ordinal);
            var expected = logEvent.Exception is null ? 1 : 2;
            var lines = written.Split('\n');

            Assert.True(
                written.Count(c => c == '\n') == expected,
                $"{what}: an event was written as {written.Count(c => c == '\n')} lines, not {expected}: '{written}'.");
            Assert.False(
                LogSafe.BreaksALine(written.Replace("\n", string.Empty, StringComparison.Ordinal)),
                $"{what}: an event was written with a character that breaks or disguises a line: '{written}'.");
            Assert.DoesNotContain(lines, line => line.StartsWith("[99:99:99", StringComparison.Ordinal));
        }
    }

    /// <summary><paramref name="logEvent"/> as <paramref name="template"/> writes it, as the File sink writes it.</summary>
    internal static string Render(LogEvent logEvent, string template)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        new MessageTemplateTextFormatter(template, CultureInfo.InvariantCulture).Format(logEvent, output);
        return output.ToString();
    }

    /// <summary>A logger that gives each event to <paramref name="captured"/> through a <see cref="LineSafeSink"/>.</summary>
    private static Logger LineSafeLogger(Capture captured) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new LineSafeSink(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger()))
            .CreateLogger();

    /// <summary>
    /// The SDK's own lines name the method, the tool and the client as the caller wrote them, and carry the exception the
    /// frame refused with: 16 forged lines came from such lines and the exceptions under them. Each is its own lines now.
    /// </summary>
    [Fact]
    public void An_sdk_line_naming_what_a_caller_wrote_and_carrying_its_exception_is_written_as_its_own_lines()
    {
        var captured = new Capture();
        using (var logger = LineSafeLogger(captured))
        {
            logger.ForContext(Constants.SourceContextPropertyName, "ModelContextProtocol.Server.McpServer").Warning(
                new InvalidOperationException($"authz_fail (rule: not-permitted). You may not use the tool '{Hostile}', or it does not exist."),
                "{EndpointName} method '{Method}' request handler failed in {ElapsedMilliseconds}ms.",
                $"Server (McpServerTemplate 1.0.0), Client ({Hostile} 1.0)",
                Hostile,
                12);
        }

        var written = Assert.Single(captured.Events);
        AssertWrittenAsItsOwnLines(written, "an SDK line");

        // Nothing is lost: the forged text is still there, escaped, on the event's own lines.
        var shown = Render(written, ShippedTemplates()[0]);
        Assert.Contains($"\\r\\n{Forged}\\u001B[2J", shown, StringComparison.Ordinal);
        Assert.Contains("\\u202Egnp.exe\\u2028", shown, StringComparison.Ordinal);
    }

    /// <summary>A template whose text is the caller's — a component logging text it was given — is its own line too.</summary>
    [Fact]
    public void A_template_whose_text_is_what_a_caller_wrote_is_written_as_its_own_line()
    {
        var captured = new Capture();
        using (var logger = LineSafeLogger(captured))
        {
            logger.Warning("Tool " + Hostile + " failed");
        }

        AssertWrittenAsItsOwnLines(Assert.Single(captured.Events), "a caller's template");
    }

    /// <summary>A value however deep — a structure, a sequence, a dictionary's keys and values — is escaped where it is.</summary>
    [Fact]
    public void A_structured_value_holding_what_a_caller_wrote_is_written_as_its_own_line()
    {
        var captured = new Capture();
        using (var logger = LineSafeLogger(captured))
        {
            logger.Information(
                "{@Request} {Names} {Arguments}",
                new { Name = Hostile, Nested = new { Uri = Hostile } },
                new[] { Hostile, "get_forecast" },
                new Dictionary<string, string> { [Hostile] = Hostile });
        }

        AssertWrittenAsItsOwnLines(Assert.Single(captured.Events), "a structured value");
    }

    /// <summary>Positive control: an event with nothing to escape is passed on as it is, the same event.</summary>
    [Fact]
    public void An_event_with_nothing_to_escape_is_passed_on_as_it_is()
    {
        var clean = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            new InvalidOperationException("a refusal"),
            new MessageTemplateParser().Parse("authz_fail: rule={Rule} target={Target}"),
            [new LogEventProperty("Rule", new ScalarValue("not-permitted")), new LogEventProperty("Target", new ScalarValue("get_forecast"))]);

        Assert.Same(clean, LineSafeSink.LineSafe(clean));
    }

    /// <summary>
    /// The server reads its log pipeline from its settings through <see cref="LineSafeLogging.ReadFromConfigurationLineSafe"/>:
    /// the logger keeps the settings' levels and their overrides (a component below the default is not written; the frame's,
    /// raised by its override, is), and the sink the settings name — a File sink, with the shipped template — writes each event
    /// as its own lines, an exception's included.
    /// </summary>
    [Fact]
    public void The_pipeline_the_settings_describe_keeps_its_levels_and_writes_each_event_as_its_own_lines()
    {
        var path = Path.Combine(_directory, "server.log");
        var settings = new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Warning",
            ["Serilog:MinimumLevel:Override:McpServerTemplate"] = "Information",
            ["Serilog:WriteTo:0:Name"] = "File",
            ["Serilog:WriteTo:0:Args:path"] = path,
            ["Serilog:WriteTo:0:Args:outputTemplate"] = ShippedTemplates()[^1],
            ["Serilog:Enrich:0"] = "FromLogContext",
        };

        using (var logger = new LoggerConfiguration()
            .ReadFromConfigurationLineSafe(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .CreateLogger())
        {
            logger.ForContext(Constants.SourceContextPropertyName, "McpServerTemplate.Infrastructure.Frame.RequestGate")
                .Information("kept by its override: {Target}", Hostile);
            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics")
                .Information("not written, below the default: {Target}", Hostile);
            logger.ForContext(Constants.SourceContextPropertyName, "ModelContextProtocol.Server.McpServer")
                .Error(new InvalidOperationException(Hostile), "\"{ToolName}\" threw an unhandled exception.", Hostile);
        }

        // File.ReadAllLines splits at a carriage return and a line feed, so a line a caller broke is a line too many.
        var lines = File.ReadAllLines(path);
        Assert.True(lines.Length == 3, $"the file holds {lines.Length} lines, not the kept event's one and the error's two: {string.Join(" | ", lines)}");
        Assert.Contains("kept by its override", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("below the default", StringComparison.Ordinal));
        Assert.Contains("threw an unhandled exception", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("System.InvalidOperationException: x\\r\\n", lines[2], StringComparison.Ordinal);
        Assert.All(lines, line => Assert.False(LogSafe.BreaksALine(line), $"a written line breaks or disguises a line: '{line}'"));
        Assert.DoesNotContain(lines, line => line.StartsWith("[99:99:99", StringComparison.Ordinal));
    }

    /// <summary>A sink that keeps every event it is given.</summary>
    internal sealed class Capture : ILogEventSink
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events => [.. _events];

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
