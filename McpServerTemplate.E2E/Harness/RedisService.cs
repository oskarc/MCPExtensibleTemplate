using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The limit store the server uses in Production.
///
/// Production refuses to start without Limits:Redis (contract-003 · G-13), and the image runs as
/// Production — the environment it ships for — so the walking skeleton needs a Redis on the network
/// before any server can start. Contract-005's direction names Redis as part of the environment;
/// T-2 and T-7 are the tests that exercise it.
///
/// contract-005 · T-7 — a test that stops Redis stops one of its own (<see cref="E2EEnvironment.StartRedisAsync"/>),
/// under a name of its own, never the environment's, which every other class's server is counting in.
/// </summary>
public sealed class RedisService : IAsyncDisposable
{
    /// <summary>redis:7.4-alpine, by the digest of its multi-arch index (resolved 2026-09-26).</summary>
    public const string Image = "redis:7.4-alpine@sha256:858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499";

    /// <summary>The environment's own Redis: the name every server's base settings give Limits:Redis.</summary>
    public const string Alias = "redis.e2e.test";
    public const int Port = 6379;

    /// <summary>How long Redis may take to say it is ready; it takes about three seconds.</summary>
    private static readonly TimeSpan ReadyWithin = TimeSpan.FromMinutes(1);

    private readonly IContainer _container;

    private RedisService(IContainer container, string host)
    {
        _container = container;
        Host = host;
    }

    /// <summary>The connection string the server is given: the environment's own Redis.</summary>
    public static string ConnectionString => $"{Alias}:{Port}";

    /// <summary>The name this Redis answers under on the run's network.</summary>
    public string Host { get; }

    /// <summary>The Limits:Redis value that points a server at this Redis.</summary>
    public string Endpoint => $"{Host}:{Port}";

    public IContainer Container => _container;

    internal static Task<RedisService> StartAsync(INetwork network, string runId, CancellationToken cancellationToken) =>
        StartAsync(network, runId, Alias, cancellationToken);

    internal static async Task<RedisService> StartAsync(INetwork network, string runId, string host, CancellationToken cancellationToken)
    {
        var container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithNetworkAliases(host)
            .WithLabel(E2ENetwork.RunLabel, runId)
            // contract-005 · UC-1 edge — the wait has a deadline of its own, so a Redis that never
            // becomes ready is a named fault rather than a run held until CI's hang timeout.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections", wait => wait.WithTimeout(ReadyWithin)))
            .Build();

        try
        {
            await container.StartAsync(cancellationToken);
            return new RedisService(container, host);
        }
        catch (TimeoutException ex)
        {
            var log = await LogOrReasonAsync(container);
            await container.DisposeAsync();
            throw new EnvironmentFaultException("redis", $"Redis did not log 'Ready to accept connections' within {ReadyWithin.TotalSeconds:F0} s. Its log: {log}", ex);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// contract-005 · T-7 — stops it, as an outage would: docker stop, and the process is gone. Every server pointed at
    /// it has lost its limit store.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _container.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>The container's log for a fault's message, or why it could not be read: the fault must survive either way.</summary>
    private static async Task<string> LogOrReasonAsync(IContainer container)
    {
        try
        {
            var (stdout, stderr) = await container.GetLogsAsync(ct: CancellationToken.None);
            return stdout + stderr;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"(not readable: {ex.GetType().Name}: {ex.Message})";
        }
    }
}
