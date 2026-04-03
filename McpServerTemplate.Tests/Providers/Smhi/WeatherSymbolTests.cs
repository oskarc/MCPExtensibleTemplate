using McpServerTemplate.Providers.Smhi.Models;

namespace McpServerTemplate.Tests.Providers.Smhi;

public class WeatherSymbolTests
{
    [Theory]
    [InlineData(1, "Clear sky")]
    [InlineData(6, "Overcast")]
    [InlineData(11, "Thunderstorm")]
    [InlineData(20, "Heavy rain")]
    [InlineData(27, "Heavy snowfall")]
    public void GetDescription_KnownCode_ReturnsExpectedText(int code, string expected)
    {
        var result = WeatherSymbol.GetDescription(code);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(-1)]
    [InlineData(999)]
    public void GetDescription_UnknownCode_ReturnsUnknownWithCode(int code)
    {
        var result = WeatherSymbol.GetDescription(code);

        Assert.StartsWith("Unknown", result);
        Assert.Contains(code.ToString(), result);
    }

    [Fact]
    public void GetAll_Returns27Entries()
    {
        var all = WeatherSymbol.GetAll().ToList();

        Assert.Equal(27, all.Count);
        Assert.All(all, entry => Assert.InRange(entry.Key, 1, 27));
    }
}
