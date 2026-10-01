using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// contract-005 · G-17 round 2 (SEC-R2-03) — the server's log pipeline, as its settings describe it, with every event made
/// one line per line the output template writes, whatever a caller put into it.
///
/// The frame cleans what it writes itself (<see cref="LogSafe.Text"/>), and that cannot reach the rest: the SDK's own lines
/// name the JSON-RPC method, the tool, the resource, the prompt and the client as the caller wrote them, and an exception's
/// text is rendered whole under its line ({Exception}). Written as they came, a line break forged the next line — an
/// authn_login_fail for another identity provider, say — an escape sequence rewrote the operator's terminal, and a
/// direction override showed the line other than it was. So the neutralising is done once, where lines are written: every
/// sink the settings name (Serilog:WriteTo, and Serilog:AuditTo) is given each event through <see cref="LineSafeSink"/>,
/// which escapes every control, format, line- and paragraph-separator character in the message template's text, in every
/// property's value, and in the exception's text (<see cref="LogSafe.Escaped"/>). What is left to break a line is the output
/// template's own newline: one line for the message, and one for an exception.
///
/// The settings are read as they always were, split in two. The logger the host logs to gets everything but the sinks:
/// its minimum levels and their overrides, which decide what a component asks to be logged at all, its enrichers, filters,
/// properties and destructuring. The sinks, with the assemblies they come from (Using) and the level switches they may
/// name (LevelSwitches), get a logger of their own that passes each event it is given straight to them. Every other setting
/// is read by both as it is.
/// </summary>
public static class LineSafeLogging
{
    /// <summary>The section Serilog's settings are read from.</summary>
    private const string Section = "Serilog";

    /// <summary>The parts of that section that name where lines are written: sinks, and audit sinks.</summary>
    private static readonly string[] SinkParts = ["WriteTo", "AuditTo"];

    /// <summary>The parts both loggers read: the assemblies sinks and enrichers come from, and the level switches either may name.</summary>
    private static readonly string[] SharedParts = ["Using", "LevelSwitches"];

    /// <summary>
    /// Reads Serilog's settings from <paramref name="configuration"/> into <paramref name="logging"/>, as
    /// <c>ReadFrom.Configuration</c> does, but with every sink given each event through a <see cref="LineSafeSink"/>.
    /// </summary>
    /// <param name="logging">The logger configuration the host logs through.</param>
    /// <param name="configuration">The server's settings.</param>
    public static LoggerConfiguration ReadFromConfigurationLineSafe(this LoggerConfiguration logging, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(configuration);

        logging.ReadFrom.Configuration(Keeping(configuration, part => !SinkParts.Contains(part, StringComparer.OrdinalIgnoreCase)));

        // contract-005 · G-17 round 3 (SEC-R3-04) — the frame's own values arrive as SafeText, escaped already: kept as they are,
        // not turned into the string they print as, so the line-safe stage knows them and does not escape their escapes again.
        logging.Destructure.AsScalar<SafeText>();

        if (SinksNamedIn(configuration, "WriteTo") is { } writeTo)
        {
            logging.WriteTo.Sink(new LineSafeSink(writeTo));
        }

        // Audit sinks fail their caller when they cannot write; given through an audit sink of this logger, they still do.
        if (SinksNamedIn(configuration, "AuditTo") is { } auditTo)
        {
            logging.AuditTo.Sink(new LineSafeSink(auditTo));
        }

        return logging;
    }

    /// <summary>
    /// A logger holding the sinks <c>Serilog:{part}</c> names and nothing else, which passes every event it is given to them;
    /// null when the part names none.
    /// </summary>
    private static Logger? SinksNamedIn(IConfiguration configuration, string part)
    {
        if (!configuration.GetSection($"{Section}:{part}").GetChildren().Any())
        {
            return null;
        }

        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .ReadFrom.Configuration(Keeping(configuration, kept =>
                kept.Equals(part, StringComparison.OrdinalIgnoreCase) || SharedParts.Contains(kept, StringComparer.OrdinalIgnoreCase)))
            .CreateLogger();
    }

    /// <summary>
    /// <paramref name="configuration"/>'s settings, read once into memory, with only the parts of Serilog's section that
    /// <paramref name="keep"/> keeps, by the name of the part (MinimumLevel, WriteTo, …); every other setting as it is, so a
    /// sink or enricher that reads the configuration it is given finds it.
    /// </summary>
    private static IConfiguration Keeping(IConfiguration configuration, Func<string, bool> keep)
    {
        var kept = configuration.AsEnumerable().Where(setting => setting.Value is not null && Keeps(setting.Key, keep));
        return new ConfigurationBuilder().AddInMemoryCollection(kept).Build();
    }

    private static bool Keeps(string key, Func<string, bool> keep)
    {
        var segments = key.Split(ConfigurationPath.KeyDelimiter);
        return segments.Length < 2 || !segments[0].Equals(Section, StringComparison.OrdinalIgnoreCase) || keep(segments[1]);
    }
}

