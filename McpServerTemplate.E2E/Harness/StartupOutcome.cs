using System.Net;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// What a server did when it was started: exited, with its code and its standard error, or came up
/// and answered /readyz.
///
/// contract-005 · T-11, UC-10 — a startup refusal is seen only through the edges an operator has: the
/// exit code and stderr. A server that did not refuse is still running when the outcome is read, so a
/// test can show what it would have served (<see cref="GetDirectAsync"/>) before it is removed.
/// </summary>
public sealed partial class StartupOutcome : IAsyncDisposable
{
    private readonly E2EEnvironment _environment;
    private readonly string _name;
    private readonly IContainer _container;

    internal StartupOutcome(E2EEnvironment environment, string name, IContainer container, long? exitCode, HttpStatusCode? readyz, string stderr)
    {
        _environment = environment;
        _name = name;
        _container = container;
        ExitCode = exitCode;
        Readyz = readyz;
        Stderr = stderr;
    }

    /// <summary>The exit code, or null when the server came up and answered /readyz instead.</summary>
    public long? ExitCode { get; }

    /// <summary>
    /// What /readyz answered under Host mcp.e2e.test when the server came up: 200, or 400 when its host
    /// filter refuses that name.
    /// </summary>
    public HttpStatusCode? Readyz { get; }

    /// <summary>The server's standard error as it stood when the outcome was read.</summary>
    public string Stderr { get; }

    /// <summary>Whether the server came up rather than exiting.</summary>
    public bool Started => ExitCode is null;

    /// <summary>The line on stderr that says why the server would not start, if there is one.</summary>
    public string? RefusalLine =>
        Stderr.Split('\n').FirstOrDefault(l => l.Contains("MCP Server cannot start", StringComparison.Ordinal))?.Trim();

    /// <summary>
    /// The whole refusal: <see cref="RefusalLine"/> and the indented lines that continue it. A refusal
    /// naming settings the server would ignore lists each on a line of its own beneath the first
    /// (contract-005 · G-12 (2), a Kestrel key), so the setting it names is not on the first line.
    /// Docker's log puts its own timestamp before every line the server wrote, so the indent is read
    /// after it.
    /// </summary>
    public string? Refusal
    {
        get
        {
            var lines = Stderr.Split('\n').Select(l => DockerTimestamp().Replace(l, string.Empty)).ToArray();
            var first = Array.FindIndex(lines, l => l.Contains("MCP Server cannot start", StringComparison.Ordinal));
            return first < 0
                ? null
                : string.Join(" ", lines.Skip(first).Take(1).Concat(lines.Skip(first + 1).TakeWhile(l => l.StartsWith(' '))).Select(l => l.Trim()));
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z ")]
    private static partial Regex DockerTimestamp();

    /// <summary>
    /// A GET sent straight to a server that started, on its published port, under
    /// <paramref name="host"/>: what a client that reached it by that name would get.
    /// </summary>
    public async Task<HttpStatusCode> GetDirectAsync(string path, string host)
    {
        if (!Started)
        {
            throw new InvalidOperationException($"The server exited with code {ExitCode}; there is nothing to send a request to.");
        }

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ServerUnderTest.Port)}{path}"));
        request.Headers.Host = host;
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>What happened, in one line, for a failure message.</summary>
    public string Describe() =>
        Started
            ? $"the server started, and /readyz answered {(int?)Readyz} under Host {TlsFront.Host}; its stderr names no refusal"
            : $"the server exited with code {ExitCode}; {Refusal ?? "its stderr names no refusal: " + Stderr.Trim()}";

    public async ValueTask DisposeAsync()
    {
        await ServerUnderTest.WriteLogsAsync(_environment, _name, _container, null);
        await _container.DisposeAsync();
    }
}
