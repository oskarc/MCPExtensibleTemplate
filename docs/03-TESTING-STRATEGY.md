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

**Example**: Test that `GetForecast()` tool correctly receives config, calls API client, and formats output

### 3. **E2E Tests** (full system scope)
- Test the complete request lifecycle
- Hit real MCP protocol entry points
- Slower execution
- Verify the entire flow works

**Example**: Simulate an MCP client calling `GetBlogPost` and verifying the response format matches MCP spec

### 4. **Security & Load Tests** (non-functional)
- Rate limiting enforcement
- Authentication validation
- Input validation and injection prevention
- Concurrent load simulation

**Example**: Verify that calling a tool 11 times in 1 minute is rejected

---

## Current Test Structure

```
McpServerTemplate.Tests/
├── Providers/
│   ├── Smhi/
│   │   ├── SmhiCoordinateValidationTests.cs
│   │   ├── SmhiFormattersTests.cs
│   │   └── WeatherSymbolTests.cs
│   └── SmhiObs/
│       └── SmhiObsFormattersTests.cs
└── [Other shared tests would go here]
```

---

## Test Examples

### Example 1: Unit Test - Formatter

**File**: `Providers/Smhi/SmhiFormattersTests.cs`

```csharp
using Xunit;

namespace McpServerTemplate.Tests.Providers.Smhi;

public class SmhiFormattersTests
{
    [Fact]
    public void FormatCurrentWeather_WithValidData_ReturnsFormattedString()
    {
        // Arrange
        var forecast = new ForecastResponse
        {
            Timeseries = new[]
            {
                new TimeseriesPoint
                {
                    Time = DateTime.UtcNow,
                    Data = new ForecastData
                    {
                        Instant = new Instant { Details = new { AirTemperature = 15.5 } }
                    }
                }
            }
        };

        // Act
        var result = SmhiFormatters.FormatCurrentWeather(forecast);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("15.5", result);  // Temperature should be in output
    }

    [Fact]
    public void FormatCurrentWeather_WithEmptyForecast_ReturnsError()
    {
        // Arrange
        var forecast = new ForecastResponse { Timeseries = Array.Empty<TimeseriesPoint>() };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => SmhiFormatters.FormatCurrentWeather(forecast));
    }
}
```

**What it tests**: Output formatting works correctly and handles edge cases

**Why it matters**: Ensures AI assistants receive properly formatted, readable responses

---

### Example 2: Unit Test - Validation

**File**: `Providers/Smhi/SmhiCoordinateValidationTests.cs`

```csharp
using Xunit;

namespace McpServerTemplate.Tests.Providers.Smhi;

public class SmhiCoordinateValidationTests
{
    [Theory]
    [InlineData(59.33, 18.07)]  // Stockholm - valid
    [InlineData(55.60, 13.00)]  // Malmö - valid
    public void ValidateCoordinates_WithValidRange_ReturnsTrue(double lat, double lon)
    {
        // Act
        var result = SmhiCoordinateValidator.IsWithinSmhiArea(lat, lon);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(90.1, 0)]       // Too far north
    [InlineData(-10, 0)]        // Too far south
    [InlineData(0, 50)]         // Too far east
    public void ValidateCoordinates_WithInvalidRange_ReturnsFalse(double lat, double lon)
    {
        // Act
        var result = SmhiCoordinateValidator.IsWithinSmhiArea(lat, lon);

        // Assert
        Assert.False(result);
    }
}
```

**What it tests**: Input validation correctly rejects out-of-range coordinates

**Why it matters**: Prevents users from requesting weather for unsupported areas

---

### Example 3: Unit Test - Data Symbol Mapping

**File**: `Providers/Smhi/WeatherSymbolTests.cs`

```csharp
using Xunit;

namespace McpServerTemplate.Tests.Providers.Smhi;

public class WeatherSymbolTests
{
    [Theory]
    [InlineData("01", "☀️ Clear sky")]
    [InlineData("02", "🌤 Partly cloudy")]
    [InlineData("05", "⛅ Fall between 6-9")]
    [InlineData("10", "🌧 Light rain")]
    [InlineData("26", "⛈ Thunder")]
    public void MapWeatherSymbol_WithValidCode_ReturnsReadableDescription(
        string code,
        string expectedDescription)
    {
        // Act
        var result = WeatherSymbol.MapToEmoji(code);

        // Assert
        Assert.Equal(expectedDescription, result);
    }

    [Fact]
    public void MapWeatherSymbol_WithUnknownCode_ReturnsDefaultSymbol()
    {
        // Act
        var result = WeatherSymbol.MapToEmoji("999");

        // Assert
        Assert.Equal("❓ Unknown", result);
    }
}
```

**What it tests**: Weather symbols are correctly translated to readable emoji + text

**Why it matters**: Users see human-friendly descriptions instead of cryptic codes

---

## How to Run Tests

### Run All Tests
```bash
cd McpServerTemplate.Tests
dotnet test
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

4. **Mock external dependencies**:
   ```csharp
   // Don't call real APIs; mock them
   var mockClient = new Mock<IApiClient>();
   mockClient.Setup(x => x.GetDataAsync(1))
       .ReturnsAsync(new DataResponse { ... });
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
        MockClient = new Mock<IYourApiClient>();
    }

    public YourDataModel TestData { get; }
    public Mock<IYourApiClient> MockClient { get; }

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
        // Use _fixture.TestData, _fixture.MockClient, etc.
    }
}
```

---

## CI/CD Integration

Add this to your CI pipeline (GitHub Actions, GitLab CI, etc.):

```yaml
- name: Run Tests
  run: |
    cd McpServerTemplate.Tests
    dotnet test --configuration Release --verbosity normal --logger "trx"

- name: Upload Test Results
  uses: actions/upload-artifact@v2
  with:
    name: test-results
    path: "**/TestResults/*.trx"
```

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
- ✅ Formatters (output correctness)
- ✅ Validators (input security)
- ✅ Models (data deserialization)
- ✅ Error handling (graceful failure)

This ensures your provider is production-ready from day one.
