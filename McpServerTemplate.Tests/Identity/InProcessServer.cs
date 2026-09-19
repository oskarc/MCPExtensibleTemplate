using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Identity;
using McpServerTemplate.Providers.JsonPlaceholder;
using McpServerTemplate.Providers.Smhi;
using McpServerTemplate.Providers.SmhiObs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// The shipped HTTP server, composed in this process, with each identity provider's bearer
/// handler pointed at a <see cref="TestIdentityProvider"/> backchannel.
///
/// contract-002 · G-10 — this is what makes the token guarantees checkable. A server spawned as a
/// child process fetches its signing keys from a real authority, so no test can mint a token it
/// would accept. Composing it here reaches the one seam that matters — the backchannel — and
/// changes nothing else: the same registration, the same middleware order, the same filters.
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

    public static async Task<InProcessServer> StartAsync(
        IEnumerable<TestIdentityProvider> identityProviders,
        string resource = "https://mcp.example.com/mcp",
        Action<Dictionary<string, string?>>? configure = null)
    {
        var providers = identityProviders.ToArray();

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
        };

        foreach (var idp in providers)
        {
            settings[$"Authentication:IdentityProviders:{idp.Name}:Authority"] = idp.Authority;
            settings[$"Authentication:IdentityProviders:{idp.Name}:Issuer"] = idp.Issuer;
            settings[$"Authentication:IdentityProviders:{idp.Name}:Algorithms:0"] = "RS256";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:0"] = "weather:read";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:1"] = "observations:read";
            settings[$"Authentication:IdentityProviders:{idp.Name}:ScopeCatalog:2"] = "demo:read";
        }

        // Every provider bound to the first identity provider unless a test says otherwise.
        foreach (var provider in new[] { "Smhi", "SmhiObs", "JsonPlaceholder" })
        {
            settings[$"Providers:{provider}:IdentityProvider"] = providers[0].Name;
        }

        configure?.Invoke(settings);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var byName = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);

        HttpServerComposition.AddHttpServer(
            builder,
            (services, configuration) => services
                .AddMcpServer(options => options.ServerInfo = new() { Name = "Test", Version = "1.0.0" })
                .WithToolsFromAssembly(typeof(SmhiTools).Assembly)
                .WithResourcesFromAssembly(typeof(SmhiTools).Assembly)
                .WithPromptsFromAssembly(typeof(SmhiTools).Assembly)
                // Registered exactly as Program.cs does. Building the server without these is how
                // a broken binding stayed invisible: the enforcement path existed and no test ran it.
                .WithRequestFilters(filters =>
                {
                    filters.AddListToolsFilter(TrustDomainFilters.List());
                    filters.AddCallToolFilter(TrustDomainFilters.Call());
                }),
            configureIdentityForTests: (name, options) =>
            {
                // The one seam. Discovery and JWKS are answered in-process, and counted.
                options.BackchannelHttpHandler = byName[name].Handler;
                options.RequireHttpsMetadata = false;
            });

        builder.Services.AddSmhiProvider(builder.Configuration);
        builder.Services.AddSmhiObsProvider(builder.Configuration);
        builder.Services.AddJsonPlaceholderProvider(builder.Configuration);

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
