using System.Collections.Concurrent;
using StackExchange.Redis;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// Where the frame keeps what must hold across server instances: per-caller rate windows, and the
/// confirmations that have already been used (contract-003 · G-7, G-8).
/// </summary>
public interface ILimitStore
{
    /// <summary>
    /// Counts one event against a sliding window and says whether it fits. Throws when the store
    /// cannot answer; the gate treats that as a refusal, never as permission.
    /// </summary>
    Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>Claims a key once. Returns false if it was already claimed within <paramref name="lifetime"/>.</summary>
    Task<bool> TryClaimOnceAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>A name for the startup log: which store is holding the limits.</summary>
    string Description { get; }
}

/// <summary>
/// Limits in Redis, so they hold on every instance. The window is computed from Redis's own clock
/// inside one script, so two instances with drifting clocks still count against the same window.
/// </summary>
public sealed class RedisLimitStore : ILimitStore, IDisposable
{
    // Sliding window: drop what fell out of the window, count what is left, admit if there is room.
    // One script, so no other instance can interleave between the count and the add.
    private const string SlidingWindow = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local window = tonumber(ARGV[1])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - window)
        if redis.call('ZCARD', KEYS[1]) < tonumber(ARGV[2]) then
          redis.call('ZADD', KEYS[1], now, ARGV[3])
          redis.call('PEXPIRE', KEYS[1], window)
          return 1
        end
        return 0
        """;

    private readonly ConnectionMultiplexer _connection;

    private RedisLimitStore(ConnectionMultiplexer connection) => _connection = connection;

    /// <inheritdoc />
    public string Description => "Redis";

    /// <summary>
    /// Connects without waiting for Redis to be up: an unreachable Redis does not stop the server
    /// starting, it makes every governed request fail closed until Redis is back (roadmap D7).
    /// </summary>
    public static RedisLimitStore Connect(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2_000;
        options.SyncTimeout = 2_000;
        options.AsyncTimeout = 2_000;
        return new RedisLimitStore(ConnectionMultiplexer.Connect(options));
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        var result = await _connection.GetDatabase().ScriptEvaluateAsync(
            SlidingWindow,
            [new RedisKey($"mcp:limit:{key}")],
            [(long)window.TotalMilliseconds, limit, Guid.NewGuid().ToString("N")]).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public Task<bool> TryClaimOnceAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        _connection.GetDatabase().StringSetAsync($"mcp:once:{key}", 1, lifetime, When.NotExists);

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Limits in this process's memory. Permitted only in Development: with more than one instance, a
/// caller escapes the limit by spreading requests across them.
/// </summary>
public sealed class InMemoryLimitStore(TimeProvider clock) : ILimitStore
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _claims = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Description => "in-memory (Development only)";

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var queue = _windows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= now - window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                return Task.FromResult(false);
            }

            queue.Enqueue(now);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryClaimOnceAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var claimed = true;
        _claims.AddOrUpdate(
            key,
            now,
            (_, previous) =>
            {
                if (now - previous < lifetime)
                {
                    claimed = false;
                    return previous;
                }

                return now;
            });
        return Task.FromResult(claimed);
    }
}
