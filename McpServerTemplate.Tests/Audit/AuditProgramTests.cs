using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-5, T-9 — the real program: started, used and stopped as an operator's would be, and then asked to
/// check the file it wrote. Over stdio in Development, where it needs no container and reaches no network; the same on the
/// shipped image is the end-to-end suite's, and is owed.
/// </summary>
public sealed class AuditProgramTests : IDisposable
{
    private readonly SpawnedServer.Cleanup _cleanup = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mcp-audit-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // The servers first: on Windows a file one holds open cannot be removed.
        _cleanup.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string ServerExecutable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpServerTemplate.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root from the test output directory");

        // The app builds beside the tests, under the same configuration and framework.
        var path = Path.Combine(
            dir!.FullName, "McpServerTemplate", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0",
            OperatingSystem.IsWindows() ? "McpServerTemplate.exe" : "McpServerTemplate");
        Assert.True(File.Exists(path), $"server executable not found at {path}; build the solution first");
        return path;
    }

    private ProcessStartInfo StartInfo(IReadOnlyList<string> arguments, IDictionary<string, string>? environment = null)
    {
        var exe = ServerExecutable();
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Nothing of the developer's own: not their environment name, not their user secrets.
        info.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        info.Environment.Remove("DOTNET_ENVIRONMENT");
        _cleanup.Isolate(info);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        return info;
    }

    /// <summary>Runs the program's check on <paramref name="files"/> and returns its exit code and what it said.</summary>
    private async Task<(int Code, string Output)> VerifyAsync(params string[] files)
    {
        using var process = _cleanup.Started(Process.Start(StartInfo(["--verify-audit", .. files]))!);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await output + await errors);
    }

    /// <summary>
    /// Starts the server over stdio, signs on, lists its tools, asks for one that is not there, and closes its input — which
    /// is how a client ends a stdio server. Returns its exit code and its one audit file.
    /// </summary>
    private async Task<(int Code, string File, string Stderr)> RunASessionAsync()
    {
        var info = StartInfo([], new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Transport"] = "stdio",
            ["Audit__Path"] = _directory,
        });
        using var process = _cleanup.Started(Process.Start(info)!);

        // Drained as it is written: a full pipe nobody reads stops the server mid-line.
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        async Task<string?> AskAsync(object message, bool answered = true)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync(limit.Token);
            return answered ? await process.StandardOutput.ReadLineAsync(limit.Token) : null;
        }

        await AskAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "audit-test", version = "1" } },
        });
        await AskAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, answered: false);
        var tools = await AskAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        Assert.Contains("get_forecast", tools, StringComparison.Ordinal);
        var refused = await AskAsync(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "no_such_tool", arguments = new { } } });
        Assert.Contains("not-permitted", refused, StringComparison.Ordinal);

        process.StandardInput.Close();
        await process.WaitForExitAsync(limit.Token);

        string all;
        lock (stderr)
        {
            all = stderr.ToString();
        }

        return (process.ExitCode, Assert.Single(AuditFiles.In(_directory)), all);
    }

    [Fact]
    public async Task T9_the_programs_start_is_its_first_record_its_stop_the_last_and_its_own_check_passes_the_file()
    {
        var (code, file, stderr) = await RunASessionAsync();
        Assert.True(code == 0, $"the server exited with {code}: {stderr}");

        var records = AuditFiles.Records(_directory);
        var start = records[0];
        Assert.Equal("sys_startup", start.GetProperty("event").GetString());
        Assert.Equal(1, start.GetProperty("seq").GetInt64());
        Assert.Equal(new string('0', 64), start.GetProperty("prev_hash").GetString());

        // What an inventory tells one running server from another by: the hash of the server's own file, and of its frame.
        var detail = start.GetProperty("detail");
        var assembly = Path.Combine(Path.GetDirectoryName(ServerExecutable())!, "McpServerTemplate.dll");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly))), detail.GetProperty("build_sha256").GetString());
        Assert.Matches("^[0-9a-f]{64}$", detail.GetProperty("frame_sha256").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("tool_lock_sha256").ValueKind);
        Assert.Equal("Smhi,SmhiObs,JsonPlaceholder", detail.GetProperty("providers").GetString());

        // In between, one record per message, in the order they came, under the Development principal.
        Assert.Equal(
            ["initialize", "notifications/initialized", "tools/list", "tools/call"],
            records.Skip(1).SkipLast(1).Select(r => r.GetProperty("mcp").GetProperty("method").GetString()!));
        Assert.Equal("dev-user", records[1].GetProperty("principal").GetProperty("sub").GetString());
        Assert.Equal("not-permitted", AuditFiles.RuleOf(records[^2]));

        Assert.Equal("sys_shutdown", records[^1].GetProperty("event").GetString());

        // And the program's own check, run as an operator would run it.
        var (verified, said) = await VerifyAsync(file);
        Assert.True(verified == 0, said);
        Assert.Contains($"intact: {records.Count} records in 1 file(s), numbered 1 to {records.Count}", said, StringComparison.Ordinal);
        Assert.Contains($"last hash:      {records[^1].GetProperty("hash").GetString()}", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T5_the_programs_check_names_the_line_that_was_changed_and_says_so_with_its_exit_code()
    {
        var (_, file, _) = await RunASessionAsync();
        var lines = File.ReadAllLines(file);

        // One character of who did it, in the fourth record: the kind of change an investigation has to be able to see.
        Assert.Contains("\"sub\":\"dev-user\"", lines[3], StringComparison.Ordinal);
        lines[3] = lines[3].Replace("\"sub\":\"dev-user\"", "\"sub\":\"dev-usez\"", StringComparison.Ordinal);
        var changed = Path.Combine(_directory, "changed.jsonl");
        File.WriteAllText(changed, string.Join("\n", lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var removed = Path.Combine(_directory, "removed.jsonl");
        File.WriteAllText(removed, string.Join("\n", File.ReadAllLines(file).Where((_, index) => index != 2)) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var (changedCode, changedSaid) = await VerifyAsync(changed);
        var (removedCode, removedSaid) = await VerifyAsync(removed);
        var (missingCode, missingSaid) = await VerifyAsync(Path.Combine(_directory, "not-there.jsonl"));
        var (usageCode, usageSaid) = await VerifyAsync();

        Assert.Equal(65, changedCode);
        Assert.Contains("BROKEN:", changedSaid, StringComparison.Ordinal);
        Assert.Contains("changed.jsonl line 4: the line was changed", changedSaid, StringComparison.Ordinal);
        Assert.Contains("intact before it: 3 records", changedSaid, StringComparison.Ordinal);

        Assert.Equal(65, removedCode);
        Assert.Contains("removed.jsonl line 3: the chain breaks here", removedSaid, StringComparison.Ordinal);

        Assert.Equal(66, missingCode);
        Assert.Contains("unreadable:", missingSaid, StringComparison.Ordinal);

        Assert.Equal(64, usageCode);
        Assert.Contains("usage: --verify-audit", usageSaid, StringComparison.Ordinal);
    }
}
