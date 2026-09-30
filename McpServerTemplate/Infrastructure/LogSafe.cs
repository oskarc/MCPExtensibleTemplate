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
/// caller's to choose. So every control, format and line- or paragraph-separator character is written as an escape, and
/// the value is cut at <see cref="MaxLength"/> characters, the cut marked with an ellipsis.
///
/// contract-005 · G-17 round 2 (SEC-R2-03) — and every value a caller writes that reaches a log line goes through
/// <see cref="Text"/> where the frame writes it: the JSON-RPC method, a tool's, resource's or prompt's name, an argument's
/// name, the Origin header, the request path, a refused token's reason. It was used for the unregistered issuer alone, and
/// the rest went into the log as written. What the frame's own call sites cannot reach — the SDK's lines, which name the
/// method, the tool, the resource and the client, and the text of an exception — is escaped on its way to the sinks
/// (<see cref="Escaped"/>, <see cref="LineSafeLogging"/>).
///
/// contract-005 · G-17 round 3 (SEC-R3-04, SEC-R3-07) — escaped, not removed, and the backslash escaped with the rest. The
/// frame's own lines removed what they escape now, so a tool name sent as get_fore + U+200B + cast was logged as the real
/// get_forecast it is not, and an issuer ending in U+200B as the configured issuer: the frame's security events named what
/// the request did not carry, beside the SDK's line for the same request, which showed it. And a backslash was left as it
/// was, so text a caller wrote as \r\n read exactly as a real line break escaped. Now two different values are never
/// written the same, and every escape can be read back to what was sent. The frame writes its own values as
/// <see cref="SafeText"/>, which the pipeline passes on as it is, so each is escaped once, as the SDK's are.
/// </summary>
public static class LogSafe
{
    /// <summary>The longest a value is written, in UTF-16 characters, before the ellipsis that marks the cut.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// <paramref name="value"/> as the frame writes a caller's value into its own lines and replies: escaped as
    /// <see cref="Escaped"/> escapes it, and cut at <see cref="MaxLength"/> characters, the cut marked with an ellipsis and
    /// never made inside an escape or between the halves of a surrogate pair (contract-005 · G-17 round 3, SEC-R3-04).
    /// </summary>
    public static string Text(string value) => EscapedAndCut(value);

    /// <summary>
    /// contract-005 · G-17 round 2 (SEC-R2-03) — <paramref name="value"/> with every character that can break or disguise a
    /// log line written as an escape instead, and nothing cut: a carriage return as \r, a line feed as \n, a tab as \t, any
    /// other control, format, line- or paragraph-separator character as \uXXXX (\UXXXXXXXX beyond the basic plane), and an
    /// unpaired surrogate as \uXXXX of itself. For what the log pipeline writes on the way to its sinks
    /// (<see cref="LineSafeSink"/>), where a value's length is not the pipeline's to choose and what was there should still
    /// show. The same instance when there is nothing to escape.
    ///
    /// contract-005 · G-17 round 3 (SEC-R3-07) — and a backslash as \\, so every escaped value reads back to exactly what it
    /// was: a caller's own \r\n is written \\r\\n, and only a real line break \r\n. A backslash in a Windows path in an
    /// exception's text is doubled with the rest.
    /// </summary>
    public static string Escaped(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var first = FirstToEscape(value, backslash: true);
        if (first < 0)
        {
            return value;
        }

        var escaped = new StringBuilder(value.Length + 16);
        escaped.Append(value, 0, first);
        var rest = value.AsSpan(first);
        while (!rest.IsEmpty)
        {
            var consumed = AppendEscaped(escaped, rest);
            rest = rest[consumed..];
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Whether <paramref name="value"/> holds a character that can break or disguise a log line, an unpaired surrogate
    /// included: what must never reach a line as it is. A backslash is not one; <see cref="NeedsEscaping"/> counts it.
    /// </summary>
    public static bool BreaksALine(string value) => FirstToEscape(value ?? throw new ArgumentNullException(nameof(value)), backslash: false) >= 0;

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-07) — whether <see cref="Escaped"/> would change <paramref name="value"/>: whether it
    /// holds a character that can break or disguise a line, an unpaired surrogate, or a backslash. The line-safe stage escapes
    /// a template's text, a value or an exception's text whenever this is true, so every one of them is escaped the same way,
    /// whole, and reads back.
    /// </summary>
    public static bool NeedsEscaping(string value) => FirstToEscape(value ?? throw new ArgumentNullException(nameof(value)), backslash: true) >= 0;

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-04) — <paramref name="value"/> escaped as <see cref="Escaped"/> escapes it, and cut
    /// once its escaped form would pass <see cref="MaxLength"/> characters: whole escapes and whole surrogate pairs only, the
    /// cut marked with an ellipsis.
    /// </summary>
    internal static string EscapedAndCut(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var kept = new StringBuilder(Math.Min(value.Length, MaxLength) + 1);
        var piece = new StringBuilder(10);
        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            piece.Clear();
            var consumed = AppendEscaped(piece, rest);
            if (kept.Length + piece.Length > MaxLength)
            {
                return kept.Append('…').ToString();
            }

            kept.Append(piece);
            rest = rest[consumed..];
        }

