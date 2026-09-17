using Microsoft.Extensions.Http.Resilience;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// A provider's timeout and retry budget, checked for coherence the moment it is declared.
///
/// contract-001 · G-6 — two traps this closes:
///
/// 1. <see cref="HttpClient.Timeout"/> wraps the entire resilience pipeline, retries included.
///    A client timeout shorter than the pipeline's total budget silently cancels the call
///    mid-retry, so the retries are configured, logged and never actually completed. Use
///    <see cref="ClientTimeout"/> and let the pipeline own every deadline — which is what the
///    resilience package documents.
///
/// 2. A total timeout smaller than attempt timeout x (1 + retries) has the same effect one level
///    down: the last attempts cannot run. That relation is checked by <see cref="Create"/>,
///    which providers call during registration — so the server refuses to start on a budget it
///    cannot honour, rather than quietly under-delivering on the first call that needs a retry.
///    Checking inside the options callback would not do: that runs lazily, when the first
///    HttpClient is built, which is a tool call and not startup.
/// </summary>
public sealed class ResilienceBudget
{
    /// <summary>
    /// The client-level timeout every provider uses: none. See trap 1 above — deadlines belong
    /// to the resilience pipeline, the only layer that knows how many attempts are still owed.
    /// </summary>
    public static readonly TimeSpan ClientTimeout = Timeout.InfiniteTimeSpan;

    private readonly TimeSpan _attemptTimeout;
    private readonly int _maxRetryAttempts;
    private readonly TimeSpan _totalTimeout;
    private readonly TimeSpan _samplingDuration;
    private readonly TimeSpan _breakDuration;

    private ResilienceBudget(
        TimeSpan attemptTimeout,
        int maxRetryAttempts,
        TimeSpan totalTimeout,
        TimeSpan samplingDuration,
        TimeSpan breakDuration)
    {
        _attemptTimeout = attemptTimeout;
        _maxRetryAttempts = maxRetryAttempts;
        _totalTimeout = totalTimeout;
        _samplingDuration = samplingDuration;
        _breakDuration = breakDuration;
    }

    /// <summary>
    /// Declares a budget, throwing <see cref="ConfigurationException"/> if the numbers cannot
    /// hold together. Called during service registration, so the throw happens at startup.
    /// </summary>
    /// <param name="providerName">Provider name, used in the failure message.</param>
    /// <param name="attemptTimeout">Deadline for a single attempt.</param>
    /// <param name="maxRetryAttempts">Retries after the first attempt.</param>
    /// <param name="totalTimeout">Deadline for the whole call, retries included.</param>
    /// <param name="samplingDuration">Circuit-breaker sampling window.</param>
    /// <param name="breakDuration">How long the circuit stays open once it trips.</param>
    public static ResilienceBudget Create(
        string providerName,
        TimeSpan attemptTimeout,
        int maxRetryAttempts,
        TimeSpan totalTimeout,
        TimeSpan samplingDuration,
        TimeSpan breakDuration)
    {
        var worstCase = attemptTimeout * (1 + maxRetryAttempts);
        if (totalTimeout <= worstCase)
        {
            throw new ConfigurationException(
                $"Provider '{providerName}' has an incoherent retry budget: a total timeout of "
                + $"{totalTimeout.TotalSeconds:0.##}s cannot accommodate {maxRetryAttempts} retries of "
                + $"{attemptTimeout.TotalSeconds:0.##}s each, which need more than "
                + $"{worstCase.TotalSeconds:0.##}s. Raise the total timeout above that, lower the attempt "
                + "timeout, or ask for fewer retries.");
        }

        // The circuit breaker needs a window at least twice the attempt timeout to see a pattern.
        if (samplingDuration < attemptTimeout * 2)
        {
            throw new ConfigurationException(
                $"Provider '{providerName}' has a circuit-breaker sampling window of "
                + $"{samplingDuration.TotalSeconds:0.##}s, which is shorter than twice its attempt timeout "
                + $"({attemptTimeout.TotalSeconds:0.##}s). The breaker cannot observe a full attempt.");
        }

        return new ResilienceBudget(
            attemptTimeout, maxRetryAttempts, totalTimeout, samplingDuration, breakDuration);
    }

    /// <summary>
    /// Writes this budget onto the standard resilience pipeline.
    /// </summary>
    public void Apply(HttpStandardResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AttemptTimeout.Timeout = _attemptTimeout;

        // Retry with exponential backoff for transient HTTP errors (5xx, 408, 429).
        options.Retry.MaxRetryAttempts = _maxRetryAttempts;
        options.Retry.Delay = TimeSpan.FromMilliseconds(500);

        // Circuit breaker: stop hammering an upstream that is already failing, and give it
        // room to recover.
        options.CircuitBreaker.SamplingDuration = _samplingDuration;
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.BreakDuration = _breakDuration;

        // Caps worst-case latency across the whole call.
        options.TotalRequestTimeout.Timeout = _totalTimeout;
    }
}
