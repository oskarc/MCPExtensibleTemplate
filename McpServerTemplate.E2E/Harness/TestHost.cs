using System.Security.Cryptography;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-10 — the test host as the suite sees it: the names of the test modules only it serves, and the
/// settings it is started with on top of the environment's base settings. The suite references neither the product
/// nor the test host (G-2), so these mirror McpServerTemplate.E2EHost/TestModules.cs, and the host's own startup line
/// is where a test checks them.
/// </summary>
public static class TestHost
{
    /// <summary>The provider module holding the irreversible tool.</summary>
    public const string IrreversibleProvider = "E2EIrreversible";

    /// <summary>The provider module holding the prompt the server completes.</summary>
    public const string CompletionsProvider = "E2ECompletions";

    /// <summary>The irreversible tool, whose every run is reported to the fake.</summary>
    public const string Tool = "e2e_irreversible_act";

    /// <summary>The scope the tool requires.</summary>
    public const string ToolScope = "demo:write";

    /// <summary>The prompt whose argument the server completes.</summary>
    public const string Prompt = "e2e_city_briefing";

    /// <summary>The prompt's completed argument.</summary>
    public const string Argument = "city";

    /// <summary>The scope the prompt, and completing it, require.</summary>
    public const string PromptScope = "demo:read";

    /// <summary>Where each run of the tool is reported: /e2e-irreversible/{target}/{run} on wiremock.e2e.test.</summary>
    public const string WitnessPath = "/e2e-irreversible/";

    /// <summary>The test modules, in the order the host lists them after the built-in providers.</summary>
    public static readonly IReadOnlyList<string> Modules = [IrreversibleProvider, CompletionsProvider];

    /// <summary>
    /// The test host's settings on top of the base settings: its test modules enabled after the built-in ones and bound
    /// to idp-a, and a confirmation key of its own — an irreversible tool does not start without one. The key is made
    /// here, for this server alone, and lives only in its environment.
    /// </summary>
    public static SettingsDelta Delta() => SettingsDelta.None
        .Set("Providers:Enabled:2", IrreversibleProvider)
        .Set("Providers:Enabled:3", CompletionsProvider)
        .Set($"Providers:{IrreversibleProvider}:IdentityProvider", E2EEnvironment.IdpA)
        .Set($"Providers:{CompletionsProvider}:IdentityProvider", E2EEnvironment.IdpA)
        .Set("Confirmation:Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
}
