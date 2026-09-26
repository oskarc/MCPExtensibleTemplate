using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// What a test class changes about the server under test's configuration.
///
/// contract-005 · G-8, G-16 — the server under test is configured through its environment only, and
/// only through one entry point: <see cref="E2EEnvironment.StartServerAsync"/> with a settings delta.
/// The harness holds the environment's base settings (<see cref="ServerUnderTest.BaseSettings"/>);
/// a delta sets or removes keys on top of them, and nothing else can reach the container's
/// configuration.
///
/// contract-005 · G-8 — "an unknown key in a delta fails the server's own startup check" holds only in
/// the sections the server's settings allowlist governs (<see cref="GovernedSections"/>). Anywhere else
/// the server ignores an unknown key silently: Bogus:Key, HttpTransport:AlowedHosts:0 and
/// Serilog:WriteTo:1:Args:pth each left a server running as if they were not there. So a key outside
/// those sections is refused here, when the delta is written and before any container starts, unless
/// it is one the server is declared to read (<see cref="ReadOutsideGovernedSections"/>). A key inside
/// them passes untouched: refusing it is the server's own check, and a test of that check needs the
/// key to arrive.
///
/// Keys are configuration keys, colon-separated (HttpTransport:AllowedHosts:0); they become
/// environment variables with double underscores. A key given in that form is read back to colons. A
/// key without a colon, such as ASPNETCORE_ENVIRONMENT, passes as it is.
/// </summary>
public sealed partial class SettingsDelta
{
    /// <summary>
    /// The sections the server's own startup check governs: SettingsAllowlist.GovernedSections in
    /// McpServerTemplate/Infrastructure/Frame/SettingsAllowlist.cs. When G-12 (2) makes HttpTransport
    /// one of them, it moves here and its keys leave <see cref="ReadOutsideGovernedSections"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> GovernedSections =
        ["Authentication", "Providers", "Limits", "Confirmation", "Development", "RateLimit"];

    /// <summary>
    /// The keys outside <see cref="GovernedSections"/> the server is declared to read, each with where
    /// it is read, under McpServerTemplate/. {n} is any array index, {*} any name.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOutsideGovernedSections =
    [
        // Program.cs — the transport, read before either host is built.
        "Transport",

        // Program.cs — which appsettings.{Environment}.json loads; the first of the two that is set.
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT",

        // Program.cs (ConfigurationGuard.IntegerInRange) — the port.
        "HttpTransport:Port",

        // Program.cs and HttpServerComposition.cs — the bind address, and the host filter's fallback.
        "HttpTransport:BindAddress",

        // HttpServerComposition.cs — the host filter.
        "HttpTransport:AllowedHosts:{n}",

        // HttpServerComposition.cs (CORS) and Identity/OriginGuardMiddleware.cs.
        "HttpTransport:AllowedOrigins:{n}",

        // HttpServerComposition.cs (forwarded headers) and Identity/TransportSecurityGuard.cs.
        "HttpTransport:KnownProxies:{n}",
        "HttpTransport:KnownNetworks:{n}",

        // Identity/TransportSecurityGuard.cs — read only to be named in its refusal.
        "HttpTransport:Certificate:Path",
        "HttpTransport:Certificate:Subject",

        // Program.cs — ReadFrom.Configuration reads the Serilog section, and the traversal check reads
        // WriteTo:1:Args:path. Declared in the shape appsettings*.json give it: levels, per-namespace
        // overrides, sinks with the arguments those files pass them, and enrichers.
        "Serilog:MinimumLevel:Default",
        "Serilog:MinimumLevel:Override:{*}",
        "Serilog:WriteTo:{n}:Name",
        "Serilog:WriteTo:{n}:Args:path",
        "Serilog:WriteTo:{n}:Args:rollingInterval",
        "Serilog:WriteTo:{n}:Args:retainedFileCountLimit",
        "Serilog:WriteTo:{n}:Args:fileSizeLimitBytes",
        "Serilog:WriteTo:{n}:Args:rollOnFileSizeLimit",
        "Serilog:WriteTo:{n}:Args:outputTemplate",
        "Serilog:WriteTo:{n}:Args:standardErrorFromLevel",
        "Serilog:Enrich:{n}",

        // Not the product's code but the runtime it ships on: .NET on Linux builds its root store from
        // these. The harness's trust is set through them (G-8), and a test that changes trust sets them.
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
    ];

