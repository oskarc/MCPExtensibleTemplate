using System.Text.Json;
using Json.Schema;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// Checks a tool call's arguments against the shape the tool declares, before the tool runs
/// (contract-003 · G-6): no names the tool does not declare, values that fit its schema, strings
/// within the policy's length, numbers that are numbers.
///
/// It checks shape, not meaning. A tool that accepts any whole number as an id gets any whole
/// number; whether that id makes sense is the provider's to decide.
/// </summary>
public sealed class ArgumentGuard
{
    private readonly JsonSchema _schema;
    private readonly IReadOnlySet<string> _declared;
    private readonly IReadOnlySet<string> _numeric;
    private readonly int _maxStringLength;

    private ArgumentGuard(JsonSchema schema, IReadOnlySet<string> declared, IReadOnlySet<string> numeric, int maxStringLength)
    {
        _schema = schema;
        _declared = declared;
        _numeric = numeric;
        _maxStringLength = maxStringLength;
    }

    /// <summary>Builds the guard from a tool's declared input schema.</summary>
    public static ArgumentGuard For(JsonElement inputSchema, int maxStringLength)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var numeric = new HashSet<string>(StringComparer.Ordinal);
        if (inputSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                declared.Add(property.Name);
                if (property.Value.TryGetProperty("type", out var type) && DeclaresNumber(type))
                {
                    numeric.Add(property.Name);
                }
            }
        }

        // A fresh build context per schema: each tool's schema is its own document.
        var schema = JsonSchema.Build(inputSchema, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        return new ArgumentGuard(schema, declared, numeric, maxStringLength);
    }

    /// <summary>Returns a refusal, or null when the arguments fit.</summary>
    public Refusal? Check(IDictionary<string, JsonElement>? arguments)
    {
        arguments ??= new Dictionary<string, JsonElement>();

        var undeclared = arguments.Keys.Where(k => !_declared.Contains(k)).Order(StringComparer.Ordinal).ToArray();
        if (undeclared.Length > 0)
        {
            return Refusal.Of(
                "extraneous-argument",
                "malicious_extraneous",
                $"The tool does not declare {string.Join(", ", undeclared)}. Send only the arguments in its input schema.");
        }

        foreach (var (name, value) in arguments)
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > _maxStringLength)
            {
                return Refusal.Of(
                    "argument-length",
                    "input_validation_fail",
                    $"The argument '{name}' is longer than the {_maxStringLength} characters this tool accepts.");
            }

            // JSON cannot carry NaN or infinity, but a string can, and the SDK's number binding
            // accepts "NaN" for a double; a coordinate of NaN then passes every range check.
            if (value.ValueKind == JsonValueKind.String &&
                _numeric.Contains(name) &&
                !double.IsFinite(double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN))
            {
                return Refusal.Of(
                    "not-a-number",
                    "input_validation_fail",
                    $"The argument '{name}' must be a finite number.");
            }
        }

        using var document = JsonSerializer.SerializeToDocument(arguments, ArgumentJson.Default.IDictionaryStringJsonElement);
        var result = _schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
        {
            var reasons = (result.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .Take(3);
            return Refusal.Of(
                "argument-schema",
                "input_validation_fail",
                "The arguments do not match the tool's input schema: " + string.Join("; ", reasons) + ".");
        }

        return null;
    }

    private static bool DeclaresNumber(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => type.GetString() is "number" or "integer",
        JsonValueKind.Array => type.EnumerateArray().Any(t => t.GetString() is "number" or "integer"),
        _ => false,
    };
}

[System.Text.Json.Serialization.JsonSerializable(typeof(IDictionary<string, JsonElement>))]
internal sealed partial class ArgumentJson : System.Text.Json.Serialization.JsonSerializerContext;
