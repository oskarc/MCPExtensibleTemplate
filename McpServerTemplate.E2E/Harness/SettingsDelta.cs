using System.Collections.Immutable;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// What a test class changes about the server under test's configuration.
///
/// contract-005 · G-8, G-16 — the server under test is configured through its environment only, and
/// only through one entry point: <see cref="E2EEnvironment.StartServerAsync"/> with a settings delta.
/// The harness holds the environment's base settings (<see cref="ServerUnderTest.BaseSettings"/>);
/// a delta sets or removes keys on top of them, and nothing else can reach the container's
/// configuration. The harness does not filter keys: a key the server does not know reaches the
/// server, whose own startup check refuses it — which is the behaviour a test of that check needs.
///
/// Keys are configuration keys, colon-separated (HttpTransport:AllowedHosts:0); they become
/// environment variables with double underscores. A key without a colon, such as
/// ASPNETCORE_ENVIRONMENT, passes as it is.
/// </summary>
public sealed class SettingsDelta
{
    private SettingsDelta(ImmutableDictionary<string, string?> changes) => Changes = changes;

    /// <summary>The server as the environment configures it, unchanged.</summary>
    public static SettingsDelta None { get; } = new(ImmutableDictionary<string, string?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    /// <summary>Each key the delta sets, with its value, or null for a key it removes.</summary>
    public ImmutableDictionary<string, string?> Changes { get; }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>.</summary>
    public SettingsDelta Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return new SettingsDelta(Changes.SetItem(key, value));
    }

    /// <summary>Removes <paramref name="key"/> and every key beneath it, so a whole array or section can go.</summary>
    public SettingsDelta Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new SettingsDelta(Changes.SetItem(key, null));
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

    private static bool Beneath(string key, string removed) =>
        key.Equals(removed, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(removed + ":", StringComparison.OrdinalIgnoreCase);
}
