using System.Text.Json.Serialization;

namespace McpServerTemplate.Providers.SmhiObs.Models;

/// <summary>
/// Deserialization models for the SMHI Meteorological Observations (metobs) API.
/// Endpoint pattern: <c>GET /api/version/1.0/parameter/{param}/station/{stationId}/period/{period}/data.json</c>
///
/// The API uses Unix timestamps (milliseconds) for dates and returns values as strings.
/// </summary>

public sealed record MetObsDataResponse
{
    [JsonPropertyName("value")]
    public IReadOnlyList<MetObsValue> Values { get; init; } = [];

    [JsonPropertyName("station")]
    public MetObsStation Station { get; init; } = new();

    [JsonPropertyName("parameter")]
    public MetObsParameter Parameter { get; init; } = new();

    [JsonPropertyName("period")]
    public MetObsPeriod Period { get; init; } = new();
}

public sealed record MetObsValue
{
    [JsonPropertyName("date")]
    public long DateUnixMs { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("quality")]
    public string Quality { get; init; } = string.Empty;

    [JsonIgnore]
    public DateTimeOffset Date => DateTimeOffset.FromUnixTimeMilliseconds(DateUnixMs);
}

public sealed record MetObsStation
{
    [JsonPropertyName("key")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("height")]
    public double Height { get; init; }
}

public sealed record MetObsParameter
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = string.Empty;
}

public sealed record MetObsPeriod
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    public long FromUnixMs { get; init; }

    [JsonPropertyName("to")]
    public long ToUnixMs { get; init; }

    [JsonIgnore]
    public DateTimeOffset From => DateTimeOffset.FromUnixTimeMilliseconds(FromUnixMs);

    [JsonIgnore]
    public DateTimeOffset To => DateTimeOffset.FromUnixTimeMilliseconds(ToUnixMs);
}

/// <summary>
/// Response from the parameter endpoint which includes the station list.
/// The response has many fields; we only need the station array.
/// </summary>
public sealed record MetObsStationListResponse
{
    [JsonPropertyName("station")]
    public IReadOnlyList<MetObsStationInfo> Stations { get; init; } = [];
}

public sealed record MetObsStationInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double Latitude { get; init; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; init; }

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("from")]
    public long FromUnixMs { get; init; }

    [JsonPropertyName("to")]
    public long ToUnixMs { get; init; }
}
