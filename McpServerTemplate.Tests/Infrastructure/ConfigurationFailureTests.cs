using McpServerTemplate.Infrastructure;
using McpServerTemplate.Providers.JsonPlaceholder;
using McpServerTemplate.Providers.Smhi;
using McpServerTemplate.Providers.SmhiObs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-001 · T-3 (G-2) — a configuration failure exits 78, not 70.
///
/// T-3 names a provider BaseUrl over http as its configuration-error case, and that case took
/// the wrong exit: the registrations raised InvalidOperationException, which Program.cs maps to
/// 70 alongside genuine crashes. An operator reading 70 goes looking for a bug; 78 tells them to
/// fix the deployment. The distinction only exists if every refusal to start raises the type
/// that carries it.
/// </summary>
public class ConfigurationFailureTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    public static TheoryData<string, Action<IServiceCollection, IConfiguration>> EveryProvider() => new()
    {
        { "Smhi", (s, c) => s.AddSmhiProvider(c, new SmhiModule().Policy.Egress) },
        { "SmhiObs", (s, c) => s.AddSmhiObsProvider(c, new SmhiObsModule().Policy.Egress) },
        { "JsonPlaceholder", (s, c) => s.AddJsonPlaceholderProvider(c, new JsonPlaceholderModule().Policy.Egress) },
    };

    [Theory]
    [MemberData(nameof(EveryProvider))]
    public void T3_a_plaintext_base_url_is_a_configuration_failure(
        string provider, Action<IServiceCollection, IConfiguration> register)
    {
        var configuration = Config(
            ($"Providers:{provider}:BaseUrl", "http://example.com"),
            ($"Providers:{provider}:UserAgent", "test/1.0"));

        var ex = Assert.Throws<ConfigurationException>(() => register(new ServiceCollection(), configuration));

        Assert.Contains("HTTPS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EveryProvider))]
    public void T3_a_missing_provider_section_is_a_configuration_failure(
        string provider, Action<IServiceCollection, IConfiguration> register)
    {
        var ex = Assert.Throws<ConfigurationException>(
            () => register(new ServiceCollection(), Config(("Unrelated:Key", "value"))));

        Assert.Contains(provider, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryProvider))]
    public void T3_a_well_formed_provider_registers(
        string provider, Action<IServiceCollection, IConfiguration> register)
    {
        var configuration = Config(
            ($"Providers:{provider}:BaseUrl", "https://example.com"),
            ($"Providers:{provider}:UserAgent", "test/1.0"));

        var services = new ServiceCollection();
        services.AddLogging();

        register(services, configuration);

        Assert.NotEmpty(services);
    }
}
