using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using McpServerTemplate.Providers.Smhi.Models;
using ModelContextProtocol;

namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// Typed HTTP client for the SMHI SNOW (Swedish National Operational Weather) forecast API.
///
/// TEMPLATE GUIDANCE:
/// Keep ALL HTTP communication in one class. This makes the provider easy to test,
/// mock, and reason about. Tools and formatters never touch <c>HttpClient</c> directly.
///
/// Error handling pattern:
///   - Validate inputs BEFORE making HTTP calls
///   - Throw <see cref="McpException"/> for errors the LLM should see (invalid coords, API down)
///   - The SDK surfaces <c>McpException.Message</c> to the LLM; other exceptions get a generic message
///   - Include recovery hints: "Try coordinates within Northern Europe (approx. 50-72N, -1-40E)"
/// </summary>
public sealed class SmhiApiClient
{
    private readonly HttpClient _httpClient;

    // Guardrail: reject responses larger than 1 MB to prevent memory exhaustion
    // from malformed or unexpected upstream responses.
    private const long MaxResponseBytes = 1_048_576;

    // SMHI SNOW API approximate geographic coverage
    private const double MinLatitude = 50.0;
    private const double MaxLatitude = 72.0;
    private const double MinLongitude = -1.0;
    private const double MaxLongitude = 40.0;

    // Cache the last-known model reference time to avoid a full forecast fetch
    // when only metadata is needed. Updated on every successful forecast call.
    // Static: typed HttpClient creates transient instances, but this cache is per-process.
    private static DateTimeOffset? _cachedReferenceTime;
    private static DateTimeOffset _cachedReferenceTimeAt;
    private static readonly TimeSpan ReferenceTimeCacheDuration = TimeSpan.FromMinutes(30);

    public SmhiApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Fetches the point forecast for the given coordinates.
    /// Returns the full forecast response (~70 hourly time steps).
    /// </summary>
    /// <exception cref="McpException">
    /// Thrown when coordinates are outside SMHI coverage or the API returns an error.
    /// </exception>
    public async Task<SmhiForecastResponse> GetPointForecastAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        ValidateCoordinates(latitude, longitude);

        // SMHI API uses lon/lat order in the URL path
        var lonStr = longitude.ToString("F6", CultureInfo.InvariantCulture);
        var latStr = latitude.ToString("F6", CultureInfo.InvariantCulture);
        var url = $"/api/category/snow1g/version/1/geotype/point/lon/{lonStr}/lat/{latStr}/data.json";

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                throw new McpException(statusCode switch
                {
                    404 => $"No forecast data available for coordinates ({latitude}, {longitude}). "
                         + "The location may be outside SMHI coverage (Northern Europe, approx. 50-72N, -1-40E) "
                         + "or over water/outside the forecast grid.",
                    >= 500 => "SMHI API is temporarily unavailable. Please try again in a few minutes.",
                    _ => $"SMHI API returned HTTP {statusCode}. "
                       + "Verify the coordinates are within SMHI coverage (approx. 50-72N, -1-40E).",
                });
            }

            // Guardrail: read full response bytes to enforce size limit regardless of
            // chunked transfer encoding (Content-Length may be absent).
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxResponseBytes)
            {
                throw new McpException(
                    "SMHI returned an unexpectedly large response. "
                    + "This may indicate an API change. Please try again later.");
            }

            var forecast = JsonSerializer.Deserialize<SmhiForecastResponse>(bytes)
                ?? throw new McpException("SMHI returned an empty response. Please try again.");

            // Basic schema validation — catch corrupted or changed upstream responses.
            if (forecast.TimeSeries is null or { Count: 0 })
                throw new McpException("SMHI returned a forecast with no time series data.");

            // Cache the model reference time so GetCreatedTimeAsync can avoid a full forecast fetch.
            UpdateReferenceTimeCache(forecast.ReferenceTime);

            return forecast;
        }
        catch (HttpRequestException ex)
        {
            throw new McpException(
                "Unable to reach the SMHI API. Check network connectivity and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException(
                "The request to SMHI timed out. Please try again.", ex);
        }
    }

    /// <summary>
    /// Fetches the latest forecast model creation time.
    /// Returns a cached value if available (updated on every forecast call),
    /// otherwise makes a lightweight point forecast request using Stockholm.
    /// </summary>
    public async Task<DateTimeOffset> GetCreatedTimeAsync(CancellationToken cancellationToken = default)
    {
        // Return cached value if fresh enough (model updates every 6-12h, 30min cache is safe).
        if (_cachedReferenceTime.HasValue &&
            DateTimeOffset.UtcNow - _cachedReferenceTimeAt < ReferenceTimeCacheDuration)
        {
            return _cachedReferenceTime.Value;
        }

        try
        {
            // Use Stockholm as a known-good point to retrieve model metadata
            var forecast = await GetPointForecastAsync(59.33, 18.07, cancellationToken);
            return forecast.ReferenceTime;
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException("Unable to retrieve SMHI forecast model information.", ex);
        }
    }

    private static void UpdateReferenceTimeCache(DateTimeOffset referenceTime)
    {
        _cachedReferenceTime = referenceTime;
        _cachedReferenceTimeAt = DateTimeOffset.UtcNow;
    }

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (latitude < MinLatitude || latitude > MaxLatitude ||
            longitude < MinLongitude || longitude > MaxLongitude)
        {
            throw new McpException(
                $"Coordinates ({latitude}, {longitude}) are outside the SMHI forecast area "
                + $"(approx. {MinLatitude}-{MaxLatitude}N, {MinLongitude}-{MaxLongitude}E). "
                + "Try coordinates within Northern Europe, e.g. Stockholm (59.33, 18.07).");
        }
    }
}
