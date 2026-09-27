namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Refuses to start when a File log sink cannot write where it will write.
///
/// contract-005 · G-12 (5) — Serilog's File sink resolves a relative path against the working
/// directory and opens its file on the first event. When it cannot, Serilog reports the failure to
/// its self-log and carries on: the sink writes nothing and says nothing, and the operator believes
/// there is a log. The startup check used to read one entry, Serilog:WriteTo:1, and only for path
/// traversal; a sink at any other index, or in a sub-logger, was never looked at. Now every File
/// entry under Serilog:WriteTo is checked, at the path the sink will resolve, before anything is
/// served. A sink that fails later — a disk that fills, a directory removed — is Serilog's self-log's
/// to report, and that goes to stderr (Program.cs).
/// </summary>
public static class LogSinkGuard
{
    /// <summary>The Serilog sink method that writes files.</summary>
    private const string FileSink = "File";

    /// <summary>Checks every File sink under <c>Serilog:WriteTo</c>.</summary>
    /// <param name="configuration">The configuration Serilog will read.</param>
    /// <param name="workingDirectory">The directory a relative path resolves against: the process's working directory.</param>
    /// <exception cref="ConfigurationException">A File sink names no path, climbs out of its directory, or cannot write where it resolves.</exception>
    public static void Validate(IConfiguration configuration, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        foreach (var sink in FileSinks(configuration.GetSection("Serilog:WriteTo")))
        {
            var key = $"{sink.Path}:Args:path";
            var configured = sink.GetSection("Args")["path"];
            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new ConfigurationException(
                    $"{sink.Path} is a File log sink with no {key}, so it has nowhere to write.");
            }

            // contract-005 · G-12 (5) — the path as the sink will get it. Serilog's configuration reader
            // expands %NAME% in every string argument (Environment.ExpandEnvironmentVariables) before the
            // sink sees it, so both checks below read it expanded. Read as written,
            // logs/%MCP_LOGDIR%/x.log with MCP_LOGDIR=../../tmp passed the traversal check and wrote /tmp.
            var path = Environment.ExpandEnvironmentVariables(configured);
            var expands = path == configured ? string.Empty : $", which expands to '{path}'";

            // A log path that can climb out of its directory is a write primitive, so it is refused
            // rather than normalised.
            if (path.Contains("..", StringComparison.Ordinal))
            {
                throw new ConfigurationException(
                    $"Log file path '{configured}' ({key}){(expands.Length > 0 ? expands + "," : string.Empty)} contains "
                    + "path traversal characters (..). Use an absolute path.");
            }

            // Where the sink will write: the file sink and its rolling form both resolve the path
            // against the working directory, and create the directory it names.
            var resolved = Path.GetFullPath(path, workingDirectory);
            var directory = Path.GetDirectoryName(resolved) ?? workingDirectory;
            try
            {
                ProbeWritable(directory, resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ConfigurationException(
                    $"{sink.Path} is a File log sink writing to {resolved} ('{configured}'{expands}, resolved against the "
                    + $"working directory {workingDirectory}), and this process cannot write there: {ex.Message} A sink "
                    + "that cannot write writes nothing, and the log it stands for would be missing without a word.");
            }
        }
    }

    /// <summary>
    /// Proves this process can write where the sink will: a probe file in <paramref name="directory"/>,
    /// making the directories the sink would make to get there, and — contract-005 · G-12 (5) — when
    /// something already stands at <paramref name="file"/>, opening it for append as the sink will, since
    /// a writable directory can hold a file that is not. The probe is removed, and so is every directory
    /// it made: the sink makes them again when it first writes, and the check leaves nothing behind.
    /// </summary>
    private static void ProbeWritable(string directory, string file)
    {
        // The directories that are not there yet, deepest first, so they can be removed in that order.
        var missing = new List<string>();
        for (string? d = directory; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
        {
            missing.Add(d);
        }

        try
        {
            Directory.CreateDirectory(directory);

            // Made, and deleted as it closes.
            new FileStream(
                Path.Combine(directory, $".log-write-check-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose).Dispose();

            // Opened for append, as the sink opens it, and closed unwritten; shared as widely as it can be,
            // so what is tested is permission, not whether another process holds the file. Only when
            // something is there: append would otherwise create it.
            if (File.Exists(file) || Directory.Exists(file))
            {
                new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();
            }
        }
        finally
        {
            foreach (var made in missing.Where(Directory.Exists))
            {
                try
                {
                    Directory.Delete(made, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not empty, or no longer the check's: something else wrote there meanwhile, and it is
                    // not the check's to remove — nor may it hide the refusal on its way out.
                }
            }
        }
    }

    /// <summary>
    /// Every sink entry named File under <paramref name="writeTo"/>, and under the WriteTo of any
    /// sub-logger configured beneath it. Names are compared without case: a sink Serilog does not
    /// recognise under that spelling is reported by its self-log, and checking it costs nothing.
    /// </summary>
    private static IEnumerable<IConfigurationSection> FileSinks(IConfigurationSection writeTo)
    {
        foreach (var entry in writeTo.GetChildren())
        {
            if (string.Equals(entry["Name"], FileSink, StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }

            foreach (var nested in NestedWriteTo(entry))
            {
                foreach (var sink in FileSinks(nested))
                {
                    yield return sink;
                }
            }
        }
    }

    /// <summary>The WriteTo sections beneath <paramref name="section"/>, each once: a sub-logger's own sub-loggers are its to find.</summary>
    private static IEnumerable<IConfigurationSection> NestedWriteTo(IConfigurationSection section) =>
        section.GetChildren().SelectMany(child =>
            child.Key.Equals("WriteTo", StringComparison.OrdinalIgnoreCase) ? [child] : NestedWriteTo(child));
}
