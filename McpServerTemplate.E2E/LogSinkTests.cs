using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-11 (5) (G-12 (5) · UC-11) — a log sink that cannot write says so.
///
/// A File sink resolves a relative path against the working directory (/app in the image, G-1) and
/// opens its file on the first event. When it cannot, Serilog reports the failure to its self-log,
/// which was switched off: the sink wrote nothing and said nothing. The startup check used to look at
/// Serilog:WriteTo:1 alone, for path traversal. A File sink at any index is now checked where it will
/// write, and refused, naming the resolved path, when the process cannot write there.
///
/// No server of its own: the one it starts is expected to refuse.
/// </summary>
public sealed class LogSinkTests(ITestOutputHelper output)
{
    /// <summary>The image's working directory (Dockerfile, WORKDIR), owned by root; the app user 1654 cannot create anything in it.</summary>
    private const string WorkingDirectory = "/app";

    [Fact]
    public async Task T11_5_a_file_sink_at_another_index_that_cannot_write_refuses_to_start_naming_the_resolved_path()
    {
        const string path = "e2e-unwritable/e2e-.log";
        const string resolved = $"{WorkingDirectory}/{path}";

        var environment = await E2EEnvironment.GetAsync();
        await using var outcome = await environment.StartupAsync(
            "log-sink-unwritable",
            SettingsDelta.None
                .Set("Serilog:WriteTo:2:Name", "File")
                .Set("Serilog:WriteTo:2:Args:path", path));
        output.WriteLine(outcome.Describe());

        var said = outcome.Stderr.Contains("e2e-unwritable", StringComparison.Ordinal)
            ? "its stderr mentions the sink's directory"
            : "its stderr never mentions the sink";
        Assert.True(
            outcome.ExitCode == 78 && outcome.RefusalLine?.Contains(resolved, StringComparison.Ordinal) == true,
            $"a File sink at Serilog:WriteTo:2 pointed at '{path}' (resolved {resolved}, which user 1654 cannot create): "
            + $"{outcome.Describe()}; {said}.");
    }
}
