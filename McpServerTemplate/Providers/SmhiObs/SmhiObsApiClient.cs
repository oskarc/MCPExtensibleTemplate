using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using McpServerTemplate.Providers.SmhiObs.Models;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;

namespace McpServerTemplate.Providers.SmhiObs;

/// <summary>
/// Typed HTTP client for the SMHI Meteorological Observations (metobs) API.
///
/// The API is structured as: /api/version/1.0/parameter/{param}/station/{stationId}/period/{period}/data.json
///
/// Key parameters:
///   1  = Air temperature (hourly, momentary)
///   5  = Precipitation (daily sum)
///   6  = Relative humidity (hourly)
///   4  = Wind speed (hourly, 10 min average)
///   7  = Precipitation (hourly sum)
///
/// Periods:
///   latest-hour   = last hour
///   latest-day    = last 24 hours
///   latest-months = last ~4 months
///   corrected-archive = full history (quality-checked)
/// </summary>
public sealed class SmhiObsApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;

    private const long MaxResponseBytes = 5_242_880; // 5 MB — historical data can be large
    private static readonly TimeSpan StationCacheDuration = TimeSpan.FromHours(6);

    // Allowlists to prevent path injection and unexpected upstream requests.
    private static readonly HashSet<string> ValidPeriods = new(StringComparer.OrdinalIgnoreCase)
    {
        "latest-hour", "latest-day", "latest-months", "corrected-archive"
    };

    private static readonly HashSet<int> SupportedParameterIds = new() { 1, 4, 5, 6, 7 };

    public SmhiObsApiClient(HttpClient httpClient, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _cache = cache;
    }

    /// <summary>
    /// Lists all active stations for a given parameter.
    /// Results are cached for 6 hours since station metadata changes very rarely.
    /// </summary>
    public async Task<IReadOnlyList<MetObsStationInfo>> GetStationsAsync(
        int parameterId,
        CancellationToken cancellationToken = default)
    {
        ValidateParameterId(parameterId);

        var cacheKey = $"smhi-obs-stations-{parameterId}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<MetObsStationInfo>? cached) && cached is not null)
            return cached;

        var url = $"/api/version/1.0/parameter/{parameterId}.json";

        try
        {
            var httpResponse = await _httpClient.GetAsync(url, cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
                throw new McpException($"SMHI station list request failed with HTTP {(int)httpResponse.StatusCode}.");

            var bytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxResponseBytes)
                throw new McpException("SMHI returned an unexpectedly large station list response.");

            var response = JsonSerializer.Deserialize<MetObsStationListResponse>(bytes)
                ?? throw new McpException("SMHI returned an empty station list.");

            _cache.Set(cacheKey, response.Stations, StationCacheDuration);
            return response.Stations;
        }
        catch (JsonException ex)
        {
            throw new McpException($"Failed to parse SMHI station list: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException("Unable to reach the SMHI observations API. Check network connectivity.", ex);
        }
    }

    /// <summary>
    /// Finds the nearest active station to given coordinates for a parameter.
    /// </summary>
    public async Task<MetObsStationInfo> FindNearestStationAsync(
        double latitude,
        double longitude,
        int parameterId,
        CancellationToken cancellationToken = default)
    {
        var stations = await GetStationsAsync(parameterId, cancellationToken);

        var nearest = stations
            .Where(s => s.Active)
            .OrderBy(s => HaversineDistance(latitude, longitude, s.Latitude, s.Longitude))
            .FirstOrDefault()
            ?? throw new McpException(
                $"No active SMHI observation station found near ({latitude}, {longitude}). "
                + "Stations are concentrated in Sweden and the Nordic region.");

        return nearest;
    }

    /// <summary>
    /// Fetches observation data for a station, parameter, and period.
    /// </summary>
    public async Task<MetObsDataResponse> GetObservationsAsync(
        int stationId,
        int parameterId,
        string period = "latest-months",
        CancellationToken cancellationToken = default)
    {
        ValidateParameterId(parameterId);
        ValidatePeriod(period);
        if (stationId <= 0)
            throw new McpException($"Invalid station ID {stationId}. Station IDs must be positive integers.");

        var url = $"/api/version/1.0/parameter/{parameterId}/station/{stationId}/period/{period}/data.json";

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                throw new McpException(statusCode switch
                {
                    404 => $"No observation data found for station {stationId}, parameter {parameterId}, period '{period}'. "
                         + "The station may not measure this parameter, or the period may be unavailable.",
                    >= 500 => "SMHI observations API is temporarily unavailable. Please try again later.",
                    _ => $"SMHI observations API returned HTTP {statusCode}.",
                });
            }

            // Read full response bytes to enforce size limit regardless of chunked transfer.
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxResponseBytes)
                throw new McpException("SMHI returned an unexpectedly large observations response.");

            var result = JsonSerializer.Deserialize<MetObsDataResponse>(bytes)
                ?? throw new McpException("SMHI returned an empty observations response.");

            // Basic schema validation — catch corrupted or changed upstream responses.
            if (string.IsNullOrEmpty(result.Station?.Name))
                throw new McpException("SMHI returned observation data with missing station information.");

            return result;
        }
        catch (JsonException ex)
        {
            throw new McpException($"Failed to parse SMHI observations response: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException("Unable to reach the SMHI observations API.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("The request to SMHI observations timed out. Please try again.", ex);
        }
    }

    /// <summary>
    /// Fetches recent observations for the nearest station to given coordinates.
    /// Combines station lookup + data fetch in one call for tool convenience.
    /// </summary>
    public async Task<MetObsDataResponse> GetNearestObservationsAsync(
        double latitude,
        double longitude,
        int parameterId,
        string period = "latest-months",
        CancellationToken cancellationToken = default)
    {
        var station = await FindNearestStationAsync(latitude, longitude, parameterId, cancellationToken);
        return await GetObservationsAsync(station.Id, parameterId, period, cancellationToken);
    }

    /// <summary>
    /// Approximate distance in km between two coordinates using the Haversine formula.
    /// </summary>
    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static void ValidateParameterId(int parameterId)
    {
        if (!SupportedParameterIds.Contains(parameterId))
        {
            throw new McpException(
                $"Unsupported parameter ID {parameterId}. "
                + $"Supported IDs: {string.Join(", ", SupportedParameterIds.Order())} "
                + "(1=temperature, 4=wind, 5=precip daily, 6=humidity, 7=precip hourly).");
        }
    }

    private static void ValidatePeriod(string period)
    {
        if (!ValidPeriods.Contains(period))
        {
            throw new McpException(
                $"Invalid period '{period}'. "
                + $"Valid values: {string.Join(", ", ValidPeriods)}.");
        }
    }
}
