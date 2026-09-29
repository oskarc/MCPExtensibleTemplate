using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerTemplate.Tests;

/// <summary>
/// contract-001 · T-1, T-2, T-3, T-4, T-8, T-12 — guarantees about the server as a process.
///
/// These start the built executable. Nothing here can be established from inside the test
/// host: whether a transport binds a socket, what exit code a failure produces, and which
/// tool names a client actually sees are all properties of the process, not of a class.
/// </summary>
[Collection("server-process")]
public sealed class ServerProcessTests(ServerProcessTests.SharedServers shared) : IDisposable
{
    // contract-005 review round 5 — what this test's servers were given, and the servers, ended when the test ends
    // (SpawnedServer.Cleanup).
    private readonly SpawnedServer.Cleanup _cleanup = new();

    public void Dispose() => _cleanup.Dispose();

    /// <summary>
    /// contract-005 · T-14 — what this collection's tests only look at, started once for all of them: the program in
    /// Production over HTTP, and the tools a Development stdio server lists. Six tests each started the first and two
    /// the second, and none changes what it looks at — a status, a header, the startup line, the tool names — so one of
    /// each serves them all. Started when the first test asks, and ended when the collection ends, with everything it was
    /// given (SpawnedServer.Cleanup). Every other test here starts the server it needs, as before.
    /// </summary>
    public sealed class SharedServers : IAsyncLifetime, IDisposable
    {
        private readonly SpawnedServer.Cleanup _cleanup = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private HttpServer? _server;
        private string? _contentRoot;
        private HashSet<string>? _tools;

        /// <summary>
        /// The program in Production over HTTP, as <see cref="StartHttpAsync"/> starts it: started once. Its content root is
        /// a directory of its own holding copies of the shipped settings files, as the settings-read-once test has it, so
        /// its configuration is the build's and its log file is its own: the Production file sink writes beneath the
        /// working directory, and a server that outlives one test, run from the build output, held the log file every other
        /// server started there writes to — on Windows each then refused to start for it.
        /// </summary>
        internal async Task<HttpServer> ServerAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_server is null)
                {
                    _contentRoot = Directory.CreateTempSubdirectory("mcp-tests-shared-server-").FullName;
                    var bin = Path.GetDirectoryName(ServerExecutable())!;
                    foreach (var file in new[] { "appsettings.json", "appsettings.Production.json" })
                    {
                        File.Copy(Path.Combine(bin, file), Path.Combine(_contentRoot, file));
                    }

                    _server = await StartHttpAsync(_cleanup, _contentRoot);
                }

                return _server;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The tool names a Development stdio server lists, as <see cref="ListToolNamesAsync"/> reads them: read once.</summary>
        internal async Task<HashSet<string>> ToolNamesAsync()
        {
            await _gate.WaitAsync();
            try
            {
                return _tools ??= await ListToolNamesAsync(_cleanup);
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task InitializeAsync() => Task.CompletedTask;

        /// <summary>The server, stopped first; then (<see cref="Dispose"/>) everything it and the listing were given.</summary>
        public async Task DisposeAsync()
        {
            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
        }

        public void Dispose()
        {
            _cleanup.Dispose();
            _gate.Dispose();

            // Once the server has exited: on Windows a file it held could not be removed before.
            if (_contentRoot is not null && Directory.Exists(_contentRoot))
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpServerTemplate.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root from the test output directory");
        return dir!.FullName;
    }

    private static string ServerExecutable()
    {
        // The app builds beside the tests, under the same configuration and framework.
        var relative = Path.Combine(
            "McpServerTemplate", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0",
            OperatingSystem.IsWindows() ? "McpServerTemplate.exe" : "McpServerTemplate");

        var path = Path.Combine(RepositoryRoot(), relative);
        Assert.True(File.Exists(path), $"server executable not found at {path}; build the solution first");
        return path;
    }

    /// <summary>
    /// A started server, with its standard error being drained.
    ///
    /// The draining is the point. A redirected pipe that nobody reads fills up, and the process
    /// then blocks on its next write to it. The server goes quiet mid-request and the symptom is
    /// indistinguishable from a hung tool call — which is exactly how it was first misread here.
    /// Anything that logs heavily on a failure path (a retrying resilience pipeline, a stack
    /// trace) crosses the buffer; anything quieter does not, so the same test passes or hangs
    /// depending on how much the server had to say.
    /// </summary>
    internal sealed class Spawned
    {
        private readonly StringBuilder _stderr = new();

        public Spawned(Process process)
        {
            Process = process;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;

                lock (_stderr)
                    _stderr.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
        }

        public Process Process { get; }

        public string Stderr
        {
            get
            {
                lock (_stderr)
                    return _stderr.ToString();
            }
        }
    }

    /// <param name="environment">The process's environment, on top of this one's.</param>
    /// <param name="redirectStdin">Whether the test writes to the server's stdin.</param>
    /// <param name="workingDirectory">
    /// The working directory, which is the server's content root: where it reads appsettings*.json and
    /// resolves log paths. The executable's own directory unless a test needs settings files of its own.
    /// </param>
    /// <param name="arguments">The server's command line.</param>
    private Spawned Start(
        IDictionary<string, string> environment, bool redirectStdin = false, string? workingDirectory = null, IReadOnlyList<string>? arguments = null) =>
        Start(_cleanup, environment, redirectStdin, workingDirectory, arguments);

    /// <summary>
    /// <see cref="Start(IDictionary{string, string}, bool, string?, IReadOnlyList{string}?)"/>, for a server ended with
    /// <paramref name="cleanup"/> rather than with this test: the collection's shared one (<see cref="SharedServers"/>).
    /// </summary>
    private static Spawned Start(
        SpawnedServer.Cleanup cleanup, IDictionary<string, string> environment, bool redirectStdin = false, string? workingDirectory = null, IReadOnlyList<string>? arguments = null) =>
        new(cleanup.Started(Process.Start(StartInfo(cleanup, environment, redirectStdin, workingDirectory, arguments))!));

    /// <summary>How <see cref="Start(IDictionary{string, string}, bool, string?, IReadOnlyList{string}?)"/> starts the server: the same parameters.</summary>
    private ProcessStartInfo StartInfo(
        IDictionary<string, string> environment, bool redirectStdin = false, string? workingDirectory = null, IReadOnlyList<string>? arguments = null) =>
        StartInfo(_cleanup, environment, redirectStdin, workingDirectory, arguments);

    private static ProcessStartInfo StartInfo(
        SpawnedServer.Cleanup cleanup, IDictionary<string, string> environment, bool redirectStdin = false, string? workingDirectory = null, IReadOnlyList<string>? arguments = null)
    {
        var exe = ServerExecutable();
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exe)!,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments ?? [])
        {
            info.ArgumentList.Add(argument);
        }

        // Clear inherited environment so a developer's own ASPNETCORE_ENVIRONMENT cannot
        // change what these tests assert.
        info.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        info.Environment.Remove("DOTNET_ENVIRONMENT");

        // contract-005 review round 4 — nor the developer's own user secrets: a store nobody keeps, unless the
        // test names one; and, round 5 addendum 2, on Linux nobody's data-protection keys (SpawnedServer). Removed when
        // the test ends.
        cleanup.Isolate(info);
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        return info;
    }