        return kept.ToString();
    }

    /// <summary>
    /// Appends the first character of <paramref name="rest"/> — a code point, or an unpaired surrogate — to
    /// <paramref name="output"/> as <see cref="Escaped"/> writes it, and returns how many UTF-16 code units it took.
    /// </summary>
    private static int AppendEscaped(StringBuilder output, ReadOnlySpan<char> rest)
    {
        if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done)
        {
            output.Append(CultureInfo.InvariantCulture, $"\\u{(int)rest[0]:X4}");
            return 1;
        }

        if (rune.Value == '\\')
        {
            output.Append("\\\\");
        }
        else if (!Breaks(rune))
        {
            output.Append(rest[..consumed]);
        }
        else
        {
            output.Append(rune.Value switch
            {
                0x0D => "\\r",
                0x0A => "\\n",
                0x09 => "\\t",
                <= 0xFFFF => string.Create(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}"),
                _ => string.Create(CultureInfo.InvariantCulture, $"\\U{rune.Value:X8}"),
            });
        }

        return consumed;
    }

    /// <summary>
    /// Whether <paramref name="rune"/> can break or disguise a log line: a control character (C0 and C1, next line and the
    /// terminal's escape among them), a format character (the direction overrides and isolates, zero-width characters), or
    /// the Unicode line and paragraph separators.
    /// </summary>
    private static bool Breaks(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// The index of the first character of <paramref name="value"/> that can break or disguise a line, an unpaired surrogate
    /// included, and — where <paramref name="backslash"/> — the first backslash too; or -1 when there is none. Printable ASCII
    /// is passed over without decoding.
    /// </summary>
    private static int FirstToEscape(string value, bool backslash)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= ' ' and < '\u007F')
            {
                if (backslash && c == '\\')
                {
                    return i;
                }

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

/// <summary>
/// contract-005 · G-17 round 3 (SEC-R3-04) — text the frame writes into its own log lines and replies: its own words with
/// every value a caller wrote escaped (<see cref="LogSafe.Escaped"/>) and each cut at <see cref="LogSafe.MaxLength"/>.
///
/// Nothing but escaping makes one. Every way to make one escapes what it is given, whoever calls it, so its text never holds
/// a character that can break or disguise a line, and every backslash in it begins an escape. That is what lets the log
/// pipeline pass it on as it is (<see cref="LineSafeSink"/>): escaped again there, each of its escapes would read as text a
/// caller wrote, and the frame's security events would be the one line in the log that does not show what was sent. So a
/// frame line shows a caller's value escaped once, as the SDK's own line for the same request does, whatever logger is
/// given it; a reply shows it escaped once too.
/// </summary>
public sealed class SafeText
{
    private readonly string _text;

    private SafeText(string text) => _text = text;

    /// <summary>Words the frame wrote, whole: escaped, and not cut.</summary>
    public static SafeText Of(string text) => new(LogSafe.Escaped(text ?? throw new ArgumentNullException(nameof(text))));

    /// <summary>One value a caller wrote: escaped, and cut at <see cref="LogSafe.MaxLength"/> (<see cref="LogSafe.Text"/>).</summary>
    public static SafeText Value(string value) => new(LogSafe.EscapedAndCut(value ?? throw new ArgumentNullException(nameof(value))));

    /// <summary>
    /// A line of the frame's words with values in it, as an interpolated string: its words escaped and not cut; each value
    /// formatted as the hole asks (invariant culture), then escaped and cut as <see cref="Value"/> is; and a
    /// <see cref="SafeText"/> in a hole kept as it is, being escaped already.
    /// </summary>
    public static SafeText Line(FormattableString text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(string.Format(Holes.Instance, LogSafe.Escaped(text.Format), text.GetArguments()));
    }

    /// <summary><paramref name="parts"/>, each escaped already, joined by <paramref name="separator"/>, escaped.</summary>
    public static SafeText Join(string separator, IEnumerable<SafeText> parts)
    {
        ArgumentNullException.ThrowIfNull(separator);
        ArgumentNullException.ThrowIfNull(parts);
        return new(string.Join(LogSafe.Escaped(separator), parts.Select(part => part._text)));
    }

    /// <summary>The escaped text.</summary>
    public override string ToString() => _text;

    /// <summary>Formats each value of a <see cref="Line"/>: a <see cref="SafeText"/> as it is, anything else escaped and cut.</summary>
    private sealed class Holes : IFormatProvider, ICustomFormatter
    {
        public static readonly Holes Instance = new();

        public object? GetFormat(Type? formatType) => formatType == typeof(ICustomFormatter) ? this : null;

        public string Format(string? format, object? arg, IFormatProvider? formatProvider) => arg switch
        {
            SafeText safe => safe._text,
            IFormattable formattable => LogSafe.EscapedAndCut(formattable.ToString(format, CultureInfo.InvariantCulture)),
            _ => LogSafe.EscapedAndCut(arg?.ToString() ?? string.Empty),
        };
    }
}
