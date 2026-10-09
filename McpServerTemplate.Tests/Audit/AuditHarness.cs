using System.Globalization;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text.Json;
using McpServerTemplate.Infrastructure.Audit;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Infrastructure.Identity;
using McpServerTemplate.Tests.Frame;
using McpServerTemplate.Tests.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// The governed server, composed exactly as the shipped one is — <see cref="GovernedServer.AddGovernedMcpServer"/>, in a
/// real host — and reached over an in-memory stream with the SDK's own client (contract-006 · T-1 … T-8).
///
/// It needs no container. The in-process HTTP server runs as Production, which requires Redis, and so every test that
/// starts one needs Docker; these tests are about what the frame records, not about where limits are kept, so they run in
/// Development, where limits are held in memory, and over a stream, where the caller is the principal the test registers —
/// the way stdio runs as its Development principal. That principal is the test's to shape: its identity provider, scopes
/// and token age, which a Development principal never varies. The audit trail on the shipped image, behind TLS with real
/// tokens and Redis, is the end-to-end suite's to prove, and is owed (docs/deferred/phase-3-audit.md).
/// </summary>
public sealed class StreamServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly bool _ownsDirectory;
    private readonly PipeWriter _toServer;

    private StreamServer(IHost host, McpClient client, string auditDirectory, bool ownsDirectory, PipeWriter toServer)
    {
        _host = host;
        Client = client;
        AuditDirectory = auditDirectory;
        _ownsDirectory = ownsDirectory;
        _toServer = toServer;
    }

    /// <summary>The SDK's client, connected and signed on.</summary>
    public McpClient Client { get; }

    /// <summary>The server's container.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>Where this server writes its audit files.</summary>
    public string AuditDirectory { get; }

    /// <summary>A second identity provider beside corp, for a provider bound elsewhere.</summary>
    public static AuthenticationConfig TwoIdentityProviders()
    {
        var identity = FrameHarness.Identity();
        identity.IdentityProviders["partner"] = new IdentityProviderConfig
        {
            Name = "partner",
            Authority = "https://partner.example.com",
            Issuer = "https://partner.example.com/",
            Algorithms = ["RS256"],
            ScopeCatalog = ["test:act", "weather:read", "observations:read", "demo:read", "demo:write"],
        };
        return identity;
    }

    /// <summary>A caller as the identity layer would have normalised one.</summary>
    public static ClaimsPrincipal Caller(
        string identityProvider = "corp",
        string subject = "alice",
        string[]? scopes = null,
        DateTimeOffset? issuedAt = null,
        string? jti = "jti-1",
        string? client = "client-1")
    {
        var claims = new List<Claim>
        {
            new(TrustDomainClaims.IdentityProvider, identityProvider),
            new("sub", subject),
            new("iat", (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };
        claims.AddRange((scopes ?? ["test:act"]).Select(scope => new Claim(TrustDomainClaims.Scope, scope)));
        if (jti is not null)
        {
            claims.Add(new Claim("jti", jti));
        }

        if (client is not null)
        {
            claims.Add(new Claim("client_id", client));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    /// <param name="modules">The provider modules; the test module with its four tools when none are given.</param>
    /// <param name="caller">Who every request comes from; <see cref="Caller"/>'s default when none is given.</param>
    /// <param name="anonymous">No caller at all: nobody is signed in.</param>
    /// <param name="configure">Changes to the settings before the server is composed.</param>
    /// <param name="outsideTheFrame">Services registered after the composition, where no provider can register them.</param>
    /// <param name="identity">The identity providers; corp alone when none are given.</param>
    /// <param name="logs">Where the server's ordinary log goes; nowhere when none is given.</param>
    /// <param name="logLevel">The lowest level <paramref name="logs"/> is given.</param>
    /// <param name="canConfirm">Whether the client answers a confirmation request.</param>
    /// <param name="confirms">What it answers.</param>
    /// <param name="auditDirectory">Where the audit files go; a directory of the server's own, removed with it, when none is given.</param>
    /// <param name="protocolVersion">The protocol version the client asks for; the SDK's newest when none is given.</param>
    public static async Task<StreamServer> StartAsync(
        IReadOnlyList<IProviderModule>? modules = null,
        ClaimsPrincipal? caller = null,
        bool anonymous = false,
        Action<Dictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? outsideTheFrame = null,
        AuthenticationConfig? identity = null,
        ILoggerProvider? logs = null,
        LogLevel logLevel = LogLevel.Information,
        bool canConfirm = false,
        bool confirms = true,
        string? auditDirectory = null,
        string? protocolVersion = null)
    {
        modules ??= [new TestModule()];
        var directory = auditDirectory ?? Path.Combine(Path.GetTempPath(), $"mcp-audit-tests-{Guid.NewGuid():N}");

        var settings = FrameHarness.Settings(modules);
        settings["Confirmation:Key"] = InProcessServer.ConfirmationKey;
        settings["Audit:Path"] = directory;
        configure?.Invoke(settings);

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.AddProvider(logs);
                logging.SetMinimumLevel(logLevel);
            }
        });
        builder.Services.AddSingleton(identity ?? FrameHarness.Identity());
        if (!anonymous)
        {
            builder.Services.AddSingleton(caller ?? Caller());
        }

        var toServer = new Pipe();
        var fromServer = new Pipe();
        builder.Services.AddGovernedMcpServer(builder.Configuration, builder.Environment, modules)
            .WithStreamServerTransport(toServer.Reader.AsStream(), fromServer.Writer.AsStream());
        outsideTheFrame?.Invoke(builder.Services);

        var host = builder.Build();
        try
        {
            GovernedServer.ValidateAtStartup(host.Services);
            await host.StartAsync();
        }
        catch
        {
            // What a server that refuses to start leaves behind is not this test's to keep.
            host.Dispose();
            if (auditDirectory is null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            throw;
        }

        var options = new McpClientOptions { ProtocolVersion = protocolVersion };
        if (canConfirm)
        {
            options.Handlers.ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult
            {
                Action = confirms ? "accept" : "decline",
                Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) },
            });
        }

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), fromServer.Reader.AsStream()), options);
        return new StreamServer(host, client, directory, ownsDirectory: auditDirectory is null, toServer.Writer);
    }

    /// <summary>Every record this server has written, oldest first, each as it reads from the file.</summary>
    public IReadOnlyList<JsonElement> Records() => AuditFiles.Records(AuditDirectory);

    /// <summary>The records about <paramref name="method"/> requests.</summary>
    public IReadOnlyList<JsonElement> RecordsOf(string method) =>
        [.. Records().Where(r => r.GetProperty("mcp").ValueKind == JsonValueKind.Object && r.GetProperty("mcp").GetProperty("method").GetString() == method)];

    /// <summary>Sends a request the SDK's client has no call for, and returns the error message it was answered with, or null.</summary>
    public async Task<string?> RawAsync(string method, object? parameters = null)
    {
        try
        {
            await Client.SendRequestAsync(new JsonRpcRequest
            {
                Method = method,
                Params = parameters is null ? null : JsonSerializer.SerializeToNode(parameters),
            });
            return null;
        }
        catch (ModelContextProtocol.McpException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Writes one line to the server as it is, past the SDK's client: for a message no well-behaved client would send. Its
    /// answer goes to the client, which is waiting for none and drops it; what the server made of it is on the audit trail,
    /// which <see cref="WaitForRecordsAsync"/> waits for. Never while the client is sending.
    /// </summary>
    public async Task InjectAsync(string json)
    {
        await _toServer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json + '\n'));
        await _toServer.FlushAsync();
    }

    /// <summary>Waits until the trail holds <paramref name="count"/> records, and returns them; fails after ten seconds.</summary>
    public async Task<IReadOnlyList<JsonElement>> WaitForRecordsAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var records = Records();
            if (records.Count >= count)
            {
                return records;
            }

            Assert.True(DateTime.UtcNow < deadline, $"the audit trail holds {records.Count} records after ten seconds, and {count} were expected: {string.Join(" | ", records.Select(r => r.GetProperty("event").GetString()))}");
            await Task.Delay(25);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await _host.StopAsync(stop.Token);
        }

        _host.Dispose();
        if (_ownsDirectory && Directory.Exists(AuditDirectory))
        {
            Directory.Delete(AuditDirectory, recursive: true);
        }
    }
}

