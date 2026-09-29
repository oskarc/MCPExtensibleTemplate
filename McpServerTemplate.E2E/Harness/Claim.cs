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
///
/// contract-005 · G-17 round 1 (HAR-R1-04) — and nothing else constructs one: HarnessSelfTests fails on any file but this
/// that does. A test whose claim is that a call succeeds folds into it only the failures that are the server's answer
/// (<see cref="IsTheServersAnswer"/>), and lets every other failure propagate as what it is.
/// </summary>
public static class Claim
{
    /// <summary>
    /// contract-005 · G-17 round 1 (HAR-R1-04) — whether <paramref name="exception"/> is the server's answer to a request, and
    /// so a failure a claim about that answer may fold in: a status that is not success (an <see cref="HttpRequestException"/>
    /// carrying its <see cref="HttpRequestException.StatusCode"/>, as HttpClient and the MCP SDK's transports throw for one), or
    /// an MCP error (<see cref="ModelContextProtocol.McpException"/>, which the SDK throws for an error the server returned and
    /// for an OAuth flow the server's metadata did not let it complete). Anything else — a connection or TLS failure (an
    /// HttpRequestException with no status), an environment fault, a timeout — is not the claim's: a test lets it propagate,
    /// and it is never turned into a <see cref="ClaimException"/>. Three tests turned any exception into their claim's
    /// failure, so a container dying or a connection reset mid-test was recorded as a red on the claim.
    /// </summary>
    public static bool IsTheServersAnswer(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: not null } => true,
        ModelContextProtocol.McpException => true,
        _ => false,
    };

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
