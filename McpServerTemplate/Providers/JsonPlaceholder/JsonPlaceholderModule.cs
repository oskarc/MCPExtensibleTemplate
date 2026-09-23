using McpServerTemplate.Infrastructure.Frame;

namespace McpServerTemplate.Providers.JsonPlaceholder;

/// <summary>
/// The demonstration provider (contract-003 · G-1, G-10). It is the only provider with tools that
/// change something, so it carries the write gate's tests; it is left out of
/// <c>Providers:Enabled</c> in Production.
/// </summary>
public sealed class JsonPlaceholderModule : IProviderModule
{
    private const string Read = "demo:read";
    private const string Write = "demo:write";

    /// <inheritdoc />
    public string Name => "JsonPlaceholder";

    /// <inheritdoc />
    public ProviderPolicy Policy { get; } = new(
        "JsonPlaceholder",
        new EgressPolicy(
            Hosts: ["jsonplaceholder.typicode.com"],
            Methods: [HttpMethod.Get, HttpMethod.Post],
            MaxResponseBytes: 1024 * 1024,
            AttemptTimeout: TimeSpan.FromSeconds(5),
            MaxRetryAttempts: 2,
            TotalTimeout: TimeSpan.FromSeconds(20),
            MaxConcurrentUpstreamCalls: 5,
            DailyCallBudget: 5_000),
        Tools: new Dictionary<string, ToolPolicy>
        {
            ["get_blog_post"] = new(Read, RiskClass.Read, PerPrincipalPerMinute: 30, Idempotent: true),
            ["get_post_comments"] = new(Read, RiskClass.Read, PerPrincipalPerMinute: 30, Idempotent: true),
            ["get_user_todos"] = new(Read, RiskClass.Read, PerPrincipalPerMinute: 30, Idempotent: true),
            ["create_blog_post"] = new(Write, RiskClass.Write, PerPrincipalPerMinute: 10),
            ["add_post_comment"] = new(Write, RiskClass.Write, PerPrincipalPerMinute: 10),
            ["create_user_todo"] = new(Write, RiskClass.Write, PerPrincipalPerMinute: 10),
        },
        Resources: new Dictionary<string, ResourcePolicy>(),
        Prompts: new Dictionary<string, PromptPolicy>());

    /// <inheritdoc />
    public IReadOnlyList<Type> ToolTypes { get; } = [typeof(JsonPlaceholderTools)];

    /// <inheritdoc />
    public IReadOnlyList<Type> ResourceTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<Type> PromptTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Settings { get; } = ["UserAgent"];

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddJsonPlaceholderProvider(configuration, Policy.Egress);
}
