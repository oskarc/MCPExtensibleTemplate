using System.Globalization;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// The audit trail: every decision the frame makes, and every moment of the server's own life, written as one record
/// before it takes effect (contract-006 · G-2, G-4; roadmap I5, I7).
///
/// The trail owns the chain — the running number and the hash of the record before — so every destination gets the same
/// line. A record counts as written once every gating sink has it on disk. When one cannot take it, <see cref="Record"/>
/// returns false and nothing of the chain moves: the caller decides what an unrecorded decision means, and for the frame
/// that is a refusal (RequestGate). Nothing here remembers that the trail was down as a reason not to try: every record is
/// tried, so the trail comes back by itself, and the first thing it writes then is how many records it missed.
/// </summary>
public sealed class AuditTrail : IDisposable
{
    private readonly Lock _gate = new();
    private readonly IAuditSink[] _gating;
    private readonly IAuditSink[] _copies;
    private readonly AuditSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditTrail> _logger;
    private long _seq;
    private string _prevHash = AuditRecordWriter.Genesis;
    private long _missed;
    private DateTimeOffset _downSince;

    /// <summary>Creates the trail over <paramref name="sinks"/>. Nothing is written until the first record.</summary>
    public AuditTrail(IEnumerable<IAuditSink> sinks, AuditSettings settings, AuditIdentity identity, TimeProvider clock, ILogger<AuditTrail> logger)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var all = sinks.ToArray();
        _gating = [.. all.Where(s => s.Gating)];
        _copies = [.. all.Where(s => !s.Gating)];
        _settings = settings;
        _clock = clock;
        _logger = logger;
        Identity = identity;
    }

    /// <summary>Which instance writes this chain.</summary>
    public AuditIdentity Identity { get; }

    /// <summary>Whether a request that changes nothing is served while the trail cannot be written (<c>Audit:ReadWhenUnavailable</c>).</summary>
    public bool ReadWhenUnavailable => _settings.ReadWhenUnavailable;

    /// <summary>Where the records go, for the startup line.</summary>
    public string Description => string.Join(" + ", _gating.Concat(_copies).Select(s => s.Description));

    /// <summary>
    /// Writes one record. True once it is durable; false when it could not be written, in which case nothing ran on the
    /// strength of it yet and the caller must not let anything.
    /// </summary>
    public bool Record(AuditEvent audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        lock (_gate)
        {
            if (_gating.Length == 0)
            {
                // A trail with nowhere to write records nothing, and says so every time: never a silent success.
                return Missed(audit, "no audit sink is installed");
            }

            // Back after an outage: the gap goes on the trail first, so the chain itself shows where records are missing.
            if (_missed > 0 && !TryAppend(Recovered(), out var stillDown))
            {
                return Missed(audit, stillDown);
            }

            if (!TryAppend(audit, out var reason))
            {
                return Missed(audit, reason);
            }

            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var sink in _gating.Concat(_copies).OfType<IDisposable>())
        {
            sink.Dispose();
        }
    }

    private bool TryAppend(AuditEvent audit, out string reason)
    {
        var now = _clock.GetUtcNow();
        var stamp = new AuditStamp(now, _settings.AppId, Identity.Hostname, Identity.Instance, _seq + 1, _prevHash);
        var (line, hash) = AuditRecordWriter.Write(audit, stamp);
        var record = new AuditRecord(audit, now, stamp.Seq, stamp.PrevHash, hash, line);

        try
        {
            foreach (var sink in _gating)
            {
                sink.Append(record);
            }
        }
        catch (Exception ex)
        {
            // Whatever the sink threw, the record is not on the trail. The chain stays where it was.
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        _seq = stamp.Seq;
        _prevHash = hash;

        if (_missed > 0)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "The audit trail is writing again after {Missed} unrecorded events since {Since}; the gap is on the trail as sys_monitor_disabled",
                    _missed, _downSince.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
            }

            _missed = 0;
        }

        foreach (var copy in _copies)
        {
            try
            {
                copy.Append(record);
            }
            catch (Exception ex)
            {
                // A copy never gates: a sink that throws here is one that broke its own promise not to.
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(ex, "An audit copy ({Sink}) threw; the record is on the trail and the copy was skipped", copy.Description);
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool Missed(AuditEvent audit, string reason)
    {
        if (_missed == 0)
        {
            _downSince = _clock.GetUtcNow();
            if (_logger.IsEnabled(LogLevel.Critical))
            {
                // The one place a stopped trail can still be said: the ordinary log. Named as the vocabulary names it.
                _logger.LogCritical(
                    "sys_monitor_disabled: the audit trail cannot be written — {Reason}. A Write or Irreversible tool is refused until it "
                    + "can; every other request is {Others}",
                    SafeText.Value(reason), _settings.ReadWhenUnavailable ? "served unrecorded (Audit:ReadWhenUnavailable=allow)" : "refused too");
            }
        }

        _missed++;
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "audit record not written: event={Event} rule={Rule} principal={Principal} target={Target}",
                audit.Name,
                audit.Decision?.Rule ?? "-",
                SafeText.Value(audit.Principal?.Key ?? "anonymous"),
                SafeText.Value(audit.Mcp?.Target ?? "-"));
        }

        return false;
    }

    private AuditEvent Recovered() => AuditEvent.System(
        AuditEvents.SysMonitorDisabled,
        string.Create(
            CultureInfo.InvariantCulture,
            $"The audit trail could not be written from {_downSince.UtcDateTime:o}: {_missed} events were not recorded. It is writing again."),
        new Dictionary<string, string?>
        {
            ["missed"] = _missed.ToString(CultureInfo.InvariantCulture),
            ["since"] = _downSince.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        });
}
