using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// MCP Tools for the SMHI weather provider.
///
/// TEMPLATE GUIDANCE — TOOL DESIGN PRINCIPLES:
///
///   1. NAME: Verb + Noun — "GetForecast", not "Forecast" or "WeatherData"
///
///   2. DESCRIPTION: Say WHEN to use, not just what it does.
///      Bad:  "Gets weather forecast"
///      Good: "Use when the user asks about upcoming weather conditions..."
///
///   3. PARAMETERS: [Description] must include examples, valid ranges, units.
///      Bad:  double latitude
///      Good: [Description("Latitude in decimal degrees, e.g. 59.33 for Stockholm...")]
///
///   4. OUTPUT: Formatted text optimized for LLM consumption.
///      Never dump raw JSON. Summarize, label, structure.
///
///   5. ERRORS: Throw McpException with recovery hints.
///      "Coordinates are outside SMHI area. Try coordinates within Northern Europe."
///
///   6. GRANULARITY: One tool per coherent user action.
///      Don't make the LLM chain 3 tools to answer "What's the weather?"
///
///   7. OUTPUT SIZE: Be concise. A 70-entry hourly forecast should be summarized.
///
/// DI NOTE: The SDK automatically resolves registered services as method parameters.
/// <c>SmhiApiClient</c> is injected because it was registered in <c>SmhiServiceRegistration</c>.
/// </summary>
[McpServerToolType]
public static class SmhiTools
{
    [McpServerTool, Description(
        "Use when the user asks about upcoming weather conditions at a specific location in Northern Europe. " +
        "Returns a multi-day weather forecast summary with temperature, wind, precipitation, and conditions " +
        "sampled every 3-6 hours. Requires latitude and longitude coordinates within the SMHI coverage area " +
        "(approximately 50-72°N, -1-40°E covering Scandinavia and the Nordic region).")]
    public static async Task<string> GetForecast(
        SmhiApiClient client,
        [Description("Latitude in decimal degrees, e.g. 59.33 for Stockholm, 55.60 for Malmö, 63.83 for Umeå. " +
                     "Valid range for SMHI: approximately 50-72°N.")]
        double latitude,
        [Description("Longitude in decimal degrees, e.g. 18.07 for Stockholm, 13.00 for Malmö, 20.26 for Umeå. " +
                     "Valid range for SMHI: approximately -1 to 40°E.")]
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var forecast = await client.GetPointForecastAsync(latitude, longitude, cancellationToken);
        return SmhiFormatters.FormatForecastSummary(forecast);
    }

    [McpServerTool, Description(
        "Use when the user asks about current weather conditions at a specific location in Northern Europe. " +
        "Returns a concise snapshot of the nearest-hour weather including temperature, wind, humidity, " +
        "precipitation, pressure, and conditions. Lighter than GetForecast when only the current state is needed. " +
        "Requires latitude and longitude within SMHI coverage (approx. 50-72°N, -1-40°E).")]
    public static async Task<string> GetCurrentWeather(
        SmhiApiClient client,
        [Description("Latitude in decimal degrees, e.g. 59.33 for Stockholm. Valid range: approximately 50-72°N.")]
        double latitude,
        [Description("Longitude in decimal degrees, e.g. 18.07 for Stockholm. Valid range: approximately -1 to 40°E.")]
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var forecast = await client.GetPointForecastAsync(latitude, longitude, cancellationToken);
        return SmhiFormatters.FormatCurrentWeather(forecast);
    }

    [McpServerTool, Description(
        "Use when the user asks about the freshness or last update time of the weather forecast model. " +
        "Returns when SMHI last ran its weather prediction model, which indicates how recent the forecast data is. " +
        "No coordinates needed — this describes the global model state.")]
    public static async Task<string> GetForecastModelInfo(
        SmhiApiClient client,
        CancellationToken cancellationToken = default)
    {
        var referenceTime = await client.GetCreatedTimeAsync(cancellationToken);

        var age = DateTimeOffset.UtcNow - referenceTime;
        var ageText = age.TotalHours < 1
            ? $"{age.TotalMinutes:F0} minutes ago"
            : $"{age.TotalHours:F1} hours ago";

        return $"SMHI Forecast Model Information (SNOW)\n"
             + $"---------------------------------------\n"
             + $"Latest model run: {referenceTime:yyyy-MM-dd HH:mm} UTC ({ageText})\n"
             + $"SMHI typically updates the SNOW model every 6-12 hours.";
    }
}
