using System.Text.Json;
using System.Text.RegularExpressions;

namespace McpServerTemplate.Tests;

/// <summary>
/// contract-001 · T-9 (G-8) and T-11 (G-10) — guarantees about the repository itself.
///
/// The secret scan also runs in CI over the whole tree; this is the part that can be asserted
/// in-process, so it holds on a developer's machine before a push rather than after one.
/// </summary>
public class RepositoryInvariantsTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpServerTemplate.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root");
        return dir!.FullName;
    }

    // ── G-8: one solution, one framework, one SDK version ─────────────────────

    [Fact]
    public void T9_both_projects_target_net10()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", props, StringComparison.Ordinal);

        // A project that sets its own framework escapes the shared one.
        foreach (var project in Directory.GetFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(project);
            Assert.DoesNotContain("<TargetFramework>", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<TargetFrameworks>", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void T9_warnings_are_errors_and_analysis_is_not_switched_off()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));

        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", props, StringComparison.Ordinal);
        Assert.Contains("<AnalysisLevel>latest-recommended</AnalysisLevel>", props, StringComparison.Ordinal);

        // The two ways to take the teeth out of the above without touching them.
        foreach (var project in Directory.GetFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(project);
            Assert.DoesNotContain("<NoWarn>", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<TreatWarningsAsErrors>false", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void T9_the_mcp_sdk_is_on_one_version_across_the_solution()
    {
        var versions = new SortedSet<string>(StringComparer.Ordinal);
        var reference = new Regex(
            @"<PackageReference\s+Include=""(ModelContextProtocol[.\w]*)""\s+Version=""([^""]+)""");

        foreach (var project in Directory.GetFiles(RepositoryRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match match in reference.Matches(File.ReadAllText(project)))
                versions.Add(match.Groups[2].Value);
        }

        Assert.NotEmpty(versions);
        Assert.Single(versions);
        Assert.Equal("2.2.0", versions.First());
    }

    // ── G-10: no credential in the repository ─────────────────────────────────

    [Fact]
    public void T11_no_appsettings_file_carries_a_credential_value()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(RepositoryRoot(), "appsettings*.json", SearchOption.AllDirectories))
        {
            // Build output is a copy of the same files; checking it twice adds nothing, and
            // a stale copy from an earlier build would report a finding already fixed.
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Walk(document.RootElement, Path.GetFileName(file), offenders);
        }

        Assert.True(
            offenders.Count == 0,
            "credentials committed to the repository: " + string.Join(", ", offenders));
    }

    private static void Walk(JsonElement element, string path, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    var isCredential =
                        name.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("Secret", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("Password", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("Token", StringComparison.OrdinalIgnoreCase);

                    if (isCredential &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(property.Value.GetString()))
                    {
                        offenders.Add($"{path}:{name}");
                    }

                    Walk(property.Value, $"{path}/{name}", offenders);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, path, offenders);
                break;

            default:
                break;
        }
    }

    [Fact]
    public void T11_development_secrets_have_somewhere_to_live()
    {
        // Without a UserSecretsId, "use user-secrets" is advice a developer cannot follow,
        // and the key goes back into appsettings.
        var project = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "McpServerTemplate", "McpServerTemplate.csproj"));

        Assert.Contains("<UserSecretsId>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void T16_providers_carry_no_second_declaration_of_their_provider_or_scope()
    {
        // contract-003 · G-1 — a provider's policy is the one place its scopes are declared. The
        // attributes that used to say the same thing on the classes are gone; a second declaration
        // is one that can disagree with the first.
        var markers = new[] { "[McpProvider(", "[McpScope(" };
        var offenders = Directory.GetFiles(Path.Combine(RepositoryRoot(), "McpServerTemplate", "Providers"), "*.cs", SearchOption.AllDirectories)
            .Where(f => markers.Any(m => File.ReadAllText(f).Contains(m, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offenders);
    }
}
