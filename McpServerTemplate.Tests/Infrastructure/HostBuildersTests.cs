using System.Net;
using System.Reflection;
using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
using McpServerTemplate.Tests.Frame;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

// contract-005 review round 4 — the user secrets a Development host reads are the ones its application's
// assembly names, and a test that builds one as the server read the developer's own. This assembly names
// a store nobody keeps, so a host that names it reads no one's secrets (HostBuildersTests).
[assembly: UserSecretsId(McpServerTemplate.Tests.Infrastructure.HostBuildersTests.TestSecretsId)]

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-003 · G-11, contract-005 review round 2 — both hosts read their settings once, at startup,
/// where every check reads them: no settings file either host reads is watched for changes.
///
/// The default hosts watch appsettings.json, appsettings.{Environment}.json and, in Development, the
/// user secrets, and read a changed file back into the running server, where no startup check sees it.
/// ServerProcessTests shows what that did to the shipped process; this holds the cause, for both hosts,
/// in both environments.
/// </summary>
public class HostBuildersTests
{
    /// <summary>The user secrets id this test assembly carries: a store nobody keeps, so it is always empty.</summary>
    public const string TestSecretsId = "mcp-server-template-tests-no-secrets";

    /// <summary>
    /// The application these hosts are built as: this test assembly. A Development host reads the user
    /// secrets its application's assembly names (UserSecretsId), so named as the server it read the
    /// developer's own; named as this assembly it reads <see cref="TestSecretsId"/>'s store, which nobody
    /// keeps. Review round 4 — the rows used to name the server.
    /// </summary>
    private static readonly string[] AsTheTests = ["--applicationName=McpServerTemplate.Tests"];

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void The_http_host_watches_no_settings_file(string environment)
    {
        var builder = HostBuilders.ForHttp([.. AsTheTests, $"--environment={environment}"]);
        using var configuration = builder.Configuration;

        AssertReadOnce(configuration, environment, builder.Environment.ApplicationName);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void The_stdio_host_watches_no_settings_file(string environment)
    {
        var builder = HostBuilders.ForStdio(AsTheTests, environment);
        using var configuration = builder.Configuration;

        AssertReadOnce(configuration, environment, builder.Environment.ApplicationName);
    }

    /// <summary>
    /// contract-005 review round 3 — the launch arguments cannot switch reading back on, whatever they
    /// are and wherever they sit. A switch with no value takes the argument after it as its value, so one
    /// at the end of the launch — alone, or the last of an odd number — swallowed a setting appended after
    /// it, and every settings file was watched again. The last row asks for reloading outright.
    /// </summary>
    public static TheoryData<string, string> Launches() => new()
    {
        { "Production", "--MyFlag" },
        { "Development", "--A --B --C" },
        { "Production", "--Transport=http --MyFlag --HttpTransport:Port=3001" },
        { "Development", "--A --MyFlag" },
        { "Production", "--hostBuilder:reloadConfigOnChange=true" },
    };

    [Theory]
    [MemberData(nameof(Launches))]
    public void The_http_host_watches_no_settings_file_however_it_is_launched(string environment, string launch)
    {
        var builder = HostBuilders.ForHttp([.. AsTheTests, $"--environment={environment}", .. launch.Split(' ')]);
        using var configuration = builder.Configuration;

        AssertReadOnce(configuration, environment, builder.Environment.ApplicationName);
    }

    [Theory]
    [MemberData(nameof(Launches))]
    public void The_stdio_host_watches_no_settings_file_however_it_is_launched(string environment, string launch)
    {
        var builder = HostBuilders.ForStdio([.. AsTheTests, .. launch.Split(' ')], environment);
        using var configuration = builder.Configuration;

        AssertReadOnce(configuration, environment, builder.Environment.ApplicationName);
    }

    /// <summary>
    /// contract-005 review round 3 — the control for the rows above: building every provider afresh loses
    /// nothing. Each host's settings, key by key, are what the framework's own host reads from the same
    /// launch.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void The_settings_read_once_are_the_settings_the_host_reads(string environment)
    {
        string[] launch = [.. AsTheTests, $"--environment={environment}", "--MyFlag"];

        var http = HostBuilders.ForHttp(launch);
        var plainHttp = WebApplication.CreateBuilder(["--hostBuilder:reloadConfigOnChange=false", .. launch]);
        using (http.Configuration)
        using (plainHttp.Configuration)
        {
            AssertSameSettings(plainHttp.Configuration, http.Configuration);
        }

        var stdio = HostBuilders.ForStdio(AsTheTests, environment);
        var plainStdio = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings
        {
            Args = ["--hostBuilder:reloadConfigOnChange=false", .. AsTheTests],
            EnvironmentName = environment,
        });
        using (stdio.Configuration)
        using (plainStdio.Configuration)
        {
            AssertSameSettings(plainStdio.Configuration, stdio.Configuration);
        }
    }

