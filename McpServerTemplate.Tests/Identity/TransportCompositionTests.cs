using McpServerTemplate.Infrastructure;
using McpServerTemplate.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-005 · G-17 round 1, on contract-002 · T-12's ground — the transport as the shipped composition sets it up
/// (HttpServerComposition.AddHttpServer): whom it takes forwarded headers from, and where it refuses plaintext.
///
/// ASP.NET Core's web host registers a forwarded-headers step of its own, run ahead of the whole pipeline, and an options
/// setup that empties the trusted lists, both switched on by its ForwardedHeaders_Enabled setting. The composition set the
/// lists only when a proxy was declared, so with none declared — every environment but Production — that switch left
/// them empty, and every client's X-Forwarded-For and X-Forwarded-Proto were believed. And the plaintext guard ran only
/// in an environment named Production: under any other name, Staging among them, a server with no proxy started and read
/// bearer tokens over plain http. ForwardedHeadersTests holds a copy of the registration; these hold the composition.
/// </summary>
public class TransportCompositionTests
{
    /// <summary>
    /// A deployment's settings, every provider bound to one identity provider, with no proxy declared; and Redis, which
    /// every environment but Development requires, when <paramref name="redis"/> is given.
    /// </summary>
    private static Dictionary<string, string?> Settings(string? redis = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Resource"] = "https://mcp.example.com/mcp",
            ["Authentication:IdentityProviders:corp:Authority"] = "https://login.example.com",
            ["Authentication:IdentityProviders:corp:Issuer"] = "https://login.example.com/",
            ["Authentication:IdentityProviders:corp:Algorithms:0"] = "RS256",
            ["Authentication:IdentityProviders:corp:ScopeCatalog:0"] = "weather:read",
            ["Authentication:IdentityProviders:corp:ScopeCatalog:1"] = "observations:read",
            ["Authentication:IdentityProviders:corp:ScopeCatalog:2"] = "demo:read",
            ["Authentication:IdentityProviders:corp:ScopeCatalog:3"] = "demo:write",
        };

        var upstreams = new Dictionary<string, string>
        {
            ["Smhi"] = "https://opendata-download-metfcst.smhi.se",
            ["SmhiObs"] = "https://opendata-download-metobs.smhi.se",
            ["JsonPlaceholder"] = "https://jsonplaceholder.typicode.com",
        };
        var i = 0;
        foreach (var (provider, upstream) in upstreams)
        {
            settings[$"Providers:Enabled:{i++}"] = provider;
            settings[$"Providers:{provider}:BaseUrl"] = upstream;
            settings[$"Providers:{provider}:UserAgent"] = "test/1.0";
            settings[$"Providers:{provider}:IdentityProvider"] = "corp";
        }

        if (redis is not null)
        {
            settings["Limits:Redis"] = redis;
        }

