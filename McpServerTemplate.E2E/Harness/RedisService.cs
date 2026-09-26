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
/// </summary>
public sealed class RedisService : IAsyncDisposable
{
    /// <summary>redis:7.4-alpine, by the digest of its multi-arch index (resolved 2026-09-26).</summary>
    public const string Image = "redis:7.4-alpine@sha256:858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499";

    public const string Alias = "redis.e2e.test";
    public const int Port = 6379;

    private readonly IContainer _container;

    private RedisService(IContainer container) => _container = container;

    /// <summary>The connection string the server is given.</summary>
    public static string ConnectionString => $"{Alias}:{Port}";

    public IContainer Container => _container;

    internal static async Task<RedisService> StartAsync(INetwork network, string runId, CancellationToken cancellationToken)
    {
        var container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithNetworkAliases(Alias)
            .WithLabel(E2ENetwork.RunLabel, runId)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();

        try
        {
            await container.StartAsync(cancellationToken);
            return new RedisService(container);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
