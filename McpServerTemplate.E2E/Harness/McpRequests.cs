using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpServerTemplate.E2E.Harness;

/// <summary>Raw MCP requests, for the tests that need to see the HTTP answer rather than what a client makes of it.</summary>
public static class McpRequests
{
    /// <summary>The protocol revision a raw request declares: one without the input-request round-trip.</summary>
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>An initialize request to <paramref name="endpoint"/>, bearing <paramref name="token"/> when one is given.</summary>
    public static HttpRequestMessage Initialize(Uri endpoint, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>
    /// contract-005 · T-2, T-5, T-8 — one JSON-RPC request, as a client that does not use the SDK sends it: the
    /// stateless transport takes a request on its own, with the protocol revision in a header.
    /// </summary>
    public static HttpRequestMessage Rpc(Uri endpoint, string token, string method, object? parameters = null) =>
        Send(
            endpoint,
            token,
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = method,
                ["params"] = JsonSerializer.SerializeToNode(parameters ?? new { }),
            },
            new Dictionary<string, string> { ["MCP-Protocol-Version"] = ProtocolVersion });

    /// <summary>
    /// contract-005 · T-8 — a request body sent as it is, with <paramref name="headers"/>: how a captured request is
    /// sent again, changed or not, under another token.
    /// </summary>
    public static HttpRequestMessage Send(Uri endpoint, string token, JsonObject body, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    /// <summary>
    /// The JSON-RPC message a response carries, whether it came as JSON or as an event stream, whose message is its
    /// data line. A response that carries none is a failure naming its status and body.
    /// </summary>
    public static async Task<JsonElement> MessageOfAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync();
        var json = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal)) is { } data
            ? data["data:".Length..].Trim()
            : body;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} with no JSON-RPC message: '{body}'", ex);
        }
    }

    /// <summary>Sends <paramref name="request"/> and returns the JSON-RPC message that came back.</summary>
    public static async Task<JsonElement> ExchangeAsync(HttpClient http, HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        using (request)
        {
            using var response = await http.SendAsync(request);
            return await MessageOfAsync(response);
        }
    }

    /// <summary>What a message says: an error's message, or the text of a result's content.</summary>
    public static string TextOf(JsonElement message) =>
        message.TryGetProperty("error", out var error)
            ? error.TryGetProperty("message", out var said) ? said.GetString() ?? string.Empty : error.GetRawText()
            : message.TryGetProperty("result", out var result) && result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? string.Join(" ", content.EnumerateArray().Select(c => c.TryGetProperty("text", out var text) ? text.GetString() : null))
                : string.Empty;

    /// <summary>
    /// contract-005 · T-2 — whether a message carries anything a caller could use: a result that is not a refusal.
    /// A refused tool call comes back as a result marked isError whose one text says which rule refused it; a refused
    /// read or get comes back as an error with no result at all. Anything else returned content. A result may also
    /// carry _meta and resultType, which the 2026-07-28 revision puts on every result: bookkeeping, not content.
    /// </summary>
    public static bool ReturnsContent(JsonElement message)
    {
        if (!message.TryGetProperty("result", out var result))
        {
            return false;
        }

        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        var blocks = result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().ToArray()
            : [];
        var onlyTheRefusal = blocks.Length == 1
            && blocks[0].TryGetProperty("text", out var text)
            && text.GetString()?.Contains("(rule: ", StringComparison.Ordinal) == true;
        var nothingElse = result.EnumerateObject().All(p => p.Name is "content" or "isError" or "_meta" or "resultType");
        return !(isError && onlyTheRefusal && nothingElse);
    }

    /// <summary>The rule a refusal names — "authz_fail (rule: not-permitted). …" — or null when it names none.</summary>
    public static string? RuleOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        const string marker = "(rule: ";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = text.IndexOf(')', start);
        return end < 0 ? null : text[start..end];
    }

    /// <summary>The resource_metadata URL a 401's challenge names, or null when it names none.</summary>
    public static string? ResourceMetadataOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        const string marker = "resource_metadata=\"";
        var start = challenge.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = challenge.IndexOf('"', start);
        return end < 0 ? null : challenge[start..end];
    }
}
