using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Providers;
using McpServerTemplate.Tests.Identity;
using McpServerTemplate.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-005 · G-17 round 2 (SEC-R2-03) — every value a caller writes that reaches a log line, by every route it reaches
/// one: the JSON-RPC method, a tool's name, a resource's URI, a prompt's name, an argument's name, the client's name, the
/// Origin header, and a token's key id. The finding sent each with a line break and a forged line after it, an escape
/// sequence and a direction override, and 16 forged lines began at column 0. The shipped server composed in this process,
/// its log going through Serilog as the program's does, with each sink given events through the line-safe stage
/// (LineSafeSink). Three things hold for each route: a line the server wrote carried what the caller wrote (the witness,
/// so the row proves something); the frame's own lines are safe where the frame writes them, and cut; and every line the
/// sinks are given is its own lines, whatever wrote it.
/// </summary>
public class LogLineSafetyTests
{
    private static readonly IReadOnlyList<McpServerTemplate.Infrastructure.Frame.IProviderModule> Everything =
        [.. BuiltInProviders.Create(), new TestModule()];

    /// <summary>What every hostile value starts with, so a cut value still shows that the route reached a line.</summary>
    private const string Marker = "SEC-R2-03-marker";

    /// <summary>What a caller writes by each route: the marker, then a line break, a forged line and the rest.</summary>
    private static readonly string Hostile = Marker + LineSafeLoggingTests.Hostile;

    /// <summary>
    /// What a caller writes in a header: the same, without the line break, which ends a header line on the wire, and with
    /// the backspaces the finding sent.
    /// </summary>
    private static readonly string HostileHeader = $"https://evil.example/{Marker}\u001b[2J\u001b[1;1H\b\b\bcleared\u202Egnp.exe\u2028";

    /// <summary>The templates of the frame's own lines: each is written by the frame's code with values it made safe.</summary>
    private static readonly string[] FramesOwn = ["authz_fail:", "{Event}:", "authn_login_fail:", "malicious_cors:", "[{CorrelationId}] Tool"];