    private static readonly (string Key, Regex Pattern)[] Declared =
        [.. ReadOutsideGovernedSections.Select(k => (k, ToRegex(k)))];

    private SettingsDelta(ImmutableDictionary<string, string?> changes) => Changes = changes;

    /// <summary>The server as the environment configures it, unchanged.</summary>
    public static SettingsDelta None { get; } = new(ImmutableDictionary<string, string?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    /// <summary>Each key the delta sets, with its value, or null for a key it removes.</summary>
    public ImmutableDictionary<string, string?> Changes { get; }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentException">The key lies outside the governed sections and the server is not declared to read it.</exception>
    public SettingsDelta Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return new SettingsDelta(Changes.SetItem(Checked(key, removal: false), value));
    }

    /// <summary>Removes <paramref name="key"/> and every key beneath it, so a whole array or section can go.</summary>
    /// <exception cref="ArgumentException">The key lies outside the governed sections and names nothing the server is declared to read.</exception>
    public SettingsDelta Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new SettingsDelta(Changes.SetItem(Checked(key, removal: true), null));
    }

    /// <summary>The environment variables a container is started with: the base settings, then this delta.</summary>
    public IReadOnlyDictionary<string, string> ApplyTo(IReadOnlyDictionary<string, string> baseSettings)
    {
        ArgumentNullException.ThrowIfNull(baseSettings);

        var settings = new Dictionary<string, string>(baseSettings, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Changes)
        {
            if (value is null)
            {
                foreach (var existing in settings.Keys.Where(k => Beneath(k, key)).ToArray())
                {
                    settings.Remove(existing);
                }
            }
            else
            {
                settings[key] = value;
            }
        }

        return settings.ToDictionary(s => ToEnvironmentName(s.Key), s => s.Value, StringComparer.Ordinal);
    }

    /// <summary>The environment variable a configuration key is read from.</summary>
    public static string ToEnvironmentName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Replace(":", "__", StringComparison.Ordinal);
    }

    public override string ToString() =>
        Changes.IsEmpty
            ? "(none)"
            : string.Join(", ", Changes.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Value is null ? $"-{c.Key}" : $"{c.Key}={c.Value}"));

    /// <summary>
    /// The key in its configuration form, or a refusal naming it and the nearest declared key. A
    /// removal may also name a section that holds declared keys (HttpTransport:KnownProxies).
    /// </summary>
    private static string Checked(string key, bool removal)
    {
        var configurationKey = key.Replace("__", ":", StringComparison.Ordinal);
        var segments = configurationKey.Split(':');

        if (GovernedSections.Contains(segments[0], StringComparer.OrdinalIgnoreCase)
            || Declared.Any(d => d.Pattern.IsMatch(configurationKey))
            || removal && Declared.Any(d => HoldsSection(d.Key, segments)))
        {
            return configurationKey;
        }

        var nearest = Declared.MinBy(d => Distance(Shape(configurationKey), d.Key)).Key;
        throw new ArgumentException(
            $"The settings delta names '{key}', which the server would ignore silently: it lies outside the "
            + $"sections the server's own startup check governs ({string.Join(", ", GovernedSections)}) and is "
            + $"not a key the server is declared to read. Did you mean '{nearest}'? A key the server does read "
            + $"joins {nameof(SettingsDelta)}.{nameof(ReadOutsideGovernedSections)}, with where it is read.",
            nameof(key));
    }

    private static bool Beneath(string key, string removed) =>
        key.Equals(removed, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(removed + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="declared"/> lies beneath the section <paramref name="section"/> names.</summary>
    private static bool HoldsSection(string declared, string[] section)
    {
        var segments = declared.Split(':');
        return segments.Length > section.Length
            && ToRegex(string.Join(":", segments.Take(section.Length))).IsMatch(string.Join(":", section));
    }

    private static Regex ToRegex(string key) => new(
        "^" + Regex.Escape(key).Replace(@"\{\*}", "[^:]+", StringComparison.Ordinal).Replace(@"\{n}", @"\d+", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Array indices become {n}, so "AlowedHosts:0" is compared with "AllowedHosts:{n}", not with a digit.
    private static string Shape(string key) => IndexPattern().Replace(key, ":{n}");

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

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
