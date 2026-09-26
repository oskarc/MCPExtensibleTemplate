using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The Docker engine Testcontainers talks to, reached directly for the few things Testcontainers does
/// not expose: a ping, an image's labels, which network holds a subnet, and the host address a port
/// is published on.
///
/// contract-005 · G-11 — "Docker unavailable" is the first self-check, so a run with no engine fails
/// in one line naming it, not with a stack trace from whichever container happened to start first.
/// </summary>
internal static class DockerEngine
{
    /// <summary>
    /// The container's exit code if it has stopped, or null while it runs. Asked of the engine each
    /// time: Testcontainers' own State is a cached value, and its GetExitCodeAsync waits for an exit.
    /// </summary>
    public static async Task<long?> ExitCodeIfStoppedAsync(IDockerClient docker, string containerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var inspected = await docker.Containers.InspectContainerAsync(containerId, cancellationToken);
        return inspected.State is { Running: false, Restarting: false } state && state.Status is "exited" or "dead"
            ? state.ExitCode
            : null;
    }

    /// <summary>
    /// Publishes every port the container is built with on 127.0.0.1 only.
    ///
    /// contract-005 · G-6, G-7 — the environment's own doors are unauthenticated by design: the test
    /// issuer's /admin/mint, its auto-approving /authorize and /token, WireMock's writable /__admin.
    /// Testcontainers publishes on every host interface, which put those doors, Keycloak and the server
    /// on the machine's LAN address for as long as a run lasted. The test process reaches them over
    /// loopback, so loopback is where they are published, and nowhere else.
    /// </summary>
    public static ContainerBuilder WithLoopbackPortsOnly(this ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithCreateParameterModifier(parameters =>
        {
            foreach (var binding in parameters.HostConfig?.PortBindings?.Values.SelectMany(b => b) ?? [])
            {
                binding.HostIP = "127.0.0.1";
            }
        });
    }

    /// <summary>A client for the endpoint Testcontainers resolved, or an environment fault naming why there is none.</summary>
    public static async Task<IDockerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig;
        if (endpoint is null)
        {
            throw new EnvironmentFaultException(
                "docker",
                "Docker unavailable: Testcontainers found no Docker endpoint (no running engine, DOCKER_HOST "
                + "unset or unreachable). Start Docker and run again.");
        }

        var client = endpoint.GetDockerClientBuilder().Build();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.System.PingAsync(timeout.Token);
            return client;
        }
        catch (Exception ex) when (ex is not EnvironmentFaultException)
        {
            client.Dispose();
            throw new EnvironmentFaultException(
                "docker",
                $"Docker unavailable: the engine at {endpoint.Endpoint} did not answer a ping ({ex.GetType().Name}: {ex.Message}).",
                ex);
        }
    }
}
