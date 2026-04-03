using System.Text.Json.Serialization;

namespace McpServerTemplate.Providers.Smhi.Models;

/// <summary>
/// Deserialization models for the SMHI SNOW (Swedish National Operational Weather) forecast API.
/// Endpoint: <c>GET /api/category/snow1g/version/1/geotype/point/lon/{lon}/lat/{lat}/data.json</c>
///
/// TEMPLATE GUIDANCE:
/// Keep API response models as plain records with <c>JsonPropertyName</c> attributes.
/// No business logic here — that belongs in the formatters and API client.
/// </summary>

public sealed record SmhiForecastResponse
{
    [JsonPropertyName("createdTime")]
    public DateTimeOffset CreatedTime { get; init; }

    [JsonPropertyName("referenceTime")]
    public DateTimeOffset ReferenceTime { get; init; }

    [JsonPropertyName("geometry")]
    public SmhiGeometry Geometry { get; init; } = null!;

    [JsonPropertyName("timeSeries")]
    public IReadOnlyList<SmhiTimeSeries> TimeSeries { get; init; } = [];
}

public sealed record SmhiGeometry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("coordinates")]
    public double[] Coordinates { get; init; } = [];
}

public sealed record SmhiTimeSeries
{
    [JsonPropertyName("time")]
    public DateTimeOffset ValidTime { get; init; }

    [JsonPropertyName("data")]
    public SmhiTimeSeriesData Data { get; init; } = new();
}

/// <summary>
/// Flat data object containing all weather parameters for a single time step.
/// SNOW v1 uses descriptive property names (e.g. air_temperature) instead of
/// the PMP3g short codes (e.g. t).
/// </summary>
public sealed record SmhiTimeSeriesData
{
    [JsonPropertyName("air_temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("wind_speed")]
    public double? WindSpeed { get; init; }

    [JsonPropertyName("wind_from_direction")]
    public double? WindDirection { get; init; }

    [JsonPropertyName("wind_speed_of_gust")]
    public double? GustSpeed { get; init; }

    [JsonPropertyName("relative_humidity")]
    public double? Humidity { get; init; }

    [JsonPropertyName("air_pressure_at_mean_sea_level")]
    public double? Pressure { get; init; }

    [JsonPropertyName("visibility_in_air")]
    public double? Visibility { get; init; }

    [JsonPropertyName("thunderstorm_probability")]
    public double? ThunderstormProbability { get; init; }

    [JsonPropertyName("precipitation_amount_mean")]
    public double? PrecipitationMean { get; init; }

    [JsonPropertyName("precipitation_amount_min")]
    public double? PrecipitationMin { get; init; }

    [JsonPropertyName("precipitation_amount_max")]
    public double? PrecipitationMax { get; init; }

    [JsonPropertyName("probability_of_precipitation")]
    public double? PrecipitationProbability { get; init; }

    [JsonPropertyName("cloud_area_fraction")]
    public double? CloudCover { get; init; }

    [JsonPropertyName("symbol_code")]
    public double? SymbolCode { get; init; }
}
