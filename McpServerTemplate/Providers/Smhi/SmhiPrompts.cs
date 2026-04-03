using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// MCP Prompts for the SMHI weather provider.
///
/// TEMPLATE GUIDANCE — WHEN TO USE PROMPTS:
/// Prompts are reusable interaction templates that guide how users start a conversation.
/// They are user-facing (the client shows them as options) and structure the initial
/// request to the LLM, often referencing tools that should be called.
///
/// Use prompts for:
///   - Common task patterns ("Give me a weather briefing for...")
///   - Complex multi-step workflows that benefit from guided structure
///   - Scenarios where the LLM needs specific instructions to produce good output
///
/// Prompts are OPTIONAL — a minimal provider only needs Tools.
/// </summary>
[McpServerPromptType]
public static class SmhiPrompts
{
    [McpServerPrompt, Description(
        "Generates a structured weather briefing for a location. " +
        "Guides the assistant to provide a comprehensive yet concise weather summary " +
        "including current conditions, upcoming forecast, and any weather alerts.")]
    public static IEnumerable<PromptMessage> ForecastBriefing(
        [Description("Latitude of the location, e.g. 59.33 for Stockholm")]
        string latitude,
        [Description("Longitude of the location, e.g. 18.07 for Stockholm")]
        string longitude)
    {
        // Guardrail: prompt parameters are strings that get interpolated into a prompt template.
        // An adversarial client could pass text like "ignore previous instructions" in place of
        // a coordinate. Validating they're numeric prevents prompt injection via these parameters.
        if (!double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return
            [
                new()
                {
                    Role = Role.User,
                    Content = new TextContentBlock
                    {
                        Text = "Invalid coordinates. Please provide numeric latitude and longitude values, "
                             + "e.g. 59.33 and 18.07 for Stockholm."
                    }
                },
            ];
        }

        return
        [
            new()
            {
                Role = Role.User,
                Content = new TextContentBlock
                {
                    Text = $"""
                        Please provide a weather briefing for coordinates ({latitude}, {longitude}).

                        Follow this structure:
                        1. **Current Conditions** — Use GetCurrentWeather to get the current snapshot
                        2. **Today's Outlook** — Summarize the rest of today from the forecast
                        3. **Coming Days** — Use GetForecast to get the multi-day forecast, highlight key changes
                        4. **Alerts** — Flag any notable weather: heavy precipitation, strong wind (>15 m/s),
                           thunderstorms, extreme temperatures, or rapid changes

                        Keep the briefing conversational and concise. Use the weather symbol descriptions
                        from the smhi://weather-symbols resource if needed for context.
                        """
                }
            },
        ];
    }
}
