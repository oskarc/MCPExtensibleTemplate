using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
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
    ///
    /// contract-005 · G-17 round 2 (the documented deployment's restart policy) — or if it has stopped and a restart policy
    /// is starting it again: docs/04's services restart unless stopped, and the docs profile runs them so, so a server that
    /// refuses its configuration (exit 78) is started again by the engine and is never seen stopped. While the engine waits
    /// to restart it (State.Restarting), its state keeps that exit's code; once it has started it again (RestartCount above
    /// zero), its state no longer does, and the code is read from the engine's own record of the exit, its last die event.
    /// Either is the exit this wait watches for.
    /// </summary>
    public static async Task<long?> ExitCodeIfStoppedAsync(IDockerClient docker, string containerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var inspected = await docker.Containers.InspectContainerAsync(containerId, cancellationToken);
        return inspected.State switch
        {
            { Running: false, Restarting: false, Status: "exited" or "dead" } state => state.ExitCode,
            { Restarting: true } state => state.ExitCode,
            _ when inspected.RestartCount > 0 => await LastExitCodeAsync(docker, inspected, cancellationToken),
            _ => null,
        };
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (the documented deployment's restart policy) — the exit code of <paramref name="container"/>'s
    /// last exit, which a restart policy has since started it again after: the engine's die event for it, read back from when
    /// the container was created until now. An exit the engine keeps no event of is named, never taken for a clean one.
    /// </summary>
    private static async Task<long> LastExitCodeAsync(IDockerClient docker, ContainerInspectResponse container, CancellationToken cancellationToken)
    {
        // From a minute before the engine created it, so no reading of its creation time's kind puts the start after its exits.
        var created = container.Created.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(container.Created, DateTimeKind.Utc)
            : container.Created.ToUniversalTime();
        var died = new LastEvent();
        await docker.System.MonitorEventsAsync(
            new ContainerEventsParameters
            {
                Since = new DateTimeOffset(created).AddMinutes(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                Until = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1).ToString(CultureInfo.InvariantCulture),
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["container"] = new Dictionary<string, bool> { [container.ID] = true },
                    ["event"] = new Dictionary<string, bool> { ["die"] = true },
                },
            },
            died,
            cancellationToken);

        return died.Last?.Actor?.Attributes is { } attributes
            && attributes.TryGetValue("exitCode", out var code)
            && long.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode)
            ? exitCode
            : throw new InvalidOperationException(
                $"The container {container.Name.TrimStart('/')} was started again {container.RestartCount} time(s) by its restart policy, and "
                + "the engine's events hold no exit code for it.");
    }

    /// <summary>The last event the engine reported, kept as it is reported: <see cref="Progress{T}"/> would report it later, on another thread.</summary>
    private sealed class LastEvent : IProgress<Message>
    {
        public Message? Last { get; private set; }

        public void Report(Message value) => Last = value;
    }

    /// <summary>
    /// Starts a server's container, which may refuse to start. Testcontainers returns from a start once its readiness check
    /// (its default wait, UntilContainerIsRunning, which its public API neither removes nor configures) passes; on a busy
    /// engine that check has instead waited out its own timeout, about 20 s, on servers that had already refused — seen
    /// 2026-09-29 with the full suite running: the test host given a non-.test authority (T-8) exited 78 a fifth of a second
    /// after it started, and the start threw "TimeoutException: The operation has timed out" 18 s later; in one cold run, three
    /// startup rows (T-10, T-11 (1), T-11 (4)) the same. A container found stopped when the check gives up is returned as it
    /// is: its exit is the outcome, which the caller reads, before it asks for any port (ServerUnderTest.WaitForStartupAsync).
    /// Such a refusal still costs that timeout; it no longer reads as a harness failure.
    ///
    /// contract-005 · G-17 round 2 (HAR-R2-08) — a start let stop like that has not had the check every container of the run gets
    /// the moment it starts, should Testcontainers give up before it runs it (<see cref="E2ENetwork.OnTheRunNetwork"/>): it is
    /// run here instead, on the engine's record of how the container was created, which a stopped container keeps.
    /// </summary>
    public static async Task StartServerAsync(this DotNet.Testcontainers.Containers.IContainer container, IDockerClient docker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);

        try
        {
            await container.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or DotNet.Testcontainers.Containers.ContainerNotRunningException)
        {
            // Stopped: the caller reads its exit code and its log. Anything else is the failure it looks like.
            if (await ExitCodeIfStoppedAsync(docker, container.Id, cancellationToken) is null)
            {
                throw;
            }

            await E2ENetwork.CheckStartedResolvingNoRealNameAsync(docker, container.Id, cancellationToken);
        }
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
