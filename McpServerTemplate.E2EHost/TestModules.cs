using System.ComponentModel;
using McpServerTemplate.Infrastructure.Frame;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.E2EHost;

/// <summary>
/// contract-005 · G-10 — the provider modules only the test host serves, beside the built-in ones. Each is an
/// <see cref="IProviderModule"/> like any provider's: the frame registers its primitives from its types, holds it
/// to its policy, and watches what it registers. Nothing about them reaches the product.
/// </summary>
public static class TestModules
{
    /// <summary>The host every test module declares: the upstream fake's neutral alias, which answers only in the test network.</summary>
    public const string WitnessHost = "wiremock.e2e.test";

    /// <summary>A fresh set of the test modules.</summary>
    public static IReadOnlyList<IProviderModule> Create() => [new IrreversibleModule(), new CompletingPromptModule()];
}

/// <summary>
/// contract-005 · G-10 — an irreversible tool, for T-8's confirmation round-trip on a real image. Its only effect is
/// to report each run, once, to a witness outside this process (<see cref="Witness"/>), so a test can count runs
/// across the process boundary where a counter in the process could not be read.
/// </summary>
public sealed class IrreversibleModule : IProviderModule
{
    /// <inheritdoc />
    public string Name => "E2EIrreversible";

    /// <inheritdoc />
    public ProviderPolicy Policy { get; } = new(
        "E2EIrreversible",
        new EgressPolicy(
            Hosts: [TestModules.WitnessHost],
            Methods: [HttpMethod.Post],
            MaxResponseBytes: 64 * 1024,
            AttemptTimeout: TimeSpan.FromSeconds(5),
            // One attempt: a retried report would be the same run reported twice, which the witness tells apart only
            // by its marker.
            MaxRetryAttempts: 0,
            TotalTimeout: TimeSpan.FromSeconds(10),
            MaxConcurrentUpstreamCalls: 5,
            DailyCallBudget: 1_000),
        Tools: new Dictionary<string, ToolPolicy>
        {
            [IrreversibleTools.Name] = new("demo:write", RiskClass.Irreversible, PerPrincipalPerMinute: 30),
        },
        Resources: new Dictionary<string, ResourcePolicy>(),
        Prompts: new Dictionary<string, PromptPolicy>());

    /// <inheritdoc />
    public IReadOnlyList<Type> ToolTypes { get; } = [typeof(IrreversibleTools)];

    /// <inheritdoc />
    public IReadOnlyList<Type> ResourceTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<Type> PromptTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Settings { get; } = [];

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddHttpClient<Witness>(client =>
        {
            client.BaseAddress = new Uri($"https://{TestModules.WitnessHost}");
            client.Timeout = Policy.Egress.TotalTimeout;
        });
}

/// <summary>The irreversible tool itself.</summary>
[McpServerToolType]
public static class IrreversibleTools
{
    /// <summary>The tool's wire name.</summary>
    public const string Name = "e2e_irreversible_act";

    /// <summary>Runs once: reports the run to the witness, under a marker no other run has, and says so.</summary>
    [McpServerTool(Name = Name), Description(
        "Test only: an action that cannot be undone. Every run reports itself, once, to a witness outside this server.")]
    public static async Task<string> Act(
        Witness witness,
        [Description("What the action is taken on.")] string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);

        var run = Guid.NewGuid().ToString("N");
        await witness.ReportRunAsync(target, run, cancellationToken).ConfigureAwait(false);
        return $"acted on {target} (run {run})";
    }
}

/// <summary>
/// contract-005 · G-10 — the witness of the irreversible tool's runs: one POST per run to
/// https://wiremock.e2e.test/e2e-irreversible/{target}/{run}, whose journal the test reads. The run's marker is new
/// for every run, so the count of distinct markers is the count of runs, whatever the transport did.
/// </summary>
public sealed class Witness(HttpClient http)
{
    /// <summary>The path every report is sent under.</summary>
    public const string Path = "/e2e-irreversible";

