using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace McpServerTemplate.Tests;

/// <summary>
/// contract-001 · T-1, T-2, T-3, T-4, T-8, T-12 — guarantees about the server as a process.
///
/// These start the built executable. Nothing here can be established from inside the test
/// host: whether a transport binds a socket, what exit code a failure produces, and which
/// tool names a client actually sees are all properties of the process, not of a class.
/// </summary>
[Collection("server-process")]
public class ServerProcessTests
{
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
    private sealed class Spawned
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

    private static Spawned Start(IDictionary<string, string> environment, bool redirectStdin = false)
    {
        var exe = ServerExecutable();
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Clear inherited environment so a developer's own ASPNETCORE_ENVIRONMENT cannot
        // change what these tests assert.
        info.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        info.Environment.Remove("DOTNET_ENVIRONMENT");
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        return new Spawned(Process.Start(info)!);
    }

    private static async Task<(int ExitCode, string Stderr)> RunToCompletionAsync(
        IDictionary<string, string> environment)
    {
        var spawned = Start(environment);
        using var process = spawned.Process;

        var exited = await Task.Run(() => process.WaitForExit(60_000));
        Assert.True(exited, "the server did not exit; it was expected to refuse to start");

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
    [InlineData("RateLimit__MaxCallsPerToolPerMinute", "0", "stdio")]
    public async Task T3_a_malformed_setting_exits_78(string key, string value, string transport)
    {
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = transport == "stdio" ? "Development" : "Production",
            ["Transport"] = transport,
            ["Authentication__ApiKey"] = "acceptance-test-key",
            [key] = value,
        };

        var (exitCode, stderr) = await RunToCompletionAsync(environment);

        Assert.Equal(78, exitCode);
        Assert.Contains(key.Replace("__", ":", StringComparison.Ordinal), stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
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

    private sealed class HttpServer : IAsyncDisposable
    {
        public required Process Process { get; init; }
        public required HttpClient Client { get; init; }

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
        // contract-002 · G-12 — these run as Production over loopback plaintext, which the
        // transport guard refuses unless the deployment says a proxy is in front. Declaring the
        // loopback network is the honest form of that here: the test harness is the proxy.
        ["HttpTransport__KnownNetworks__0"] = "127.0.0.0/8",
    };

    private static async Task<HttpServer> StartHttpAsync()
    {
        var port = FreePort();
        var environment = IdentityEnvironment();
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["Transport"] = "http";
        environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["HttpTransport__BindAddress"] = "127.0.0.1";

        var spawned = Start(environment);
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
                return new HttpServer { Process = process, Client = client };
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
        await using var server = await StartHttpAsync();

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
        await using var server = await StartHttpAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = "attacker.example.com";

        using var response = await server.Client.SendAsync(request);

        // Rejected by the host allowlist, even though /healthz itself needs no credential —
        // which places the allowlist ahead of the health endpoints in the pipeline.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task T4_the_mcp_endpoint_requires_a_verified_token()
    {
        // Replaces the shared-key gate. A key everyone copies could not say who was calling; a
        // token can, and an unreadable one is refused exactly like an absent one.
        await using var server = await StartHttpAsync();

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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
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
    private static async Task<JsonElement?> CallToolAsync(string tool, object arguments)
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
        await using var server = await StartHttpAsync();

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/");
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
        var exposed = await ListToolNamesAsync();
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
        var exposed = await ListToolNamesAsync();

        var root = RepositoryRoot();
        var text = new StringBuilder();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
            text.Append(File.ReadAllText(file));
        text.Append(File.ReadAllText(Path.Combine(root, "README.md")));

        var documentation = text.ToString();
        var missing = exposed.Where(name => !documentation.Contains(name, StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0, "tools the server exposes but the docs never name: " + string.Join(", ", missing));
    }

    private static async Task<HashSet<string>> ListToolNamesAsync()
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
}
