using System.Diagnostics;
using System.Text;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-17 round 3 (CON-R3-01) — the Docker command line, for the one thing the engine's API does not do: build with
/// BuildKit, the builder `docker build` and `docker compose build` use. The engine's build API, which Testcontainers built the
/// harness's images through, runs the engine's classic builder, and the two do not make the same image of the same Dockerfile:
/// the classic builder made the image's WORKDIR, /app, root's, where BuildKit makes it the base image's user's (1654). So the
/// image under test was not the image an operator builds, in a property that decides what the server's own user may rewrite.
/// Every image the harness builds is built here, by `docker buildx build` (<see cref="Images.BuildAsync"/>), and so is the
/// product's publish output the shipped image's /app is compared with (<see cref="Images.PublishOutputAsync"/>).
///
/// The command line talks to the engine its own context names, as `docker build` does; the harness's client talks to the one
/// Testcontainers resolved. On a developer's machine and on the runner they are the same engine; where they are not, the image a
/// build made is not on the engine the harness reads, and the build says so as the environment's fault.
/// </summary>
internal static class DockerCli
{
    /// <summary>What one run of the command line gave: its exit code, its standard output as bytes, and its standard error.</summary>
    /// <param name="ExitCode">The command's exit code.</param>
    /// <param name="Output">Its standard output: a build's exported archive, when it exports one there.</param>
    /// <param name="Errors">Its standard error: a build's progress, in plain text, and its failure.</param>
    internal sealed record Result(int ExitCode, byte[] Output, string Errors)
    {
        /// <summary>The last <paramref name="lines"/> lines of its standard error, for a failure message.</summary>
        public string Tail(int lines = 40)
        {
            var all = Errors.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
    }

    /// <summary>
    /// Runs `docker` with <paramref name="arguments"/> in <paramref name="workingDirectory"/>, giving it <paramref name="standardInput"/>
    /// (UTF-8, no byte-order mark) when there is some, and returns what it gave; killed, with every process it started, when
    /// <paramref name="cancellationToken"/> is cancelled. A command line that cannot be started is the environment's fault, named.
    /// </summary>
    public static async Task<Result> RunAsync(
        IReadOnlyList<string> arguments, string workingDirectory, string? standardInput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo("docker", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (standardInput is not null)
        {
            start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        // Plain progress, and no colour codes, so a failure reads as text.
        start.Environment["BUILDKIT_PROGRESS"] = "plain";
        start.Environment["NO_COLOR"] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("docker did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EnvironmentFaultException(
                "image",
                $"the docker command line could not be started ({ex.Message}). The harness builds its images with it, through BuildKit, "
                + "as `docker build` does: install Docker's command line with its buildx plugin, and run again.",
                ex);
        }

        using (process)
        {
            using var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                    process.StandardInput.Close();
                }

                await process.WaitForExitAsync(cancellationToken);
                await reading;
                return new Result(process.ExitCode, output.ToArray(), await errors);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited meanwhile.
                }

                throw;
            }
        }
    }
}
