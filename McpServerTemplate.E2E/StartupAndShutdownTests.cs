using System.Globalization;
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
/// after Docker's 10-second grace — ends it cleanly, inside the grace, with a shutdown line: idle, and with a request in
/// flight. And the request kinds nobody governs — resources/subscribe, logging/setLevel and a method nobody has defined —
/// are each refused by the frame's request-kind rule, while a kind the image's own startup line says it governs is
/// answered (G-11).
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

        // The framework's own shutdown timeout, which the server would ignore: it stops within its own 8 seconds.
        { "shutdown-timeout-set", "DOTNET_shutdownTimeoutSeconds", "30", "shutdownTimeoutSeconds is '30' from the environment, as DOTNET_shutdownTimeoutSeconds" },
    };

    [Theory]
    [MemberData(nameof(Misconfigurations))]
    [Sabotage("t10-misconfiguration-left-out", SabotageActs.ContainerEnvironment, Sabotage.MisconfigurationLeftOut)]
    public async Task T10_a_misconfiguration_exits_78_naming_its_cause(string row, string key, string? value, string cause)
    {
        var delta = (value is null ? SettingsDelta.None.Remove(key) : SettingsDelta.None.Set(key, value))
            .Sabotaged("t10-misconfiguration-left-out", _ => SettingsDelta.None);

        await using var outcome = await fixture.Environment.StartupAsync($"startup-{row}", delta);
        output.WriteLine($"{row} ({(value is null ? $"-{key}" : $"{key}={value}")}): {outcome.Describe()}");

        Claim.True(
            outcome.ExitCode == 78 && outcome.Refusal?.Contains(cause, StringComparison.Ordinal) == true,
            $"{row}, {(value is null ? $"without {key}" : $"{key}={value}")}: {outcome.Describe()}; the cause, '{cause}', is not named with exit 78.");
    }

    /// <summary>
    /// A grace of 0 seconds cannot be this test's sabotage: Docker's client drops a wait of zero, and the engine then waits
    /// its default 10 seconds.
    /// </summary>
    [Fact]
    [Sabotage("t10-stop-signal-swallowed", SabotageActs.Inputs,
        "docker stop sends SIGWINCH, which the server does not handle, instead of the image's own stop signal, as an entrypoint "
        + "that swallows SIGTERM would leave it: the server never begins to stop, and Docker kills it once the grace has passed.")]
    public async Task T10_docker_stop_ends_the_server_cleanly_within_the_grace_with_a_shutdown_line()
    {
        await using var outcome = await fixture.Environment.StartupAsync("stop-sigterm", SettingsDelta.None);

        // Positive control: it came up, answered /readyz, and had written no shutdown line.
        Assert.True(outcome.Started && outcome.Readyz == HttpStatusCode.OK, $"the server to be stopped did not come up: {outcome.Describe()}");
        Assert.DoesNotContain(ShutdownLine, outcome.Stderr, StringComparison.Ordinal);

        var stopped = await outcome.StopAsync(StopGrace, Sabotage.Choose<string?>("t10-stop-signal-swallowed", null, "SIGWINCH"));
        var line = stopped.Stderr.Split('\n').LastOrDefault(l => l.Contains(ShutdownLine, StringComparison.Ordinal))?.Trim();
        output.WriteLine($"docker stop ({stopped.Signal}, grace {stopped.Grace.TotalSeconds:0} s): exit {stopped.ExitCode} after {stopped.Took.TotalSeconds:0.0} s");
        output.WriteLine($"Its shutdown line: {line ?? "(none)"}");

        Claim.True(
            stopped.Signal == "SIGTERM" && stopped.Grace == StopGrace && stopped.ExitCode == 0 && stopped.Took < StopGrace && line is not null,
            $"docker stop sent {stopped.Signal} and the server exited {stopped.ExitCode} after {stopped.Took.TotalSeconds:0.0} s "
            + $"(grace {stopped.Grace.TotalSeconds:0} s), {(line is null ? "with no shutdown line" : $"logging '{line}'")}; a clean stop is SIGTERM, "
            + $"exit 0 within Docker's {StopGrace.TotalSeconds:0}-second grace, and a shutdown line. Its stderr ends: "
            + string.Join(" | ", stopped.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(5)));
    }

    /// <summary>
    /// contract-005 · T-10 — docker stop while a request is in flight. The fake holds back the upstream answer to a forecast
    /// at a point of its own for 30 seconds, three times the grace. The call is made straight to the server's published port,
    /// and the server is seen starting it — its "Tool call" line — and still holding it when docker stop sends SIGTERM with
    /// Docker's 10-second grace. The server stops taking requests, gives the one it holds until its shutdown timeout, and
    /// exits 0 inside the grace with its shutdown line: a busy server is stopped, not killed.
    /// </summary>
    [Fact]
    [Sabotage("t10-busy-stop-signal-swallowed", SabotageActs.Inputs,
        "docker stop sends SIGWINCH, which the server does not handle, instead of the image's own stop signal, while the call is "
        + "in flight: the busy server never begins to stop, and Docker kills it once the grace has passed.")]
    public async Task T10_docker_stop_with_a_request_in_flight_ends_the_server_cleanly_within_the_grace()
    {
        var latitude = Math.Round(55 + (Random.Shared.NextDouble() * 10), 4);
        var longitude = Math.Round(12 + (Random.Shared.NextDouble() * 10), 4);
        var point = $"/lon/{longitude.ToString("F6", CultureInfo.InvariantCulture)}/lat/{latitude.ToString("F6", CultureInfo.InvariantCulture)}/";

        using var http = fixture.CreateClient();
        var stub = await WireMockService.StubAsync(http, $"*{point}*", TimeSpan.FromSeconds(30));
        try
        {
            // The call's start is on stderr only when the SDK's server logs at Information, which Production does not.
            await using var outcome = await fixture.Environment.StartupAsync(
                "stop-busy", SettingsDelta.None.Set("Serilog:MinimumLevel:Override:ModelContextProtocol", "Information"));
            Assert.True(outcome.Started && outcome.Readyz == HttpStatusCode.OK, $"the server to be stopped did not come up: {outcome.Describe()}");

            using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
            using var request = McpRequests.Rpc(
                outcome.DirectEndpoint, await fixture.KeycloakTokenAsync(), "tools/call", new { name = "get_forecast", arguments = new { latitude, longitude } });
            request.Headers.Host = TlsFront.Host;
            var call = direct.SendAsync(request);

            // Self-check, not the claim: the server has started the call, and it is still in flight.
            var started = await outcome.StderrLineAsync("Tool call: get_forecast", TimeSpan.FromSeconds(20));
            if (started is null || call.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"the call the server was to be stopped during was not in flight: {(started is null ? "the server logged no 'Tool call: get_forecast' within 20 s" : "it had already ended")} "
                    + $"({await EndOfAsync(call)}). The fake was to hold back {point} for 30 s.");
            }

            var stopped = await outcome.StopAsync(StopGrace, Sabotage.Choose<string?>("t10-busy-stop-signal-swallowed", null, "SIGWINCH"));
            var ended = await EndOfAsync(call);
            var line = stopped.Stderr.Split('\n').LastOrDefault(l => l.Contains(ShutdownLine, StringComparison.Ordinal))?.Trim();
            output.WriteLine($"In flight: {started}");
            output.WriteLine($"docker stop ({stopped.Signal}, grace {stopped.Grace.TotalSeconds:0} s): exit {stopped.ExitCode} after {stopped.Took.TotalSeconds:0.0} s; the call: {ended}");
            output.WriteLine($"Its shutdown line: {line ?? "(none)"}");

            Claim.True(
                stopped.Signal == "SIGTERM" && stopped.Grace == StopGrace && stopped.ExitCode == 0 && stopped.Took < StopGrace && line is not null,
                $"with a call in flight (the fake holding its upstream answer for 30 s), docker stop sent {stopped.Signal} and the server exited "
                + $"{stopped.ExitCode} after {stopped.Took.TotalSeconds:0.0} s (grace {stopped.Grace.TotalSeconds:0} s), "
                + $"{(line is null ? "with no shutdown line" : $"logging '{line}'")}; the call: {ended}. A clean stop is SIGTERM, exit 0 within "
                + $"Docker's {StopGrace.TotalSeconds:0}-second grace, and a shutdown line. Its stderr ends: "
                + string.Join(" | ", stopped.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(5)));
        }
        finally
        {
            await WireMockService.RemoveStubAsync(http, stub);
        }
    }

    /// <summary>The request kinds T-10 names, which the frame governs none of: the last is a method nobody has defined.</summary>
    public static TheoryData<string> UngovernedKinds() => new()
    {
        "resources/subscribe",
        "logging/setLevel",
        "e2e/no-such-method",
    };

    [Theory]
    [MemberData(nameof(UngovernedKinds))]
    [Sabotage("t10-request-kind-sent-as-ping", SabotageActs.Inputs,
        "The request whose answer the claim reads is sent as ping, a kind the image's startup line names, instead of the row's method; it is answered.")]
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

        var refused = await McpRequests.ExchangeAsync(
            http, McpRequests.Rpc(ServerUnderTest.Endpoint, token, Sabotage.Choose("t10-request-kind-sent-as-ping", method, "ping")));
        var text = McpRequests.TextOf(refused);
        output.WriteLine($"{method}: {refused}");

        Claim.True(
            McpRequests.RuleOf(text) == "request-kind" && !refused.TryGetProperty("result", out _),
            $"{method}, a kind the image's startup line does not govern ([{string.Join(", ", line.RequestKinds)}]), got "
            + $"{JsonSerializer.Serialize(refused)}, not a request-kind refusal with no result.");
    }

    /// <summary>How a request sent straight to the server ended: its status, or how it failed.</summary>
    private static async Task<string> EndOfAsync(Task<HttpResponseMessage> call)
    {
        try
        {
            using var response = await call;
            return $"answered {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return $"no answer: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }
    }
}