    public static TheoryData<string> Routes() => new()
    {
        "the JSON-RPC method",
        "a tool's name",
        "a resource's URI",
        "a prompt's name",
        "an argument's name",
        "a client's name",
        "an Origin header",
        "a token's key id",
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task What_a_caller_writes_never_breaks_forges_or_disguises_a_log_line(string route)
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        var raw = new LineSafeLoggingTests.Capture();
        var written = new LineSafeLoggingTests.Capture();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(raw)
            .WriteTo.Sink(new LineSafeSink(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(written).CreateLogger()))
            .CreateLogger();

        using (var logs = new Serilog.Extensions.Logging.SerilogLoggerProvider(logger, dispose: true))
        {
            await using var server = await InProcessServer.StartAsync([corp], modules: Everything, logs: logs, logLevel: LogLevel.Trace);
            await SendAsync(route, server, corp);
        }

        // The witness: what the caller wrote reached a line the server wrote.
        Assert.True(
            raw.Events.Any(Carries),
            $"{route}: no line the server wrote carried what the caller wrote ({Marker}), so this row shows nothing about it.");

        // The frame's own lines are safe where the frame writes them, whatever logger the host gives it, and a value is cut.
        foreach (var logEvent in raw.Events.Where(e => FramesOwn.Any(own => e.MessageTemplate.Text.StartsWith(own, StringComparison.Ordinal))))
        {
            var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
            Assert.False(LogSafe.BreaksALine(message), $"{route}: the frame wrote a line that breaks or disguises a line: '{message}'.");
            Assert.True(message.Length < 2_000, $"{route}: the frame wrote a line of {message.Length} characters: the caller chose its length.");
            if (logEvent.Exception is { } exception)
            {
                Assert.False(LogSafe.BreaksALine(exception.Message), $"{route}: the frame refused with '{exception.Message}'.");
            }
        }

        // Every line the sinks are given is its own lines: one for the message, and one for an exception.
        Assert.NotEmpty(written.Events);
        foreach (var logEvent in written.Events)
        {
            LineSafeLoggingTests.AssertWrittenAsItsOwnLines(logEvent, route);
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-04) — the finding's four: a tool name, a resource URI, a principal's subject and a
    /// token's issuer, each differing from a real one only by an invisible character. The frame's own line removed the
    /// character, and so named the real tool, resource, principal or issuer — the configured one — which the request did not
    /// carry, beside the SDK's line, which showed what was sent. The frame's line names what was sent now, escaped; so does the
    /// reply; and neither names the real one in its place.
    /// </summary>
    [Theory]
    [InlineData("a tool's name")]
    [InlineData("a resource's URI")]
    [InlineData("a principal's subject")]
    [InlineData("a token's issuer")]
    public async Task A_value_differing_from_a_real_one_by_an_invisible_character_is_logged_as_itself(string route)
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var log = new RequestGateTests.CapturedLog();
        await using var server = await InProcessServer.StartAsync([corp], modules: Everything, logs: log);

        string reply;
        string[] shown;
        string[] notShown;
        switch (route)
        {
            case "a tool's name":
                reply = GateClient.TextOf(await GateClient.RpcAsync(
                    server, corp.MintToken(GateClient.Resource, scopes: ["weather:read"]), "tools/call",
                    new { name = "get_fore​cast", arguments = new { latitude = 59.3, longitude = 18.1 } }));
                shown = ["target=get_fore\\u200Bcast", "Tool call: get_fore\\u200Bcast"];
                notShown = ["target=get_forecast", "Tool call: get_forecast"];
                Assert.Contains("'get_fore\\u200Bcast'", reply, StringComparison.Ordinal);
                break;

            case "a resource's URI":
                reply = GateClient.TextOf(await GateClient.RpcAsync(
                    server, corp.MintToken(GateClient.Resource, scopes: ["weather:read"]), "resources/read", new { uri = "smhi://coverage­-area" }));
                shown = ["target=smhi://coverage\\u00AD-area"];
                notShown = ["target=smhi://coverage-area"];
                Assert.Contains("'smhi://coverage\\u00AD-area'", reply, StringComparison.Ordinal);
                break;

            case "a principal's subject":
                reply = GateClient.TextOf(await GateClient.RpcAsync(
                    server, corp.MintToken(GateClient.Resource, subject: "user⁠-1", scopes: ["weather:read"]), "tools/call",
                    new { name = "test_echo", arguments = new { text = "hi" } }));
                shown = ["principal=corp:user\\u2060-1"];
                notShown = ["principal=corp:user-1"];
                break;

            case "a token's issuer":
                // Unsigned, and naming corp's issuer but for one invisible character: refused by the router, before any key is
                // looked up, and logged naming the issuer the token names.
                var token = string.Join('.',
                    Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("""{"alg":"RS256","typ":"JWT"}"""),
                    Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("""{"iss":"https://corp.example.com/​","sub":"user-1"}"""),
                    "c2lnbmF0dXJl");
                using (var response = await server.PostAsync(token))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }

                reply = string.Empty;
                shown = ["for issuer 'https://corp.example.com/\\u200B'"];
                notShown = ["for issuer 'https://corp.example.com/'"];
                Assert.Equal(0, corp.JwksRequests);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "a route this test sends by");
        }

        var lines = log.Lines;
        foreach (var expected in shown)
        {
            Assert.True(
                lines.Any(line => line.Contains(expected, StringComparison.Ordinal)),
                $"{route}: no line the frame wrote says '{expected}': {string.Join(" | ", lines.Where(l => l.Contains("fail", StringComparison.Ordinal) || l.Contains("Tool call", StringComparison.Ordinal)))}");
        }

        foreach (var wrong in notShown)
        {
            Assert.DoesNotContain(lines, line => line.Contains(wrong, StringComparison.Ordinal));
        }

        Assert.DoesNotContain("get_forecast'", reply, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="logEvent"/> carries the marker: in its message, a property, or its exception.</summary>
    private static bool Carries(LogEvent logEvent) =>
        logEvent.RenderMessage(CultureInfo.InvariantCulture).Contains(Marker, StringComparison.Ordinal)
        || logEvent.Properties.Values.Any(value => value.ToString().Contains(Marker, StringComparison.Ordinal))
        || (logEvent.Exception?.ToString().Contains(Marker, StringComparison.Ordinal) ?? false);

    /// <summary>The request that carries <see cref="Hostile"/> by <paramref name="route"/>.</summary>
    private static async Task SendAsync(string route, InProcessServer server, TestIdentityProvider corp)
    {
        var token = corp.MintToken(GateClient.Resource, scopes: ["weather:read", "test:act"]);
        switch (route)
        {
            case "the JSON-RPC method":
                await GateClient.RpcAsync(server, token, Hostile);
                break;

            case "a tool's name":
                // Long as well: the frame's line is cut, where the caller chose its length.
                await GateClient.RpcAsync(server, token, "tools/call", new { name = Hostile + new string('A', 5_000), arguments = new { } });
                break;

            case "a resource's URI":
                await GateClient.RpcAsync(server, token, "resources/read", new { uri = Hostile });
                break;

            case "a prompt's name":
                await GateClient.RpcAsync(server, token, "prompts/get", new { name = Hostile });
                break;

            case "an argument's name":
                await GateClient.RpcAsync(server, token, "tools/call", new
                {
                    name = "test_echo",
                    arguments = new Dictionary<string, object> { ["text"] = "hi", [Hostile] = 1 },
                });
                break;

            case "a client's name":
                await GateClient.RpcAsync(server, token, "initialize", new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = Hostile, version = "1.0" },
                });
                break;

            case "an Origin header":
                Assert.StartsWith("HTTP/1.1 403", await OriginAsync(server, HostileHeader), StringComparison.Ordinal);
                break;

            case "a token's key id":
                // A token that names corp's issuer, signed by a key corp never published, under a key id the caller wrote: the
                // bearer handler refuses it quoting the key id, before any signature is trusted.
                using (var rsa = RSA.Create(2048))
                {
                    var unverifiable = corp.MintToken(
                        GateClient.Resource,
                        scopes: ["weather:read"],
                        signingCredentials: new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = Hostile }, SecurityAlgorithms.RsaSha256));
                    using var response = await server.PostAsync(unverifiable);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "a route this test sends by");
        }
    }

    /// <summary>
    /// A request carrying <paramref name="origin"/> as its Origin header, written on the wire as it is (UTF-8), which a client
    /// library would refuse to send; the status line it got.
    /// </summary>
    private static async Task<string> OriginAsync(InProcessServer server, string origin)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Address.Host, server.Address.Port);
        await using var stream = tcp.GetStream();
        var head = $"POST {InProcessServer.McpPath} HTTP/1.1\r\nHost: {server.Address.Authority}\r\nOrigin: {origin}\r\n"
            + "Content-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
        using var reader = new StreamReader(stream, Encoding.Latin1);
        return await reader.ReadLineAsync() ?? string.Empty;
    }
}
