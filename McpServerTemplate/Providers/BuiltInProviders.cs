using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers.JsonPlaceholder;
using McpServerTemplate.Providers.Smhi;
using McpServerTemplate.Providers.SmhiObs;

namespace McpServerTemplate.Providers;

/// <summary>
/// The providers this server ships with. Which of them a deployment serves is
/// <c>Providers:Enabled</c>; to add a provider, write an <see cref="IProviderModule"/> and list it here.
/// </summary>
public static class BuiltInProviders
{
    /// <summary>A fresh set of the built-in modules.</summary>
    public static IReadOnlyList<IProviderModule> Create() =>
        [new SmhiModule(), new SmhiObsModule(), new JsonPlaceholderModule()];
}
