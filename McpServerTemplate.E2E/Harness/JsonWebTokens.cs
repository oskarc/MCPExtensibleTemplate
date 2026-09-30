using System.Text.RegularExpressions;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// A JSON web token as it appears in text: three base64url parts, the first a JSON object's ("eyJ"). The one pattern the
/// harness reads tokens by — the sabotage record redacts them by it, and T-6 searches upstream records for them.
///
/// contract-005 · G-17 round 3 (HAR-R3-02) — here rather than in UpstreamTests, which declared it as a regular-expression
/// generator's partial method and so was a partial class: a test class is now declared in one file, and no other file can add
/// to one that is not partial.
/// </summary>
internal static partial class JsonWebTokens
{
    /// <summary>A JSON web token.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*")]
    public static partial Regex Pattern();
}