    /// <summary>
    /// contract-005 review round 4 — a spawned server reads no one's user secrets: the store it resolves,
    /// by the framework's own order, is never the one this process resolves (the developer's), and lies in
    /// a location nobody keeps. Paths only — neither store is opened or listed.
    /// </summary>
    [Theory]
    [InlineData("Development", "stdio")]
    [InlineData("Development", "http")]
    [InlineData("Production", "http")]
    public void T3_a_spawned_server_resolves_a_user_secrets_store_nobody_keeps(string environmentName, string transport)
    {
        var serverSecrets = typeof(McpServerTemplate.Infrastructure.HostBuilders).Assembly
            .GetCustomAttribute<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>()!.UserSecretsId;
        var developers = Microsoft.Extensions.Configuration.UserSecrets.PathHelper.GetSecretsPathFromSecretsId(serverSecrets);

        // Positive control: the order followed here gives the framework's own answer for this process.
        Assert.Equal(developers, SpawnedServer.UserSecretsPath(SpawnedServer.CurrentEnvironment(), serverSecrets));

        var info = StartInfo(new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = environmentName, ["Transport"] = transport });
        var spawned = SpawnedServer.UserSecretsPath(info.Environment, serverSecrets);
        Assert.True(
            spawned != developers && spawned.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                && !Directory.Exists(Path.GetDirectoryName(spawned)),
            $"a spawned {environmentName} {transport} server resolves its user secrets (UserSecretsId {serverSecrets}) to "
            + (spawned == developers ? "the store this process resolves: the developer's own." : "a location this test does not own."));
    }

    /// <summary>
    /// The control for the rows above: the variable set there is the one the real server finds its store
    /// by. Given an APPDATA of this test's own, holding a store with a setting the server does not read, a
    /// Development server reads that store, and refuses the setting, naming it.
    /// </summary>
    [Fact]
    public async Task T3_a_spawned_server_reads_user_secrets_only_from_the_store_it_is_given()
    {
        var appData = Directory.CreateTempSubdirectory("mcp-tests-user-secrets-").FullName;
        try
        {
            var serverSecrets = typeof(McpServerTemplate.Infrastructure.HostBuilders).Assembly
                .GetCustomAttribute<Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute>()!.UserSecretsId;
            var store = SpawnedServer.UserSecretsPath(new Dictionary<string, string?> { ["APPDATA"] = appData }, serverSecrets);
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            await File.WriteAllTextAsync(store, """{ "Limits": { "OnlyInTheTestStore": "1" } }""");

            var (exitCode, stderr) = await RunToCompletionAsync(new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Transport"] = "stdio",
                ["APPDATA"] = appData,
            });

            Assert.Equal(78, exitCode);
            Assert.Contains("'Limits:OnlyInTheTestStore' is not a setting this server reads", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    /// <summary>
    /// contract-005 review round 5, addendum 2, then G-18 — a spawned server is given no key location. Addendum 2 gave
    /// it one on Linux, beside its user secrets, for data protection's key ring; since G-18 the server loads no key
    /// ring on any operating system (T17_a_spawned_server_loads_no_key_ring), so LOCALAPPDATA is left as this process
    /// has it everywhere (SpawnedServer says why). Paths only; nothing is read or listed.
    /// </summary>
    [Fact]
    public void T3_a_spawned_server_is_given_no_key_location_since_it_loads_no_key_ring()
    {
        var info = new ProcessStartInfo("server");
        SpawnedServer.IsolateFromTheDeveloper(info);
        Assert.Equal(
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            info.Environment.TryGetValue("LOCALAPPDATA", out var local) ? local : null);
    }

    /// <summary>
    /// contract-005 review round 8 — a server whose test disposed its handle while it still ran is still ended when the
    /// test ends. RunToCompletionAsync did exactly that when a server it expected to refuse started instead, and the
    /// cleanup took a disposed handle for an exited server: the server ran on, holding the rolling log file the next
    /// servers from the same directory write to, which then refused to start for that.
    /// </summary>
    [Fact]
    public void T3_a_spawned_server_whose_handle_its_test_disposed_is_still_ended_when_the_test_ends()
    {
        var test = new ServerProcessTests(shared);
        var spawned = test.Start(
            new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["Transport"] = "stdio" },
            redirectStdin: true);
        var id = spawned.Process.Id;
        var started = spawned.Process.StartTime;
        spawned.Process.Dispose();

        test.Dispose();

        using var survivor = RunningProcess(id, started);
        try
        {
            Assert.True(survivor is null, $"the server (process {id}) is still running after its test ended.");
        }
        finally
        {
            survivor?.Kill(entireProcessTree: true);
            survivor?.WaitForExit();
        }
    }

    /// <summary>The process with <paramref name="id"/> that started at <paramref name="started"/>, if it still runs.</summary>
    private static Process? RunningProcess(int id, DateTime started)
    {
        try
        {
            var process = Process.GetProcessById(id);
            if (!process.HasExited && process.StartTime == started)
            {
                return process;
            }

            process.Dispose();
        }
        catch (ArgumentException)
        {
            // No process has that id.
        }
        catch (InvalidOperationException)
        {
            // It exited while it was looked at.
        }

        return null;
    }

    /// <summary>
    /// contract-005 review round 5 — nothing a spawned server is given is left behind: its location of its own
    /// (SpawnedServer) is removed, with whatever the server wrote there, when the test that started it ends, passed or
    /// failed, once the server has exited. On Linux a server writes its data-protection key there; on Windows nothing
    /// does, so this one is given a log file there, which it holds open while it runs. The test it belongs to ends
    /// with it still running, as a failing test would.
    /// </summary>
    [Fact]
    public async Task T3_a_spawned_servers_own_location_is_removed_when_its_test_ends()
    {
        var test = new ServerProcessTests(shared);
        Spawned spawned;
        string location;
        bool wrote;
        try
        {
            spawned = test.Start(
                new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["Transport"] = "stdio",
                    ["Serilog__WriteTo__2__Name"] = "File",
                    ["Serilog__WriteTo__2__Args__path"] = "%APPDATA%/server.log",
                },
                redirectStdin: true);
            location = spawned.Process.StartInfo.Environment["APPDATA"]!;

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(location, "server.log")) && !spawned.Process.HasExited)
            {
                await Task.Delay(100);
            }

            wrote = File.Exists(Path.Combine(location, "server.log"));
        }
        finally
        {
            test.Dispose();
        }

        Assert.True(wrote, $"the server wrote nothing in {location}: {spawned.Stderr}");
        Assert.False(Directory.Exists(location), $"{location}, given to a server its test started, is still there after the test ended.");
    }

    private async Task<(int ExitCode, string Stderr)> RunToCompletionAsync(
        IDictionary<string, string> environment, string? workingDirectory = null)
    {
        var spawned = Start(environment, workingDirectory: workingDirectory);
        using var process = spawned.Process;

        var exited = await Task.Run(() => process.WaitForExit(60_000));
        if (!exited)
        {
            // Review round 8 — ended here, before the assertion disposes the handle, so a server that started instead
            // of refusing is not left running.
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        Assert.True(exited, $"the server did not exit; it was expected to refuse to start: {spawned.Stderr}");

        // WaitForExit(int) does not wait for the redirected readers to finish; this overload does,
        // so the buffer is complete before it is read.
        process.WaitForExit();
        return (process.ExitCode, spawned.Stderr);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ── G-1: stdio is a plain host, Development only ──────────────────────────

    [Fact]
    public async Task T1_stdio_answers_initialize_and_binds_no_tcp_port()
    {
        // Hand the process a URL to listen on. A web host would bind it; a plain generic host
        // has nothing that reads ASPNETCORE_URLS, so the port stays free. Checking a specific
        // port this test reserved is the only way to attribute a listener to this process --
        // the machine-wide listener list says nothing about who owns port 5000.
        var webPort = FreePort();

        using var process = Start(
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Transport"] = "stdio",
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{webPort}",
            },
            redirectStdin: true).Process;

        try
        {
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "acceptance-test", version = "1" },
                },
            });

            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync();

            var reply = await process.StandardOutput.ReadLineAsync(new CancellationTokenSource(30_000).Token);

            Assert.False(string.IsNullOrWhiteSpace(reply), "the server sent no reply to initialize");
            using var document = JsonDocument.Parse(reply!);
            Assert.True(
                document.RootElement.TryGetProperty("result", out var result),
                $"initialize did not succeed: {reply}");
            Assert.Equal(
                "McpServerTemplate",
                result.GetProperty("serverInfo").GetProperty("name").GetString());

            // The point of the guarantee: a plain host, no web server.
            Assert.False(process.HasExited, "the server exited before its ports could be inspected");

            using var probe = new TcpClient();
            var connected = true;
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, webPort)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (SocketException)
            {
                connected = false;
            }

            Assert.False(
                connected,
                $"stdio transport is listening on {webPort}: it built a web server, and this server "
                + "used to bind a port in stdio mode");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task T2_stdio_outside_development_exits_78(string environmentName)
    {
        var (exitCode, stderr) = await RunToCompletionAsync(new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = environmentName,
            ["Transport"] = "stdio",
        });

        Assert.Equal(78, exitCode);
        Assert.Contains("Development", stderr, StringComparison.Ordinal);
    }

    // ── G-2: a failure never exits 0 ──────────────────────────────────────────

    [Fact]
    public async Task T3_unknown_transport_is_a_configuration_failure()
    {
        var (exitCode, stderr) = await RunToCompletionAsync(new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Transport"] = "carrier-pigeon",
        });

        Assert.Equal(78, exitCode);
        Assert.Contains("carrier-pigeon", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T3_http_without_identity_configured_is_a_configuration_failure()
    {
        // Replaces the API-key check this contract deleted. The guarantee is unchanged: a server
        // that cannot authenticate anyone must refuse to start rather than come up and discover
        // it on the first request.
        // Valid in every respect except the one under test, so the refusal that comes back is
        // the one this asserts. Without the trusted network declared, the transport guard refuses
        // first and the test would pass on the wrong message.
        var (exitCode, stderr) = await RunToCompletionAsync(new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Transport"] = "http",
            ["HttpTransport__KnownNetworks__0"] = "127.0.0.0/8",
        });

        Assert.Equal(78, exitCode);
        Assert.Contains("Authentication:IdentityProviders", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T3_a_plaintext_provider_url_exits_78()
    {
        // The case T-3 actually names. It exited 70 with a stack trace, because the provider
        // registrations raised a type Program.cs maps to "unhandled" alongside genuine crashes.
        var (exitCode, stderr) = await RunToCompletionAsync(new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Transport"] = "stdio",
            ["Providers__JsonPlaceholder__BaseUrl"] = "http://jsonplaceholder.typicode.com",
        });

        Assert.Equal(78, exitCode);
        Assert.Contains("HTTPS", stderr, StringComparison.OrdinalIgnoreCase);

        // An operator reading this needs the setting to change, not a stack trace to read.
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
    }

    [Theory]
    // A malformed setting is the operator's mistake, not the server's fault. Each of these exited
    // 70 with a stack trace, which sends whoever is on call looking for a bug in the code.
    [InlineData("HttpTransport__Port", "notanumber", "http")]
    [InlineData("HttpTransport__Port", "999999", "http")]
    [InlineData("Limits__PerPrincipalPerMinute", "0", "stdio")]
    public async Task T3_a_malformed_setting_exits_78(string key, string value, string transport)
    {
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = transport == "stdio" ? "Development" : "Production",
            ["Transport"] = transport,
            [key] = value,
        };

        var (exitCode, stderr) = await RunToCompletionAsync(environment);

        Assert.Equal(78, exitCode);
        Assert.Contains(key.Replace("__", ":", StringComparison.Ordinal), stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (5) — a File sink at an index other than 1 that cannot write: it used to
    /// write nothing and say nothing. Beneath a file, so no directory can be made there on any
    /// platform; the message names the path resolved against the process's working directory.
    /// </summary>
    [Fact]
    public async Task T3_a_file_sink_that_cannot_write_exits_78_naming_the_resolved_path()
    {
        const string path = "appsettings.json/unwritable-.log";
        var resolved = Path.GetFullPath(path, Path.GetDirectoryName(ServerExecutable())!);

        var (exitCode, stderr) = await RunToCompletionAsync(new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Transport"] = "stdio",
            ["Serilog__WriteTo__2__Name"] = "File",
            ["Serilog__WriteTo__2__Args__path"] = path,
        });

        Assert.Equal(78, exitCode);
        Assert.Contains($"Serilog:WriteTo:2 is a File log sink writing to {resolved}", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (5) — Serilog's self-log reaches stderr, so a sink failure the startup
    /// check cannot see is said aloud. The failure used here is a sink Serilog cannot find by its
    /// name: it reports that to the self-log alone, which was off.
    /// </summary>
    [Fact]
    public async Task T3_serilogs_self_log_reaches_stderr()
    {
        var spawned = Start(
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Transport"] = "stdio",
                ["Serilog__WriteTo__2__Name"] = "Flie",
            },
            redirectStdin: true);
        using var process = spawned.Process;

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !spawned.Stderr.Contains("Unable to find a method called Flie", StringComparison.Ordinal))
            {
                Assert.False(process.HasExited, $"the server exited: {spawned.Stderr}");
                await Task.Delay(100);
            }

            Assert.Contains("Unable to find a method called Flie", spawned.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// contract-003 · G-11, contract-005 review round 2 — settings are read once, at startup, where they
    /// are checked. The host watched its settings files and applied what they gained while it ran: a
    /// Kestrel endpoint written into appsettings.json after startup — a key the settings allowlist
    /// refuses at startup — opened a listener no check had seen. The server's content root here is a
    /// directory of its own holding copies of the shipped settings files, so the file changed is the one
    /// it reads; this process watches the same file, so the window below starts once the change has been
    /// delivered. A restart reads the change, and refuses it.
    /// </summary>
    [Fact]
    public async Task T3_a_settings_file_changed_while_the_server_runs_changes_nothing_until_it_restarts()
    {
        var root = Directory.CreateTempSubdirectory("mcp-settings-read-once-").FullName;
        try
        {
            var bin = Path.GetDirectoryName(ServerExecutable())!;
            foreach (var file in new[] { "appsettings.json", "appsettings.Production.json" })
            {
                File.Copy(Path.Combine(bin, file), Path.Combine(root, file));
            }

            var port = FreePort();
            var late = FreePort();
            var environment = IdentityEnvironment();
            environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            environment["Transport"] = "http";
            environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            environment["HttpTransport__BindAddress"] = "127.0.0.1";
            environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var spawned = Start(environment, workingDirectory: root);
            using (var process = spawned.Process)
            {
                try
                {
                    Assert.True(await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(60)) == HttpStatusCode.OK, $"the server did not come up: {spawned.Stderr}");

                    using var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root);
                    var delivered = new TaskCompletionSource();
                    using var watch = files.Watch("appsettings.json").RegisterChangeCallback(_ => delivered.TrySetResult(), null);

                    var settings = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "appsettings.json")))!.AsObject();
                    settings["Kestrel"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Endpoints"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["Late"] = new System.Text.Json.Nodes.JsonObject { ["Url"] = $"http://127.0.0.1:{late}" },
                        },
                    };
                    await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), settings.ToJsonString());
                    await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    // A watched JSON file reloads 250 ms after it changes, and Kestrel binds a new endpoint at
                    // once: three seconds is well past both.
                    var opened = await AnswerWithinAsync(http, late, TimeSpan.FromSeconds(3));
                    Assert.True(
                        opened is null,
                        $"a Kestrel endpoint written into appsettings.json after startup opened http://127.0.0.1:{late}: /healthz "
                        + $"answered {(int?)opened} there. The running server applied a setting no startup check had read.");

                    // Unchanged until it restarts: it answers where it did.
                    Assert.Equal(HttpStatusCode.OK, await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            // The restart reads the file as it now is, and refuses what it gained.
            var (exitCode, stderr) = await RunToCompletionAsync(environment, workingDirectory: root);
            Assert.Equal(78, exitCode);
            Assert.Contains("'Kestrel:Endpoints:Late:Url' is not a setting this server honours", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// contract-005 review round 3 — an explicit request to read settings again, by any route the host reads
    /// it from, is a setting the server would ignore: it reads its settings once whatever the request says
    /// (HostBuilders). The frame refuses such a setting everywhere else, so this one stops the server too,
    /// naming it and where it came from. The server's own "false", first on its command line, never does.
    /// </summary>
    [Theory]
    [InlineData("http", "the command line")]
    [InlineData("http", "DOTNET_")]
    [InlineData("http", "ASPNETCORE_")]
    [InlineData("stdio", "the command line")]
    public async Task T3_a_request_to_read_settings_again_exits_78_naming_where_it_came_from(string transport, string route)
    {
        const string setting = "hostBuilder:reloadConfigOnChange";
        Dictionary<string, string> environment;
        if (transport == "http")
        {
            environment = IdentityEnvironment();
            environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            environment["Transport"] = "http";
            environment["HttpTransport__Port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
            environment["HttpTransport__BindAddress"] = "127.0.0.1";
            environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();
        }
        else
        {
            environment = new() { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["Transport"] = "stdio" };
        }

        string[] arguments = [];
        if (route == "the command line")
        {
            arguments = [$"--{setting}=true"];
        }
        else
        {
            environment[$"{route}{setting.Replace(":", "__", StringComparison.Ordinal)}"] = "true";
        }

        var spawned = Start(environment, redirectStdin: transport == "stdio", arguments: arguments);
        using var process = spawned.Process;
        try
        {
            // Started means the frame installed itself; refused means it exited first.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!process.HasExited && !spawned.Stderr.Contains("Frame installed:", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(
                process.HasExited,
                $"asked to read its settings again ({setting}=true, from {route}), the {transport} server started and installed its "
                + "frame: a request it ignores, as it reads its settings once.");

            await process.WaitForExitAsync();
            Assert.Equal(78, process.ExitCode);
            var from = route == "the command line" ? route : $"the environment, as {route}{setting.Replace(":", "__", StringComparison.Ordinal)}";
            Assert.Contains($"{setting} is 'true' from {from}", spawned.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// contract-005 · T-10 — the HTTP server stops within its own 8 seconds of being told to, so it finishes inside a
    /// container's stop grace (HostBuilders.ShutdownTimeout). The framework's own host setting for that time,
    /// shutdownTimeoutSeconds, is one it would ignore, so it is refused like every other such setting, by any route the
    /// host reads it from, naming where it came from.
    /// </summary>
    [Theory]
    [InlineData("the command line")]
    [InlineData("DOTNET_")]
    [InlineData("ASPNETCORE_")]
    [InlineData("no prefix")]
    public async Task T10_a_shutdown_timeout_setting_exits_78_naming_where_it_came_from(string route)
    {
        const string setting = "shutdownTimeoutSeconds";
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.0.0.1";
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        string[] arguments = [];
        var variable = route == "no prefix" ? setting : $"{route}{setting}";
        if (route == "the command line")
        {
            arguments = [$"--{setting}=30"];
        }
        else
        {
            environment[variable] = "30";
        }

        var spawned = Start(environment, arguments: arguments);
        using var process = spawned.Process;
        try
        {
            // Started means the frame installed itself; refused means it exited first.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!process.HasExited && !spawned.Stderr.Contains("Frame installed:", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(
                process.HasExited,
                $"given {setting}=30 from {route}, the http server started and installed its frame: a setting it ignores, as it stops "
                + "within its own 8 seconds.");

            await process.WaitForExitAsync();
            Assert.Equal(78, process.ExitCode);
            var from = route == "the command line" ? route : $"the environment, as {variable}";
            Assert.Contains($"{setting} is '30' from {from}", spawned.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>What /healthz on 127.0.0.1:<paramref name="port"/> answered within <paramref name="window"/>, or null when nothing did.</summary>
    private static async Task<HttpStatusCode?> AnswerWithinAsync(HttpClient http, int port, TimeSpan window, string host = "127.0.0.1")
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri($"http://{host}:{port}/healthz"));
                return response.StatusCode;
            }
            catch (HttpRequestException)
            {
                // Nothing listening there.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task T3_a_configuration_failure_never_exits_zero()
    {
        // The regression this guards: every one of these used to log a fatal error and exit 0,
        // so a supervisor kept a server that had refused to start in rotation.
        foreach (var environment in new[]
        {
            new Dictionary<string, string> { ["Transport"] = "nonsense", ["ASPNETCORE_ENVIRONMENT"] = "Development" },
            new Dictionary<string, string> { ["Transport"] = "stdio", ["ASPNETCORE_ENVIRONMENT"] = "Production" },
        })
        {
            var (exitCode, _) = await RunToCompletionAsync(environment);
            Assert.NotEqual(0, exitCode);
        }
    }

    // ── G-3 / G-7: the HTTP pipeline ──────────────────────────────────────────

    internal sealed class HttpServer : IAsyncDisposable
    {
        public required Process Process { get; init; }
        public required HttpClient Client { get; init; }
        public Spawned? Spawned { get; init; }

        public string StderrSnapshot() => Spawned?.Stderr ?? string.Empty;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
            Process.Dispose();
        }
    }

    /// <summary>
    /// Identity as a deployment configures it. Nothing here reaches the authority: these tests
    /// present no token, so no discovery is triggered.
    /// </summary>
    internal static Dictionary<string, string> IdentityEnvironment() => new()
    {
        // (loopback declared below for G-12)
        ["Authentication__Resource"] = "https://mcp.example.com/mcp",
        ["Authentication__IdentityProviders__corp__Authority"] = "https://login.example.com",
        ["Authentication__IdentityProviders__corp__Issuer"] = "https://login.example.com/",
        ["Authentication__IdentityProviders__corp__Algorithms__0"] = "RS256",
        ["Authentication__IdentityProviders__corp__ScopeCatalog__0"] = "weather:read",
        ["Authentication__IdentityProviders__corp__ScopeCatalog__1"] = "observations:read",
        ["Authentication__IdentityProviders__corp__ScopeCatalog__2"] = "demo:read",

        // Every provider answers to corp in these tests (G-5).
        ["Providers__Smhi__IdentityProvider"] = "corp",
        ["Providers__SmhiObs__IdentityProvider"] = "corp",
        ["Providers__JsonPlaceholder__IdentityProvider"] = "corp",
        // contract-002 · G-12 — these run as Production over loopback plaintext, which the
        // transport guard refuses unless the deployment says a proxy is in front. Declaring the
        // loopback network is the honest form of that here: the test harness is the proxy.
        ["HttpTransport__KnownNetworks__0"] = "127.0.0.0/8",
    };

    /// <summary>
    /// The program in Production over HTTP, as a deployment runs it, ended with <paramref name="cleanup"/>, its content
    /// root <paramref name="workingDirectory"/> or, when none is given, the build output. contract-005 · T-14 — the tests
    /// that only look at it share one (<see cref="SharedServers.ServerAsync"/>).
    /// </summary>
    private static async Task<HttpServer> StartHttpAsync(SpawnedServer.Cleanup cleanup, string? workingDirectory = null)
    {
        var port = FreePort();
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.0.0.1";

        // contract-003 · G-8 — Production refuses to start without Redis.
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        var spawned = Start(cleanup, environment, workingDirectory: workingDirectory);
        var process = spawned.Process;

        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                Assert.Fail($"the server exited during startup ({process.ExitCode}): {spawned.Stderr}");
            }

            try
            {
                using var probe = await client.GetAsync("/healthz");
                return new HttpServer { Process = process, Client = client, Spawned = spawned };
            }
            catch (HttpRequestException)
            {
                await Task.Delay(300);
            }
        }

        process.Kill(entireProcessTree: true);
        throw new TimeoutException("the HTTP server did not start within 60 seconds");
    }

    [Fact]
    public async Task T8_liveness_and_readiness_answer_without_a_credential()
    {
        // contract-005 · T-14 — the collection's one started program (SharedServers), which nothing here changes.
        var server = await shared.ServerAsync();

        using var liveness = await server.Client.GetAsync("/healthz");
        using var readiness = await server.Client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal("alive", await liveness.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal("ready", await readiness.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task T4_a_foreign_host_header_is_rejected_before_anything_else()
    {
        // contract-005 · T-14 — the collection's one started program (SharedServers), which nothing here changes.
        var server = await shared.ServerAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = "attacker.example.com";

        using var response = await server.Client.SendAsync(request);

        // Rejected by the host allowlist, even though /healthz itself needs no credential —
        // which places the allowlist ahead of the health endpoints in the pipeline.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 5 addendum — a loopback bind written another way than ::1
    /// answers for the loopback names. Kestrel binds [::1] for 0::1 and 0:0:0:0:0:0:0:1, exactly as for ::1; the
    /// host allowlist used to default to the spelling, which no request can match, so the server started and
    /// answered every request 400, under its own names too.
    /// </summary>
    [Theory]
    [InlineData("0::1")]
    [InlineData("0:0:0:0:0:0:0:1")]
    public async Task T11_2_a_loopback_bind_however_written_answers_for_the_loopback_names(string bindAddress)
    {
        var port = FreePort();
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = bindAddress;
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var spawned = Start(environment);
        using var process = spawned.Process;
        try
        {
            var bracketed = await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(60), "[::1]");
            var named = await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(5), "localhost");
            Assert.True(
                bracketed == HttpStatusCode.OK && named == HttpStatusCode.OK,
                $"a server bound to {bindAddress}, which Kestrel binds as [::1], with no AllowedHosts, answered GET /healthz at "
                + $"http://[::1]:{port} with {(int?)bracketed} and at http://localhost:{port} with {(int?)named}. {spawned.Stderr}");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 6 addendum — an empty bind address refuses to start, naming
    /// the key. With an allowed host set, as a compose file with an unset variable leaves it, the server used to
    /// pass its checks and stop in Kestrel instead: "Invalid url", exit 70.
    /// </summary>
    [Fact]
    public async Task T11_2_an_empty_bind_address_refuses_to_start_naming_the_key()
    {
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = string.Empty;
        environment["HttpTransport__AllowedHosts__0"] = "mcp.example.com";
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        var (exitCode, stderr) = await RunToCompletionAsync(environment);

        Assert.True(
            exitCode == 78 && stderr.Contains("HttpTransport:BindAddress is empty", StringComparison.Ordinal),
            $"a server with an empty HttpTransport:BindAddress exited {exitCode}: {stderr}");
    }

    /// <summary>
    /// contract-005 · T-17 (G-18) — a spawned server loads no key ring. Data protection came with authentication,
    /// unused, and at startup the framework loaded a key ring for it: on Windows the user's own, from the profile's
    /// ASP.NET\DataProtection-Keys, saying so on the log. Development, whose log carries that Information line.
    /// </summary>
    [Fact]
    public async Task T17_a_spawned_server_loads_no_key_ring()
    {
        var port = FreePort();
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.0.0.1";

        // Development serves the demo provider's writing tool, whose scope the catalog must hold.
        environment["Authentication__IdentityProviders__corp__ScopeCatalog__3"] = "demo:write";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var spawned = Start(environment);
        using var process = spawned.Process;
        try
        {
            Assert.True(await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(60)) == HttpStatusCode.OK, $"the server did not come up: {spawned.Stderr}");

            var keyRing = spawned.Stderr.Split('\n')
                .Where(l => l.Contains("DataProtection", StringComparison.Ordinal) || l.Contains("key repository", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Trim())
                .ToList();
            Assert.True(keyRing.Count == 0, $"a spawned server loaded a key ring: {string.Join(" | ", keyRing)}");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 6 — a bind address that carries a port refuses to start. The
    /// server listens on HttpTransport:Port, and [::1]:9999 used to start there, on [::1], ignoring 9999: a setting
    /// the server would not act on.
    /// </summary>
    [Fact]
    public async Task T11_2_a_bind_address_that_carries_a_port_refuses_to_start()
    {
        var port = FreePort();
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "[::1]:9999";
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var spawned = Start(environment);
        using var process = spawned.Process;
        try
        {
            HttpStatusCode? answered = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (answered is null && !process.HasExited && DateTime.UtcNow < deadline)
            {
                answered = await AnswerWithinAsync(http, port, TimeSpan.FromMilliseconds(200), "[::1]");
            }

            if (process.HasExited)
            {
                await process.WaitForExitAsync();
            }

            Assert.True(
                answered is null && process.HasExited && process.ExitCode == 78
                    && spawned.Stderr.Contains("HttpTransport:BindAddress is '[::1]:9999', which carries a port.", StringComparison.Ordinal),
                answered is null
                    ? $"a server with HttpTransport:BindAddress [::1]:9999 did not refuse, naming the port: {spawned.Stderr}"
                    : $"a server with HttpTransport:BindAddress [::1]:9999 started, and answered GET /healthz at http://[::1]:{port}, "
                        + $"its HttpTransport:Port, with {(int)answered}: 9999 was ignored.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 5 addendum 2, then round 8 — another loopback address answers
    /// under its standard form, which is now the only way to write it. Kestrel binds 127.0.0.2 alone, and a request
    /// reaching it carries Host 127.0.0.2. Round 5 turned 127.2 into that; round 8 refuses 127.2, saying how it would
    /// be read and what to write, and 127.0.0.2 answers as before.
    /// </summary>
    [Fact]
    public async Task T11_2_another_loopback_address_answers_in_its_standard_form_and_no_other()
    {
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.2";
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        var (exitCode, stderr) = await RunToCompletionAsync(environment);
        Assert.True(
            exitCode == 78 && stderr.Contains("HttpTransport:BindAddress is '127.2', which would be read as 127.0.0.2; write 127.0.0.2", StringComparison.Ordinal),
            $"a server with HttpTransport:BindAddress 127.2 exited {exitCode}: {stderr}");

        var port = FreePort();
        environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.0.0.2";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var spawned = Start(environment);
        using var process = spawned.Process;
        try
        {
            var answer = await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(60), "127.0.0.2");
            Assert.True(
                answer == HttpStatusCode.OK,
                $"a server bound to 127.0.0.2, with no AllowedHosts, answered GET /healthz at http://127.0.0.2:{port} with "
                + $"{(int?)answer}. {spawned.Stderr}");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 8 — an accepted address this machine does not hold refuses to
    /// start, naming the bind address and the socket error in plain words: Kestrel cannot bind it, and the server used
    /// to exit 70, "MCP Server terminated unexpectedly", with a stack trace. 192.0.2.1 is a documentation address
    /// (RFC 5737), held by no machine.
    /// </summary>
    [Fact]
    public async Task T11_2_a_bind_address_this_machine_does_not_hold_refuses_to_start()
    {
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "192.0.2.1";
        environment["HttpTransport__AllowedHosts__0"] = "mcp.example.com";
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        var (exitCode, stderr) = await RunToCompletionAsync(environment);

        Assert.True(
            exitCode == 78
                && stderr.Contains("HttpTransport:BindAddress is '192.0.2.1', and the server cannot listen there: the address is not available on this machine", StringComparison.Ordinal)
                && !stderr.Contains("   at ", StringComparison.Ordinal),
            $"a server bound to 192.0.2.1 exited {exitCode}: {stderr}");
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 9 — a trusted proxy or network that is not one refuses to start,
    /// naming its key. It used to be parsed only as the pipeline was built: exit 70, with a stack trace.
    /// </summary>
    [Theory]
    [InlineData("HttpTransport__KnownProxies__0", "not-an-ip", "HttpTransport:KnownProxies:0 is 'not-an-ip'")]
    [InlineData("HttpTransport__KnownNetworks__0", "10.0.0.0/99", "HttpTransport:KnownNetworks:0 is '10.0.0.0/99'")]
    public async Task T11_2_a_trusted_proxy_that_is_not_one_refuses_to_start_naming_its_key(string variable, string value, string refusal)
    {
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.0.0.1";
        environment[variable] = value;
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        var (exitCode, stderr) = await RunToCompletionAsync(environment);

        Assert.True(
            exitCode == 78 && stderr.Contains(refusal, StringComparison.Ordinal) && !stderr.Contains("   at ", StringComparison.Ordinal),
            $"a server with {variable}={value} exited {exitCode}: {stderr}");
    }

    /// <summary>
    /// contract-005 · T-11 (2) (G-12 (2)), review round 8 — a port already in use refuses to start, naming
    /// HttpTransport:Port, the cause: the server used to exit 70 with Kestrel's "address already in use" and a stack trace.
    /// </summary>
    [Fact]
    public async Task T11_2_a_port_in_use_refuses_to_start_naming_the_port()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.0.0.1";
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();

        var (exitCode, stderr) = await RunToCompletionAsync(environment);

        Assert.True(
            exitCode == 78
                && stderr.Contains($"HttpTransport:Port is {port}, and the server cannot listen on it", StringComparison.Ordinal)
                && stderr.Contains("already in use", StringComparison.Ordinal)
                && !stderr.Contains("   at ", StringComparison.Ordinal),
            $"a server on a port already in use ({port}) exited {exitCode}: {stderr}");
    }

    [Fact]
    public async Task T4_the_mcp_endpoint_requires_a_verified_token()
    {
        // Replaces the shared-key gate. A key everyone copies could not say who was calling; a
        // token can, and an unreadable one is refused exactly like an absent one.
        // contract-005 · T-14 — the collection's one started program (SharedServers), which nothing here changes.
        var server = await shared.ServerAsync();

        using var missing = await Post(server, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var unreadable = await Post(server, token: "not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, unreadable.StatusCode);

        // Both are told where to go, which a bare 401 never did.
        foreach (var response in new[] { missing, unreadable })
        {
            var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
            Assert.Contains("resource_metadata", challenge, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Posts an initialize request, which is what a client sends. A GET is rejected as a bad
    /// method before authorization is consulted at all.
    /// </summary>
    private static async Task<HttpResponseMessage> Post(HttpServer server, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"acceptance-test","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        return await server.Client.SendAsync(request);
    }

    // ── G-4 / UC-3: a failing tool answers, and says what to do ───────────────

    [Fact]
    public async Task T5_a_tool_that_fails_still_answers_the_client()
    {
        // Every other test for this guarantee calls the provider directly. That misses the thing
        // that actually reaches a client: the answer has to survive the server's filter pipeline.
        // It did not — a tool throwing McpException produced no JSON-RPC response at all, and the
        // caller waited forever.
        var reply = await CallToolAsync("get_current_weather", new { latitude = 999, longitude = 999 });

        Assert.NotNull(reply);

        var result = reply!.Value.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));

        // The recovery has to reach the caller, not just the server's log.
        Assert.Contains("Stockholm", text!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Calls one tool over stdio and returns the reply, or null if none arrived. A null return is
    /// the failure this exists to catch, so the wait is generous rather than tight.
    /// </summary>
    private async Task<JsonElement?> CallToolAsync(string tool, object arguments)
    {
        using var process = Start(
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Transport"] = "stdio",
            },
            redirectStdin: true).Process;

        try
        {
            var cancellation = new CancellationTokenSource(60_000).Token;

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "acceptance-test", version = "1" },
                },
            }));
            await process.StandardInput.FlushAsync();
            await process.StandardOutput.ReadLineAsync(cancellation);

            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = tool, arguments },
            }));
            await process.StandardInput.FlushAsync();

            try
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellation);
                if (string.IsNullOrWhiteSpace(line))
                    return null;

                return JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (OperationCanceledException)
            {
                return null; // no reply: the failure this test is for
            }
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task T8_a_foreign_origin_is_refused_before_the_token_is_examined()
    {
        // contract-002 · G-7. The C# SDK does not validate Origin and the specification requires
        // it: without this a page in a browser drives this server using a session the browser
        // already holds, and every downstream control sees a valid principal.
        // contract-005 · T-14 — the collection's one started program (SharedServers), which nothing here changes.
        var server = await shared.ServerAsync();

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        foreign.Headers.Add("Origin", "https://evil.example");
        using var refused = await server.Client.SendAsync(foreign);

        // 403 rather than 401: refused for being cross-origin, not told whether a token would
        // have helped.
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // A caller that sends no Origin at all — a CLI, a native client, server to server — is
        // the ordinary case and is not affected.
        using var native = await Post(server, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, native.StatusCode);
    }

    // ── G-11: documented names are the names the server exposes ───────────────

    [Fact]
    public async Task T12_every_documented_tool_name_is_one_the_server_exposes()
    {
        // contract-005 · T-14 — the collection's one reading of the tools (SharedServers).
        var exposed = await shared.ToolNamesAsync();
        Assert.NotEmpty(exposed);

        var root = RepositoryRoot();
        var documents = Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "README.md"))
            .Where(File.Exists);

        // Tool names as a reader would copy them: snake_case identifiers in the docs.
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:get|create|add|list|update|delete)_[a-z0-9_]+\b");

        var undefined = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in documents)
        {
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(File.ReadAllText(file)))
            {
                if (!exposed.Contains(match.Value))
                    undefined.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(
            undefined.Count == 0,
            "the docs name tools the server does not expose: " + string.Join(", ", undefined));

        // Tool tables list one name per row as `name`. Those cells must be wire names: a
        // PascalCase C# method name there reads as a tool name and is not callable. Anchored on
        // the table header rather than the section heading, because the tables sit under
        // per-provider sub-headings that say nothing about tools.
        var tableCell = new System.Text.RegularExpressions.Regex(@"^\|\s*`([A-Za-z_][A-Za-z0-9_]*)`\s*\|");
        var toolTableHeader = new System.Text.RegularExpressions.Regex(@"^\|\s*Tool\s*\|");
        var wrongInTables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in documents)
        {
            var inToolTable = false;
            foreach (var line in File.ReadLines(file))
            {
                if (toolTableHeader.IsMatch(line))
                {
                    inToolTable = true;
                    continue;
                }

                // A table ends at the first line that is not one of its rows.
                if (inToolTable && !line.StartsWith('|'))
                {
                    inToolTable = false;
                    continue;
                }

                if (!inToolTable)
                    continue;

                var cell = tableCell.Match(line);
                if (cell.Success && !exposed.Contains(cell.Groups[1].Value))
                    wrongInTables.Add($"{Path.GetFileName(file)}: {cell.Groups[1].Value}");
            }
        }

        // T-12's failure condition is "a PascalCase spelling of a tool name appears in the docs",
        // which is wider than the tables. Five such lines sat outside them — a file-tree comment,
        // a testing example, a transcript — each reading to a caller as the name to call. The
        // exception is a genuine C# reference: Type.Method(), or a fenced code block.
        var pascalOfExposed = exposed
            .ToDictionary(
                name => string.Concat(name.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..])),
                name => name,
                StringComparer.Ordinal);

        var pascalOutsideTables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in documents)
        {
            var inCodeFence = false;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    // Only a C# fence is exempt, and only because PascalCase there is the method
                    // and is correct. A bare fence holds file trees, transcripts and sample output
                    // — where a PascalCase name reads as the name to call, which is the defect.
                    // Excluding every fence made this check blind to three README lines it was
                    // written to catch.
                    var language = trimmed[3..].Trim();
                    inCodeFence = !inCodeFence &&
                        (language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
                         language.Equals("cs", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                if (inCodeFence)
                    continue;

                foreach (var (pascal, wire) in pascalOfExposed)
                {
                    if (!line.Contains(pascal, StringComparison.Ordinal))
                        continue;

                    // A qualified C# reference is the method, and is correct as written.
                    if (line.Contains($".{pascal}", StringComparison.Ordinal))
                        continue;

                    pascalOutsideTables.Add($"{Path.GetFileName(file)}:{lineNumber} {pascal} (wire name: {wire})");
                }
            }
        }

        Assert.True(
            pascalOutsideTables.Count == 0,
            "PascalCase tool names presented to a reader: " + string.Join("; ", pascalOutsideTables));

        Assert.True(
            wrongInTables.Count == 0,
            "tool tables list names the server does not expose: " + string.Join(", ", wrongInTables));
    }

    [Fact]
    public async Task T12_every_exposed_tool_is_documented()
    {
        // contract-005 · T-14 — the collection's one reading of the tools (SharedServers).
        var exposed = await shared.ToolNamesAsync();

        var root = RepositoryRoot();
        var text = new StringBuilder();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
            text.Append(File.ReadAllText(file));
        text.Append(File.ReadAllText(Path.Combine(root, "README.md")));

        var documentation = text.ToString();
        var missing = exposed.Where(name => !documentation.Contains(name, StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0, "tools the server exposes but the docs never name: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The tool names a Development stdio server lists, the server ended with <paramref name="cleanup"/>. contract-005 ·
    /// T-14 — read once for the tests that need them (<see cref="SharedServers.ToolNamesAsync"/>).
    /// </summary>
    private static async Task<HashSet<string>> ListToolNamesAsync(SpawnedServer.Cleanup cleanup)
    {
        using var process = Start(
            cleanup,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Transport"] = "stdio",
            },
            redirectStdin: true).Process;

        try
        {
            var cancellation = new CancellationTokenSource(45_000).Token;

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "acceptance-test", version = "1" },
                },
            }));
            await process.StandardInput.FlushAsync();
            await process.StandardOutput.ReadLineAsync(cancellation);

            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/list" }));
            await process.StandardInput.FlushAsync();

            var reply = await process.StandardOutput.ReadLineAsync(cancellation);
            Assert.False(string.IsNullOrWhiteSpace(reply), "the server sent no reply to tools/list");

            using var document = JsonDocument.Parse(reply!);
            return document.RootElement
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    // ── contract-003: the shipped process is the tested one ───────────────────

    private static string FrameManifestIn(string stderr)
    {
        var line = stderr.Split('\n').FirstOrDefault(l => l.Contains("Frame installed:", StringComparison.Ordinal));
        Assert.False(line is null, $"the process logged no frame manifest: {stderr}");
        return line![(line.IndexOf(" :: ", StringComparison.Ordinal) + 4)..].Trim();
    }

    private static async Task WaitForManifestAsync(Spawned spawned)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !spawned.Stderr.Contains("Frame installed:", StringComparison.Ordinal))
        {
            Assert.False(spawned.Process.HasExited, $"the server exited: {spawned.Stderr}");
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task T10_the_shipped_process_installs_exactly_the_frame_the_tests_exercise()
    {
        // The real program, in Production, as a deployment runs it.
        // contract-005 · T-14 — the collection's one started program (SharedServers), which nothing here changes.
        var shipped = await shared.ServerAsync();
        await WaitForManifestAsync(shipped.Spawned!);
        var shippedManifest = FrameManifestIn(shipped.StderrSnapshot());

        // The in-process server the gate tests use, with the providers Production enables.
        using var corp = new TestIdentityProvider("corp", "https://login.example.com/");
        await using var tested = await Identity.InProcessServer.StartAsync(
            [corp], modules: [new McpServerTemplate.Providers.Smhi.SmhiModule(), new McpServerTemplate.Providers.SmhiObs.SmhiObsModule()]);
        var testedManifest = McpServerTemplate.Infrastructure.Frame.FrameManifest.Describe(
            tested.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value,
            tested.Services.GetRequiredService<McpServerTemplate.Infrastructure.Frame.FrameManifest>(),
            tested.Services.GetRequiredService<McpServerTemplate.Infrastructure.Frame.PolicyRegistry>());

        // Every filter list, whose filter sits where, and every policy in force: identical.
        Assert.Equal(shippedManifest, testedManifest);
    }

    [Fact]
    public void T10_the_test_project_declares_no_request_checks_of_its_own()
    {
        // The names are assembled so this file does not match itself.
        var registrations = new[] { "Add" + "CallToolFilter", "Add" + "ListToolsFilter", "With" + "RequestFilters", "With" + "MessageFilters", "Add" + "IncomingFilter" };
        var separator = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "McpServerTemplate.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Where(f => registrations.Any(r => File.ReadAllText(f).Contains(r, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task T11_production_serves_no_demo_tools()
    {
        // contract-005 · T-14 — the collection's one started program (SharedServers), which nothing here changes.
        var shipped = await shared.ServerAsync();
        await WaitForManifestAsync(shipped.Spawned!);
        var log = shipped.StderrSnapshot();

        Assert.Contains("providers=Smhi,SmhiObs ", log, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonPlaceholder", FrameManifestIn(log), StringComparison.Ordinal);
        Assert.DoesNotContain("demo:", FrameManifestIn(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T12_the_retired_throttle_setting_stops_the_shipped_process()
    {
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["Limits__Redis"] = await TestRedis.ConnectionStringAsync();
        environment["RateLimit__MaxCallsPerToolPerMinute"] = "10";

        var (exitCode, stderr) = await RunToCompletionAsync(environment);

        Assert.Equal(78, exitCode);
        Assert.Contains("'RateLimit:MaxCallsPerToolPerMinute' is retired", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T15_the_shipped_local_mode_runs_the_same_gate_against_redis()
    {
        // The real program over stdio, with limits in Redis. Its Development principal holds only
        // observations:read here: every index is overridden, since configuration merges arrays by index.
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Transport"] = "stdio",
            ["Limits__Redis"] = await TestRedis.ConnectionStringAsync(),
        };
        for (var i = 0; i < 4; i++)
        {
            environment[$"Development__DevPrincipal__Scopes__{i}"] = "observations:read";
        }

        var spawned = Start(environment, redirectStdin: true);
        using var process = spawned.Process;
        try
        {
            async Task<string> SendAsync(int id, string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(30_000);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.False(line is null, $"the server closed its output: {spawned.Stderr}");
                    if (line!.Contains($"\"id\":{id}", StringComparison.Ordinal))
                    {
                        return line;
                    }
                }
            }

            await SendAsync(1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "acceptance-test", version = "1" } });

            // A tool its scopes do not reach.
            var hidden = await SendAsync(2, "tools/call", new { name = "get_forecast", arguments = new { latitude = 59.3, longitude = 18.0 } });
            Assert.Contains("rule: insufficient_scope", hidden, StringComparison.Ordinal);

            // A tool it may call, with an argument the tool does not declare.
            var extra = await SendAsync(3, "tools/call", new { name = "get_recent_temperature", arguments = new { latitude = 59.3, longitude = 18.0, station = "x" } });
            Assert.Contains("rule: extraneous-argument", extra, StringComparison.Ordinal);

            // A request kind nobody governs.
            var ungoverned = await SendAsync(4, "logging/setLevel", new { level = "debug" });
            Assert.Contains("rule: request-kind", ungoverned, StringComparison.Ordinal);

            // And the limits it checked were held in Redis.
            await WaitForManifestAsync(spawned);
            Assert.Contains("limits=Redis", spawned.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}

/// <summary>
/// contract-005 · T-14 — the collection ServerProcessTests runs in, one test at a time, and what its tests share
/// (<see cref="ServerProcessTests.SharedServers"/>): started for the first test that asks, ended when the collection ends.
/// </summary>
[CollectionDefinition("server-process")]
public sealed class ServerProcessDefinition : ICollectionFixture<ServerProcessTests.SharedServers>;
