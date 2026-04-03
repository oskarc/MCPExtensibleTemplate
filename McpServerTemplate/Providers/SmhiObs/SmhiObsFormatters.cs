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
    private const int MaxClimatologyReadings = 50_000;

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
    /// Formats a climate comparison: groups by month and shows monthly averages
    /// across available years. Useful for "what's typical for April?" queries.
    /// </summary>
    public static string FormatMonthlyClimatology(MetObsDataResponse data, int targetMonth)
    {
        var monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(targetMonth);

        var sb = new StringBuilder();
        sb.AppendLine($"SMHI Historical Observations — {data.Parameter.Name}");
        sb.AppendLine($"Station: {data.Station.Name}");
        sb.AppendLine($"Month: {monthName}");
        sb.AppendLine($"Unit: {data.Parameter.Unit}");
        sb.AppendLine(new string('-', 50));

        var parsed = data.Values
            .Take(MaxClimatologyReadings) // Cap to prevent memory issues with large archives
            .Where(v => DateTimeOffset.FromUnixTimeMilliseconds(v.DateUnixMs).Month == targetMonth)
            .Select(v => (Date: DateTimeOffset.FromUnixTimeMilliseconds(v.DateUnixMs),
                          Value: double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null))
            .Where(v => v.Value.HasValue)
            .ToList();

        if (parsed.Count == 0)
        {
            sb.AppendLine($"No observations available for {monthName}.");
            return sb.ToString();
        }

        var byYear = parsed
            .GroupBy(v => v.Date.Year)
            .OrderByDescending(g => g.Key);

        foreach (var year in byYear)
        {
            var values = year.Select(v => v.Value!.Value).ToList();
            sb.AppendLine($"  {year.Key}: min {values.Min():F1}, max {values.Max():F1}, mean {values.Average():F1} {data.Parameter.Unit} ({values.Count} readings)");
        }

        var allValues = parsed.Select(v => v.Value!.Value).ToList();
        sb.AppendLine();
        sb.AppendLine($"Overall {monthName} climate: min {allValues.Min():F1}, max {allValues.Max():F1}, mean {allValues.Average():F1} {data.Parameter.Unit}");

        return sb.ToString();
    }
}
