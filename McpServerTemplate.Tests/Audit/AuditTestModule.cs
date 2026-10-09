using System.ComponentModel;
using System.Diagnostics;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Tests.Frame;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Tests.Audit;

/// <summary>Tools that exist only for the audit tests: each shows one thing the trail must get right.</summary>
[McpServerToolType]
public static class AuditTools
{
    private static int _changes;

    /// <summary>How many times <see cref="AuditChange"/> has run. Tests that read it run in one sequential collection.</summary>
    public static int Changes => Volatile.Read(ref _changes);

    [McpServerTool, Description("A write: changes something, and counts that it did.")]
    public static string AuditChange([Description("What to set.")] string value)
    {
        Interlocked.Increment(ref _changes);
        return $"changed to {value}";
    }

    [McpServerTool, Description("Takes a number.")]
    public static string AuditMeasure([Description("A measurement.")] double value) => $"measured {value}";

    [McpServerTool, Description("Takes a password its policy marks sensitive, and a note it does not.")]
    public static string AuditLogin([Description("The password.")] string password, [Description("A note.")] string note) =>
        $"signed in ({password.Length} characters), note: {note.Length} characters";

    [McpServerTool, Description("Says which trace the tool runs under.")]
    public static string AuditTrace() => Activity.Current?.TraceId.ToHexString() ?? "none";
}

/// <summary>A prompt, whose argument the test server completes.</summary>
[McpServerPromptType]
public static class AuditPrompts
{
    public const string Name = "audit_brief";

    [McpServerPrompt(Name = Name), Description("A brief on a topic.")]
    public static string Brief([Description("The topic.")] string topic) => $"Write a brief on {topic}.";

    /// <summary>The server-wide completion handler a test installs beside the composition, as the end-to-end test host does.</summary>
    public static ValueTask<CompleteResult> CompleteAsync(RequestContext<CompleteRequestParams> context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CompleteResult { Completion = new Completion { Values = ["audit", "authorisation"] } });
}

/// <summary>A resource at a fixed address.</summary>
[McpServerResourceType]
public static class AuditResources
{
    public const string Uri = "audit://note";

    [McpServerResource(UriTemplate = Uri, Name = "Audit note", MimeType = "text/plain"), Description("A note.")]
    public static string Note() => "a note";
}

/// <summary>The audit tests' provider: the four tools above, one prompt, one resource, all under test:act.</summary>
public sealed class AuditModule : IProviderModule
{
    public string Name => "AuditTest";

    public ProviderPolicy Policy { get; } = new(
        "AuditTest",
        TestModule.DefaultEgress,
        new Dictionary<string, ToolPolicy>
        {
            ["audit_change"] = new("test:act", RiskClass.Write, PerPrincipalPerMinute: 1000),
            ["audit_measure"] = new("test:act", RiskClass.Read, PerPrincipalPerMinute: 1000),
            ["audit_login"] = new("test:act", RiskClass.Read, PerPrincipalPerMinute: 1000, MaxStringArgLength: 20_000, SensitiveArgs: ["password"]),
            ["audit_trace"] = new("test:act", RiskClass.Read, PerPrincipalPerMinute: 1000),
        },
        new Dictionary<string, ResourcePolicy> { [AuditResources.Uri] = new("test:act") },
        new Dictionary<string, PromptPolicy> { [AuditPrompts.Name] = new("test:act") });

    public IReadOnlyList<Type> ToolTypes { get; } = [typeof(AuditTools)];

    public IReadOnlyList<Type> ResourceTypes { get; } = [typeof(AuditResources)];

    public IReadOnlyList<Type> PromptTypes { get; } = [typeof(AuditPrompts)];

    public IReadOnlyCollection<string> Settings { get; } = [];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}
