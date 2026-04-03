using System.Collections.Frozen;

namespace McpServerTemplate.Providers.Smhi.Models;

/// <summary>
/// SMHI weather symbol codes (1-27) mapped to human-readable descriptions.
///
/// TEMPLATE GUIDANCE:
/// Static reference data like this is a great candidate for an MCP Resource.
/// See <c>SmhiResources.cs</c> — the symbol table is exposed as <c>smhi://weather-symbols</c>
/// so the LLM can pull it into context without calling a tool.
/// </summary>
public static class WeatherSymbol
{
    private static readonly FrozenDictionary<int, string> Symbols = new Dictionary<int, string>
    {
        [1] = "Clear sky",
        [2] = "Nearly clear sky",
        [3] = "Variable cloudiness",
        [4] = "Halfclear sky",
        [5] = "Cloudy sky",
        [6] = "Overcast",
        [7] = "Fog",
        [8] = "Light rain showers",
        [9] = "Moderate rain showers",
        [10] = "Heavy rain showers",
        [11] = "Thunderstorm",
        [12] = "Light sleet showers",
        [13] = "Moderate sleet showers",
        [14] = "Heavy sleet showers",
        [15] = "Light snow showers",
        [16] = "Moderate snow showers",
        [17] = "Heavy snow showers",
        [18] = "Light rain",
        [19] = "Moderate rain",
        [20] = "Heavy rain",
        [21] = "Thunder",
        [22] = "Light sleet",
        [23] = "Moderate sleet",
        [24] = "Heavy sleet",
        [25] = "Light snowfall",
        [26] = "Moderate snowfall",
        [27] = "Heavy snowfall",
    }.ToFrozenDictionary();

    /// <summary>
    /// Gets the human-readable description for a weather symbol code.
    /// Returns "Unknown (code)" for unrecognized codes.
    /// </summary>
    public static string GetDescription(int code)
        => Symbols.TryGetValue(code, out var description) ? description : $"Unknown ({code})";

    /// <summary>
    /// Returns all symbol entries for resource/reference use.
    /// </summary>
    public static IEnumerable<KeyValuePair<int, string>> GetAll() => Symbols;
}
