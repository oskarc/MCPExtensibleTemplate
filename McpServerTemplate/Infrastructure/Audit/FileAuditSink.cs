using System.Globalization;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>One record as it was written: the event, where it sits in the chain, and its line.</summary>
/// <param name="Event">The event.</param>
/// <param name="At">When it was written.</param>
/// <param name="Seq">Its number in the chain.</param>
/// <param name="PrevHash">The hash of the record before.</param>
/// <param name="Hash">Its own hash.</param>
/// <param name="Line">Its line, without a line end. The same bytes go to every destination.</param>
public sealed record AuditRecord(AuditEvent Event, DateTimeOffset At, long Seq, string PrevHash, string Hash, ReadOnlyMemory<byte> Line);

/// <summary>
/// Somewhere audit records go (roadmap P3.1). A gating sink is the record itself: when it cannot take a record it throws,
/// and the trail treats the decision as unrecorded (contract-006 · G-4). A sink that does not gate takes a copy and never
/// throws; what it cannot deliver it counts and reports.
/// </summary>
public interface IAuditSink
{
    /// <summary>Whether a record this sink cannot take counts as not written.</summary>
    bool Gating { get; }

    /// <summary>A name for the logs: what the sink is and where it writes.</summary>
    string Description { get; }

    /// <summary>Takes one record. A gating sink returns only once the record is durable, and throws when it is not.</summary>
    void Append(AuditRecord record);
}

/// <summary>Which server instance writes a chain: one chain, and one set of files, per instance (contract-006 · G-5).</summary>
/// <param name="Hostname">The machine's name.</param>
/// <param name="Instance">Eight hex characters chosen at startup.</param>
public sealed record AuditIdentity(string Hostname, string Instance)
{
    /// <summary>This process: the machine's name, and an instance id nobody else has.</summary>
    public static AuditIdentity Create() =>
        new(Environment.MachineName, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)));
}

/// <summary>
/// The audit trail's file (contract-006 · G-4, G-5): JSON lines, one file per server instance per UTC day, each append
/// flushed to disk before it returns.
///
/// One file belongs to one instance. Its name carries the day, the host and the instance, and it is created new — never
/// opened to append — so no two processes can write one chain, and a restarted server starts a file of its own. It is opened
/// for reading by others, so a log forwarder can follow it. When the day turns, the next record opens the next day's file;
/// the chain itself is the trail's and carries on across it. Files older than the retention are removed then.
///
/// A write that fails may leave half a line behind. The file is then left as it is, for the check to find, and the next
/// record opens a new file for the same day: a damaged file is never written to again.
/// </summary>
public sealed class FileAuditSink : IAuditSink, IDisposable
{
    private const string Prefix = "audit-";
    private const string Extension = ".jsonl";

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _stem;
    private readonly int _retentionDays;
    private FileStream? _stream;
    private DateOnly _day;
    private int _segment;

    /// <summary>Creates the sink. No file is opened until the first record.</summary>
    public FileAuditSink(AuditSettings settings, AuditIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(identity);

        _directory = settings.Directory;
        _retentionDays = settings.RetentionDays;
        _stem = $"{FileSafe(identity.Hostname)}-{identity.Instance}";
    }

    /// <inheritdoc />
    public bool Gating => true;

    /// <inheritdoc />
    public string Description => $"file in {_directory}";

    /// <summary>The file the last record went to; null before the first.</summary>
    public string? CurrentFile { get; private set; }

    /// <inheritdoc />
    public void Append(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            var day = DateOnly.FromDateTime(record.At.UtcDateTime);
            if (_stream is null || day != _day)
            {
                Open(day);
            }

            try
            {
                // The line and its end in one write, so a reader following the file never sees a line without its end.
                var line = record.Line.Span;
                var buffer = new byte[line.Length + 1];
                line.CopyTo(buffer);
                buffer[^1] = (byte)'\n';
                _stream!.Write(buffer);
                _stream.Flush(flushToDisk: true);
            }
            catch
            {
                Abandon();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void Open(DateOnly day)
    {
        _stream?.Dispose();
        _stream = null;
        if (day != _day)
        {
            _day = day;
            _segment = 0;
        }

        Directory.CreateDirectory(_directory);

        // Created new, never opened to append. A name already taken is a file this instance abandoned, or another's: the next.
        for (; ; _segment++)
        {
            var name = _segment == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{Prefix}{day:yyyyMMdd}-{_stem}{Extension}")
                : string.Create(CultureInfo.InvariantCulture, $"{Prefix}{day:yyyyMMdd}-{_stem}-{_segment:D3}{Extension}");
            var path = Path.Combine(_directory, name);
            try
            {
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.None);
                CurrentFile = path;
                break;
            }
            catch (IOException) when (File.Exists(path) && _segment < 999)
            {
            }
        }

        RemoveExpired(day);
    }

    private void Abandon()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (IOException)
        {
            // The file is being left as it is; closing it failing as well changes nothing.
        }

        _stream = null;
        _segment++;
    }

    /// <summary>Removes the audit files of days older than the retention. Only files named as this sink names them.</summary>
    private void RemoveExpired(DateOnly today)
    {
        var oldest = today.AddDays(-_retentionDays);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, $"{Prefix}*{Extension}"))
            {
                var name = Path.GetFileName(path);
                if (name.Length >= Prefix.Length + 8
                    && DateOnly.TryParseExact(name.AsSpan(Prefix.Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && day < oldest)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An old file that cannot be removed today is tried again at the next turn of the day; it stops no record.
        }
    }

    /// <summary>A host name as part of a file name: letters, digits, dots and hyphens, at most 63 of them.</summary>
    private static string FileSafe(string value)
    {
        var safe = new string([.. value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').Take(63)]);
        return safe.Length == 0 ? "host" : safe;
    }
}
