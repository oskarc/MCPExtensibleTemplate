using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Tests.Frame;
using McpServerTemplate.Tests.Identity;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-3, T-8 (the caller's own trace), T-10 — what only HTTP has: a real token, an address, a user agent, a
/// traceparent, and the HTTP layer's own refusals. The shipped HTTP server, composed in this process, with no Redis behind
/// it (<see cref="NoRedis"/>): everything these tests ask of it is decided before a limit is counted.
/// </summary>
public sealed class AuditHttpTests
{
    private static readonly IReadOnlyList<IProviderModule> Modules = [new TestModule(), new AuditModule()];

    /// <summary>Every member of a record, in the order the writer writes them (contract-006 · G-3, G-5).</summary>
    private static readonly string[] Members =
    [
        "datetime", "appid", "event", "level", "description", "trace_id", "request_id", "principal", "source_ip", "hostname",
        "request_uri", "request_method", "useragent", "mcp", "args", "decision", "upstream", "detail", "instance", "seq",
        "prev_hash", "hash",
    ];

    private static Task<InProcessServer> ServerAsync(TestIdentityProvider corp) =>
        InProcessServer.StartAsync([corp], modules: Modules, redis: NoRedis.ConnectionString);

    private static async Task<HttpResponseMessage> PostAsync(
        InProcessServer server, string? token, string method = "tools/list", object? parameters = null, Action<HttpRequestMessage>? shape = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, InProcessServer.McpPath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        if (token is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        shape?.Invoke(request);
        return await server.Client.SendAsync(request);
    }

    private static string FileOf(InProcessServer server) => string.Join("\n", AuditFiles.In(server.AuditDirectory).SelectMany(AuditFiles.Lines));

    [Fact]
    public async Task T3_a_record_names_the_caller_the_request_and_the_callers_own_trace_and_never_the_token()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await ServerAsync(corp);
        const string Trace = "0af7651916cd43dd8448eb211c80319c";
        var token = corp.MintToken(GateClient.Resource, subject: "user-7", clientId: "ide-1", jti: "jti-abc-123", scopes: ["weather:read", "test:act"]);

        using var response = await PostAsync(server, token, shape: request =>
        {
            request.Headers.UserAgent.ParseAdd("audit-test/1.0");
            request.Headers.Add("traceparent", $"00-{Trace}-b7ad6b7169203331-01");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var records = AuditFiles.Records(server.AuditDirectory);
        var record = Assert.Single(records, r => r.GetProperty("mcp").ValueKind == JsonValueKind.Object);

        // Who.
        var principal = record.GetProperty("principal");
        Assert.Equal("corp", principal.GetProperty("idp").GetString());
        Assert.Equal("user-7", principal.GetProperty("sub").GetString());
        Assert.Equal("ide-1", principal.GetProperty("client_id").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("jti-abc-123"))), principal.GetProperty("jti_sha256").GetString());
        Assert.Equal(["test:act", "weather:read"], principal.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!));

        // How it arrived.
        Assert.Equal("127.0.0.1", record.GetProperty("source_ip").GetString());
        Assert.Equal("/mcp", record.GetProperty("request_uri").GetString());
        Assert.Equal("POST", record.GetProperty("request_method").GetString());
        Assert.Equal("audit-test/1.0", record.GetProperty("useragent").GetString());
        Assert.False(string.IsNullOrEmpty(record.GetProperty("request_id").GetString()));
        Assert.Equal(Environment.MachineName, record.GetProperty("hostname").GetString());

        // Under the caller's own trace.
        Assert.Equal(Trace, record.GetProperty("trace_id").GetString());

        // What, and what was decided.
        Assert.Equal("tools/list", record.GetProperty("mcp").GetProperty("method").GetString());
        Assert.Equal("mcp_request:sub=user-7", record.GetProperty("event").GetString());
        Assert.Equal("allow", AuditFiles.OutcomeOf(record));
        Assert.Equal("INFO", record.GetProperty("level").GetString());
        Assert.Equal("mcp-server", record.GetProperty("appid").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", record.GetProperty("datetime").GetString());

        // Every record has every member, in the writer's order.
        Assert.All(records, each => Assert.Equal(Members, each.EnumerateObject().Select(member => member.Name)));

        // And neither the token nor its id is anywhere in the file.
        var file = FileOf(server);
        Assert.DoesNotContain(token, file, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Split('.')[2], file, StringComparison.Ordinal);
        Assert.DoesNotContain("jti-abc-123", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T3_a_refusal_over_http_leaves_one_record_with_the_same_facts()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await ServerAsync(corp);
        var token = corp.MintToken(GateClient.Resource, scopes: ["weather:read"]);

        using var response = await PostAsync(server, token, "tools/call", new { name = "test_echo", arguments = new { text = "hi" } });

        var record = Assert.Single(AuditFiles.Records(server.AuditDirectory), r => r.GetProperty("mcp").ValueKind == JsonValueKind.Object);
        Assert.Equal("insufficient_scope", AuditFiles.RuleOf(record));
        Assert.Equal("authz_fail:sub=user-1,resource=test_echo", record.GetProperty("event").GetString());
        Assert.Equal("127.0.0.1", record.GetProperty("source_ip").GetString());
        Assert.Equal("hi", record.GetProperty("args").GetProperty("text").GetString());
    }

    [Fact]
    public async Task T10_a_request_from_a_foreign_origin_leaves_one_record_with_no_caller()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await ServerAsync(corp);

        using var response = await PostAsync(
            server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]), shape: request => request.Headers.Add("Origin", "https://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var record = Assert.Single(AuditFiles.Records(server.AuditDirectory).Skip(1));
        Assert.Equal("malicious_cors", record.GetProperty("event").GetString());
        Assert.Equal("CRITICAL", record.GetProperty("level").GetString());
        Assert.Equal(("deny", "origin"), (AuditFiles.OutcomeOf(record), AuditFiles.RuleOf(record)));
        Assert.Equal(JsonValueKind.Null, record.GetProperty("principal").ValueKind);
        Assert.Contains("https://evil.example", record.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("127.0.0.1", record.GetProperty("source_ip").GetString());
        Assert.Matches("^[0-9a-f]{32}$", record.GetProperty("trace_id").GetString());
    }

    [Fact]
    public async Task T10_a_refused_token_leaves_one_record_with_its_reason_no_caller_and_no_token()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        using var stranger = new TestIdentityProvider("stranger", "https://stranger.example.com/");
        await using var server = await ServerAsync(corp);
        var unregistered = stranger.MintToken(GateClient.Resource, scopes: ["test:act"]);
        var expired = corp.MintToken(
            GateClient.Resource, scopes: ["test:act"], issuedAt: DateTime.UtcNow.AddMinutes(-30), notBefore: DateTime.UtcNow.AddMinutes(-31), expires: DateTime.UtcNow.AddMinutes(-10));

        using var first = await PostAsync(server, unregistered);
        using var second = await PostAsync(server, expired);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        var added = AuditFiles.Records(server.AuditDirectory).Skip(1).ToArray();
        Assert.Equal(2, added.Length);
        Assert.All(added, record =>
        {
            Assert.Equal("authn_login_fail", record.GetProperty("event").GetString());
            Assert.Equal("WARN", record.GetProperty("level").GetString());
            Assert.Equal(("deny", "token"), (AuditFiles.OutcomeOf(record), AuditFiles.RuleOf(record)));
            Assert.Equal(JsonValueKind.Null, record.GetProperty("principal").ValueKind);
            Assert.Equal("/mcp", record.GetProperty("request_uri").GetString());
        });
        Assert.Equal("unattributable", added[0].GetProperty("detail").GetProperty("scheme").GetString());
        Assert.Equal("idp:corp", added[1].GetProperty("detail").GetProperty("scheme").GetString());

        var file = FileOf(server);
        foreach (var token in new[] { unregistered, expired })
        {
            Assert.DoesNotContain(token, file, StringComparison.Ordinal);
            Assert.DoesNotContain(token.Split('.')[2], file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task T10_the_per_address_limit_leaves_one_record_however_many_requests_it_turns_away()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        await using var server = await ServerAsync(corp);

        // Sixty requests are the address's minute. None carries a token: each is answered with where to get one, and that
        // challenge is not a refused token and leaves no record.
        for (var n = 1; n <= 60; n++)
        {
            using var within = await PostAsync(server, token: null);
            Assert.Equal(HttpStatusCode.Unauthorized, within.StatusCode);
        }

        Assert.Single(AuditFiles.Records(server.AuditDirectory));

        for (var n = 61; n <= 66; n++)
        {
            using var over = await PostAsync(server, token: null);
            Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        }

        var record = Assert.Single(AuditFiles.Records(server.AuditDirectory).Skip(1));
        Assert.Equal("excess_rate_limit_exceeded", record.GetProperty("event").GetString());
        Assert.Equal("WARN", record.GetProperty("level").GetString());
        Assert.Equal(("deny", "address-rate"), (AuditFiles.OutcomeOf(record), AuditFiles.RuleOf(record)));
        Assert.Equal("127.0.0.1", record.GetProperty("source_ip").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("principal").ValueKind);
    }

    [Fact]
    public async Task The_servers_start_is_the_first_record_and_its_stop_the_last()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        string directory;
        IReadOnlyList<string> files;
        await using (var server = await ServerAsync(corp))
        {
            directory = server.AuditDirectory;
            using var response = await PostAsync(server, corp.MintToken(GateClient.Resource, scopes: ["test:act"]));
            files = AuditFiles.In(directory);

            // Read before the server goes: it removes its directory when it does.
            var whileRunning = AuditFiles.Records(directory);
            Assert.Equal("sys_startup", whileRunning[0].GetProperty("event").GetString());
            Assert.Equal("Redis", whileRunning[0].GetProperty("detail").GetProperty("limits").GetString());
            Assert.Equal("TestAct,AuditTest", whileRunning[0].GetProperty("detail").GetProperty("providers").GetString());
        }

        // The directory went with the server; what it wrote last is checked on the real program (AuditProgramTests, T-9).
        Assert.Single(files);
        Assert.False(Directory.Exists(directory));
    }
}
