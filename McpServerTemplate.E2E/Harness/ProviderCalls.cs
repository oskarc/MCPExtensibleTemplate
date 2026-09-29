using System.Net;
using System.Text.Json;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · T-6 (G-7 · UC-6) — how the suite calls the built-in providers' tools and sees each call at the fake that
/// answers its host: the host each provider calls, each tool's arguments, and what an upstream owner recorded from one server.
///
/// The hosts and the arguments are the suite's own knowledge — each provider's BaseUrl as the image's appsettings.json names
/// it, each tool's parameters as its method declares them — since the suite references neither the product nor its settings
/// (G-2). A provider or a tool on a startup line that these tables do not name fails the test that meets it, rather than
/// going uncalled.
/// </summary>
public static class ProviderCalls
{
    /// <summary>The host each built-in provider calls, by the provider's name on the startup line.</summary>
    public static IReadOnlyDictionary<string, string> Hosts { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Smhi"] = "opendata-download-metfcst.smhi.se",
        ["SmhiObs"] = "opendata-download-metobs.smhi.se",
        ["JsonPlaceholder"] = "jsonplaceholder.typicode.com",
    };

    /// <summary>
    /// Each built-in tool's arguments, as its schema takes them. The forecast tools each have a point of their own, apart from
    /// Stockholm, where get_forecast_model_info asks its forecast, so each one's request is its own; the write tools carry bodies,
    /// which T-6 searches with every header and URL.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Arguments { get; } = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["get_forecast_model_info"] = new { },
        ["get_forecast"] = new { latitude = 63.83, longitude = 20.26 },
        ["get_current_weather"] = new { latitude = 55.60, longitude = 13.00 },
        ["get_recent_temperature"] = new { latitude = 59.33, longitude = 18.07 },
        ["get_temperature_history"] = new { latitude = 57.71, longitude = 11.97 },
        ["get_precipitation_history"] = new { latitude = 63.83, longitude = 20.26 },
        ["get_monthly_climate"] = new { latitude = 59.33, longitude = 18.07, month = 7 },
        ["get_blog_post"] = new { postId = 1 },
        ["get_post_comments"] = new { postId = 1 },
        ["get_user_todos"] = new { userId = 1 },
        ["create_blog_post"] = new { userId = 1, title = "e2e upstreams", body = "contract-005 T-6: a body searched for the caller's token." },
        ["add_post_comment"] = new { postId = 1, name = "e2e", email = "e2e@example.invalid", body = "contract-005 T-6: a body searched for the caller's token." },
        ["create_user_todo"] = new { userId = 1, title = "e2e upstreams", completed = false },
    };

    /// <summary>One tools/call of <paramref name="tool"/> with <paramref name="arguments"/>, through the front, and the message that came back.</summary>
    public static Task<JsonElement> CallAsync(HttpClient http, string token, string tool, object arguments) =>
        McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, "tools/call", new { name = tool, arguments }));

    /// <summary>Whether a message is an error: a JSON-RPC error, or a result marked isError.</summary>
    public static bool IsError(JsonElement message) =>
        message.TryGetProperty("error", out _)
        || (message.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True);

    /// <summary>
    /// The requests <paramref name="owner"/> has recorded from <paramref name="server"/> under <paramref name="host"/> — at
    /// <paramref name="path"/> alone, when one is given. contract-005 · G-17 round 2 (HAR-R2-02) — from its address since it
    /// started (<see cref="RequestSource"/>): read by address alone, a record an earlier server at the same address had left
    /// counted as this one's.
    /// </summary>
    public static async Task<IReadOnlyList<UpstreamRequest>> FromServerAsync(
        IUpstreamService owner, HttpClient http, RequestSource server, string host, string? path = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(server);
        return [.. (await owner.RecordedAsync(http, cancellationToken))
            .Where(r => server.Made(r) && r.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && (path is null || r.Path == path))];
    }

    /// <summary>
    /// How many requests <paramref name="owner"/> has recorded from <paramref name="server"/> under <paramref name="host"/> (at
    /// <paramref name="path"/>, when one is given) beyond the <paramref name="before"/> it had, looking again for up to three
    /// seconds until there is one: a record follows its request a moment later (the stand-in's is its container's log).
    /// </summary>
    public static async Task<int> SeenSinceAsync(
        IUpstreamService owner, HttpClient http, RequestSource server, string host, int before, string? path = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            var seen = (await FromServerAsync(owner, http, server, host, path, cancellationToken)).Count - before;
            if (seen > 0 || DateTime.UtcNow >= deadline)
            {
                return seen;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
