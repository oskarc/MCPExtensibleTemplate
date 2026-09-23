using System.ComponentModel;
using McpServerTemplate.Infrastructure.Frame;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Tests.Frame;

// ── Primitive types that exist only in the tests ──

[McpServerToolType]
public static class TestActTools
{
    /// <summary>
    /// Tests that count this class's calls run in one sequential collection: the counters are
    /// static, and a test class calling the same tool in parallel would move them.
    /// </summary>
    public const string Collection = "TestActTools counters";

    private static int _runs;

    private static int _echoes;

    public static int Runs => Volatile.Read(ref _runs);

    public static int Echoes => Volatile.Read(ref _echoes);

    [McpServerTool, Description("Reads nothing; echoes its input.")]
    public static string TestEcho([Description("Text to echo.")] string text)
    {
        Interlocked.Increment(ref _echoes);
        return text;
    }

    [McpServerTool, Description("A write: changes something that can be changed back.")]
    public static string TestUpdate([Description("The value to set.")] string value) => $"set {value}";

    [McpServerTool, Description("An irreversible action. Counts how many times it has run.")]
    public static string TestDestroy([Description("What to destroy.")] string target)
    {
        Interlocked.Increment(ref _runs);
        return $"destroyed {target}";
    }

    [McpServerTool, Description("Returns more than its cap allows.")]
    public static string TestFlood() => new('x', 10_000);
}

[McpServerToolType]
public static class TestUndeclaredTools
{
    [McpServerTool, Description("A tool no policy names.")]
    public static string TestOrphan() => "orphan";
}

[McpServerToolType]
public static class TestMislabelledTools
{
    [McpServerTool(Destructive = true), Description("Claims to be destructive; its policy says Read.")]
    public static string TestMislabelled() => "?";
}

public sealed class NotStaticTools
{
    [McpServerTool, Description("On an instance type.")]
    public static string TestInstance() => "?";
}

// ── Modules ──

/// <summary>A test module whose every part can be set. Defaults to a well-formed Read provider.</summary>
public class TestModule : IProviderModule
{
    public static EgressPolicy DefaultEgress => new(
        Hosts: ["api.example.com"],
        Methods: [HttpMethod.Get],
        MaxResponseBytes: 1024,
        AttemptTimeout: TimeSpan.FromSeconds(1),
        MaxRetryAttempts: 1,
        TotalTimeout: TimeSpan.FromSeconds(5),
        MaxConcurrentUpstreamCalls: 1,
        DailyCallBudget: 100);

    public TestModule(
        string name = "TestAct",
        IReadOnlyList<Type>? toolTypes = null,
        IReadOnlyDictionary<string, ToolPolicy>? tools = null,
        EgressPolicy? egress = null)
    {
        Name = name;
        ToolTypes = toolTypes ?? [typeof(TestActTools)];
        Policy = new ProviderPolicy(
            name,
            egress ?? DefaultEgress,
            tools ?? ActPolicies(),
            new Dictionary<string, ResourcePolicy>(),
            new Dictionary<string, PromptPolicy>());
    }

    public static Dictionary<string, ToolPolicy> ActPolicies() => new()
    {
        ["test_echo"] = new("test:act", RiskClass.Read, PerPrincipalPerMinute: 1000, MaxStringArgLength: 20),
        ["test_update"] = new("test:act", RiskClass.Write, PerPrincipalPerMinute: 1000),
        ["test_destroy"] = new("test:act", RiskClass.Irreversible, PerPrincipalPerMinute: 1000),
        ["test_flood"] = new("test:act", RiskClass.Read, PerPrincipalPerMinute: 1000, MaxOutputBytes: 1_000),
    };

    public string Name { get; }

    public ProviderPolicy Policy { get; }

    public IReadOnlyList<Type> ToolTypes { get; }

    public IReadOnlyList<Type> ResourceTypes { get; } = [];

    public IReadOnlyList<Type> PromptTypes { get; } = [];

    public IReadOnlyCollection<string> Settings { get; } = [];

    public virtual void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}

/// <summary>Removes a registration it did not make — here, the logging the host added.</summary>
public sealed class RemovingModule() : TestModule("Remover")
{
    public override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.RemoveAll<Microsoft.Extensions.Logging.ILoggerFactory>();
}

/// <summary>Adds its own request filter by configuring the MCP server's options.</summary>
public sealed class FilterAddingModule() : TestModule("FilterAdder")
{
    public override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.Configure<McpServerOptions>(o => o.Filters.Request.CallToolFilters.Add(next => next));
}

/// <summary>Registers a tool directly in the container, around the module's declared types.</summary>
public sealed class SideDoorModule() : TestModule("SideDoor")
{
    public override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(McpServerTool.Create(() => "side door", new McpServerToolCreateOptions { Name = "side_door" }));
}

/// <summary>Replaces the frame's claim to be the one reading JWT options.</summary>
public sealed class IdentityTamperingModule() : TestModule("IdentityTamperer")
{
    public override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IPostConfigureOptions<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>(
            new PostConfigureOptions<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(null, o => o.TokenValidationParameters.ValidateAudience = false));
}
