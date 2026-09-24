# Testing Strategy

## Overview

The MCP Server Template ships with comprehensive test coverage using **xUnit** to ensure reliability, security, and maintainability. This guide explains the testing approach, test structure, and how to add tests for new providers.

---

## Test Layers

The testing strategy covers **4 layers**:

### 1. **Unit Tests** (smallest scope)
- Test individual methods in isolation
- Mock external dependencies (HTTP calls)
- Fast execution (milliseconds)
- Focus on logic correctness

**Example**: Test that `FormatCurrentWeather()` correctly transforms an API response into readable text

### 2. **Integration Tests** (component scope)
- Test multiple components working together
- May use real HTTP clients (with mocked responses)
- Moderate execution time (seconds)
- Verify data flows between layers

**Example**: Test that the `get_forecast` tool correctly receives config, calls API client, and formats output

### 3. **E2E Tests** (full system scope)
- Test the complete request lifecycle through real MCP entry points
- Two forms, and the difference matters:
  - **In-process server** (`Identity/InProcessServer.cs`) — the shipped HTTP server, composed by the same method the program calls, with each identity provider's key lookups answered in-process. This is how a test can present a token the server accepts. Tests drive it with the SDK's own client or with raw JSON-RPC.
  - **Real process** (`ServerProcessTests.cs`) — the built program started as a child process, over stdio or HTTP. It cannot be given a token (it fetches signing keys from a real identity provider over HTTPS), so it proves startup, refusals before authentication, local-mode behaviour, and that it installs exactly the frame the in-process tests exercise.
- Redis runs in a container started by **Testcontainers**, so Docker must be running

**Example**: The SDK's client calls an irreversible tool, confirms, and the tool runs once; the captured confirmation is then replayed, altered and aged, and the tool never runs again (`Frame/ConfirmationTests.cs`)

### 4. **Security & Load Tests** (non-functional)
- Every refusal the frame can make, one test each (`Frame/RequestGateTests.cs`)
- Startup refusals: every way a policy or setting can disagree with what is served (`Frame/StartupRefusalTests.cs`)
- A provider that tries to remove or add to the frame's checks (`Frame/FrameIntegrityTests.cs`)
- Per-caller limits across two server instances sharing one Redis (`Frame/LimitsTests.cs`)
- Token validation: the six ways a token is refused (`Identity/TokenRejectionTests.cs`)

**Example**: A caller at their limit on one instance is refused on the other; another caller is not; with Redis stopped, requests are refused rather than let through

---

## Current Test Structure

```
McpServerTemplate.Tests/
├── Frame/                          # The policy frame
│   ├── StartupRefusalTests.cs      # Declarations the server refuses to start with
│   ├── RequestGateTests.cs         # Every refusal on the request path
│   ├── ConfirmationTests.cs        # The irreversible-tool round-trip
│   ├── LimitsTests.cs              # Limits across instances, failing closed
│   ├── FrameIntegrityTests.cs      # Providers that reach into the frame; settings typos
│   ├── FrameHarness.cs             # Composes the frame without a transport
│   ├── GateClient.cs               # SDK client and raw JSON-RPC helpers
│   └── TestModules.cs              # Provider modules that exist only in the tests
├── Identity/                       # Bearer tokens, trust domains, transport
│   ├── InProcessServer.cs          # The shipped HTTP server, in-process
│   ├── TestIdentityProvider.cs     # Mints tokens; answers discovery and key lookups in-process
│   └── ...Tests.cs
├── Infrastructure/                 # Configuration failures, resilience budgets
├── Providers/                      # Formatters, validation, round trips per provider
│   ├── JsonPlaceholder/
│   ├── Smhi/
│   └── SmhiObs/
├── ServerProcessTests.cs           # The real program, as a child process
├── DocumentationTests.cs           # Documents and comments match the server
├── RepositoryInvariantsTests.cs    # Build settings, secrets, provider declarations
└── TestRedis.cs                    # One Redis container for the test run
```

---

## Test Examples

### Example 1: Unit Test - Formatter

**File**: `Providers/Smhi/SmhiFormattersTests.cs`