/// <summary>
/// contract-005 · G-17 round 2 (SEC-R2-03) — a sink that writes each event to the sinks it holds with every character that
/// can break or disguise a line escaped (<see cref="LogSafe.Escaped"/>): in the message template's text, in every property's
/// value, however deep, and in the exception's text. An event with nothing to escape is passed on as it is.
/// </summary>
public sealed class LineSafeSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private readonly Logger _sinks;
    private int _disposed;

    /// <summary>Writes to <paramref name="sinks"/>, a logger that passes every event it is given to its sinks.</summary>
    public LineSafeSink(Logger sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent) => _sinks.Write(LineSafe(logEvent));

    /// <summary>Disposes the sinks, which flushes them.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _sinks.Dispose();
        }
    }

    /// <summary>Disposes the sinks, which flushes them.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _sinks.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <paramref name="logEvent"/> with every character that can break or disguise a line escaped: its message template's
    /// text, every property's value, and its exception's text. The same event when there is nothing to escape.
    /// </summary>
    public static LogEvent LineSafe(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var template = LineSafe(logEvent.MessageTemplate);

        Dictionary<string, LogEventPropertyValue>? properties = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            var safe = LineSafe(value);
            if (!ReferenceEquals(safe, value))
            {
                properties ??= new Dictionary<string, LogEventPropertyValue>(logEvent.Properties);
                properties[name] = safe;
            }
        }

        var exception = logEvent.Exception is { } thrown ? LineSafeException.For(thrown) : null;

        if (ReferenceEquals(template, logEvent.MessageTemplate) && properties is null && ReferenceEquals(exception, logEvent.Exception))
        {
            return logEvent;
        }

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception,
            template,
            (properties ?? logEvent.Properties).Select(property => new LogEventProperty(property.Key, property.Value)),
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
    }

    /// <summary>
    /// The template with its text escaped, token by token; the same template when there is nothing to escape. contract-005 ·
    /// G-17 round 3 (SEC-R3-07) — a backslash in it counts, so its text is escaped whole or not at all, and reads back.
    /// </summary>
    private static MessageTemplate LineSafe(MessageTemplate template)
    {
        if (!LogSafe.NeedsEscaping(template.Text))
        {
            return template;
        }

        return new MessageTemplate(
            LogSafe.Escaped(template.Text),
            [.. template.Tokens.Select(token => token is TextToken text ? new TextToken(LogSafe.Escaped(text.Text)) : token)]);
    }

    /// <summary>
    /// A property's value with every string in it escaped, however deep; a scalar that is not a string is rendered by its own
    /// text, so it is replaced by that text, escaped, only where that text would break a line. The same value when there is
    /// nothing to escape.
    /// </summary>
    private static LogEventPropertyValue LineSafe(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: null or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Enum }:
                return value;

            // contract-005 · G-17 round 3 (SEC-R3-04) — a value the frame escaped itself: nothing but escaping makes a SafeText,
            // so it holds nothing that breaks a line, and escaped again its escapes would read as text the caller wrote.
            // contract-005 · G-17 round 4 (SEC-R4-02) — passed on as the string it holds: a sink renders any other value as
            // JSON, quoted and with every backslash escaped a second time, which the File sink did under {Message:lj}.
            case ScalarValue { Value: SafeText safe }:
                return new ScalarValue(safe.ToString());

            case ScalarValue { Value: string text }:
                var escaped = LogSafe.Escaped(text);
                return ReferenceEquals(escaped, text) ? value : new ScalarValue(escaped);

            case ScalarValue { Value: { } other }:
                var shown = other.ToString() ?? string.Empty;
                return LogSafe.NeedsEscaping(shown) ? new ScalarValue(LogSafe.Escaped(shown)) : value;

            case SequenceValue sequence:
                var elements = sequence.Elements.Select(LineSafe).ToArray();
                return elements.Where((element, i) => !ReferenceEquals(element, sequence.Elements[i])).Any()
                    ? new SequenceValue(elements)
                    : value;

            case StructureValue structure:
                var members = structure.Properties.Select(p => new LogEventProperty(p.Name, LineSafe(p.Value))).ToArray();
                return members.Where((member, i) => !ReferenceEquals(member.Value, structure.Properties[i].Value)).Any()
                    ? new StructureValue(members, structure.TypeTag)
                    : value;

            case DictionaryValue dictionary:
                var entries = dictionary.Elements
                    .Select(entry => new KeyValuePair<ScalarValue, LogEventPropertyValue>((ScalarValue)LineSafe(entry.Key), LineSafe(entry.Value)))
                    .ToArray();
                return entries.Zip(dictionary.Elements).Any(pair => !ReferenceEquals(pair.First.Key, pair.Second.Key) || !ReferenceEquals(pair.First.Value, pair.Second.Value))
                    ? new DictionaryValue(entries)
                    : value;

            default:
                return value;
        }
    }

    /// <summary>
    /// An exception as a line writes it: its whole text — type, message, inner exceptions, stack — escaped, on one line.
    /// Only its text is kept, and nothing of the exception it stands for, so no sink can render the original's text around it.
    /// </summary>
    private sealed class LineSafeException : Exception
    {
        private readonly string _text;

        private LineSafeException(string message, string text)
            : base(message) => _text = text;

        /// <summary>
        /// The escaped text of <paramref name="exception"/>, or <paramref name="exception"/> itself when there is nothing to
        /// escape. contract-005 · G-17 round 3 (SEC-R3-07) — a backslash counts, so the text is escaped whole or not at all.
        /// </summary>
        public static Exception For(Exception exception)
        {
            var text = exception.ToString();
            return LogSafe.NeedsEscaping(text) ? new LineSafeException(LogSafe.Escaped(exception.Message), LogSafe.Escaped(text)) : exception;
        }

        public override string ToString() => _text;
    }
}
