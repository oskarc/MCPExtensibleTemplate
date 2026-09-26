namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// A fault in the end-to-end environment rather than in the product.
///
/// contract-005 · G-11 — the fixture's self-checks name the phase and the cause: Docker unavailable,
/// CA trust missing, clock skew, subnet taken, forwarded headers ignored. A red test that is really a
/// dead Docker daemon or a clock that slept through a laptop lid costs an afternoon if it reads as a
/// product failure; this reads as what it is.
/// </summary>
public sealed class EnvironmentFaultException : Exception
{
    public EnvironmentFaultException()
        : this("unknown", "an unnamed environment fault")
    {
    }

    public EnvironmentFaultException(string message)
        : this("unknown", message)
    {
    }

    public EnvironmentFaultException(string message, Exception innerException)
        : this("unknown", message, innerException)
    {
    }

    public EnvironmentFaultException(string phase, string cause, Exception? innerException = null)
        : base(
            $"End-to-end environment fault in phase '{phase}': {cause} "
            + "This is the test environment failing, not the product.",
            innerException)
    {
        Phase = phase;
        Cause = cause;
    }

    /// <summary>The phase of starting the environment that failed.</summary>
    public string Phase { get; } = "unknown";

    /// <summary>What went wrong, in terms an operator of the environment can act on.</summary>
    public string Cause { get; } = string.Empty;
}
