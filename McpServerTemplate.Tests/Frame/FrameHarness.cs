using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// Composes the governed server exactly as the shipped server does — through
/// <see cref="GovernedServer.AddGovernedMcpServer"/> — without a transport, and runs the startup
/// checks. What throws here is what makes the shipped process exit 78 (contract-001 · G-2).
/// </summary>
public static class FrameHarness
{
    private static readonly Dictionary<string, string> BuiltInUpstreams = new()
    {
        ["Smhi"] = "https://opendata-download-metfcst.smhi.se",
        ["SmhiObs"] = "https://opendata-download-metobs.smhi.se",
        ["JsonPlaceholder"] = "https://jsonplaceholder.typicode.com",
    };

    public static Dictionary<string, string?> Settings(IEnumerable<IProviderModule> modules, string idp = "corp")
    {
        var settings = new Dictionary<string, string?> { ["Limits:PerPrincipalPerMinute"] = "1000" };
        var i = 0;
        foreach (var module in modules)
        {
            settings[$"Providers:Enabled:{i++}"] = module.Name;
            settings[$"Providers:{module.Name}:IdentityProvider"] = idp;
            if (BuiltInUpstreams.TryGetValue(module.Name, out var upstream))
            {
                settings[$"Providers:{module.Name}:BaseUrl"] = upstream;
                settings[$"Providers:{module.Name}:UserAgent"] = "test/1.0";
            }
        }

        return settings;
    }

    public static AuthenticationConfig Identity(params string[] catalog) => new()
    {
        Resource = "https://mcp.example.com/mcp",
        IdentityProviders =
        {
            ["corp"] = new IdentityProviderConfig
            {
                Name = "corp",
                Authority = "https://login.example.com",
                Issuer = "https://login.example.com/",
                Algorithms = ["RS256"],
                ScopeCatalog = catalog.Length == 0 ? ["test:act", "weather:read", "observations:read", "demo:read", "demo:write"] : catalog,
            },
        },
    };

    /// <summary>Composes and validates; returns the container, or throws what startup would.</summary>
    public static ServiceProvider Compose(
        IReadOnlyList<IProviderModule> modules,
        Dictionary<string, string?>? settings = null,
        AuthenticationConfig? identity = null,
        string environment = "Development")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings(modules)).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(identity ?? Identity());

        services.AddGovernedMcpServer(
            configuration,
            new HostingEnvironment { EnvironmentName = environment, ApplicationName = "test", ContentRootPath = AppContext.BaseDirectory },
            modules);

        var provider = services.BuildServiceProvider();
        GovernedServer.ValidateAtStartup(provider);
        return provider;
    }

    /// <summary>The message startup refuses with.</summary>
    public static string Refusal(
        IReadOnlyList<IProviderModule> modules,
        Dictionary<string, string?>? settings = null,
        AuthenticationConfig? identity = null,
        string environment = "Development") =>
        Assert.Throws<ConfigurationException>(() => Compose(modules, settings, identity, environment)).Message;
}
