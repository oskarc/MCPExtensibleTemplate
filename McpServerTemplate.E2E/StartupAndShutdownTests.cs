using System.Net;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-10 (G-1, G-8 · UC-10) — the image starts and stops properly.
///
/// Four representative misconfigurations — a setting the server does not read, Production without Redis, an identity
/// provider's authority over plaintext, and a wildcard bind with no allowed host — each stop it at startup with exit
/// code 78 and a refusal naming the cause; the full matrix stays in the fast suite. docker stop — SIGTERM, then SIGKILL
/// after Docker's 10-second grace — ends it cleanly, well inside the grace, with a shutdown line. And the request kinds
/// nobody governs — resources/subscribe, logging/setLevel and a method nobody has defined — are each refused by the
/// frame's request-kind rule, while a kind the image's own startup line says it governs is answered (G-11).
/// </summary>
public sealed class StartupAndShutdownTests(StartupAndShutdownTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<StartupAndShutdownTests.Server>
{
    /// <summary>The environment's server as it ships: the positive control for every refusal below.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None);

    private const string IssuerA = "idp-a.e2e.test";

    /// <summary>Docker's default for docker stop before it kills (contract-005's magnitudes table).</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    /// <summary>The line the server writes as it shuts down, once the host has stopped.</summary>
    private const string ShutdownLine = "sys_shutdown:";

    /// <summary>Each misconfiguration: what the delta does, and the words that must name the cause.</summary>
    public static TheoryData<string, string, string?, string> Misconfigurations() => new()
    {
        // A key in a section the server's settings check governs, which it does not read.
        { "unknown-setting", "Limits:PerPrincipalPerMinit", "10", "'Limits:PerPrincipalPerMinit' is not a setting this server reads" },

        // The image runs as Production; without Redis its limits would hold per instance only.
        { "production-without-redis", "Limits:Redis", null, "Limits:Redis is required outside Development" },

        // Discovery and keys would be fetched over plaintext.
        { "http-authority", $"Authentication:IdentityProviders:{E2EEnvironment.IdpA}:Authority", $"http://{IssuerA}",
            $"Authentication:IdentityProviders:{E2EEnvironment.IdpA}:Authority must be an absolute https URI" },

        // The environment binds 0.0.0.0 (G-8); with no allowed host, any Host would be answered.
        { "missing-allowed-hosts", "HttpTransport:AllowedHosts", null, "HttpTransport:AllowedHosts must name the host names clients reach this server by" },
    };

    /// <summary>
    /// Sabotage (G-11): start the row's server with SettingsDelta.None instead of its delta. It is the environment's
    /// server, which comes up, and the claim's assertion goes red.
    /// </summary>
    [Theory]
    [MemberData(nameof(Misconfigurations))]
    public async Task T10_a_misconfiguration_exits_78_naming_its_cause(string row, string key, string? value, string cause)
    {
        var delta = value is null ? SettingsDelta.None.Remove(key) : SettingsDelta.None.Set(key, value);

        await using var outcome = await fixture.Environment.StartupAsync($"startup-{row}", delta);
        output.WriteLine($"{row} ({(value is null ? $"-{key}" : $"{key}={value}")}): {outcome.Describe()}");

        Assert.True(
            outcome.ExitCode == 78 && outcome.Refusal?.Contains(cause, StringComparison.Ordinal) == true,
            $"{row}, {(value is null ? $"without {key}" : $"{key}={value}")}: {outcome.Describe()}; the cause, '{cause}', is not named with exit 78.");
    }

    /// <summary>
    /// Sabotage (G-11): have docker stop send SIGWINCH, a signal the server does not handle, instead of the image's own
    /// stop signal — as an entrypoint that swallows SIGTERM would leave it. The server never begins to stop, Docker kills it
    /// once the grace has passed — exit 137, no shutdown line — and the claim's assertion goes red. (A grace of 0 seconds
    /// cannot be the sabotage: Docker's client drops a wait of zero, and the engine then waits its default 10 seconds.)
    /// </summary>
    [Fact]
    public async Task T10_docker_stop_ends_the_server_cleanly_within_the_grace_with_a_shutdown_line()
    {
        await using var outcome = await fixture.Environment.StartupAsync("stop-sigterm", SettingsDelta.None);

        // Positive control: it came up, answered /readyz, and had written no shutdown line.
        Assert.True(outcome.Started && outcome.Readyz == HttpStatusCode.OK, $"the server to be stopped did not come up: {outcome.Describe()}");
        Assert.DoesNotContain(ShutdownLine, outcome.Stderr, StringComparison.Ordinal);

        var stopped = await outcome.StopAsync(StopGrace);
        var line = stopped.Stderr.Split('\n').LastOrDefault(l => l.Contains(ShutdownLine, StringComparison.Ordinal))?.Trim();
        output.WriteLine($"docker stop ({stopped.Signal}, grace {stopped.Grace.TotalSeconds:0} s): exit {stopped.ExitCode} after {stopped.Took.TotalSeconds:0.0} s");
        output.WriteLine($"Its shutdown line: {line ?? "(none)"}");

        Assert.True(
            stopped.Signal == "SIGTERM" && stopped.Grace == StopGrace && stopped.ExitCode == 0 && stopped.Took < StopGrace && line is not null,
            $"docker stop sent {stopped.Signal} and the server exited {stopped.ExitCode} after {stopped.Took.TotalSeconds:0.0} s "
            + $"(grace {stopped.Grace.TotalSeconds:0} s), {(line is null ? "with no shutdown line" : $"logging '{line}'")}; a clean stop is SIGTERM, "
            + $"exit 0 within Docker's {StopGrace.TotalSeconds:0}-second grace, and a shutdown line. Its stderr ends: "
            + string.Join(" | ", stopped.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(5)));
    }

    /// <summary>The request kinds T-10 names, which the frame governs none of: the last is a method nobody has defined.</summary>
    public static TheoryData<string> UngovernedKinds() => new()
    {
        "resources/subscribe",
        "logging/setLevel",
        "e2e/no-such-method",
    };

    /// <summary>
    /// Sabotage (G-11): in the last request, the one whose answer the claim reads, send "ping", a kind the line names,
    /// instead of the row's method. It is answered, and the claim's assertion goes red.
    /// </summary>
    [Theory]
    [MemberData(nameof(UngovernedKinds))]
    public async Task T10_a_request_kind_nobody_governs_is_refused_request_kind(string method)
    {
        using var http = fixture.CreateClient();
        var token = await TestIssuerService.MintAsync(http, IssuerA, "valid", ServerUnderTest.Resource, ["weather:read"]);

        // The governed kinds, from the image's own startup line (G-11): the method is not one of them.
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        Assert.True(line.RequestKinds.Count > 0, $"the image's startup line names no request kinds it governs (G-11): {line.Text}");
        Assert.DoesNotContain(method, line.RequestKinds);

        // Positive control: a kind the line names, under the same token, is answered.
        var answered = await McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, "ping"));
        Assert.True(line.RequestKinds.Contains("ping") && answered.TryGetProperty("result", out _), $"ping, which the line governs, got {answered}.");

        var refused = await McpRequests.ExchangeAsync(http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, method));
        var text = McpRequests.TextOf(refused);
        output.WriteLine($"{method}: {refused}");

        Assert.True(
            McpRequests.RuleOf(text) == "request-kind" && !refused.TryGetProperty("result", out _),
            $"{method}, a kind the image's startup line does not govern ([{string.Join(", ", line.RequestKinds)}]), got "
            + $"{JsonSerializer.Serialize(refused)}, not a request-kind refusal with no result.");
    }
}
