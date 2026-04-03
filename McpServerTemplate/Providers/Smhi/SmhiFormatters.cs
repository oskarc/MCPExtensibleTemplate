using System.Text;
using McpServerTemplate.Providers.Smhi.Models;

namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// Transforms raw SMHI API responses into concise, LLM-optimized text.
///
/// TEMPLATE GUIDANCE — OUTPUT ENGINEERING:
/// LLMs have limited context windows and work best with structured, labeled text.
/// Never dump raw JSON into a tool response. Instead:
///
///   1. SUMMARIZE — A 70-entry hourly forecast becomes key time points every 3-6h
///   2. LABEL — Every value has a name and unit: "Temperature: 15°C", not "15"
///   3. CONTEXTUALIZE — "Wind: 8 m/s from SW (moderate breeze)", not "ws=8 wd=225"
///   4. HANDLE MISSING DATA — Show "N/A" instead of magic sentinel values (9999, -9)
///   5. STRUCTURE — Use consistent formatting so the LLM can parse patterns
///
/// The LLM never sees these formatters directly — it sees the output.
/// Good formatting = better LLM reasoning = better answers to the user.
/// </summary>
public static class SmhiFormatters
{
    private const double MissingValue = 9999.0;

    // Guardrail: hard cap on forecast entries to protect the LLM's context window.
    // Even with 3/6h sampling, an anomalous API response with thousands of entries
    // could produce output that exhausts tokens. 48 entries ≈ 10 days at 6h intervals.
    private const int MaxForecastEntries = 48;

    /// <summary>
    /// Formats a multi-day forecast summary, sampling every 3 hours for the first 24h
    /// and every 6 hours beyond that to keep output concise.
    /// </summary>
    public static string FormatForecastSummary(SmhiForecastResponse forecast)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SMHI Weather Forecast (SNOW model)");
        sb.AppendLine($"Model run: {forecast.ReferenceTime:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Created: {forecast.CreatedTime:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine(new string('-', 50));

        var now = DateTimeOffset.UtcNow;
        string? currentDate = null;
        var entryCount = 0;

        foreach (var entry in forecast.TimeSeries)
        {
            var hoursAhead = (entry.ValidTime - now).TotalHours;

            // Sample rate: every 3h for first 24h, every 6h after that
            if (hoursAhead > 24 && entry.ValidTime.Hour % 6 != 0)
                continue;
            if (hoursAhead <= 24 && entry.ValidTime.Hour % 3 != 0 && hoursAhead > 1)
                continue;

            // Guardrail: hard cap to protect LLM context window
            if (++entryCount > MaxForecastEntries)
            {
                sb.AppendLine();
                sb.AppendLine($"(Forecast truncated at {MaxForecastEntries} time points to conserve context)");
                break;
            }

            var dateStr = entry.ValidTime.ToString("yyyy-MM-dd (ddd)");
            if (dateStr != currentDate)
            {
                currentDate = dateStr;
                sb.AppendLine();
                sb.AppendLine($"📅 {currentDate}");
            }

            FormatTimeEntry(sb, entry);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats the current weather snapshot — the time entry closest to now.
    /// </summary>
    public static string FormatCurrentWeather(SmhiForecastResponse forecast)
    {
        var now = DateTimeOffset.UtcNow;
        var nearest = forecast.TimeSeries
            .OrderBy(t => Math.Abs((t.ValidTime - now).TotalMinutes))
            .FirstOrDefault();

        if (nearest is null)
            return "No current weather data available.";

        var sb = new StringBuilder();
        sb.AppendLine("Current Weather (SMHI SNOW Forecast)");
        sb.AppendLine($"Valid for: {nearest.ValidTime:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Model run: {forecast.ReferenceTime:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine(new string('-', 40));

        var d = nearest.Data;

        if (d.SymbolCode.HasValue)
            sb.AppendLine($"Conditions: {WeatherSymbol.GetDescription((int)d.SymbolCode.Value)}");

        sb.AppendLine($"Temperature: {FormatValue(d.Temperature, "°C")}");
        sb.AppendLine($"Wind: {FormatValue(d.WindSpeed, "m/s")} from {FormatDirection(d.WindDirection)}");

        if (d.GustSpeed.HasValue && !IsMissing(d.GustSpeed.Value))
            sb.AppendLine($"Wind gusts: {FormatValue(d.GustSpeed, "m/s")}");

        sb.AppendLine($"Humidity: {FormatValue(d.Humidity, "%")}");
        sb.AppendLine($"Precipitation: {FormatValue(d.PrecipitationMean, "mm/h")}");

        if (d.Pressure.HasValue && !IsMissing(d.Pressure.Value))
            sb.AppendLine($"Pressure: {FormatValue(d.Pressure, "hPa")}");

        if (d.Visibility.HasValue && !IsMissing(d.Visibility.Value))
            sb.AppendLine($"Visibility: {FormatValue(d.Visibility, "km")}");

        if (d.ThunderstormProbability.HasValue && d.ThunderstormProbability.Value > 0 && !IsMissing(d.ThunderstormProbability.Value))
            sb.AppendLine($"Thunderstorm probability: {FormatValue(d.ThunderstormProbability, "%")}");

        return sb.ToString();
    }

    /// <summary>
    /// Converts wind direction in degrees to cardinal direction (N/NE/E/SE/S/SW/W/NW).
    /// </summary>
    public static string ToCardinalDirection(double degrees)
    {
        if (IsMissing(degrees))
            return "N/A";

        // Normalize to 0-360
        degrees = ((degrees % 360) + 360) % 360;

        return degrees switch
        {
            >= 337.5 or < 22.5 => "N",
            >= 22.5 and < 67.5 => "NE",
            >= 67.5 and < 112.5 => "E",
            >= 112.5 and < 157.5 => "SE",
            >= 157.5 and < 202.5 => "S",
            >= 202.5 and < 247.5 => "SW",
            >= 247.5 and < 292.5 => "W",
            _ => "NW",
        };
    }

    private static void FormatTimeEntry(StringBuilder sb, SmhiTimeSeries entry)
    {
        var d = entry.Data;

        var symbolText = d.SymbolCode.HasValue
            ? WeatherSymbol.GetDescription((int)d.SymbolCode.Value)
            : "—";

        var precipText = d.PrecipitationMean.HasValue && !IsMissing(d.PrecipitationMean.Value) && d.PrecipitationMean.Value > 0
            ? $", Precip: {d.PrecipitationMean.Value:F1} mm/h"
            : "";

        sb.AppendLine(
            $"  {entry.ValidTime:HH:mm} — {symbolText}, " +
            $"{FormatValue(d.Temperature, "°C")}, " +
            $"Wind: {FormatValue(d.WindSpeed, "m/s")} {FormatDirection(d.WindDirection)}" +
            $"{precipText}");
    }

    private static string FormatValue(double? value, string unit)
    {
        if (!value.HasValue || IsMissing(value.Value))
            return "N/A";

        return $"{value.Value:F1} {unit}";
    }

    private static string FormatDirection(double? degrees)
    {
        if (!degrees.HasValue || IsMissing(degrees.Value))
            return "N/A";

        return ToCardinalDirection(degrees.Value);
    }

    private static bool IsMissing(double value)
        => Math.Abs(value - MissingValue) < 0.1;
}
