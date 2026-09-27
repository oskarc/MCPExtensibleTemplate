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

        // contract-005 · G-12 (5) — the check leaves nothing behind: the logs directory it made to
        // write its probe in is removed with the probe (the sink makes it when it first writes). The
        // refusals below show where the path resolves: beneath a file in this working directory.
        Assert.Equal([Path.Combine(_workingDirectory, "a-file")], Directory.GetFileSystemEntries(_workingDirectory));
    }

    [Fact]
    public void A_log_directory_that_was_there_already_is_left_as_it_was()
    {
        // The control for the clean-up above: only what the check made is removed.
        var logs = Directory.CreateDirectory(Path.Combine(_workingDirectory, "logs")).FullName;
        File.WriteAllText(Path.Combine(logs, "mcp-server-20260101.log"), "earlier\n");

        LogSinkGuard.Validate(Config(Shipped()), _workingDirectory);

        Assert.Equal([Path.Combine(logs, "mcp-server-20260101.log")], Directory.GetFileSystemEntries(logs));
    }

    /// <summary>
    /// contract-005 · G-12 (5) — Serilog's reader expands %NAME% in a sink's arguments before the sink
    /// resolves its path, so the path is checked expanded. As written, logs/%VAR%/mcp-.log climbs
    /// nowhere; with VAR=../.., the sink writes above the working directory. The check used to pass it,
    /// and left a directory named %VAR% behind.
    /// </summary>
    [Fact]
    public void A_file_sink_whose_path_climbs_out_through_an_environment_variable_is_refused()
    {
        var variable = $"MCP_LOG_SINK_GUARD_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "../..");
        try
        {
            var settings = Shipped();
            settings["Serilog:WriteTo:2:Name"] = "File";
            settings["Serilog:WriteTo:2:Args:path"] = $"logs/%{variable}%/mcp-.log";

            var ex = Assert.Throws<ConfigurationException>(() => LogSinkGuard.Validate(Config(settings), _workingDirectory));

            Assert.Contains("path traversal", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Serilog:WriteTo:2:Args:path", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'logs/../../mcp-.log'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void A_file_sink_is_checked_where_its_expanded_path_resolves()
    {
        // Expanded, the path lies beneath a file, where nothing can be written; as written, it named a
        // directory called %VAR% that the check made, wrote in, and passed.
        var variable = $"MCP_LOG_SINK_GUARD_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "a-file");
        try
        {
            var settings = Shipped();
            settings["Serilog:WriteTo:2:Name"] = "File";
            settings["Serilog:WriteTo:2:Args:path"] = $"%{variable}%/mcp-.log";

            var ex = Assert.Throws<ConfigurationException>(() => LogSinkGuard.Validate(Config(settings), _workingDirectory));

            Assert.StartsWith($"Serilog:WriteTo:2 is a File log sink writing to {Path.Combine(_workingDirectory, "a-file", "mcp-.log")}", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(_workingDirectory, $"%{variable}%")), "the check left a directory named after the unexpanded variable");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// What can stand at a sink's file path, in a directory the process may write, that the process
    /// cannot append to. A read-only file binds any process on Windows and an unprivileged one on
    /// Unix; root on Unix may append to it anyway, and so may its sink, so there it is no case.
    /// </summary>
    public static TheoryData<string> Unappendable()
    {
        var data = new TheoryData<string> { "a directory" };
        if (OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            data.Add("a read-only file");
        }

        return data;
    }

    /// <summary>
    /// contract-005 · G-12 (5) — the directory may be writable while the file the sink appends to is
    /// not. The check wrote its probe beside the file and passed; the sink then wrote nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Unappendable))]
    public void An_existing_file_the_process_cannot_append_to_is_refused_naming_it(string what)
    {
        var logs = Directory.CreateDirectory(Path.Combine(_workingDirectory, "logs")).FullName;
        var file = Path.Combine(logs, "mcp.log");
        if (what == "a directory")
        {
            Directory.CreateDirectory(file);
        }
        else
        {
            File.WriteAllText(file, "earlier\n");
            SetReadOnly(file, readOnly: true);
        }

        try
        {
            var settings = Shipped();
            settings["Serilog:WriteTo:2:Name"] = "File";
            settings["Serilog:WriteTo:2:Args:path"] = "logs/mcp.log";

            var ex = Assert.Throws<ConfigurationException>(() => LogSinkGuard.Validate(Config(settings), _workingDirectory));

            Assert.StartsWith($"Serilog:WriteTo:2 is a File log sink writing to {file}", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(file))
            {
                SetReadOnly(file, readOnly: false);
            }
        }
    }

    private static void SetReadOnly(string file, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(file, readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal);
        }
        else
        {
            File.SetUnixFileMode(file, readOnly
                ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
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
