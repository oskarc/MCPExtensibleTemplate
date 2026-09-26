using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// How long each phase of a run took.
///
/// contract-005 · T-1, G-14 — the walking skeleton records a timing for every phase, on each
/// platform, and CI's wall-clock budget is set from those measurements rather than estimated. A phase
/// that fails is recorded too, with the time it took to fail: a Keycloak that dies after forty
/// seconds and one that dies at once are different faults.
/// </summary>
public sealed class Timings
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Phase> _phases = new();

    /// <summary>One measured phase: when it started after the run began, how long it took, and how it ended.</summary>
    public sealed record Phase(string Name, double StartSeconds, double Seconds, string Outcome);

    public IReadOnlyList<Phase> Phases => [.. _phases.OrderBy(p => p.StartSeconds)];

    public async Task<T> MeasureAsync<T>(string name, Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var start = _clock.Elapsed;
        try
        {
            var result = await body();
            Record(name, start, "ok");
            return result;
        }
        catch (Exception ex)
        {
            Record(name, start, $"failed: {ex.GetType().Name}");
            throw;
        }
    }

    public async Task MeasureAsync(string name, Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        await MeasureAsync(name, async () =>
        {
            await body();
            return true;
        });
    }

    /// <summary>
    /// Measures a phase of the environment, and names the phase in its failure.
    ///
    /// contract-005 · UC-1 edge, T-1 — a phase that fails is reported naming its cause. A failure
    /// that is not already an <see cref="EnvironmentFaultException"/> — an image pull, a container
    /// start or a wait surfacing as the library's own exception — becomes one, with the phase's name
    /// and the original as its inner exception, instead of reaching the test as a bare library
    /// error. Not for a test's own phases, where a failure is the product's: it must never read as
    /// the environment's. <paramref name="isProductFailure"/> lets a phase that can fail either way
    /// (the server image's build) pass the product's failures through in their own words. A timeout
    /// is named like any other failure: nothing cancels an environment phase but its own deadline.
    /// </summary>
    public async Task<T> MeasureEnvironmentAsync<T>(string phase, Func<Task<T>> body, Func<Exception, bool>? isProductFailure = null)
    {
        try
        {
            return await MeasureAsync(phase, body);
        }
        catch (Exception ex) when (ex is not EnvironmentFaultException && isProductFailure?.Invoke(ex) != true)
        {
            throw new EnvironmentFaultException(phase, $"{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc cref="MeasureEnvironmentAsync{T}"/>
    public async Task MeasureEnvironmentAsync(string phase, Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        await MeasureEnvironmentAsync(phase, async () =>
        {
            await body();
            return true;
        });
    }

    /// <summary>The phases as a table, for a test's output.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var phase in Phases)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {phase.Name,-40} start {phase.StartSeconds,7:F1}s  took {phase.Seconds,7:F1}s  {phase.Outcome}");
        }

        return text.ToString();
    }

    /// <summary>Writes the phases as JSON, with the platform they were measured on.</summary>
    public void WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new
            {
                platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ci = Environment.GetEnvironmentVariable("CI") is not null,
                phases = Phases,
            },
            Indented));
    }

    private void Record(string name, TimeSpan start, string outcome)
    {
        var elapsed = _clock.Elapsed;
        _phases.Enqueue(new Phase(name, Math.Round(start.TotalSeconds, 2), Math.Round((elapsed - start).TotalSeconds, 2), outcome));
    }
}