    /// <summary>
    /// The same keys, with the same values. Review round 4 — these are a host's real settings, so a failure
    /// names keys, and for a value that differs a hash of each side; never a value itself.
    /// </summary>
    private static void AssertSameSettings(IConfiguration expected, IConfiguration actual)
    {
        var want = expected.AsEnumerable().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var got = actual.AsEnumerable().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var missing = want.Keys.Where(k => !got.ContainsKey(k)).Order(StringComparer.Ordinal).ToArray();
        var added = got.Keys.Where(k => !want.ContainsKey(k)).Order(StringComparer.Ordinal).ToArray();
        var changed = want
            .Where(kv => got.TryGetValue(kv.Key, out var value) && !string.Equals(value, kv.Value, StringComparison.Ordinal))
            .Select(kv => $"{kv.Key} (sha256 {Hash(kv.Value)} became {Hash(got[kv.Key])})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0 && added.Length == 0 && changed.Length == 0,
            $"the settings read once are not the settings the host reads. Missing: [{string.Join(", ", missing)}]. "
            + $"Added: [{string.Join(", ", added)}]. Changed: [{string.Join(", ", changed)}].");

        static string Hash(string? value) => value is null
            ? "(none)"
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];
    }

    /// <summary>
    /// contract-005 review round 3 — the late change the finding showed, in the shipped composition,
    /// launched with a trailing switch that has no value: a Kestrel endpoint written into appsettings.json
    /// while the server runs opened a listener. The server's content root is a directory of its own
    /// holding copies of the shipped settings files; this process watches the same file, so the window
    /// starts once the change has been delivered. The next start reads the change, and refuses it.
    /// </summary>
    [Fact]
    public async Task A_settings_file_changed_while_the_http_host_runs_changes_nothing_after_a_trailing_switch()
    {
        var root = Directory.CreateTempSubdirectory("mcp-read-once-").FullName;
        WebApplication? app = null;
        try
        {
            foreach (var file in new[] { "appsettings.json", "appsettings.Production.json" })
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(root, file));
            }

            string[] launch = [$"--contentRoot={root}", "--environment=Production", "--MyFlag"];
            var port = FreePort();
            var late = FreePort();
            app = await StartAsync(launch, port);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.Equal(HttpStatusCode.OK, await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(10)));

            using var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root);
            var delivered = new TaskCompletionSource();
            using var watch = files.Watch("appsettings.json").RegisterChangeCallback(_ => delivered.TrySetResult(), null);

            var settings = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "appsettings.json")))!.AsObject();
            settings["Kestrel"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Endpoints"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Late"] = new System.Text.Json.Nodes.JsonObject { ["Url"] = $"http://127.0.0.1:{late}" },
                },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), settings.ToJsonString());
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // A watched JSON file is read again 250 ms after it changes, and Kestrel binds a new endpoint at
            // once: two seconds is well past both.
            var opened = await AnswerWithinAsync(http, late, TimeSpan.FromSeconds(2));
            Assert.True(
                opened is null,
                $"launched with {string.Join(' ', launch.Skip(2))}, a Kestrel endpoint written into appsettings.json after startup "
                + $"opened http://127.0.0.1:{late}: /healthz answered {(int?)opened} there. The running server applied a setting "
                + "no startup check had read.");
            Assert.Equal(HttpStatusCode.OK, await AnswerWithinAsync(http, port, TimeSpan.FromSeconds(5)));

            await app.StopAsync();
            await app.DisposeAsync();
            app = null;

            // The next start reads the file as it now is, and refuses what it gained.
            var refusal = await Assert.ThrowsAsync<ConfigurationException>(() => StartAsync(launch, FreePort()));
            Assert.Contains("'Kestrel:Endpoints:Late:Url' is not a setting this server honours", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// contract-005 review round 3 — the checked refusal: whatever built the configuration, a source that
    /// could read settings again while the server runs stops it at startup, named. Each row is one way a
    /// source can do that: a file read again when it changes, the same inside a chained configuration,
    /// and a kind of source the server cannot tell reads once.
    /// </summary>
    [Theory]
    [InlineData("a watched file")]
    [InlineData("a chained configuration holding a watched file")]
    [InlineData("a source of another kind")]
    public void A_settings_source_that_could_read_again_stops_the_server_naming_it(string source)
    {
        var directory = Directory.CreateTempSubdirectory("mcp-read-once-").FullName;
        var module = new TestModule();
        IConfigurationRoot? inner = null;
        IConfigurationRoot? configuration = null;
        try
        {
            File.WriteAllText(Path.Combine(directory, "extra.json"), "{}");
            var builder = new ConfigurationBuilder().AddInMemoryCollection(StartupRefusalTests.SettingsWithKey([module]));
            switch (source)
            {
                case "a watched file":
                    builder.AddJsonFile(Path.Combine(directory, "extra.json"), optional: false, reloadOnChange: true);
                    break;
                case "a chained configuration holding a watched file":
                    inner = new ConfigurationBuilder().AddJsonFile(Path.Combine(directory, "extra.json"), optional: false, reloadOnChange: true).Build();
                    builder.AddConfiguration(inner);
                    break;
                default:
                    builder.Add(new Refreshing());
                    break;
            }

            configuration = builder.Build();
            var refusal = Assert.Throws<ConfigurationException>(() => Compose(configuration, module));

            Assert.Contains(source == "a source of another kind" ? nameof(Refreshing) : "'extra.json'", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
            (inner as IDisposable)?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_sources_that_are_read_once_compose()
    {
        // The control for the rows above: settings in memory, from the environment, from the command line,
        // in a chained configuration of those, and in a file that is not watched.
        var directory = Directory.CreateTempSubdirectory("mcp-read-once-").FullName;
        var module = new TestModule();
        try
        {
            File.WriteAllText(Path.Combine(directory, "extra.json"), "{}");
            var inner = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(StartupRefusalTests.SettingsWithKey([module]))
                .AddEnvironmentVariables("MCP_READ_ONCE_TEST_")
                .AddCommandLine([])
                .AddConfiguration(inner)
                .AddJsonFile(Path.Combine(directory, "extra.json"), optional: false, reloadOnChange: false)
                .Build();

            Compose(configuration, module);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Compose(IConfiguration configuration, TestModule module)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernedMcpServer(
            configuration,
            new HostingEnvironment { EnvironmentName = "Development", ApplicationName = "test", ContentRootPath = AppContext.BaseDirectory },
            [module]);
    }

    /// <summary>The server's own user secrets id: the developer's store, which no test may read.</summary>
    private static string? ServerSecretsId => typeof(HostBuilders).Assembly.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;

    /// <summary>A settings source of a kind the server has no reason to believe reads once.</summary>
    private sealed class Refreshing : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
    }

    private static void AssertReadOnce(ConfigurationManager configuration, string environment, string applicationName)
    {
        var files = configuration.Sources.OfType<FileConfigurationSource>().ToArray();

        // Positive control: the files a host reads are the ones seen here.
        Assert.Contains(files, f => f.Path == "appsettings.json");
        Assert.Contains(files, f => f.Path == $"appsettings.{environment}.json");
        if (environment == "Development")
        {
            Assert.Contains(files, f => f.Path == "secrets.json");

            // Review round 4 — whose: the store the host resolves is the one its application's assembly names,
            // so the id is where it reads from. Never the server's own id, which is the developer's store.
            var secrets = Assembly.Load(new AssemblyName(applicationName)).GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
            Assert.True(
                secrets == TestSecretsId,
                $"a Development host named '{applicationName}' reads the user secrets of UserSecretsId '{secrets}'"
                + (secrets == ServerSecretsId ? ", the server's own: the developer's store" : string.Empty)
                + $", not the test-only '{TestSecretsId}'.");
        }

        Assert.All(files, f => Assert.False(f.ReloadOnChange, $"{f.Path} is read again whenever it changes, and no check reads it then."));
    }

    /// <summary>The shipped composition, in this process, from <paramref name="launch"/> — as Program.cs builds it, less logging.</summary>
    private static async Task<WebApplication> StartAsync(string[] launch, int port)
    {
        var builder = HostBuilders.ForHttp(launch);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Resource"] = "https://mcp.example.com/mcp",
            ["Authentication:IdentityProviders:corp:Authority"] = "https://login.example.com",
            ["Authentication:IdentityProviders:corp:Issuer"] = "https://login.example.com/",
            ["Authentication:IdentityProviders:corp:Algorithms:0"] = "RS256",
            ["Authentication:IdentityProviders:corp:ScopeCatalog:0"] = "weather:read",
            ["Authentication:IdentityProviders:corp:ScopeCatalog:1"] = "observations:read",
            ["HttpTransport:KnownNetworks:0"] = "127.0.0.0/8",
            ["Limits:Redis"] = await TestRedis.ConnectionStringAsync(),
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        HttpServerComposition.AddHttpServer(builder, BuiltInProviders.Create());
        var app = builder.Build().UseHttpServer();
        await app.StartAsync();
        return app;
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>What /healthz on 127.0.0.1:<paramref name="port"/> answered within <paramref name="window"/>, or null when nothing did.</summary>
    private static async Task<HttpStatusCode?> AnswerWithinAsync(HttpClient http, int port, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/healthz"));
                return response.StatusCode;
            }
            catch (HttpRequestException)
            {
                // Nothing listening there.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(200);
        }
    }
}
