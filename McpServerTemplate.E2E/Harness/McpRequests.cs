using System.Text;

namespace McpServerTemplate.E2E.Harness;

/// <summary>Raw MCP requests, for the tests that need to see the HTTP answer rather than what a client makes of it.</summary>
public static class McpRequests
{
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
