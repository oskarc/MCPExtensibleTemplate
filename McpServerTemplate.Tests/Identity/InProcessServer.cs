using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// The shipped HTTP server, composed in this process, with each identity provider's bearer
/// handler pointed at a <see cref="TestIdentityProvider"/> backchannel.
///
/// contract-002 · G-10 — this is what makes the token guarantees checkable. A server spawned as a
/// child process fetches its signing keys from a real authority, so no test can mint a token it
/// would accept. Composing it here reaches the one seam that matters — the backchannel — and
/// changes nothing else: the same registration, the same middleware order, the same filters.
///
/// contract-003 · G-3 — and the MCP server inside it is composed by the same method the shipped
/// server calls. This harness used to declare its own copy of the request filters "exactly as
/// Program.cs does"; the copy had already drifted. It now passes only a list of provider modules,
/// and a test compares what it installs with what the shipped process logs (T-10).
/// </summary>
public sealed class InProcessServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private InProcessServer(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    /// <summary>The server's container, for tests that inspect what the frame installed.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The address the server listens on.</summary>
    public Uri Address => Client.BaseAddress!;

    /// <summary>A base64 key for signing confirmations, the same in every test run.</summary>
    public const string ConfirmationKey = "dGVzdC1rZXktdGVzdC1rZXktdGVzdC1rZXktdGVzdC1rZXk=";

    public static async Task<InProcessServer> StartAsync(
        IEnumerable<TestIdentityProvider> identityProviders,
        string resource = "https://mcp.example.com/mcp",
        Action<Dictionary<string, string?>>? configure = null,
        IReadOnlyList<IProviderModule>? modules = null,
        string? redis = null)
    {
        var providers = identityProviders.ToArray();
        modules ??= BuiltInProviders.Create();

        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Resource"] = resource,

            // Loopback plaintext under test: the harness is the trusted proxy (G-12).
            ["HttpTransport:KnownNetworks:0"] = "127.0.0.0/8",

            // The demo providers still need their own configuration to register.
            ["Providers:Smhi:BaseUrl"] = "https://opendata-download-metfcst.smhi.se",
            ["Providers:Smhi:UserAgent"] = "test/1.0",
            ["Providers:SmhiObs:BaseUrl"] = "https://opendata-download-metobs.smhi.se",
            ["Providers:SmhiObs:UserAgent"] = "test/1.0",
            ["Providers:JsonPlaceholder:BaseUrl"] = "https://jsonplaceholder.typicode.com",
            ["Providers:JsonPlaceholder:UserAgent"] = "test/1.0",

            // contract-003 — limits in Redis, as Production requires, and a confirmation key.
            ["Limits:Redis"] = redis ?? await TestRedis.ConnectionStringAsync(),
            ["Confirmation:Key"] = ConfirmationKey,
        };

        for (var i = 0; i < modules.Count; i++)
        {
            settings[$"Providers:Enabled:{i}"] = modules[i].Name;
        }

        // Only the providers this server has: a section for any other is a setting it would
        // ignore, which the settings allowlist refuses (contract-003 · G-11).
        foreach (var key in settings.Keys.Where(k => k.StartsWith("Providers:", StringComparison.Ordinal) &&
                     !k.StartsWith("Providers:Enabled:", StringComparison.Ordinal) &&
                     modules.All(m => !k.StartsWith($"Providers:{m.Name}:", StringComparison.Ordinal))).ToArray())
        {
            settings.Remove(key);
        }

        foreach (var idp in providers)
        {
            settings[$"Authentication:IdentityProviders:{idp.Name}:Authority"] = idp.Authority;
            settings[$"Authentication:IdentityProviders:{idp.Name}:Issuer"] = idp.Issuer;
            settings[$"Authentication:IdentityProviders:{idp.Name}:Algorithms:0"] = "RS256";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:0"] = "weather:read";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:1"] = "observations:read";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:2"] = "demo:read";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:3"] = "demo:write";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:4"] = "test:act";
        }

        // Every provider bound to the first identity provider unless a test says otherwise.
        foreach (var module in modules)
        {
            settings[$"Providers:{module.Name}:IdentityProvider"] = providers[0].Name;
        }

        configure?.Invoke(settings);

        var builder = WebApplication.CreateBuilder();

        // The shipped appsettings files sit beside the test assembly and name every built-in
        // provider. A test declares its configuration in full, so they are not read here: a section
        // for a provider this test did not enable is a setting the server would ignore, and the
        // settings allowlist rightly refuses it (contract-003 · G-11).
        foreach (var file in builder.Configuration.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>().ToArray())
        {
            builder.Configuration.Sources.Remove(file);
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var byName = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);

        HttpServerComposition.AddHttpServer(
            builder,
            modules,
            configureIdentityForTests: (name, options) =>
            {
                // The one seam. Discovery and JWKS are answered in-process, and counted.
                options.BackchannelHttpHandler = byName[name].Handler;
                options.RequireHttpsMetadata = false;
            });

        var app = builder.Build().UseHttpServer();
        await app.StartAsync();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };

        return new InProcessServer(app, client);
    }

    /// <summary>Posts an initialize request, optionally bearing a token.</summary>
    public async Task<HttpResponseMessage> PostAsync(string? token, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        if (token is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
