using System.Text.RegularExpressions;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// Refuses any setting the frame does not know, in the sections it governs (contract-003 · G-11).
///
/// A misspelled key is ignored silently by configuration binding, and the operator believes it is in
/// force. That is the false comfort Phase 1 found in a certificate setting nothing read; here it is
/// closed for every governed key at once. Retired settings get the same treatment, with the reason
/// they were retired.
/// </summary>
public static partial class SettingsAllowlist
{
    // {*} is any name (an identity provider), {n} any array index.
    private static readonly string[] FrameKeys =
    [
        "Authentication:Resource",
        "Authentication:AdminIdentityProvider",
        "Authentication:IdentityProviders:{*}:Name",
        "Authentication:IdentityProviders:{*}:Authority",
        "Authentication:IdentityProviders:{*}:Issuer",
        "Authentication:IdentityProviders:{*}:Algorithms:{n}",
        "Authentication:IdentityProviders:{*}:ScopeClaim",
        "Authentication:IdentityProviders:{*}:ClientIdClaim",
        "Authentication:IdentityProviders:{*}:ScopeCatalog:{n}",
        "Providers:Enabled:{n}",
        "Limits:Redis",
        "Limits:PerPrincipalPerMinute",
        "Confirmation:Key",
        "Development:DevPrincipal:IdentityProvider",
        "Development:DevPrincipal:Subject",
        "Development:DevPrincipal:ClientId",
        "Development:DevPrincipal:Scopes:{n}",
    ];

    private static readonly Dictionary<string, string> Retired = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RateLimit:MaxCallsPerToolPerMinute"] =
            "it was one limit per tool shared by every caller. Limits are now per caller: set "
            + "Limits:PerPrincipalPerMinute, and each tool's own limit in its provider's policy.",
        ["Authentication:ApiKey"] =
            "API keys were removed in Phase 1; callers present a bearer token from a configured identity provider.",
    };

    /// <summary>The sections this check governs. Anything else in configuration is left alone.</summary>
    public static readonly IReadOnlyList<string> GovernedSections =
        ["Authentication", "Providers", "Limits", "Confirmation", "Development", "RateLimit"];

    /// <summary>Throws when a governed section holds a key the frame does not know.</summary>
    public static void Validate(IConfiguration configuration, IEnumerable<IProviderModule> modules)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var known = FrameKeys.ToList();
        foreach (var module in modules)
        {
            known.Add($"Providers:{module.Name}:IdentityProvider");
            known.Add($"Providers:{module.Name}:BaseUrl");
            known.AddRange(module.Settings.Select(s => $"Providers:{module.Name}:{s}"));
        }

        var patterns = known.Select(k => (Key: k, Pattern: ToRegex(k))).ToArray();
        var problems = new List<string>();

        foreach (var section in GovernedSections)
        {
            foreach (var (key, value) in configuration.GetSection(section).AsEnumerable())
            {
                if (value is null)
                {
                    continue; // an intermediate section, not a setting
                }

                if (Retired.TryGetValue(key, out var why))
                {
                    problems.Add($"'{key}' is retired: {why}");
                }
                else if (!patterns.Any(p => p.Pattern.IsMatch(key)))
                {
                    var nearest = patterns.MinBy(p => Distance(Shape(key), p.Key)).Key;
                    problems.Add($"'{key}' is not a setting this server reads. Did you mean '{nearest}'?");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new ConfigurationException(
                "Configuration names settings the server would silently ignore, so what it says would not "
                + "be what the server does:\n  " + string.Join("\n  ", problems));
        }
    }

    private static Regex ToRegex(string key) => new(
        "^" + Regex.Escape(key).Replace(@"\{\*}", "[^:]+", StringComparison.Ordinal).Replace(@"\{n}", @"\d+", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Array indices become {n} so "Scopez:0" is compared with "Scopes:{n}", not with a digit.
    private static string Shape(string key) => IndexPattern().Replace(key, ":{n}");

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }

    [GeneratedRegex(@":\d+(?=:|$)")]
    private static partial Regex IndexPattern();
}
