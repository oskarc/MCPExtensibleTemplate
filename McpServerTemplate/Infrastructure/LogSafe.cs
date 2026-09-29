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
}
