using System.Globalization;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Reads settings that must be within a range, and refuses to start when they are not.
///
/// contract-001 · G-2 — a configuration failure exits 78 and an unhandled one exits 70, and the
/// difference is what tells whoever is on call whether to change a setting or read a stack trace.
/// Reading a malformed value straight through <c>GetValue&lt;int&gt;</c> loses that distinction:
/// the framework raises its own exception type, the process exits 70, and an operator who typed a
/// port wrong is sent looking for a bug in the server.
///
/// Anything an operator can mistype belongs here rather than at the point of use.
/// </summary>
public static class ConfigurationGuard
{
    /// <summary>
    /// Reads an integer setting, returning <paramref name="fallback"/> when it is absent.
    /// </summary>
    /// <param name="configuration">Configuration to read from.</param>
    /// <param name="key">Setting key, in its configuration form, e.g. "HttpTransport:Port".</param>
    /// <param name="minimum">Smallest accepted value, inclusive.</param>
    /// <param name="maximum">Largest accepted value, inclusive.</param>
    /// <param name="fallback">Value to use when the setting is absent or blank.</param>
    /// <param name="because">
    /// What the range is for, in the operator's terms. It is the whole value of the message:
    /// "must be between 1 and 65535" says less than "a TCP port".
    /// </param>
    public static int IntegerInRange(
        IConfiguration configuration,
        string key,
        int minimum,
        int maximum,
        int fallback,
        string because)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ConfigurationException(
                $"{key} must be a whole number ({because}), but is '{raw}'.");
        }

        if (value < minimum || value > maximum)
        {
            throw new ConfigurationException(
                $"{key} must be between {minimum} and {maximum} ({because}), but is {value}.");
        }

        return value;
    }
}
