using System.Text;
using McpServerTemplate.Providers.SmhiObs;

namespace McpServerTemplate.Tests.Providers.SmhiObs;

/// <summary>
/// contract-001 · T-6 (G-5) — the climate tool must be able to succeed against its upstream,
/// and must filter by month before capping rows.
///
/// The fixture reproduces SMHI's corrected-archive layout: the preamble blocks, the blank-line
/// separators, the data header, and rows carrying quality flags.
/// </summary>
public class MetObsArchiveCsvTests
{
    private const string Archive = """
        Stationsnamn;Stationsnummer;Stationsnät;Mäthöjd (meter över marken)
        Abisko Aut;188790;SMHIs stationsnät;2.0

        Parameternamn;Beskrivning;Enhet
        Lufttemperatur;momentanvärde, 1 gång/tim;celsius

        Tidsperiod (fr.o.m);Tidsperiod (t.o.m);Höjd (meter över havet);Latitud (decimalgrader);Longitud (decimalgrader)
        1985-04-01 00:00:00;1997-02-28 23:59:59;388.0;68.3555;18.8211

        Datum;Tid (UTC);Lufttemperatur;Kvalitet;;Tidsutsnitt:
        1985-07-01;00:00:00;10.0;G;;Kvalitetskontrollerade historiska data
        1985-07-02;00:00:00;12.0;G;;Tidsperiod (fr.o.m.) = 1985-04-01 00:00:00 (UTC)
        1985-08-01;00:00:00;99.0;G;;
        1990-01-15;00:00:00;-20.0;G;;
        2020-07-01;00:00:00;14.0;G;;
        2021-07-01;00:00:00;16.0;Y;;
        2022-07-01;00:00:00;500.0;B;;
        2023-07-01;00:00:00;18.0;G;;
        """;

    private static MemoryStream StreamOf(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task T6_ReadAsync_takes_the_station_and_unit_from_the_preamble()
    {
        var series = await MetObsArchiveCsv.ReadAsync(StreamOf(Archive), targetMonth: 7, maxReadings: 100);

        Assert.Equal("Abisko Aut", series.StationName);
        Assert.Equal("Lufttemperatur", series.ParameterName);
        Assert.Equal("celsius", series.Unit);
    }

    [Fact]
    public async Task T6_ReadAsync_keeps_only_the_requested_month()
    {
        var series = await MetObsArchiveCsv.ReadAsync(StreamOf(Archive), targetMonth: 7, maxReadings: 100);

        Assert.All(series.Readings, r => Assert.Equal(7, r.Timestamp.Month));

        // The August and January rows carry values that would be obvious in the output if they leaked.
        Assert.DoesNotContain(series.Readings, r => r.Value is 99.0 or -20.0);
    }

    [Fact]
    public async Task T6_ReadAsync_drops_readings_the_publisher_rejected()
    {
        var series = await MetObsArchiveCsv.ReadAsync(StreamOf(Archive), targetMonth: 7, maxReadings: 100);

        // Quality B means SMHI rejected the reading; 500 degrees should never reach an answer.
        Assert.DoesNotContain(series.Readings, r => r.Value == 500.0);

        // Y (suspect) is kept — it is data the publisher still stands behind, flagged.
        Assert.Contains(series.Readings, r => r.Value == 16.0);
    }

    [Fact]
    public async Task T6_ReadAsync_filters_by_month_before_applying_the_cap()
    {
        // This is the defect the old JSON path had: it capped first, so the cap was spent on rows
        // that were never candidates and the answer described only the oldest years.
        // With a cap of 2 and the month filter applied first, the two most recent July readings survive.
        var series = await MetObsArchiveCsv.ReadAsync(StreamOf(Archive), targetMonth: 7, maxReadings: 2);

        Assert.Equal(2, series.Readings.Count);
        Assert.True(series.Truncated, "the series should report that it dropped readings");
        Assert.Equal([2021, 2023], series.Readings.Select(r => r.Timestamp.Year).ToArray());
    }

    [Fact]
    public async Task T6_ReadAsync_reports_an_unrecognisable_archive_rather_than_returning_nothing()
    {
        var notAnArchive = "<html><body>Service unavailable</body></html>";

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => MetObsArchiveCsv.ReadAsync(StreamOf(notAnArchive), targetMonth: 7, maxReadings: 10));

        Assert.Contains("data header", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task T6_FormatMonthlyClimatology_reports_the_years_it_covered()
    {
        var series = await MetObsArchiveCsv.ReadAsync(StreamOf(Archive), targetMonth: 7, maxReadings: 100);

        var text = SmhiObsFormatters.FormatMonthlyClimatology(series, targetMonth: 7);

        Assert.Contains("Abisko Aut", text, StringComparison.Ordinal);
        Assert.Contains("July", text, StringComparison.Ordinal);
        Assert.Contains("1985-2023", text, StringComparison.Ordinal);
        Assert.Contains("2023", text, StringComparison.Ordinal);
    }
}
