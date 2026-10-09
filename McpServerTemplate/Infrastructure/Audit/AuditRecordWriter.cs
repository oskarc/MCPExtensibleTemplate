using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>Where and when a record was written, and its place in the chain.</summary>
/// <param name="At">When.</param>
/// <param name="AppId">The application id (roadmap LOG-02).</param>
/// <param name="Hostname">The machine's name.</param>
/// <param name="Instance">This server instance: one chain per instance.</param>
/// <param name="Seq">The record's number in its chain, from 1.</param>
/// <param name="PrevHash">The hash of the record before; <see cref="AuditRecordWriter.Genesis"/> for the first.</param>
internal readonly record struct AuditStamp(DateTimeOffset At, string AppId, string Hostname, string Instance, long Seq, string PrevHash);

/// <summary>
/// The one writer of an audit record (contract-006 · G-3, G-5, G-6): one JSON object on one line, its members in a fixed
/// order, the roadmap's names (§3.5) with a running number and the instance beside them, and the two hashes last.
///
/// The hash is the SHA-256 of the line as written without its hash member — which holds prev_hash, so each record is bound to
/// the one before it. The check reads the same bytes back and re-serialises nothing (<see cref="AuditVerifier"/>): a check
/// that rebuilt the JSON to compare would pass or fail on how two serialisers agree, not on what is in the file.
///
/// JSON encoding is the only escaping. The default encoder writes every control character, every character outside ASCII
/// and the line and paragraph separators as an escape, so nothing a caller sends can end the line or start another.
/// </summary>
internal static class AuditRecordWriter
{
    /// <summary>The prev_hash of a chain's first record.</summary>
    public static readonly string Genesis = new('0', 64);

    private static readonly byte[] HashMember = ",\"hash\":\""u8.ToArray();

    /// <summary>The length of a record's ending: <c>,"hash":"</c>, 64 hex characters, <c>"}</c>.</summary>
    public static readonly int HashSuffixLength = HashMember.Length + 64 + 2;

