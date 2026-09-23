using System.Text;
using System.Text.Json;
using McpServerTemplate.Tests.Identity;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// Talks to an in-process server the two ways a caller can: through the SDK's own client, which
/// is what a real integration does, and as raw JSON-RPC, for the requests no well-behaved client
/// would send.
/// </summary>
public static class GateClient
{
    public const string Resource = "https://mcp.example.com/mcp";

    public static async Task<McpClient> ConnectAsync(InProcessServer server, string token, bool canConfirm = false, bool confirms = true)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = server.Address,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        });

        var options = new McpClientOptions();
        if (canConfirm)
        {
            options.Handlers.ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult
            {
                Action = confirms ? "accept" : "decline",
                Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) },
            });
        }

        return await McpClient.CreateAsync(transport, options);
    }

    /// <summary>Posts one JSON-RPC request and returns the response's JSON, whatever its shape.</summary>
    public static async Task<JsonElement> RpcAsync(InProcessServer server, string token, string method, object? parameters = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        using var response = await server.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Streamable HTTP may answer as an event stream; the message is the data line.
        var json = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal)) is { } data
            ? data["data:".Length..].Trim()
            : body;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The text of a tool result, or the error message of a refused request.</summary>
    public static string TextOf(JsonElement response) =>
        response.TryGetProperty("error", out var error)
            ? error.GetProperty("message").GetString() ?? string.Empty
            : string.Join(" ", response.GetProperty("result").TryGetProperty("content", out var content)
                ? content.EnumerateArray().Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : null)
                : []);
}
