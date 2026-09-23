using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// The confirmation round-trip in front of an Irreversible tool (contract-003 · G-7, roadmap §3.4).
///
/// The first call is answered with an input-required result: a question for the client, and an
/// opaque state the client must echo back. The state is a signed statement of who asked, for which
/// tool, with which arguments, and when. On the retry the frame checks the signature, that the
/// caller, tool and arguments are the same, that it is under 120 seconds old — and that it has not
/// been used before. That last check is kept in the limit store, because a signature alone cannot
/// tell a first use from a second, and an irreversible action performed twice on one confirmation
/// is the thing this exists to prevent.
///
/// What it does not prove: that a person answered. The server can refuse to act without a
/// confirmation; which of its users the client asks is the client's decision.
/// </summary>
public sealed class ConfirmationService
{
    /// <summary>How long a confirmation stays valid.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromSeconds(120);

    /// <summary>The key of the one question the frame asks.</summary>
    public const string InputKey = "confirm";

    /// <summary>The smallest key the frame will sign with.</summary>
    public const int MinimumKeyBytes = 32;

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service over a key of at least <see cref="MinimumKeyBytes"/> bytes.</summary>
    public ConfirmationService(byte[] key, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinimumKeyBytes)
        {
            throw new ConfigurationException(
                $"Confirmation:Key is {key.Length} bytes; it must be at least {MinimumKeyBytes}. "
                + "Generate one with 'openssl rand -base64 32' and keep it in a secret store.");
        }

        _key = key;
        _clock = clock;
    }

    /// <summary>A state for a caller, tool and arguments, signed now.</summary>
    public string Issue(Caller caller, string tool, IDictionary<string, JsonElement>? arguments)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var statement = new Statement(
            Guid.NewGuid().ToString("N"), caller.Key, tool, HashArguments(arguments), _clock.GetUtcNow().ToUnixTimeSeconds());
        var body = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(statement, StatementJson.Default.Statement));
        return body + "." + Base64Url.EncodeToString(Sign(body));
    }

    /// <summary>The question put to the client.</summary>
    public static InputRequest Question(string tool) => InputRequest.ForElicitation(new ElicitRequestParams
    {
        Message = $"The tool '{tool}' makes a change that cannot be undone. Confirm to run it once, with exactly the arguments given.",
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                [InputKey] = new ElicitRequestParams.BooleanSchema { Description = "Run it" },
            },
            Required = [InputKey],
        },
    });

    /// <summary>
    /// Checks a retry. Returns the statement's id to claim once, or a refusal saying what was wrong.
    /// </summary>
    public (string? Id, Refusal? Refusal) Verify(
        string? state, IDictionary<string, InputResponse>? responses, Caller caller, string tool, IDictionary<string, JsonElement>? arguments)
    {
        ArgumentNullException.ThrowIfNull(caller);

        static (string?, Refusal?) Refuse(string why) =>
            (null, Refusal.Of("confirmation", "authz_fail", why + " Call the tool again without a confirmation to be asked afresh."));

        var parts = state?.Split('.') ?? [];
        if (parts.Length != 2)
        {
            return Refuse("The confirmation is missing or malformed.");
        }

        byte[] signature;
        byte[] body;
        try
        {
            signature = Base64Url.DecodeFromChars(parts[1]);
            body = Base64Url.DecodeFromChars(parts[0]);
        }
        catch (FormatException)
        {
            return Refuse("The confirmation is malformed.");
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))
        {
            return Refuse("The confirmation was not issued by this server, or has been altered.");
        }

        Statement? statement;
        try
        {
            statement = JsonSerializer.Deserialize(body, StatementJson.Default.Statement);
        }
        catch (JsonException)
        {
            return Refuse("The confirmation is malformed.");
        }

        if (statement is null || statement.Principal != caller.Key || statement.Tool != tool)
        {
            return Refuse("The confirmation was issued to another caller or for another tool.");
        }

        if (statement.ArgumentsHash != HashArguments(arguments))
        {
            return Refuse("The confirmation was given for different arguments.");
        }

        var age = _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(statement.IssuedAt);
        if (age > Validity || age < TimeSpan.FromSeconds(-30))
        {
            return Refuse($"The confirmation is {age.TotalSeconds:0} seconds old; it is valid for {Validity.TotalSeconds:0}.");
        }

        if (responses is null || !responses.TryGetValue(InputKey, out var response) ||
            response.Deserialize(InputResponse.ElicitResultJsonTypeInfo) is not { IsAccepted: true } answer ||
            answer.Content is null || !answer.Content.TryGetValue(InputKey, out var confirmed) ||
            confirmed.ValueKind != JsonValueKind.True)
        {
            return Refuse("The confirmation was declined or not answered.");
        }

        return (statement.Id, null);
    }

    private byte[] Sign(string body) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(body));

    private static string HashArguments(IDictionary<string, JsonElement>? arguments)
    {
        var canonical = new StringBuilder();
        foreach (var (name, value) in (arguments ?? new Dictionary<string, JsonElement>()).OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            canonical.Append(name).Append('=').Append(JsonSerializer.Serialize(value, StatementJson.Default.JsonElement)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    internal sealed record Statement(string Id, string Principal, string Tool, string ArgumentsHash, long IssuedAt);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(ConfirmationService.Statement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class StatementJson : System.Text.Json.Serialization.JsonSerializerContext;
