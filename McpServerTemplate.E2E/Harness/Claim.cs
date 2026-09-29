using System.Diagnostics.CodeAnalysis;
using Xunit.Sdk;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The assertion that carries a test's claim.
///
/// contract-005 · G-11, T-12 — a sabotage's red counts only when it fails on the assertion that carries the test's
/// claim: a red at a self-check, a positive control or startup does not. Every end-to-end test states its claim through
/// this class and through nothing else, so a failure says which kind it is. A claim that does not hold throws a
/// <see cref="ClaimException"/>, which nothing else throws, and the runner reports it by its type
/// ("McpServerTemplate.E2E.Harness.ClaimException : …"). A self-check throws <see cref="EnvironmentFaultException"/> or
/// <see cref="InvalidOperationException"/>, a positive control fails as an ordinary assertion, and a server that never
/// comes up fails its fixture: none of them is this.
/// </summary>
public static class Claim
{
    /// <summary>The claim holds when <paramref name="condition"/> is true; otherwise it fails, saying <paramref name="message"/>.</summary>
    public static void True([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new ClaimException(message);
        }
    }

    /// <summary>
    /// The claim holds when <paramref name="assertion"/>, an xUnit assertion, passes; its failure is the claim's, in the
    /// assertion's own words.
    /// </summary>
    public static void Holds(Action assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);

        try
        {
            assertion();
        }
        catch (XunitException ex) when (ex is not ClaimException)
        {
            throw new ClaimException(ex.Message);
        }
    }
}

/// <summary>A test's claim did not hold (<see cref="Claim"/>): the only failure a sabotage's red may be.</summary>
public sealed class ClaimException : XunitException
{
    public ClaimException()
        : base("The test's claim did not hold.")
    {
    }

    public ClaimException(string message)
        : base(message)
    {
    }

    public ClaimException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