    /// <summary>Writes <paramref name="audit"/> as its line, without a line end, and returns it with its hash.</summary>
    public static (byte[] Line, string Hash) Write(AuditEvent audit, AuditStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(audit);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("datetime", stamp.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            json.WriteString("appid", stamp.AppId);
            json.WriteString("event", EventText(audit));
            json.WriteString("level", AuditEvents.Word(audit.Level));
            json.WriteString("description", AuditRedaction.Text(audit.Description, AuditRedaction.MaxDescriptionLength));
            json.WriteString("trace_id", audit.TraceId);
            json.WriteString("request_id", audit.RequestId ?? audit.Http?.RequestId);

            if (audit.Principal is { } principal)
            {
                json.WriteStartObject("principal");
                json.WriteString("idp", principal.IdentityProvider);
                json.WriteString("sub", AuditRedaction.Text(principal.Subject));
                json.WriteString("client_id", AuditRedaction.Text(principal.ClientId));
                json.WriteString("jti_sha256", principal.JtiSha256);
                json.WriteStartArray("scopes");
                foreach (var scope in principal.Scopes)
                {
                    json.WriteStringValue(AuditRedaction.Text(scope));
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("principal");
            }

            json.WriteString("source_ip", audit.Http?.SourceIp);
            json.WriteString("hostname", stamp.Hostname);
            json.WriteString("request_uri", AuditRedaction.Text(audit.Http?.RequestUri));
            json.WriteString("request_method", AuditRedaction.Text(audit.Http?.RequestMethod));
            json.WriteString("useragent", AuditRedaction.Text(audit.Http?.UserAgent));

            if (audit.Mcp is { } mcp)
            {
                var target = AuditRedaction.Text(mcp.Target);
                json.WriteStartObject("mcp");
                json.WriteString("method", AuditRedaction.Text(mcp.Method));
                json.WriteString("provider", mcp.Provider);
                json.WriteString("kind", mcp.Kind);
                json.WriteString("target", target);
                json.WriteString("tool", mcp.Kind == "tool" ? target : null);
                json.WriteString("risk", mcp.Risk);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("mcp");
            }

            if (audit.Arguments is { } arguments)
            {
                json.WritePropertyName("args");
                AuditRedaction.WriteArguments(json, arguments, audit.SensitiveArguments);
            }
            else
            {
                json.WriteNull("args");
            }

            if (audit.Decision is { } decision)
            {
                json.WriteStartObject("decision");
                json.WriteString("outcome", AuditEvents.Word(decision.Outcome));
                json.WriteString("rule", decision.Rule);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("decision");
            }

            if (audit.Upstream is { } upstream)
            {
                json.WriteStartObject("upstream");
                json.WriteString("host", AuditRedaction.Text(upstream.Host));
                json.WriteString("method", AuditRedaction.Text(upstream.Method));
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("upstream");
            }

            if (audit.Detail is { } detail)
            {
                json.WriteStartObject("detail");
                foreach (var (name, value) in detail.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    json.WriteString(name, AuditRedaction.Text(value, AuditRedaction.MaxDescriptionLength));
                }

                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("detail");
            }

            json.WriteString("instance", stamp.Instance);
            json.WriteNumber("seq", stamp.Seq);
            json.WriteString("prev_hash", stamp.PrevHash);
            json.WriteEndObject();
        }

        // What was written is the record without its hash, closed: {…,"prev_hash":"…"}. Its hash goes in before that brace.
        var body = buffer.WrittenSpan;
        var hash = HashOf(body);
        var line = new byte[body.Length - 1 + HashSuffixLength];
        body[..^1].CopyTo(line);
        HashMember.CopyTo(line.AsSpan(body.Length - 1));
        Encoding.ASCII.GetBytes(hash, line.AsSpan(body.Length - 1 + HashMember.Length));
        line[^2] = (byte)'"';
        line[^1] = (byte)'}';
        return (line, hash);
    }

    /// <summary>The hash of a record's body: the line without its hash member, closed.</summary>
    public static string HashOf(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>
    /// Takes a line apart into the body its hash was computed over and the hash it carries; false when the line does not end
    /// as a record does.
    /// </summary>
    public static bool TrySplit(ReadOnlySpan<byte> line, out byte[] body, out string hash)
    {
        body = [];
        hash = string.Empty;
        if (line.Length <= HashSuffixLength || line[^1] != (byte)'}' || line[^2] != (byte)'"')
        {
            return false;
        }

        var suffix = line[^HashSuffixLength..];
        if (!suffix.StartsWith(HashMember))
        {
            return false;
        }

        var hex = suffix.Slice(HashMember.Length, 64);
        foreach (var c in hex)
        {
            if (c is not ((>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f')))
            {
                return false;
            }
        }

        hash = Encoding.ASCII.GetString(hex);
        body = new byte[line.Length - HashSuffixLength + 1];
        line[..^HashSuffixLength].CopyTo(body);
        body[^1] = (byte)'}';
        return true;
    }

    /// <summary>
    /// The event as the OWASP vocabulary writes one: its name, then who and what where they are known —
    /// <c>authz_fail:sub=…,resource=…</c> (roadmap §3.5). The name is always the text before the first colon.
    /// </summary>
    private static string EventText(AuditEvent audit)
    {
        var who = audit.Principal is { } principal ? "sub=" + AuditRedaction.Text(principal.Subject) : null;
        var what = audit.Mcp?.Target is { Length: > 0 } target ? "resource=" + AuditRedaction.Text(target) : null;
        return (who, what) switch
        {
            (null, null) => audit.Name,
            (not null, null) => $"{audit.Name}:{who}",
            (null, not null) => $"{audit.Name}:{what}",
            _ => $"{audit.Name}:{who},{what}",
        };
    }
}
