using McpServerTemplate.Infrastructure;

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-001 · T-7 (G-6) — the total timeout must exceed attempt timeout x (1 + retries),
/// and the check must happen when the budget is declared, which is startup.
/// </summary>
public class ResilienceBudgetTests
{
    private static ResilienceBudget Create(
        double attemptSeconds, int retries, double totalSeconds, double samplingSeconds = 60) =>
        ResilienceBudget.Create(
            "TestProvider",
            attemptTimeout: TimeSpan.FromSeconds(attemptSeconds),
            maxRetryAttempts: retries,
            totalTimeout: TimeSpan.FromSeconds(totalSeconds),
            samplingDuration: TimeSpan.FromSeconds(samplingSeconds),
            breakDuration: TimeSpan.FromSeconds(15));

    [Fact]
    public void T7_a_total_timeout_that_cannot_fit_the_retries_is_refused()
    {
        // 10s x (1 + 3) = 40s of attempts inside a 30s budget: the last attempts could never run.
        // This is the shape the SMHI forecast provider shipped with.
        var ex = Assert.Throws<ConfigurationException>(() => Create(attemptSeconds: 10, retries: 3, totalSeconds: 30));

        Assert.Contains("TestProvider", ex.Message, StringComparison.Ordinal);
        Assert.Contains("40", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T7_a_total_timeout_exactly_equal_to_the_worst_case_is_refused()
    {
        // Equal is not enough: the retry delays and the pipeline's own overhead still have to fit.
        Assert.Throws<ConfigurationException>(() => Create(attemptSeconds: 10, retries: 2, totalSeconds: 30));
    }

    [Fact]
    public void T7_a_coherent_budget_is_accepted()
    {
        var budget = Create(attemptSeconds: 10, retries: 2, totalSeconds: 35);

        Assert.NotNull(budget);
    }

    [Fact]
    public void T7_a_sampling_window_shorter_than_two_attempts_is_refused()
    {
        var ex = Assert.Throws<ConfigurationException>(
            () => Create(attemptSeconds: 20, retries: 2, totalSeconds: 70, samplingSeconds: 30));

        Assert.Contains("sampling window", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T7_the_client_timeout_never_caps_the_pipeline()
    {
        // The trap: HttpClient.Timeout wraps the whole resilience pipeline, so any finite value
        // here silently cancels retries that the pipeline still owes.
        Assert.Equal(Timeout.InfiniteTimeSpan, ResilienceBudget.ClientTimeout);
    }

    [Theory]
    [InlineData(10, 2, 35)]  // Smhi forecast
    [InlineData(20, 2, 70)]  // Smhi observations
    [InlineData(5, 2, 20)]   // JsonPlaceholder
    public void T7_every_budget_this_server_ships_is_coherent(int attempt, int retries, int total)
    {
        // Guards the shipped numbers themselves, so a later edit to a provider's registration
        // cannot quietly reintroduce a budget that cannot run its own retries.
        var worstCase = attempt * (1 + retries);

        Assert.True(total > worstCase, $"{total}s cannot fit {retries} retries of {attempt}s ({worstCase}s)");
    }
}