/// <summary>
/// The in-process HTTP server, for what only HTTP has — real tokens, an address, a user agent, the HTTP layer's own
/// refusals — without a container (contract-006 · T-3, T-10).
///
/// That server runs as Production, which requires Redis, and the suite's Redis is a container. Pointed instead at an
/// address where no Redis answers, it starts all the same (an unreachable Redis does not stop the server, it makes every
/// limit check fail closed) and everything in front of the limit checks works as shipped: the HTTP pipeline,
/// authentication, the request-kind gate, the lists, and every check of the request gate that comes before a limit is
/// counted. These tests use only those. What needs a live Redis is the container-backed suite's, which cannot run today.
/// </summary>
public static class NoRedis
{
    /// <summary>A Redis address nothing answers at: loopback, port 1.</summary>
    public const string ConnectionString = "127.0.0.1:1,abortConnect=false";
}

/// <summary>Reading audit files as a test does: while the server may still hold them open.</summary>
public static class AuditFiles
{
    /// <summary>The audit files in <paramref name="directory"/>, in name order: a day's before the next day's.</summary>
    public static IReadOnlyList<string> In(string directory) =>
        Directory.Exists(directory) ? [.. Directory.GetFiles(directory, "audit-*.jsonl").Order(StringComparer.Ordinal)] : [];

    /// <summary>A file's lines, read while another process may be writing it.</summary>
    public static IReadOnlyList<string> Lines(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>Every record in <paramref name="directory"/>, oldest first.</summary>
    public static IReadOnlyList<JsonElement> Records(string directory) =>
        [.. In(directory).SelectMany(Lines).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        })];

    /// <summary>A record's event name: the text of its event before the first colon.</summary>
    public static string EventOf(JsonElement record)
    {
        var text = record.GetProperty("event").GetString()!;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? text : text[..colon];
    }

    /// <summary>A record's rule, or null.</summary>
    public static string? RuleOf(JsonElement record) =>
        record.GetProperty("decision").ValueKind == JsonValueKind.Object ? record.GetProperty("decision").GetProperty("rule").GetString() : null;

    /// <summary>A record's outcome, or null.</summary>
    public static string? OutcomeOf(JsonElement record) =>
        record.GetProperty("decision").ValueKind == JsonValueKind.Object ? record.GetProperty("decision").GetProperty("outcome").GetString() : null;
}

/// <summary>A sink a test controls: it keeps what it is given, and fails while it is told to.</summary>
public sealed class SwitchableSink : IAuditSink
{
    private readonly List<AuditRecord> _records = [];

    /// <summary>While true, every append throws, as a disk that cannot be written does.</summary>
    public bool Failing { get; set; }

    /// <summary>Called with each record as it is appended, before the append returns.</summary>
    public Action<AuditRecord>? OnAppend { get; set; }

    public bool Gating => true;

    public string Description => "a test's sink";

    public IReadOnlyList<AuditRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public void Append(AuditRecord record)
    {
        if (Failing)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        OnAppend?.Invoke(record);
        lock (_records)
        {
            _records.Add(record);
        }
    }
}