```csharp
using Xunit;
using McpServerTemplate.Providers.Smhi;
using McpServerTemplate.Providers.Smhi.Models;

namespace McpServerTemplate.Tests.Providers.Smhi;

public class SmhiFormattersTests
{
    [Fact]
    public void FormatCurrentWeather_WithValidData_ReturnsFormattedString()
    {
        // Arrange
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

        // Act
        var result = SmhiFormatters.FormatCurrentWeather(forecast);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("15", result);  // Temperature should be in output
        Assert.Contains("Clear sky", result);
    }

    [Fact]
    public void FormatCurrentWeather_WithEmptyForecast_ReturnsNoDataMessage()
    {
        // Arrange
        var forecast = new SmhiForecastResponse
        {
            ReferenceTime = DateTimeOffset.UtcNow,
            CreatedTime = DateTimeOffset.UtcNow,
            TimeSeries = []
        };

        // Act
        var result = SmhiFormatters.FormatCurrentWeather(forecast);

        // Assert — returns a friendly message, does NOT throw
        Assert.Equal("No current weather data available.", result);
    }
}
```

**What it tests**: Output formatting works correctly and handles edge cases (empty data returns a friendly message, not an exception)

**Why it matters**: Ensures AI assistants receive properly formatted, readable responses

---

### Example 2: Unit Test - Validation

**File**: `Providers/Smhi/SmhiCoordinateValidationTests.cs`

```csharp
using Xunit;
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

    /// Minimal fake handler that returns a preconfigured response.
    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
```

**What it tests**: Coordinate validation rejects out-of-range values via `SmhiApiClient.GetPointForecastAsync()`, which throws `McpException`

**Why it matters**: Prevents users from requesting weather for unsupported areas

---

### Example 3: Unit Test - Data Symbol Mapping

**File**: `Providers/Smhi/WeatherSymbolTests.cs`

```csharp
using Xunit;
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
        // Act
        var result = WeatherSymbol.GetDescription(code);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(-1)]
    public void GetDescription_UnknownCode_ReturnsUnknownWithCode(int code)
    {
        // Act
        var result = WeatherSymbol.GetDescription(code);

        // Assert
        Assert.StartsWith("Unknown", result);
        Assert.Contains(code.ToString(), result);
    }
}
```

**What it tests**: Weather symbol codes (int 1-27) are correctly mapped to human-readable descriptions via `WeatherSymbol.GetDescription()`

**Why it matters**: Users see human-friendly descriptions instead of cryptic codes

---

## How to Run Tests

### Run All Tests

Docker must be running: the tests start Redis in a container, and a missing Redis fails them rather than skipping them.

```bash
dotnet test McpServerTemplate.sln
```

### Run Tests with Verbose Output
```bash
dotnet test --verbosity detailed
```

### Run a Specific Test Class
```bash
dotnet test --filter FullyQualifiedName~SmhiFormattersTests
```

### Run Tests with Coverage
```bash
dotnet test --collect:"XPlat Code Coverage"
```

---

## Writing Tests for Your New Provider

### Template: The Policy Starts

The first test a provider needs is that the server starts with it: every tool has a policy, every scope is issuable, every host is declared. `FrameHarness` composes the frame the way the shipped server does:

```csharp
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers.YourProvider;
using McpServerTemplate.Tests.Frame;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerTemplate.Tests.Providers.YourProvider;

public class YourProviderPolicyTests
{
    [Fact]
    public void The_provider_starts_with_its_policy()
    {
        IReadOnlyList<IProviderModule> modules = [new YourProviderModule()];
        var settings = FrameHarness.Settings(modules);
        settings["Providers:YourProvider:BaseUrl"] = "https://api.example.com";

        using var server = FrameHarness.Compose(modules, settings, FrameHarness.Identity("your:read"));

        Assert.Contains("your_tool", server.GetRequiredService<PolicyRegistry>().Tools.Keys);
    }
}
```

### Template: Formatter Tests

Create `McpServerTemplate.Tests/Providers/YourProvider/YourProviderFormattersTests.cs`:

```csharp
using Xunit;
using McpServerTemplate.Providers.YourProvider;
using McpServerTemplate.Providers.YourProvider.Models;

namespace McpServerTemplate.Tests.Providers.YourProvider;

public class YourProviderFormattersTests
{
    [Fact]
    public void FormatData_WithValidData_ReturnsFormattedString()
    {
        // Arrange
        var data = new YourDataModel
        {
            Id = 1,
            Title = "Test",
            Description = "Test description"
        };

        // Act
        var result = YourProviderFormatters.FormatData(data);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("1", result);        // ID should be in output
        Assert.Contains("Test", result);     // Title should be in output
    }

    [Fact]
    public void FormatDataList_WithEmptyList_ReturnsEmptyMessage()
    {
        // Arrange
        var data = new List<YourDataModel>();

        // Act
        var result = YourProviderFormatters.FormatDataList(data);

        // Assert
        Assert.Contains("No data", result.ToLower());
    }
}
```

### Template: Validation Tests

Create `McpServerTemplate.Tests/Providers/YourProvider/YourProviderValidationTests.cs`:

```csharp
using Xunit;
using McpServerTemplate.Providers.YourProvider;

namespace McpServerTemplate.Tests.Providers.YourProvider;

public class YourProviderValidationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ValidateId_WithValidRange_ReturnsTrue(int id)
    {
        // Act
        var result = YourProviderValidator.IsValidId(id);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void ValidateId_WithInvalidRange_ReturnsFalse(int id)
    {
        // Act
        var result = YourProviderValidator.IsValidId(id);

        // Assert
        Assert.False(result);
    }
}
```

---

## Testing Best Practices

### ✅ DO

1. **Name tests clearly**: `FormatCurrentWeather_WithValidData_ReturnsFormattedString()`
   - Format: `MethodName_Condition_ExpectedResult`

2. **Use Arrange-Act-Assert**: Every test should have these 3 sections
   ```csharp
   // Arrange - set up test data
   var input = new Data { ... };
   
   // Act - execute the method
   var result = Method(input);
   
   // Assert - verify the result
   Assert.NotNull(result);
   ```

3. **Test edge cases**:
   ```csharp
   [Theory]
   [InlineData("")]              // Empty string
   [InlineData(null)]            // Null value
   [InlineData(int.MaxValue)]    // Boundary value
   public void TestMethod(string input) { ... }
   ```

4. **Fake external dependencies**:
   ```csharp
   // Don't call real APIs; use fake handlers
   var handler = new FakeHttpMessageHandler(
       new HttpResponseMessage(HttpStatusCode.OK)
       {
           Content = new StringContent("{...}", Encoding.UTF8, "application/json")
       });
   var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
   var client = new YourApiClient(httpClient);
   ```

5. **Use `[Theory]` for multiple test cases**:
   ```csharp
   [Theory]
   [InlineData(59.33, 18.07, true)]    // Stockholm is valid
   [InlineData(90.1, 0, false)]        // Invalid coordinate
   public void ValidTest(double lat, double lon, bool expected)
   ```

### ❌ DON'T

1. **Don't test the framework**: Don't test that `HttpClient` works; assume it does
2. **Don't make tests interdependent**: Each test should be independent
3. **Don't hardcode test data in multiple places**: Create shared test fixtures
4. **Don't skip error cases**: Test both success and failure paths
5. **Don't write massive tests**: If a test is > 30 lines, break it down

---

## Test Fixture Example

For tests that share common setup, create a fixture:

```csharp
public class YourProviderTestFixture : IDisposable
{
    public YourProviderTestFixture()
    {
        // Setup common test data
        TestData = new YourDataModel { Id = 1, Title = "Test" };
        FakeHandler = new FakeHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    public YourDataModel TestData { get; }
    public FakeHttpMessageHandler FakeHandler { get; }

    public void Dispose()
    {
        // Cleanup if needed
    }
}

// Usage in test class
public class YourProviderToolsTests : IClassFixture<YourProviderTestFixture>
{
    private readonly YourProviderTestFixture _fixture;

    public YourProviderToolsTests(YourProviderTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetData_CallsClient()
    {
        // Use _fixture.TestData, _fixture.FakeHandler, etc.
    }
}
```

---

## CI/CD Integration

The repository's workflow (`.github/workflows/ci.yml`) runs on every push, on a runner with Docker:

```yaml
- name: Restore (locked)
  run: dotnet restore McpServerTemplate.sln --locked-mode

- name: Build (warnings are errors)
  run: dotnet build McpServerTemplate.sln --no-restore --configuration Release -warnaserror

- name: Test
  run: dotnet test McpServerTemplate.sln --no-build --configuration Release --verbosity normal
```

followed by a vulnerable-package audit and a secret scan. The Redis tests run there as they do locally.

---

## Coverage Goals

- **Formatters**: 100% coverage (translate API responses to readable text)
- **Validators**: 100% coverage (security-critical)
- **Tools**: 80%+ coverage (test main paths and error cases)
- **DTO Models**: 0% required (these are data containers)

Check coverage with:
```bash
dotnet test /p:CollectCoverage=true
```

---

## Summary

The testing strategy ensures:

1. **Correctness**: Logic works as expected
2. **Security**: Invalid inputs are rejected
3. **Reliability**: Edge cases are handled gracefully
4. **Maintainability**: Tests serve as documentation
5. **Confidence**: Changes don't break existing functionality

When adding a new provider, write tests for:
- ✅ The policy starts (every tool declared, scopes issuable, hosts declared)
- ✅ Formatters (output correctness)
- ✅ Validators (input security)
- ✅ Models (data deserialization)
- ✅ Error handling (graceful failure)

This ensures your provider is production-ready from day one.
