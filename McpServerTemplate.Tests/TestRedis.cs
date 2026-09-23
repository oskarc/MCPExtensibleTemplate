using Testcontainers.Redis;

namespace McpServerTemplate.Tests;

/// <summary>
/// One Redis for the whole test run, started on first use (contract-003 · G-13). Production
/// refuses to start without Redis, and the in-process server runs as Production, so every test that
/// starts a server needs one. There is no fallback: with no Docker, these tests fail — a limit that
/// is never exercised against the store that holds it in production is not tested.
/// </summary>
public static class TestRedis
{
    private static readonly Lazy<Task<RedisContainer>> Shared = new(StartAsync);

    /// <summary>The shared container's connection string.</summary>
    public static async Task<string> ConnectionStringAsync() => (await Shared.Value).GetConnectionString();

    /// <summary>A container of a test's own, for a test that stops it.</summary>
    public static async Task<RedisContainer> StartAsync()
    {
        var container = new RedisBuilder("redis:7.4-alpine").Build();
        await container.StartAsync();
        return container;
    }
}
