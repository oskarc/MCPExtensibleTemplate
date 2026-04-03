using McpServerTemplate.Providers.SmhiObs;
using McpServerTemplate.Providers.SmhiObs.Models;

namespace McpServerTemplate.Tests.Providers.SmhiObs;

public class SmhiObsFormattersTests
{
    [Fact]
    public void FormatDailySummary_EmptyValues_ReturnsNoObservationsMessage()
    {
        var data = CreateDataResponse([]);

        var result = SmhiObsFormatters.FormatDailySummary(data);

        Assert.Contains("No valid observations", result);
    }

    [Fact]
    public void FormatDailySummary_WithValues_GroupsByDay()
    {
        var baseDate = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var values = new List<MetObsValue>
        {
            CreateValue(baseDate, "10.5"),
            CreateValue(baseDate.AddHours(3), "12.0"),
            CreateValue(baseDate.AddHours(6), "11.0"),
            CreateValue(baseDate.AddDays(1), "8.0"),
            CreateValue(baseDate.AddDays(1).AddHours(3), "9.5"),
        };

        var data = CreateDataResponse(values);
        var result = SmhiObsFormatters.FormatDailySummary(data);

        Assert.Contains("2026-03-15", result);
        Assert.Contains("2026-03-16", result);
    }

    [Fact]
    public void FormatLatestReadings_ReturnsCorrectCount()
    {
        var baseDate = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var values = Enumerable.Range(0, 48)
            .Select(i => CreateValue(baseDate.AddHours(i), (10.0 + i * 0.1).ToString("F1")))
            .ToList();

        var data = CreateDataResponse(values);
        var result = SmhiObsFormatters.FormatLatestReadings(data, count: 5);

        // Should contain only 5 readings (the most recent)
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataLines = lines.Where(l => l.TrimStart().StartsWith("2026")).ToList();
        Assert.Equal(5, dataLines.Count);
    }

    [Fact]
    public void FormatDailySummary_NonNumericValues_Skipped()
    {
        var baseDate = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var values = new List<MetObsValue>
        {
            CreateValue(baseDate, "not-a-number"),
            CreateValue(baseDate.AddHours(1), "15.0"),
        };

        var data = CreateDataResponse(values);
        var result = SmhiObsFormatters.FormatDailySummary(data);

        // The valid value should appear in the summary (either as daily min/max/mean)
        Assert.Contains("2026-03-15", result);
        Assert.DoesNotContain("No valid observations", result);
    }

    private static MetObsValue CreateValue(DateTimeOffset date, string value) =>
        new()
        {
            DateUnixMs = date.ToUnixTimeMilliseconds(),
            Value = value,
            Quality = "G"
        };

    private static MetObsDataResponse CreateDataResponse(IReadOnlyList<MetObsValue> values) =>
        new()
        {
            Values = values,
            Station = new MetObsStation { Id = "98210", Name = "Stockholm-Observatoriekullen", Height = 40 },
            Parameter = new MetObsParameter { Key = "1", Name = "Lufttemperatur", Unit = "°C" },
            Period = new MetObsPeriod
            {
                Key = "latest-months",
                FromUnixMs = DateTimeOffset.UtcNow.AddMonths(-4).ToUnixTimeMilliseconds(),
                ToUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
}