    /// <summary>Reports one run. Any answer means the witness recorded it: WireMock answers 404 to a path it has no stub for.</summary>
    public async Task ReportRunAsync(string target, string run, CancellationToken cancellationToken)
    {
        using var report = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Path}/{Uri.EscapeDataString(target)}/{run}", UriKind.Relative));
        using var answer = await http.SendAsync(report, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// contract-005 · G-10 — a prompt provider that offers completions, for T-8's completion leg. contract-003's T-3
/// asks that an unscoped completion be refused by the frame's own rule; on the shipped image no provider offers
/// completion, so the SDK answers before the frame's check can run. This one does.
///
/// The SDK offers completion through one server-wide handler, which a provider module cannot install: the frame
/// refuses a module that registers anything of the MCP SDK's (FrameIntegrity). So the module declares the prompt and
/// its policy, and the test host installs <see cref="CompletingPrompts.CompleteAsync"/> beside the composition
/// (Program.cs). The frame's completion filter runs in front of it, as it runs in front of every handler.
/// </summary>
public sealed class CompletingPromptModule : IProviderModule
{
    /// <inheritdoc />
    public string Name => "E2ECompletions";

    /// <inheritdoc />
    public ProviderPolicy Policy { get; } = new(
        "E2ECompletions",
        // It calls nothing. The frame asks every provider to declare where and how it may call, with every limit at
        // least 1, so it declares the least there is: the one host the test modules share, one method, one call a day.
        new EgressPolicy(
            Hosts: [TestModules.WitnessHost],
            Methods: [HttpMethod.Get],
            MaxResponseBytes: 1024,
            AttemptTimeout: TimeSpan.FromSeconds(1),
            MaxRetryAttempts: 0,
            TotalTimeout: TimeSpan.FromSeconds(2),
            MaxConcurrentUpstreamCalls: 1,
            DailyCallBudget: 1),
        Tools: new Dictionary<string, ToolPolicy>(),
        Resources: new Dictionary<string, ResourcePolicy>(),
        Prompts: new Dictionary<string, PromptPolicy>
        {
            [CompletingPrompts.Name] = new("demo:read"),
        });

    /// <inheritdoc />
    public IReadOnlyList<Type> ToolTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<Type> ResourceTypes { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<Type> PromptTypes { get; } = [typeof(CompletingPrompts)];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Settings { get; } = [];

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}

/// <summary>The prompt whose argument the server completes, and the completion itself.</summary>
[McpServerPromptType]
public static class CompletingPrompts
{
    /// <summary>The prompt's name.</summary>
    public const string Name = "e2e_city_briefing";

    /// <summary>The argument the server completes.</summary>
    public const string Argument = "city";

    /// <summary>What the argument is completed from.</summary>
    public static readonly IReadOnlyList<string> Cities = ["Gothenburg", "Malmö", "Stockholm", "Umeå", "Uppsala"];

    /// <summary>A briefing request for one city.</summary>
    [McpServerPrompt(Name = Name), Description("Test only: a briefing for a city, whose argument the server completes.")]
    public static string CityBriefing([Description("A Swedish city, e.g. Umeå.")] string city) =>
        $"Give a short briefing for {city}.";

    /// <summary>
    /// Completes this prompt's city argument from <see cref="Cities"/> by prefix, and nothing else. Reached only
    /// through the frame's completion filter, which admits a caller that may use the prompt.
    /// </summary>
    public static ValueTask<CompleteResult> CompleteAsync(RequestContext<CompleteRequestParams> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IList<string> values = context.Params is { Ref: PromptReference { Name: Name }, Argument: { Name: Argument } argument }
            ? [.. Cities.Where(city => city.StartsWith(argument.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase))]
            : [];
        return ValueTask.FromResult(new CompleteResult
        {
            Completion = new Completion { Values = values, Total = values.Count, HasMore = false },
        });
    }
}
