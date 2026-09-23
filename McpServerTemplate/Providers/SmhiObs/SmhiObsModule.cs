using McpServerTemplate.Infrastructure.Frame;

namespace McpServerTemplate.Providers.SmhiObs;

/// <summary>
/// SMHI's measured weather observations, as a governed provider (contract-003 · G-1): four reading
/// tools under <c>observations:read</c>, calling one host.
/// </summary>
public sealed class SmhiObsModule : IProviderModule
{
    private const string Scope = "observations:read";

    /// <inheritdoc />
    public string Name => "SmhiObs";

    /// <inheritdoc />
    public ProviderPolicy Policy { get; } = new(
        "SmhiObs",
        new EgressPolicy(
            Hosts: ["opendata-download-metobs.smhi.se"],
            Methods: [HttpMethod.Get],
            MaxResponseBytes: 8 * 1024 * 1024,
            // Historical observation series are larger and slower than a forecast.
            AttemptTimeout: TimeSpan.FromSeconds(20),
            MaxRetryAttempts: 2,
            TotalTimeout: TimeSpan.FromSeconds(70),
            MaxConcurrentUpstreamCalls: 10,
            DailyCallBudget: 10_000),
        Tools: new Dictionary<string, ToolPolicy>
        {
            ["get_recent_temperature"] = new(Scope, RiskClass.Read, PerPrincipalPerMinute: 20, Idempotent: true),
            ["get_temperature_history"] = new(Scope, RiskClass.Read, PerPrincipalPerMinute: 20, Idempotent: true),
            ["get_precipitation_history"] = new(Scope, RiskClass.Read, PerPrincipalPerMinute: 20, Idempotent: true),
            ["get_monthly_climate"] = new(Scope, RiskClass.Read, PerPrincipalPerMinute: 10, Idempotent: true),
        },
        Resources: new Dictionary<string, ResourcePolicy>(),
        Prompts: new Dictionary<string, PromptPolicy>());

    /// <inheritdoc />
    public IReadOnlyList<Type> ToolTypes { get; } = [typeof(SmhiObsTools)];

    /// <inheritdoc />
    public IReadOnlyList<Type> ResourceTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<Type> PromptTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Settings { get; } = ["UserAgent"];

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddSmhiObsProvider(configuration, Policy.Egress);
}
