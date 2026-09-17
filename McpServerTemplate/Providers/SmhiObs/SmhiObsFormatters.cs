using System.Globalization;
using System.Text;
using McpServerTemplate.Providers.SmhiObs.Models;

namespace McpServerTemplate.Providers.SmhiObs;

/// <summary>
/// Transforms raw SMHI observation data into concise, LLM-optimized text.
/// </summary>
public static class SmhiObsFormatters
{
    private const int MaxValues = 200;

    /// <summary>
    /// Formats a daily summary of recent observations — groups hourly readings
    /// into daily min/max/mean for a compact overview.
    /// </summary>
    public static string FormatDailySummary(MetObsDataResponse data)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SMHI Observations — {data.Parameter.Name}");
        sb.AppendLine($"Station: {data.Station.Name} (ID: {data.Station.Id})");
        sb.AppendLine($"Period: {data.Period.From:yyyy-MM-dd} to {data.Period.To:yyyy-MM-dd}");
        sb.AppendLine($"Unit: {data.Parameter.Unit}");
        sb.AppendLine(new string('-', 50));

        var parsed = data.Values
            .Select(v => (v.Date, Value: double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null, v.Quality))
            .Where(v => v.Value.HasValue)
            .ToList();

        if (parsed.Count == 0)
        {
            sb.AppendLine("No valid observations in this period.");
            return sb.ToString();
        }

        var byDay = parsed
            .GroupBy(v => v.Date.Date)
            .OrderByDescending(g => g.Key)
            .Take(MaxValues);

        foreach (var day in byDay)
        {
            var values = day.Select(v => v.Value!.Value).ToList();
            var min = values.Min();
            var max = values.Max();
            var mean = values.Average();

            sb.AppendLine($"  {day.Key:yyyy-MM-dd (ddd)}  min: {min:F1}  max: {max:F1}  mean: {mean:F1} {data.Parameter.Unit}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats the latest few observations as a simple time series.
    /// </summary>
    public static string FormatLatestReadings(MetObsDataResponse data, int count = 24)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SMHI Observations — {data.Parameter.Name}");
        sb.AppendLine($"Station: {data.Station.Name} (ID: {data.Station.Id})");
        sb.AppendLine($"Unit: {data.Parameter.Unit}");
        sb.AppendLine(new string('-', 40));

        var recent = data.Values
            .OrderByDescending(v => v.DateUnixMs)
            .Take(count)
            .Reverse();

        foreach (var v in recent)
        {
            var q = v.Quality == "G" ? "" : $" [{v.Quality}]";
            sb.AppendLine($"  {v.Date:yyyy-MM-dd HH:mm}  {v.Value} {data.Parameter.Unit}{q}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a climate comparison for one month of the year: per-year statistics from the
    /// station's corrected archive, then the overall picture.
    ///
    /// The series arrives already filtered to the requested month (see MetObsArchiveCsv), so
    /// nothing here has to guess which readings the cap admitted.
    /// </summary>
    public static string FormatMonthlyClimatology(ArchiveSeries series, int targetMonth)
    {
        ArgumentNullException.ThrowIfNull(series);

        var monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(targetMonth);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"SMHI Corrected Archive — {series.ParameterName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Station: {series.StationName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Month: {monthName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Unit: {series.Unit}");
        sb.AppendLine(new string('-', 50));

        if (series.Readings.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"No archived observations available for {monthName}.");
            return sb.ToString();
        }

        var byYear = series.Readings
            .GroupBy(r => r.Timestamp.Year)
            .OrderByDescending(g => g.Key);

        foreach (var year in byYear)
        {
            var values = year.Select(r => r.Value).ToList();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {year.Key}: min {values.Min():F1}, max {values.Max():F1}, mean {values.Average():F1} {series.Unit} ({values.Count} readings)");
        }

        var all = series.Readings.Select(r => r.Value).ToList();
        var years = series.Readings.Select(r => r.Timestamp.Year).ToList();
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Overall {monthName} climate ({years.Min()}-{years.Max()}): min {all.Min():F1}, max {all.Max():F1}, mean {all.Average():F1} {series.Unit}");

        if (series.Truncated)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Note: the archive held more {monthName} readings than this tool returns; the most recent were kept.");
        }

        return sb.ToString();
    }
}
