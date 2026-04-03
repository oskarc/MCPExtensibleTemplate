using System.Globalization;
using McpServerTemplate.Providers.Smhi;
using McpServerTemplate.Providers.Smhi.Models;

namespace McpServerTemplate.Tests.Providers.Smhi;

public class SmhiFormattersTests
{
    [Fact]
    public void FormatCurrentWeather_NoTimeSeries_ReturnsNoDataMessage()
    {
        var forecast = new SmhiForecastResponse
        {
            ReferenceTime = DateTimeOffset.UtcNow,
            CreatedTime = DateTimeOffset.UtcNow,
            TimeSeries = []
        };

        var result = SmhiFormatters.FormatCurrentWeather(forecast);

        Assert.Equal("No current weather data available.", result);
    }

    [Fact]
    public void FormatCurrentWeather_WithData_IncludesTemperatureAndWind()
    {
        var now = DateTimeOffset.UtcNow;
        var forecast = new SmhiForecastResponse
        {
            ReferenceTime = now,
            CreatedTime = now,
            TimeSeries =
            [
                new SmhiTimeSeries
                {
                    ValidTime = now,
                    Data = new SmhiTimeSeriesData
                    {
                        Temperature = 15.5,
                        WindSpeed = 8.0,
                        WindDirection = 225.0,
                        Humidity = 65.0,
                        PrecipitationMean = 0.0,
                        SymbolCode = 1.0
                    }
                }
            ]
        };

        var result = SmhiFormatters.FormatCurrentWeather(forecast);

        // Use locale-aware expected value (F1 uses current culture decimal separator)
        var expectedTemp = 15.5.ToString("F1", CultureInfo.CurrentCulture);
        Assert.Contains($"Temperature: {expectedTemp}", result);
        Assert.Contains("m/s", result);
        Assert.Contains("SW", result);
        Assert.Contains("Clear sky", result);
    }

    [Fact]
    public void FormatForecastSummary_ManyEntries_ProducesConciseOutput()
    {
        var now = DateTimeOffset.UtcNow;
        // Create many entries — the formatter samples every 3-6h,
        // so we verify that the output is shorter than the input.
        var timeSeries = Enumerable.Range(0, 200)
            .Select(i => new SmhiTimeSeries
            {
                ValidTime = now.AddHours(i),
                Data = new SmhiTimeSeriesData
                {
                    Temperature = 10.0 + i * 0.1,
                    WindSpeed = 5.0,
                    WindDirection = 180.0
                }
            })
            .ToList();

        var forecast = new SmhiForecastResponse
        {
            ReferenceTime = now,
            CreatedTime = now,
            TimeSeries = timeSeries
        };

        var result = SmhiFormatters.FormatForecastSummary(forecast);

        // The output should contain the model header and some forecast data
        Assert.Contains("SMHI Weather Forecast", result);
        // With 200 hourly entries, the formatter samples every 3-6h and caps at 48 entries,
        // so the output should be significantly shorter than the raw input
        var lineCount = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(lineCount < 200, $"Expected concise output but got {lineCount} lines");
        Assert.True(lineCount > 3, "Expected more than just the header");
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(45, "NE")]
    [InlineData(90, "E")]
    [InlineData(135, "SE")]
    [InlineData(180, "S")]
    [InlineData(225, "SW")]
    [InlineData(270, "W")]
    [InlineData(315, "NW")]
    [InlineData(360, "N")]
    public void ToCardinalDirection_ReturnsExpectedDirection(double degrees, string expected)
    {
        var result = SmhiFormatters.ToCardinalDirection(degrees);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToCardinalDirection_MissingValue_ReturnsNA()
    {
        var result = SmhiFormatters.ToCardinalDirection(9999.0);

        Assert.Equal("N/A", result);
    }

    [Fact]
    public void FormatCurrentWeather_MissingValues_ShowsNA()
    {
        var now = DateTimeOffset.UtcNow;
        var forecast = new SmhiForecastResponse
        {
            ReferenceTime = now,
            CreatedTime = now,
            TimeSeries =
            [
                new SmhiTimeSeries
                {
                    ValidTime = now,
                    Data = new SmhiTimeSeriesData
                    {
                        Temperature = null,
                        WindSpeed = null,
                        WindDirection = null,
                        Humidity = null,
                        PrecipitationMean = null
                    }
                }
            ]
        };

        var result = SmhiFormatters.FormatCurrentWeather(forecast);

        Assert.Contains("Temperature: N/A", result);
        Assert.Contains("Wind: N/A", result);
    }
}
