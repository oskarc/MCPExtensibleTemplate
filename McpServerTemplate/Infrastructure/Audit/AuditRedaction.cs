using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// What is taken out of a record before any destination sees it (contract-006 · G-6; roadmap P3.3, I8).
///
/// An argument its tool's policy marks sensitive becomes a tag carrying eight hex characters of its SHA-256, enough to tell
/// two values apart and not enough to find one. A value shaped like a credential — a JSON web token, a Bearer header, an AWS
/// access key id, an sk- key — is replaced the same way wherever it sits, whoever declared what: a caller pastes a token into
/// the wrong argument, and the trail must not keep it. A long string is cut and carries the hash of the whole. And the
/// arguments of one record have a size of their own, since a request body may be a megabyte and a record should not be.
/// </summary>
internal static partial class AuditRedaction
{
    /// <summary>A string argument longer than this, in UTF-8 bytes, is cut (roadmap P3.3: 2 KB).</summary>
    public const int MaxStringBytes = 2048;

    /// <summary>One record's arguments, redacted, are at most this many bytes; past it only their size and hash are kept.</summary>
    public const int MaxArgumentsBytes = 16 * 1024;

    /// <summary>A caller-written name or header is written at most this long.</summary>
    public const int MaxNameLength = 512;

    /// <summary>A description is written at most this long. Each caller's value in it is cut at 200 already (LogSafe).</summary>
    public const int MaxDescriptionLength = 2000;

    private const int MaxDepth = 32;

    /// <summary>
    /// The shapes of a credential (roadmap P3.3): a JSON web token, a Bearer header's value, an AWS access key id, an sk- key.
    /// No backtracking: the input is a caller's, and as long as a request body may be.
    /// </summary>
    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}|[Bb][Ee][Aa][Rr][Ee][Rr]\s+[A-Za-z0-9._~+/=-]{8,}|\bAKIA[0-9A-Z]{16}\b|\bsk-[A-Za-z0-9_-]{16,}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    /// <summary>Whether <paramref name="value"/> holds something shaped like a credential.</summary>
    public static bool LooksSecret(string value) => Secret().IsMatch(value);

    /// <summary>
    /// <paramref name="value"/> as free text may be written: anything shaped like a credential replaced, and the whole cut at
    /// <paramref name="maxLength"/> characters. For the fields a caller writes or influences that are not arguments: the
    /// description, the method, the target, the user agent, the path.
    /// </summary>
    public static string? Text(string? value, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var scrubbed = Secret().Replace(value, "[redacted]");
        if (scrubbed.Length <= maxLength)
        {
            return scrubbed;
        }

        // Never between the halves of a surrogate pair: half of one is not a character, and JSON would write it as an escape.
        var cut = char.IsHighSurrogate(scrubbed[maxLength - 1]) ? maxLength - 1 : maxLength;
        return string.Concat(scrubbed.AsSpan(0, cut), "…");
    }

    /// <summary>The tag a redacted value is replaced by: <c>[redacted:sha256:</c> and eight hex characters of its hash.</summary>
    public static string Tag(ReadOnlySpan<byte> utf8) =>
        string.Create(CultureInfo.InvariantCulture, $"[redacted:sha256:{Convert.ToHexStringLower(SHA256.HashData(utf8))[..8]}]");

    /// <summary>Writes <paramref name="arguments"/> as the record's <c>args</c> value, redacted.</summary>
    public static void WriteArguments(Utf8JsonWriter writer, IDictionary<string, JsonElement> arguments, IReadOnlyCollection<string>? sensitive)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var inner = new Utf8JsonWriter(buffer))
        {
            inner.WriteStartObject();

            // In name order, so two records of the same arguments read the same.
            foreach (var (name, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                inner.WritePropertyName(Name(name));
                if (sensitive is not null && sensitive.Contains(name, StringComparer.Ordinal))
                {
                    inner.WriteStringValue(Tag(Encoding.UTF8.GetBytes(value.GetRawText())));
                }
                else
                {
                    WriteValue(inner, value, depth: 0);
                }
            }

            inner.WriteEndObject();
        }

        if (buffer.WrittenCount <= MaxArgumentsBytes)
        {
            writer.WriteRawValue(buffer.WrittenSpan, skipInputValidation: true);
            return;
        }

        // Too large for one record: what is kept says so, how large they were once redacted, and their hash in that form.
        writer.WriteStartObject();
        writer.WriteBoolean("_truncated", true);
        writer.WriteNumber("bytes", buffer.WrittenCount);
        writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan)));
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(writer, value.GetString() ?? string.Empty);
                break;

            case JsonValueKind.Object when depth < MaxDepth:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(Name(property.Name));
                    WriteValue(writer, property.Value, depth + 1);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array when depth < MaxDepth:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteValue(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.Object or JsonValueKind.Array:
                // Deeper than any tool's schema goes: what is there is not read, only marked.
                writer.WriteStringValue(Tag(Encoding.UTF8.GetBytes(value.GetRawText())));
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteString(Utf8JsonWriter writer, string value)
    {
        if (LooksSecret(value))
        {
            writer.WriteStringValue(Tag(Encoding.UTF8.GetBytes(value)));
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes <= MaxStringBytes)
        {
            writer.WriteStringValue(value);
            return;
        }

        // The longest beginning that fits, whole characters only, then what was cut: how long it was and the hash of all of it.
        var kept = 0;
        var size = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (size + rune.Utf8SequenceLength > MaxStringBytes)
            {
                break;
            }

            size += rune.Utf8SequenceLength;
            kept += rune.Utf16SequenceLength;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        writer.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"{value.AsSpan(0, kept)}…[truncated:{bytes} bytes:sha256:{hash}]"));
    }

    /// <summary>An argument's name as it is written: a name shaped like a credential is one too.</summary>
    private static string Name(string name) => LooksSecret(name) ? Tag(Encoding.UTF8.GetBytes(name)) : name;
}
