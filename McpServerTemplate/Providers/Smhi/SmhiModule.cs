using McpServerTemplate.Infrastructure.Frame;

namespace McpServerTemplate.Providers.Smhi;

/// <summary>
/// SMHI's open weather forecasts, as a governed provider (contract-003 · G-1): three reading tools,
/// two reference resources and one prompt, all under <c>weather:read</c>, calling one host.
/// </summary>
public sealed class SmhiModule : IProviderModule
{
    private const string Scope = "weather:read";

    /// <inheritdoc />
    public string Name => "Smhi";

    /// <inheritdoc />
    public ProviderPolicy Policy { get; } = new(
        "Smhi",
        new EgressPolicy(
            Hosts: ["opendata-download-metfcst.smhi.se"],
            Methods: [HttpMethod.Get],
            MaxResponseBytes: 4 * 1024 * 1024,
            // A forecast is a small document; an attempt that has not answered in 10s is stuck.
            AttemptTimeout: TimeSpan.FromSeconds(10),
            MaxRetryAttempts: 2,
            TotalTimeout: TimeSpan.FromSeconds(35),
            MaxConcurrentUpstreamCalls: 10,
            DailyCallBudget: 10_000),
        Tools: new Dictionary<string, ToolPolicy>
        {
            ["get_forecast"] = new(Scope, RiskClass.Read, PerPrincipalPerMinute: 30, Idempotent: true),
            ["get_current_weather"] = new(Scope, RiskClass.Read, PerPrincipalPerMinute: 30, Idempotent: true),
            ["get_forecast_model_info"] = new(Scope, RiskClass.Read, PerPrincipalPerMinute: 30, Idempotent: true),
        },
        Resources: new Dictionary<string, ResourcePolicy>
        {
            ["smhi://weather-symbols"] = new(Scope),
            ["smhi://coverage-area"] = new(Scope),
        },
        Prompts: new Dictionary<string, PromptPolicy>
        {
            ["forecast_briefing"] = new(Scope),
        });

    /// <inheritdoc />
    public IReadOnlyList<Type> ToolTypes { get; } = [typeof(SmhiTools)];

    /// <inheritdoc />
    public IReadOnlyList<Type> ResourceTypes { get; } = [typeof(SmhiResources)];

    /// <inheritdoc />
    public IReadOnlyList<Type> PromptTypes { get; } = [typeof(SmhiPrompts)];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Settings { get; } = ["UserAgent"];

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddSmhiProvider(configuration, Policy.Egress);
}
