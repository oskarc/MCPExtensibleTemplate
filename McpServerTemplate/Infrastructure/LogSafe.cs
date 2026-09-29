using System.Buffers;
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
///
/// contract-005 · G-17 round 2 (SEC-R2-03) — and every value a caller writes that reaches a log line goes through
/// <see cref="Text"/> where the frame writes it: the JSON-RPC method, a tool's, resource's or prompt's name, an argument's
/// name, the Origin header, the request path, a refused token's reason. It was used for the unregistered issuer alone, and
/// the rest went into the log as written. What the frame's own call sites cannot reach — the SDK's lines, which name the
/// method, the tool, the resource and the client, and the text of an exception — is escaped on its way to the sinks
/// (<see cref="Escaped"/>, <see cref="LineSafeLogging"/>).
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
            if (Breaks(rune))
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
    /// contract-005 · G-17 round 2 (SEC-R2-03) — <paramref name="value"/> with every character that can break or disguise a
    /// log line written as an escape instead, and nothing cut: a carriage return as \r, a line feed as \n, a tab as \t, any
    /// other control, format, line- or paragraph-separator character as \uXXXX (\UXXXXXXXX beyond the basic plane), and an
    /// unpaired surrogate as \uXXXX of itself. For what the log pipeline writes on the way to its sinks
    /// (<see cref="LineSafeSink"/>), where a value's length is not the pipeline's to choose and what was there should still
    /// show. The same instance when there is nothing to escape.
    /// </summary>
    public static string Escaped(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var first = FirstBreaking(value);
        if (first < 0)
        {
            return value;
        }

        var escaped = new StringBuilder(value.Length + 16);
        escaped.Append(value, 0, first);
        var rest = value.AsSpan(first);
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                escaped.Append(CultureInfo.InvariantCulture, $"\\u{(int)rest[0]:X4}");
                rest = rest[1..];
                continue;
            }

            if (!Breaks(rune))
            {
                escaped.Append(rest[..consumed]);
            }
            else
            {
                escaped.Append(rune.Value switch
                {
                    0x0D => "\\r",
                    0x0A => "\\n",
                    0x09 => "\\t",
                    <= 0xFFFF => string.Create(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}"),
                    _ => string.Create(CultureInfo.InvariantCulture, $"\\U{rune.Value:X8}"),
                });
            }

            rest = rest[consumed..];
        }

        return escaped.ToString();
    }

    /// <summary>Whether <paramref name="value"/> holds a character <see cref="Escaped"/> would escape.</summary>
    public static bool BreaksALine(string value) => FirstBreaking(value ?? throw new ArgumentNullException(nameof(value))) >= 0;

    /// <summary>
    /// Whether <paramref name="rune"/> can break or disguise a log line: a control character (C0 and C1, next line and the
    /// terminal's escape among them), a format character (the direction overrides and isolates, zero-width characters), or
    /// the Unicode line and paragraph separators.
    /// </summary>
    private static bool Breaks(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// The index of the first character of <paramref name="value"/> that <see cref="Escaped"/> would escape, an unpaired
    /// surrogate included, or -1 when there is none. Printable ASCII is passed over without decoding.
    /// </summary>
    private static int FirstBreaking(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= ' ' and < '\u007F')
            {
                continue;
            }

            if (c < ' ' || c == '\u007F'
                || Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var consumed) != OperationStatus.Done
                || Breaks(rune))
            {
                return i;
            }

            i += consumed - 1;
        }

        return -1;
    }

    /// <summary>
    /// contract-005 · G-17 round 1 — <paramref name="url"/>, a URL an operator configured, as written, but with the parts that
    /// can carry a credential written as ***: its user information (scheme://***@host/path), follow-up, its query
    /// (…/path?***, which ?client_secret=… would otherwise print), and, batch B, its fragment (…/path#***, where a browser's
    /// implicit-flow URL carries the access token, #access_token=…). The refusals of such a URL echoed it whole, and put the
    /// secret in the log the server stops with; each echo goes through this now.
    ///
    /// Read from the text, as the refusals echo it, and never from a parse: a password can hold / ? # : or @ unescaped, and
    /// one that leaves the URL unparseable was refused as not a URL and echoed all the same. So user information goes first:
    /// everything from after the scheme's :// (from the start, where there is none before it) to the last @ — an @ in a path,
    /// a query or a fragment is taken for it too, which hides more than the credential, never less. Then the query: everything
    /// after the first ? that is left. Then the fragment: everything after the first # that is left. A ? or # inside a password
    /// is gone with the user information by then, so it is never taken for where a later part starts; a # after the query's ?
    /// is gone with the query; and a ? inside a fragment — the query's mark, written by the step before — goes with the
    /// fragment, so whichever of the two comes first hides everything after it.
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
        if (query >= 0)
        {
            url = string.Concat(url.AsSpan(0, query + 1), "***");
        }

        var fragment = url.IndexOf('#', StringComparison.Ordinal);
        return fragment < 0 ? url : string.Concat(url.AsSpan(0, fragment + 1), "***");
    }
}
