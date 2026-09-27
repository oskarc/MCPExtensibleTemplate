using McpServerTemplate.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-005 · G-12 (5) — every File log sink is checked where it will write, and refused when it
/// cannot write there, naming the path it resolved. A sink that cannot write used to write nothing and
/// say nothing, and only Serilog:WriteTo:1 was looked at.
///
/// "Cannot write" is made portably by pointing a sink beneath a file: a directory cannot be created
/// where a file already is, as root or not, on Windows or Linux.
/// </summary>
public sealed class LogSinkGuardTests : IDisposable
{
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("log-sink-guard-").FullName;

    public LogSinkGuardTests() => File.WriteAllText(Path.Combine(_workingDirectory, "a-file"), "not a directory");

    public void Dispose() => Directory.Delete(_workingDirectory, recursive: true);

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>The shipped shape: a console sink at 0 and a writable file sink at 1.</summary>
    private static Dictionary<string, string?> Shipped() => new()
    {
        ["Serilog:WriteTo:0:Name"] = "Console",
        ["Serilog:WriteTo:1:Name"] = "File",
        ["Serilog:WriteTo:1:Args:path"] = "logs/mcp-server-.log",
    };

    [Fact]
    public void A_writable_file_sink_passes_and_its_path_resolves_against_the_working_directory()
    {
        LogSinkGuard.Validate(Config(Shipped()), _workingDirectory);

        // The directory the sink would create is created where the sink would create it, and the
        // check leaves nothing else behind.
        var logs = Path.Combine(_workingDirectory, "logs");
        Assert.True(Directory.Exists(logs));
        Assert.Empty(Directory.GetFileSystemEntries(logs));
    }

    [Theory]
    [InlineData("Serilog:WriteTo:2")]
    [InlineData("Serilog:WriteTo:0")]
    [InlineData("Serilog:WriteTo:1")]
    public void A_file_sink_at_any_index_that_cannot_write_is_refused_naming_the_resolved_path(string sink)
    {
        var settings = Shipped();
        settings[$"{sink}:Name"] = "File";
        settings[$"{sink}:Args:path"] = "a-file/mcp-.log";

        var ex = Assert.Throws<ConfigurationException>(() => LogSinkGuard.Validate(Config(settings), _workingDirectory));

        Assert.StartsWith($"{sink} is a File log sink writing to {Path.Combine(_workingDirectory, "a-file", "mcp-.log")}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_sink_in_a_sub_logger_is_checked_too()
    {
        // Serilog's Logger sink configures a sub-logger with a WriteTo of its own.
        var settings = Shipped();
        settings["Serilog:WriteTo:2:Name"] = "Logger";
        settings["Serilog:WriteTo:2:Args:configureLogger:WriteTo:0:Name"] = "File";
        settings["Serilog:WriteTo:2:Args:configureLogger:WriteTo:0:Args:path"] = "a-file/audit-.log";

        var ex = Assert.Throws<ConfigurationException>(() => LogSinkGuard.Validate(Config(settings), _workingDirectory));

        Assert.StartsWith("Serilog:WriteTo:2:Args:configureLogger:WriteTo:0 is a File log sink", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_sink_whose_path_climbs_out_is_refused_at_any_index()
    {
        var settings = Shipped();
        settings["Serilog:WriteTo:2:Name"] = "File";
        settings["Serilog:WriteTo:2:Args:path"] = "logs/../../elsewhere/mcp-.log";

        var ex = Assert.Throws<ConfigurationException>(() => LogSinkGuard.Validate(Config(settings), _workingDirectory));

        Assert.Contains("path traversal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Serilog:WriteTo:2:Args:path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_sink_with_no_path_is_refused()
    {
        var settings = Shipped();
        settings["Serilog:WriteTo:2:Name"] = "File";

        var ex = Assert.Throws<ConfigurationException>(() => LogSinkGuard.Validate(Config(settings), _workingDirectory));

        Assert.Contains("Serilog:WriteTo:2 is a File log sink with no", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sink_that_is_not_a_file_is_not_checked()
    {
        var settings = Shipped();
        settings["Serilog:WriteTo:2:Name"] = "Console";
        settings["Serilog:WriteTo:2:Args:path"] = "a-file/ignored.log";

        LogSinkGuard.Validate(Config(settings), _workingDirectory);
    }
}
