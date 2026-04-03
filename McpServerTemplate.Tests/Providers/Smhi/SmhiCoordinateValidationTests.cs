using McpServerTemplate.Providers.Smhi;

namespace McpServerTemplate.Tests.Providers.Smhi;

public class SmhiCoordinateValidationTests
{
    private const string FakeBaseUrl = "https://example.com";

    [Theory]
    [InlineData(49.0, 18.0)]  // Below min latitude
    [InlineData(73.0, 18.0)]  // Above max latitude
    [InlineData(59.0, -2.0)]  // Below min longitude
    [InlineData(59.0, 41.0)]  // Above max longitude
    public async Task GetPointForecastAsync_InvalidCoordinates_ThrowsMcpException(
        double latitude, double longitude)
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(FakeBaseUrl) };
        var client = new SmhiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => client.GetPointForecastAsync(latitude, longitude));

        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPointForecastAsync_Api404_ThrowsDescriptiveError()
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(FakeBaseUrl) };
        var client = new SmhiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => client.GetPointForecastAsync(59.33, 18.07));

        Assert.Contains("No forecast data", ex.Message);
    }

    [Fact]
    public async Task GetPointForecastAsync_Api500_ThrowsTemporarilyUnavailable()
    {
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(FakeBaseUrl) };
        var client = new SmhiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => client.GetPointForecastAsync(59.33, 18.07));

        Assert.Contains("temporarily unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPointForecastAsync_OversizedResponse_ThrowsMcpException()
    {
        // Generate a response body that exceeds the 1 MB limit.
        var oversizedBody = new string('x', 1_048_577);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        response.Content = new StringContent(oversizedBody, System.Text.Encoding.UTF8, "application/json");
        var handler = new FakeHttpMessageHandler(response);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(FakeBaseUrl) };
        var client = new SmhiApiClient(httpClient);

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => client.GetPointForecastAsync(59.33, 18.07));

        Assert.Contains("large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Minimal fake handler that returns a preconfigured response.
    /// </summary>
    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
