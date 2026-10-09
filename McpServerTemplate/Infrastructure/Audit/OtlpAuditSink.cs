using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// A copy of every audit record, sent to a collector as OTLP log records over HTTP (contract-006 · G-11; roadmap P3.1, D6).
///
/// It is a copy and never the record. A record is on the trail once the file has it; this sink then queues it and a single
/// background sender delivers it in batches. A collector that is slow, down or refusing never delays a request and never
/// refuses one: what cannot be delivered is dropped, counted, and reported on the ordinary log, at most once a minute. The
/// queue is bounded, so an unreachable collector costs a fixed amount of memory and no more.
///
/// The body of each log record is the record's own line, byte for byte as the file holds it, with its hash, its prev_hash
/// and its number as attributes — so the copy can be checked against the file, and a gap in the numbers a collector holds
/// is a record that was dropped on the way or removed from the file.
///
/// OTLP/HTTP in its JSON encoding, written with the platform's own HTTP client: no package is added for it. Redirects are
/// not followed, so a collector cannot send audit records on to somewhere else.
/// </summary>
public sealed class OtlpAuditSink : IAuditSink, IDisposable
{
    /// <summary>How many records wait for the sender at most. Past it, the newest are dropped and counted.</summary>
    public const int QueueCapacity = 10_000;

    /// <summary>How many records go in one export.</summary>
    public const int BatchSize = 100;

    /// <summary>How long the sender lets a batch gather before it sends what it has.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long one export may take.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<AuditRecord> _queue;
    private readonly HttpClient _http;
    private readonly AuditOtlpSettings _settings;
    private readonly AuditIdentity _identity;
    private readonly string _appId;
    private readonly ILogger _logger;
    private readonly TimeSpan _flushInterval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private long _dropped;
    private long _unreported;
    private long _lastReport;

    /// <summary>Creates the sink and starts its sender.</summary>
    public OtlpAuditSink(AuditOtlpSettings settings, AuditIdentity identity, string appId, ILogger<OtlpAuditSink> logger)
        : this(settings, identity, appId, logger, new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, DefaultFlushInterval)
    {
    }

    /// <summary>Creates the sink over a handler of the caller's, for a test that stands in for the collector.</summary>
    public OtlpAuditSink(AuditOtlpSettings settings, AuditIdentity identity, string appId, ILogger logger, HttpMessageHandler handler, TimeSpan flushInterval)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(handler);

        _settings = settings;
        _identity = identity;
        _appId = appId;
        _logger = logger;
        _flushInterval = flushInterval;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = SendTimeout };
        _queue = Channel.CreateBounded<AuditRecord>(
            new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => Undelivered(1, "the queue of records waiting for the collector is full"));
        _sender = Task.Run(SendLoopAsync);
    }

    /// <inheritdoc />
    public bool Gating => false;

    /// <inheritdoc />
    public string Description => $"copy to {_settings.Endpoint.GetLeftPart(UriPartial.Authority)}";

    /// <summary>How many records were not delivered since the sink started.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public void Append(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Never blocks and never throws: a full queue drops the record through the channel's own callback.
        if (!_queue.Writer.TryWrite(record))
        {
            Undelivered(1, "the sink is closed");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // What is queued gets one last chance, briefly; the file has every record whatever happens here.
        _queue.Writer.TryComplete();
        try
        {
            _sender.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The sender ended on its own failure; it reported it.
        }

        _stop.Cancel();
        _http.Dispose();
        _stop.Dispose();
    }

    private async Task SendLoopAsync()
    {
        var batch = new List<AuditRecord>(BatchSize);
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                // Let a batch gather, unless the queue is closing: then send what there is at once.
                if (reader.Count < BatchSize && !reader.Completion.IsCompleted && _flushInterval > TimeSpan.Zero)
                {
                    await Task.Delay(_flushInterval, _stop.Token).ConfigureAwait(false);
                }

                batch.Clear();
                while (batch.Count < BatchSize && reader.TryRead(out var record))
                {
                    batch.Add(record);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    await SendAsync(batch, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }

                    Undelivered(batch.Count, ex is TaskCanceledException ? $"the collector did not answer within {SendTimeout.TotalSeconds:0} seconds" : ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private async Task SendAsync(List<AuditRecord> batch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint)
        {
            Content = new ByteArrayContent(Payload(batch)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in _settings.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"the collector answered {(int)response.StatusCode}");
        }
    }

    /// <summary>One OTLP/HTTP export of <paramref name="batch"/>, in the protocol's JSON encoding.</summary>
    private byte[] Payload(List<AuditRecord> batch)
    {
        var buffer = new ArrayBufferWriter<byte>(batch.Count * 1024);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("resourceLogs");
            json.WriteStartObject();

            json.WriteStartObject("resource");
            json.WriteStartArray("attributes");
            Attribute(json, "service.name", _appId);
            Attribute(json, "service.instance.id", _identity.Instance);
            Attribute(json, "host.name", _identity.Hostname);
            json.WriteEndArray();
            json.WriteEndObject();

            json.WriteStartArray("scopeLogs");
            json.WriteStartObject();
            json.WriteStartObject("scope");
            json.WriteString("name", "McpServerTemplate.Audit");
            json.WriteEndObject();

            json.WriteStartArray("logRecords");
            foreach (var record in batch)
            {
                var nanos = (record.At.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture);
                json.WriteStartObject();
                json.WriteString("timeUnixNano", nanos);
                json.WriteString("observedTimeUnixNano", nanos);
                json.WriteNumber("severityNumber", record.Event.Level switch { AuditLevel.Info => 9, AuditLevel.Warn => 13, _ => 21 });
                json.WriteString("severityText", AuditEvents.Word(record.Event.Level));
                json.WriteStartObject("body");
                json.WriteString("stringValue", Encoding.UTF8.GetString(record.Line.Span));
                json.WriteEndObject();

                json.WriteStartArray("attributes");
                Attribute(json, "event.name", record.Event.Name);
                Attribute(json, "audit.hash", record.Hash);
                Attribute(json, "audit.prev_hash", record.PrevHash);
                json.WriteStartObject();
                json.WriteString("key", "audit.seq");
                json.WriteStartObject("value");
                json.WriteString("intValue", record.Seq.ToString(CultureInfo.InvariantCulture));
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndArray();

                if (record.Event.TraceId is { Length: 32 } trace)
                {
                    json.WriteString("traceId", trace);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void Attribute(Utf8JsonWriter json, string key, string value)
    {
        json.WriteStartObject();
        json.WriteString("key", key);
        json.WriteStartObject("value");
        json.WriteString("stringValue", value);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>Counts what was not delivered, and says so on the ordinary log at most once a minute.</summary>
    private void Undelivered(int count, string reason)
    {
        Interlocked.Add(ref _dropped, count);
        var waiting = Interlocked.Add(ref _unreported, count);

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastReport);
        if ((last != 0 && now - last < (long)ReportInterval.TotalMilliseconds) || Interlocked.CompareExchange(ref _lastReport, now == 0 ? 1 : now, last) != last)
        {
            return;
        }

        Interlocked.Add(ref _unreported, -waiting);
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "The audit copy to {Collector} is not being delivered: {Count} records since the last report did not arrive — {Reason}. "
                + "Every record is in the audit file; the collector's copy has a gap in its running numbers",
                _settings.Endpoint.GetLeftPart(UriPartial.Authority), waiting, SafeText.Value(reason));
        }
    }
}
