using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using Testcontainers.Redis;

namespace McpServerTemplate.Tests;

/// <summary>
/// One Redis for the whole test run, started on first use (contract-003 · G-13). Production
/// refuses to start without Redis, and the in-process server runs as Production, so every test that
/// starts a server needs one. There is no fallback: with no Docker, these tests fail — a limit that
/// is never exercised against the store that holds it in production is not tested.
///
/// contract-005 · T-13 — every image reference is pinned by digest, in this suite as in the
/// end-to-end one (McpServerTemplate.E2E/Harness/RedisService.cs holds the same Redis).
///
/// contract-005 · G-4 — nothing this suite starts is published beyond loopback. Redis's port is bound
/// to 127.0.0.1 only, and Testcontainers' resource reaper is off: it published its own port on every
/// host interface, where anyone on the machine's network could ask it to remove containers. What it
/// did — remove the containers of a run whose process died before its teardown — the next run does
/// when it starts (<see cref="SweepDeadRunsAsync"/>), by the label every container here carries.
/// </summary>
public static class TestRedis
{
    /// <summary>redis:7.4-alpine, by the digest of its multi-arch index (resolved 2026-09-26).</summary>
    private const string Image = "redis:7.4-alpine@sha256:858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499";

    /// <summary>The label on every container this suite starts, naming the run that owns it.</summary>
    private const string RunLabel = "org.mcp-server-template.tests.run";

    /// <summary>This run: the test process, by its id and its start time, so a reused id is not mistaken for it.</summary>
    private static readonly string ThisRun = RunOf(Environment.ProcessId);

    private static readonly Lazy<Task<RedisContainer>> Shared = new(StartAsync);
    private static readonly Lazy<Task> Swept = new(SweepDeadRunsAsync);

    static TestRedis()
    {
        // Set before the first container is built, which is when Testcontainers would start the reaper.
        TestcontainersSettings.ResourceReaperEnabled = false;

        // The reaper removed the shared container when the run ended; now the run does, as its process
        // exits. A run killed before then leaves it for the next run's sweep.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveShared();
    }

    /// <summary>The shared container's connection string.</summary>
    public static async Task<string> ConnectionStringAsync() => (await Shared.Value).GetConnectionString();

    /// <summary>A container of a test's own, for a test that stops it.</summary>
    public static async Task<RedisContainer> StartAsync()
    {
        await Swept.Value;

        var container = new RedisBuilder(Image)
            .WithLabel(RunLabel, ThisRun)
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var binding in parameters.HostConfig?.PortBindings?.Values.SelectMany(b => b) ?? [])
                {
                    binding.HostIP = "127.0.0.1";
                }
            })
            .Build();
        await container.StartAsync();
        return container;
    }

    private static void RemoveShared()
    {
        if (Shared.IsValueCreated && Shared.Value.IsCompletedSuccessfully)
        {
            Shared.Value.Result.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Removes every container labelled with a run whose process is gone. A run whose process is alive
    /// — another test run on this machine — is left alone.
    /// </summary>
    private static async Task SweepDeadRunsAsync()
    {
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder().Build();
        var labelled = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [RunLabel] = true },
        };

        foreach (var container in await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = labelled }))
        {
            if (container.Labels.TryGetValue(RunLabel, out var run) && run != ThisRun && !IsLive(run))
            {
                try
                {
                    await docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
                }
                catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Removed meanwhile by its own run: gone either way.
                }
            }
        }
    }

    /// <summary>"pid@start", the start in whole seconds since the Unix epoch.</summary>
    private static string RunOf(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return string.Create(CultureInfo.InvariantCulture, $"{processId}@{new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds()}");
    }

    /// <summary>
    /// Whether the process a run label names is still running. Its start time must match within two
    /// seconds, since a start time read from another process can differ by rounding; a process whose
    /// start time cannot be read is taken to be alive, so a run is never removed on a guess.
    /// </summary>
    private static bool IsLive(string run)
    {
        var parts = run.Split('@');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var started))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited
                && Math.Abs(new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds() - started) <= 2;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // exited while being asked
        }
        catch (Win32Exception)
        {
            return true;
        }
    }
}
