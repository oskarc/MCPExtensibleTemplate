using System.Globalization;
using System.Text;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// A value this server did not choose, made safe to write into one log line.
///
/// contract-005 · T-3 — some refusals are worth logging only with the value that caused them: the issuer an unregistered
/// token names is the attacker's to write. Written as it came, a line break in it ends the log line and forges the next,
/// an escape sequence rewrites a terminal, a direction override shows the line other than it is, and its length is the
/// caller's to choose. So every control, format and line- or paragraph-separator character is removed, and the value is
/// cut at <see cref="MaxLength"/> characters, the cut marked with an ellipsis.
/// </summary>
public static class LogSafe
{
    /// <summary>The longest a value is written, in UTF-16 characters, before the ellipsis that marks the cut.</summary>
    public const int MaxLength = 200;

    /// <summary><paramref name="value"/> without the characters that can break or disguise a log line, cut at <see cref="MaxLength"/>.</summary>
    public static string Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var kept = new StringBuilder(Math.Min(value.Length, MaxLength + 1));
        foreach (var rune in value.EnumerateRunes())
        {
            // An unpaired surrogate is read as U+FFFD, which is kept: it shows something was there.
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (kept.Length + rune.Utf16SequenceLength > MaxLength)
            {
                return kept.Append('…').ToString();
            }

            kept.Append(rune.ToString());
        }

        return kept.ToString();
    }

    /// <summary>
    /// contract-005 · G-17 round 1 — <paramref name="url"/>, a URL an operator configured, as written, but with the parts that
    /// can carry a credential written as ***: its user information (scheme://***@host/path) and, follow-up, its query
    /// (…/path?***, which ?client_secret=… would otherwise print). The refusals of such a URL echoed it whole, and put the
    /// secret in the log the server stops with; each echo goes through this now.
    ///
    /// Read from the text, as the refusals echo it, and never from a parse: a password can hold / ? # : or @ unescaped, and
    /// one that leaves the URL unparseable was refused as not a URL and echoed all the same. So user information goes first:
    /// everything from after the scheme's :// (from the start, where there is none before it) to the last @ — an @ in a path
    /// or a query is taken for it too, which hides more than the credential, never less. Then the query: everything after the
    /// first ? that is left. A ? inside a password is gone with the user information by then, so it is never taken for the
    /// query's start.
    /// </summary>
    public static string Url(string? url)
    {
        if (url is null)
        {
            return string.Empty;
        }

        var at = url.LastIndexOf('@');
        if (at >= 0)
        {
            var scheme = url.IndexOf("://", StringComparison.Ordinal);
            var start = scheme >= 0 && scheme < at ? scheme + 3 : 0;
            url = string.Concat(url.AsSpan(0, start), "***", url.AsSpan(at));
        }

        var query = url.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? url : string.Concat(url.AsSpan(0, query + 1), "***");
    }
}
