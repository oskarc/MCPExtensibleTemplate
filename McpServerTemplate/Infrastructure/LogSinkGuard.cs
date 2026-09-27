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
            var path = sink.GetSection("Args")["path"];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ConfigurationException(
                    $"{sink.Path} is a File log sink with no {key}, so it has nowhere to write.");
            }

            // A log path that can climb out of its directory is a write primitive, so it is refused
            // rather than normalised.
            if (path.Contains("..", StringComparison.Ordinal))
            {
                throw new ConfigurationException(
                    $"Log file path '{path}' ({key}) contains path traversal characters (..). Use an absolute path.");
            }

            // Where the sink will write: the file sink and its rolling form both resolve the path
            // against the working directory, and create the directory it names.
            var resolved = Path.GetFullPath(path, workingDirectory);
            var directory = Path.GetDirectoryName(resolved) ?? workingDirectory;
            try
            {
                Directory.CreateDirectory(directory);
                using var probe = new FileStream(
                    Path.Combine(directory, $".log-write-check-{Guid.NewGuid():N}"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ConfigurationException(
                    $"{sink.Path} is a File log sink writing to {resolved} ('{path}', resolved against the working "
                    + $"directory {workingDirectory}), and this process cannot write there: {ex.Message} A sink that "
                    + "cannot write writes nothing, and the log it stands for would be missing without a word.");
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
