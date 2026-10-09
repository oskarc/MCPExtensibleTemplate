using System.Globalization;
using System.Text.Json;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// Walks an audit file's chain and names the first line that breaks it (contract-006 · G-5; roadmap P3.2).
///
/// For each line: it ends as a record does; the SHA-256 of its bytes without the hash member is the hash it carries; its
/// prev_hash is the hash of the line before; and its number is one more. The bytes are read as they lie on disk and nothing
/// is re-written to compare. Several files are walked in the order given, as one chain: a day's file, then the next day's.
///
/// What it proves is that nobody changed, removed, added or reordered a line without redoing every line after it. It does
/// not prove the end of the file is the end of what was written, or that the whole chain was not rewritten: the first
/// record's prev_hash and the last record's hash are printed so they can be compared with a copy held elsewhere.
/// </summary>
public static class AuditVerifier
{
    /// <summary>The chain is intact.</summary>
    public const int Intact = 0;

    /// <summary>The command was not given a file (EX_USAGE).</summary>
    public const int Usage = 64;

    /// <summary>A line breaks the chain (EX_DATAERR).</summary>
    public const int Broken = 65;

    /// <summary>A file cannot be read (EX_NOINPUT).</summary>
    public const int Unreadable = 66;

    /// <summary>What a walk found.</summary>
    /// <param name="Code">One of this class's exit codes.</param>
    /// <param name="Records">How many records were intact before the walk ended.</param>
    /// <param name="File">The file the walk stopped in; null when it finished.</param>
    /// <param name="Line">The line it stopped at, from 1.</param>
    /// <param name="Reason">Why it stopped.</param>
    /// <param name="FirstPrevHash">The prev_hash of the first record: where this chain says it continues from.</param>
    /// <param name="LastHash">The hash of the last intact record.</param>
    /// <param name="FirstSeq">The first record's number.</param>
    /// <param name="LastSeq">The last intact record's number.</param>
    public sealed record Result(int Code, long Records, string? File, long Line, string? Reason, string? FirstPrevHash, string? LastHash, long FirstSeq, long LastSeq);

    /// <summary>Walks <paramref name="files"/> in order, as one chain.</summary>
    public static Result Verify(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        long records = 0;
        string? firstPrev = null;
        string? lastHash = null;
        long firstSeq = 0;
        long lastSeq = 0;

        Result Stop(int code, string file, long line, string reason) =>
            new(code, records, file, line, reason, firstPrev, lastHash, firstSeq, lastSeq);

        foreach (var file in files)
        {
            byte[] bytes;
            try
            {
                // Read while a server may still be writing it: today's file is checked like any other.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return Stop(Unreadable, file, 0, $"the file cannot be read: {ex.Message}");
            }

            var rest = bytes.AsSpan();
            long number = 0;
            while (!rest.IsEmpty)
            {
                number++;
                var end = rest.IndexOf((byte)'\n');
                if (end < 0)
                {
                    return Stop(Broken, file, number, "the line has no end: the file was cut short, or the record was never written whole");
                }

                var line = rest[..end];
                rest = rest[(end + 1)..];

                if (!AuditRecordWriter.TrySplit(line, out var body, out var carried))
                {
                    return Stop(Broken, file, number, "the line does not end with a record's hash");
                }

                var computed = AuditRecordWriter.HashOf(body);
                if (!string.Equals(computed, carried, StringComparison.Ordinal))
                {
                    return Stop(Broken, file, number, $"the line was changed: it carries hash {carried} and its bytes hash to {computed}");
                }

                string? prev;
                long seq;
                try
                {
                    using var document = JsonDocument.Parse(body);
                    prev = document.RootElement.GetProperty("prev_hash").GetString();
                    seq = document.RootElement.GetProperty("seq").GetInt64();
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    return Stop(Broken, file, number, "the line is not a record: it has no prev_hash and seq to read");
                }

                if (records == 0)
                {
                    firstPrev = prev;
                    firstSeq = seq;
                }
                else
                {
                    if (!string.Equals(prev, lastHash, StringComparison.Ordinal))
                    {
                        return Stop(Broken, file, number, $"the chain breaks here: its prev_hash is {prev} and the record before it hashes to {lastHash} — a line was removed, added or moved");
                    }

                    if (seq != lastSeq + 1)
                    {
                        return Stop(Broken, file, number, $"the numbering breaks here: it is record {seq} and the one before it is {lastSeq}");
                    }
                }

                lastHash = carried;
                lastSeq = seq;
                records++;
            }
        }

        return new Result(Intact, records, null, 0, null, firstPrev, lastHash, firstSeq, lastSeq);
    }

    /// <summary>The <c>--verify-audit</c> command: walks the files named, says what it found, and returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> files, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(output);

        if (files.Count == 0)
        {
            output.WriteLine("usage: --verify-audit <file> [<next file> ...]   the files of one server instance, oldest first");
            return Usage;
        }

        var result = Verify(files);
        if (result.Code == Intact)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"intact: {result.Records} records in {files.Count} file(s), numbered {result.FirstSeq} to {result.LastSeq}"));
            output.WriteLine($"continues from: {result.FirstPrevHash}");
            output.WriteLine($"last hash:      {result.LastHash}");
            return Intact;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{(result.Code == Unreadable ? "unreadable" : "BROKEN")}: {result.File} line {result.Line}: {result.Reason}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"intact before it: {result.Records} records"));
        return result.Code;
    }
}
