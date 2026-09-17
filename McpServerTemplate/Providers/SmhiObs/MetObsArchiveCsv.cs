using System.Globalization;
using ModelContextProtocol;

namespace McpServerTemplate.Providers.SmhiObs;

/// <summary>
/// One reading from SMHI's corrected archive.
/// </summary>
/// <param name="Timestamp">Observation time, UTC.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Quality">SMHI's quality flag: G = controlled and approved, Y = suspect, B = rejected.</param>
public sealed record ArchiveReading(DateTimeOffset Timestamp, double Value, string Quality);

/// <summary>
/// A month-filtered slice of one station's corrected archive.
/// </summary>
/// <param name="StationName">Station name as the archive declares it.</param>
/// <param name="ParameterName">Measured parameter, e.g. Lufttemperatur.</param>
/// <param name="Unit">Unit of the values, e.g. celsius.</param>
/// <param name="Readings">Readings for the requested month, oldest first.</param>
/// <param name="Truncated">True when the cap was reached and older readings were dropped.</param>
public sealed record ArchiveSeries(
    string StationName,
    string ParameterName,
    string Unit,
    IReadOnlyList<ArchiveReading> Readings,
    bool Truncated);

/// <summary>
/// Reader for SMHI's corrected-archive CSV.
///
/// contract-001 · G-5 — the corrected archive is published as CSV only; the .json variant of that
/// period is 404 at every station. This reader is what lets the climate tool succeed against its
/// upstream at all.
///
/// It also fixes the order the old JSON path got wrong. The archive is decades of hourly readings
/// (Abisko: ~157,000 rows, 1985 to now). Capping the rows first and filtering by month afterwards
/// answers "what is a typical July here" from whatever the cap happened to admit — the oldest
/// years — while presenting it as the current climate. So the month filter is applied as the file
/// is read, and the cap applies to readings that survived it.
///
/// The file is read as a stream and never held whole in memory.
///
/// FORMAT (semicolon-separated, several preamble blocks separated by blank lines):
///   Stationsnamn;Stationsnummer;Stationsnät;Mäthöjd (meter över marken)
///   Abisko Aut;188790;SMHIs stationsnät;2.0
///
///   Parameternamn;Beskrivning;Enhet
///   Lufttemperatur;momentanvärde, 1 gång/tim;celsius
///   ...
///   Datum;Tid (UTC);Lufttemperatur;Kvalitet;;Tidsutsnitt:
///   1985-06-01;00:00:00;1.6;G;;...
/// </summary>
public static class MetObsArchiveCsv
{
    private const char Separator = ';';

    /// <summary>
    /// Reads the archive, keeping only readings in <paramref name="targetMonth"/>.
    /// </summary>
    /// <param name="stream">The CSV stream, as served.</param>
    /// <param name="targetMonth">Month to keep, 1-12.</param>
    /// <param name="maxReadings">Cap on kept readings. Applied after the month filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<ArchiveSeries> ReadAsync(
        Stream stream,
        int targetMonth,
        int maxReadings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetMonth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetMonth, 12);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReadings, 1);

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var stationName = "unknown station";
        var parameterName = "unknown parameter";
        var unit = string.Empty;

        // Ring buffer of the most recent kept readings: when an archive has more readings for the
        // month than the cap allows, the recent decades are the ones a climate question is about.
        var kept = new Queue<ArchiveReading>(maxReadings);
        var truncated = false;
        var inData = false;
        var sawHeader = false;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!inData)
            {
                // The preamble labels each block on one line and answers it on the next.
                if (line.StartsWith("Stationsnamn", StringComparison.Ordinal))
                {
                    var row = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    var fields = row?.Split(Separator);
                    if (fields is { Length: > 0 } && !string.IsNullOrWhiteSpace(fields[0]))
                        stationName = fields[0].Trim();
                    continue;
                }

                if (line.StartsWith("Parameternamn", StringComparison.Ordinal))
                {
                    var row = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    var fields = row?.Split(Separator);
                    if (fields is { Length: >= 3 })
                    {
                        parameterName = fields[0].Trim();
                        unit = fields[2].Trim();
                    }
                    continue;
                }

                // The data block's own header. Everything after it is readings.
                if (line.StartsWith("Datum", StringComparison.Ordinal))
                {
                    inData = true;
                    sawHeader = true;
                }

                continue;
            }

            var parsed = ParseReading(line);
            if (parsed is null)
            {
                continue;
            }

            // Filter first. The cap below then applies to readings that are actually candidates
            // for the answer, not to rows that were never going to be part of it.
            if (parsed.Timestamp.Month != targetMonth)
            {
                continue;
            }

            kept.Enqueue(parsed);
            if (kept.Count > maxReadings)
            {
                kept.Dequeue();
                truncated = true;
            }
        }

        if (!sawHeader)
        {
            throw new McpException(
                "SMHI's archive did not contain a recognisable data header. The archive format may have "
                + "changed; the observation tools that use the recent-months periods are unaffected.");
        }

        return new ArchiveSeries(stationName, parameterName, unit, [.. kept], truncated);
    }

    /// <summary>
    /// Parses one data row, returning null for rows that are not readings — SMHI appends
    /// explanatory columns and trailing notes to the same block.
    /// </summary>
    private static ArchiveReading? ParseReading(string line)
    {
        var fields = line.Split(Separator);
        if (fields.Length < 4)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return null;
        }

        if (!TimeOnly.TryParseExact(fields[1], "HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            return null;
        }

        if (!double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var quality = fields[3].Trim();

        // B means SMHI rejected the reading. Including it would put a value the publisher
        // disowns into an answer about what is normal.
        if (quality.Equals("B", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ArchiveReading(new DateTimeOffset(date.ToDateTime(time), TimeSpan.Zero), value, quality);
    }
}
