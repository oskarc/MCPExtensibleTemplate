using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Providers.SmhiObs;

/// <summary>
/// MCP Tools for the SMHI Observations provider.
///
/// These tools give the LLM access to historical weather measurements from SMHI's
/// network of weather stations across Sweden and the Nordic region. The API automatically
/// finds the nearest active station to the requested coordinates.
/// </summary>
[McpServerToolType]
public static class SmhiObsTools
{
    [McpServerTool, Description(
        "Use when the user asks about recent actual weather observations (not forecasts) at a location in Sweden. " +
        "Returns the last 24 hours of temperature readings from the nearest SMHI weather station. " +
        "This is measured data, not a forecast. Useful for verifying what actually happened vs what was predicted.")]
    public static async Task<string> GetRecentTemperature(
        SmhiObsApiClient client,
        [Description("Latitude in decimal degrees, e.g. 59.33 for Stockholm. Must be near a Swedish SMHI station.")]
        double latitude,
        [Description("Longitude in decimal degrees, e.g. 18.07 for Stockholm.")]
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var data = await client.GetNearestObservationsAsync(latitude, longitude, parameterId: 1, period: "latest-day", cancellationToken);
        return SmhiObsFormatters.FormatLatestReadings(data);
    }

    [McpServerTool, Description(
        "Use when the user asks about historical weather patterns, typical weather for a month, or climate data " +
        "at a location in Sweden. Returns daily min/max/mean temperature from the nearest SMHI station " +
        "over the last ~4 months. Good for questions like 'what has the weather been like this winter?' " +
        "or 'how cold was it last month?'")]
    public static async Task<string> GetTemperatureHistory(
        SmhiObsApiClient client,
        [Description("Latitude in decimal degrees, e.g. 59.33 for Stockholm.")]
        double latitude,
        [Description("Longitude in decimal degrees, e.g. 18.07 for Stockholm.")]
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var data = await client.GetNearestObservationsAsync(latitude, longitude, parameterId: 1, period: "latest-months", cancellationToken);
        return SmhiObsFormatters.FormatDailySummary(data);
    }

    [McpServerTool, Description(
        "Use when the user asks about precipitation history or how much rain/snow has fallen recently " +
        "at a location in Sweden. Returns daily precipitation totals from the nearest SMHI station " +
        "over the last ~4 months.")]
    public static async Task<string> GetPrecipitationHistory(
        SmhiObsApiClient client,
        [Description("Latitude in decimal degrees, e.g. 59.33 for Stockholm.")]
        double latitude,
        [Description("Longitude in decimal degrees, e.g. 18.07 for Stockholm.")]
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var data = await client.GetNearestObservationsAsync(latitude, longitude, parameterId: 7, period: "latest-months", cancellationToken);
        return SmhiObsFormatters.FormatDailySummary(data);
    }

    [McpServerTool, Description(
        "Use when the user asks 'what is the typical weather in [month]?' or 'what are normal temperatures ' " +
        "for a Swedish location. Compares the same month across recent years using actual station data. " +
        "Requires a month number (1=January, 12=December).")]
    public static async Task<string> GetMonthlyClimate(
        SmhiObsApiClient client,
        [Description("Latitude in decimal degrees, e.g. 59.33 for Stockholm.")]
        double latitude,
        [Description("Longitude in decimal degrees, e.g. 18.07 for Stockholm.")]
        double longitude,
        [Description("Month number: 1=January, 2=February, ..., 12=December.")]
        int month,
        CancellationToken cancellationToken = default)
    {
        if (month < 1 || month > 12)
            throw new ModelContextProtocol.McpException("Month must be between 1 and 12.");

        var data = await client.GetNearestObservationsAsync(latitude, longitude, parameterId: 1, period: "corrected-archive", cancellationToken);
        return SmhiObsFormatters.FormatMonthlyClimatology(data, month);
    }
}
