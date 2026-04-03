using System.ComponentModel;
using System.Text;
using McpServerTemplate.Providers.Smhi.Models;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// MCP Resources for the SMHI weather provider.
///
/// TEMPLATE GUIDANCE — WHEN TO USE RESOURCES:
/// Resources provide static or slowly-changing reference data that the LLM can
/// pull into context without calling a tool. Use resources for:
///   - Lookup tables (code → description)
///   - API documentation summaries
///   - Coverage/limitation descriptions
///   - Configuration or metadata
///
/// Unlike tools, resources don't take dynamic parameters from the LLM.
/// The LLM (or client) reads them to build context before reasoning.
///
/// Resources are OPTIONAL — a minimal provider only needs Tools.
/// </summary>
[McpServerResourceType]
public static class SmhiResources
{
    [McpServerResource(
        UriTemplate = "smhi://weather-symbols",
        Name = "Weather Symbol Codes",
        MimeType = "text/plain")]
    [Description("Reference table mapping SMHI weather symbol codes (1-27) to human-readable descriptions. " +
                "Useful for interpreting the 'Wsymb2' parameter in forecast data.")]
    public static string GetWeatherSymbols()
    {
        var sb = new StringBuilder();
        sb.AppendLine("SMHI Weather Symbol Codes");
        sb.AppendLine("========================");
        sb.AppendLine();

        foreach (var (code, description) in WeatherSymbol.GetAll())
        {
            sb.AppendLine($"  {code,2}: {description}");
        }

        sb.AppendLine();
        sb.AppendLine("These codes appear as the 'Wsymb2' parameter in SMHI forecast data.");
        return sb.ToString();
    }

    [McpServerResource(
        UriTemplate = "smhi://coverage-area",
        Name = "SMHI Forecast Coverage Area",
        MimeType = "text/plain")]
    [Description("Description of the geographic area covered by SMHI weather forecasts, " +
                "including coordinate boundaries and coverage limitations.")]
    public static string GetCoverageArea()
    {
        return """
               SMHI Forecast Coverage Area
               ===========================

               Geographic bounds (approximate):
                 Latitude:  50°N to 72°N
                 Longitude: -1°E to 40°E

               Covers:
                 - Sweden (full coverage)
                 - Norway (most areas)
                 - Finland (full coverage)
                 - Denmark (full coverage)
                 - Baltic states (Estonia, Latvia, Lithuania)
                 - Parts of northern Poland, Germany, and western Russia

               Limitations:
                 - Points over open ocean may not have data
                 - Resolution is approximately 2.5 km grid
                 - Forecast horizon: ~10 days (~70 hourly time steps for first ~3 days,
                   then 3-hourly, then 6-hourly)

               Coordinate examples:
                 Stockholm:   59.33°N, 18.07°E
                 Gothenburg:  57.71°N, 11.97°E
                 Malmö:       55.60°N, 13.00°E
                 Oslo:        59.91°N, 10.75°E
                 Helsinki:    60.17°N, 24.94°E
                 Copenhagen:  55.68°N, 12.57°E
               """;
    }
}