        return settings;
    }

    /// <summary>
    /// A web host as the program's is created, in <paramref name="environment"/>, with <paramref name="settings"/> and not
    /// the shipped settings files, which name providers a test may not enable (as InProcessServer has it).
    /// </summary>
    private static WebApplicationBuilder Builder(string environment, Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        foreach (var file in builder.Configuration.Sources.OfType<JsonConfigurationSource>().ToArray())
        {
            builder.Configuration.Sources.Remove(file);
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Environment.EnvironmentName = environment;
        return builder;
    }

    /// <summary>
    /// The shipped composition over <paramref name="builder"/>; then the limit store it connected, which it registers as an
    /// instance no container disposes, is let go of, refusal or not.
    /// </summary>
    private static void Compose(WebApplicationBuilder builder)
    {
        try
        {
            HttpServerComposition.AddHttpServer(builder, BuiltInProviders.Create());
        }
        finally
        {
            foreach (var store in builder.Services
                .Where(d => d.ServiceType == typeof(McpServerTemplate.Infrastructure.Frame.ILimitStore))
                .Select(d => d.ImplementationInstance)
                .OfType<IDisposable>())
            {
                store.Dispose();
            }
        }
    }

    /// <summary>The forwarded-headers options the composed server's middleware is given.</summary>
    private static ForwardedHeadersOptions OptionsOf(WebApplicationBuilder builder)
    {
        using var services = builder.Services.BuildServiceProvider();
        return services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    /// <summary>What the framework's own setup does to the lists when its switch is on: it empties both.</summary>
    private static void EmptyTheLists(ForwardedHeadersOptions options)
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
    }

    /// <summary>
    /// With no proxy declared, forwarded headers are trusted from loopback alone — the framework's own defaults, ::1 and
    /// 127.0.0.0/8 — set by the composition itself, so nothing configured before it or after it can leave the lists empty.
    /// </summary>
    [Fact]
    public void T12_with_no_proxy_declared_forwarded_headers_are_trusted_from_loopback_alone_whatever_emptied_the_lists()
    {
        var builder = Builder("Development", Settings());

        // As the web host's defaults register their setup: before the composition. And once more after it.
        builder.Services.Configure<ForwardedHeadersOptions>(EmptyTheLists);
        Compose(builder);
        builder.Services.Configure<ForwardedHeadersOptions>(EmptyTheLists);

        var options = OptionsOf(builder);

        Assert.Equal(["::1"], options.KnownProxies.Select(p => p.ToString()));
        Assert.Equal(["127.0.0.0/8"], options.KnownIPNetworks.Select(n => n.ToString()));
    }

    /// <summary>The declared proxies and networks are the only ones trusted, whatever else added to the lists, before or after.</summary>
    [Fact]
    public void T12_declared_proxies_are_the_only_ones_trusted_whatever_else_configured_the_lists()
    {
        var settings = Settings();
        settings["HttpTransport:KnownProxies:0"] = "10.1.2.3";
        settings["HttpTransport:KnownNetworks:0"] = "10.9.0.0/16";
        var builder = Builder("Development", settings);

        builder.Services.Configure<ForwardedHeadersOptions>(options => options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("0.0.0.0/1")));
        Compose(builder);
        builder.Services.Configure<ForwardedHeadersOptions>(options => options.KnownProxies.Add(System.Net.IPAddress.Parse("203.0.113.9")));

        var options = OptionsOf(builder);

        Assert.Equal(["10.1.2.3"], options.KnownProxies.Select(p => p.ToString()));
        Assert.Equal(["10.9.0.0/16"], options.KnownIPNetworks.Select(n => n.ToString()));
    }

    /// <summary>
    /// The web host's own forwarded-headers step (a startup filter that runs the middleware ahead of the whole pipeline) and
    /// its options setup are left out of the composition, as the key ring's loader is: nothing but the composition's own
    /// step reads forwarded headers, with the lists the composition sets.
    /// </summary>
    [Fact]
    public void T12_the_frameworks_own_forwarded_headers_step_and_setup_are_left_out_of_the_composition()
    {
        static string[] Framework(IServiceCollection services) =>
            [.. services
                .Where(d => d.ImplementationType is { } type && type.Assembly == typeof(WebApplication).Assembly
                    && type.Name.StartsWith("ForwardedHeaders", StringComparison.Ordinal))
                .Select(d => d.ImplementationType!.Name)
                .Order(StringComparer.Ordinal)];

        var builder = Builder("Development", Settings());

        // Positive control: the web host's defaults register both, so their absence below is the composition's doing.
        Assert.Equal(["ForwardedHeadersOptionsSetup", "ForwardedHeadersStartupFilter"], Framework(builder.Services));

        Compose(builder);

        Assert.Empty(Framework(builder.Services));

        // Only those two: host filtering's own startup filter stays.
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IStartupFilter) && d.ImplementationType?.Name == "HostFilteringStartupFilter");
    }

    /// <summary>
    /// The plaintext guard applies in every environment but Development, where the frame's other deployment rules (Redis,
    /// Providers:Enabled) already apply: a server that reads bearer tokens sits behind a proxy it trusts, which terminates
    /// TLS, whatever its environment is named.
    /// </summary>
    [Theory]
    [InlineData("Staging")]
    [InlineData("Prod")]
    public async Task T12_outside_development_a_server_with_no_proxy_it_trusts_refuses_to_start(string environment)
    {
        var settings = Settings(await TestRedis.ConnectionStringAsync());

        // Positive controls: Development, on the developer's own loopback, composes without a proxy; and this environment
        // composes with one, so the refusal below is the missing proxy's.
        Compose(Builder("Development", settings));
        Compose(Builder(environment, new Dictionary<string, string?>(settings) { ["HttpTransport:KnownProxies:0"] = "10.1.2.3" }));

        var ex = Assert.Throws<ConfigurationException>(() => Compose(Builder(environment, settings)));

        Assert.Contains("must sit behind a proxy it trusts explicitly", ex.Message, StringComparison.Ordinal);
        Assert.Contains("KnownProxies", ex.Message, StringComparison.Ordinal);
    }
}
