using System.Text.RegularExpressions;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;

namespace McpServerTemplate.Tests;

/// <summary>
/// contract-004 — what the documents and code comments say is what the server does, wherever that
/// can be checked. An operator who follows a document that names a refused setting or a removed
/// mechanism believes they are protected by something that is not there.
///
/// The roadmap (06), the reports and the contracts are history and plans, and are not checked
/// here; the interactive field guide presents the plan.
/// </summary>
public partial class DocumentationTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "McpServerTemplate.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The documents a reader follows to run, configure and extend the server.</summary>
    public static IEnumerable<string> CurrentDocuments()
    {
        var root = RepositoryRoot();
        yield return Path.Combine(root, "README.md");
        yield return Path.Combine(root, "docs", "README.md");
        foreach (var file in Directory.GetFiles(Path.Combine(root, "docs"), "0*.md").Order(StringComparer.Ordinal))
        {
            if (!Path.GetFileName(file).StartsWith("06-", StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    private static readonly string[] SourceProjects = ["McpServerTemplate", "McpServerTemplate.Tests"];

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRoot();
        var separator = Path.DirectorySeparatorChar;
        return SourceProjects
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal) && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal));
    }

    // Text outside <!-- retired --> … <!-- /retired --> blocks: a retired name may appear only
    // where the document says it is retired.
    private static string OutsideRetiredBlocks(string text) => RetiredBlock().Replace(text, string.Empty);

    // ── T-1 (G-2): no document names a governed setting the server would refuse ──

    [Fact]
    public void T1_every_governed_setting_a_document_names_is_one_the_server_reads()
    {
        var modules = BuiltInProviders.Create();
        var providerNames = modules.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var file in CurrentDocuments())
        {
            foreach (Match match in GovernedKey().Matches(OutsideRetiredBlocks(File.ReadAllText(file))))
            {
                var key = match.Value.Replace("__", ":", StringComparison.Ordinal).TrimEnd(':', '.');
                var parts = key.Split(':');

                // A placeholder provider (YourApi, {Name}) may declare any setting of its own; only
                // the keys the frame owns are checked for it.
                if (parts.Length >= 3 && parts[0] == "Providers" && parts[1] != "Enabled" && !providerNames.Contains(parts[1]))
                {
                    if (parts[2] is not ("IdentityProvider" or "BaseUrl" or "UserAgent") && !parts[2].StartsWith('{'))
                    {
                        continue;
                    }

                    parts[1] = "Smhi";
                }

                // Placeholders become a concrete example the matcher can judge.
                var concrete = string.Join(":", parts.Select(p => p switch
                {
                    "{n}" or "N" => "0",
                    "{name}" or "{idp}" or "{Name}" => "corp",
                    _ => p,
                }));

                var section = parts.Length == 1 ||
                    (parts.Length == 2 && parts[0] == "Providers" && parts[1] != "Enabled") ||
                    concrete.EndsWith(":*", StringComparison.Ordinal) || concrete.EndsWith(":{Name}", StringComparison.Ordinal) ||
                    (!SettingsAllowlist.IsKnown(concrete, modules) && SettingsAllowlist.IsKnownSection(concrete, modules));
                if (section)
                {
                    continue; // a section named as a whole, not a key
                }

                if (!SettingsAllowlist.IsKnown(concrete, modules) && !SettingsAllowlist.IsKnown(concrete + ":0", modules))
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}: '{match.Value}'" +
                        (SettingsAllowlist.IsRetired(concrete, out _) ? " is retired" : " is not a setting the server reads"));
                }
            }
        }

        Assert.True(problems.Count == 0, "documents name settings the server would refuse:\n  " + string.Join("\n  ", problems.Distinct()));
    }

    // ── T-2 (G-3): no document or comment describes a removed mechanism ──

    private static readonly string[] Retired =
    [
        "Authentication:ApiKey", "Authentication__ApiKey", "X-Api-Key", "ApiKeyMiddleware",
        "ToolCallThrottleFilter", "ThrottleFilter", "MaxCallsPerToolPerMinute",
        "WithToolsFromAssembly", "WithResourcesFromAssembly", "WithPromptsFromAssembly",
        "HealthProbe", "McpProvider", "McpScope", "ProviderBinding", "TrustDomainFilters",
        ".NET 8", "net8.0",
    ];

    [Fact]
    public void T2_no_document_describes_a_removed_mechanism_outside_a_retired_block()
    {
        var problems = new List<string>();
        foreach (var file in CurrentDocuments())
        {
            var lines = OutsideRetiredBlocks(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var term in Retired.Where(t => lines[i].Contains(t, StringComparison.Ordinal)))
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}: '{term}' in \"{lines[i].Trim()}\"");
                }
            }
        }

        Assert.True(problems.Count == 0, "documents describe removed mechanisms:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void T2_no_code_comment_describes_a_removed_mechanism()
    {
        var problems = new List<string>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var at = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                var comment = lines[i][at..];
                foreach (var term in Retired.Where(t => comment.Contains(t, StringComparison.Ordinal)))
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}:{i + 1}: '{term}'");
                }
            }
        }

        Assert.True(problems.Count == 0, "comments describe removed mechanisms:\n  " + string.Join("\n  ", problems));
    }

    // ── T-3 (G-4): every relative link and anchor resolves ──

    [Fact]
    public void T3_every_relative_link_and_anchor_in_the_documents_resolves()
    {
        var problems = new List<string>();
        foreach (var file in CurrentDocuments())
        {
            foreach (Match match in MarkdownLink().Matches(File.ReadAllText(file)))
            {
                var target = match.Groups["target"].Value;
                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    continue;
                }

                var hash = target.IndexOf('#', StringComparison.Ordinal);
                var path = hash < 0 ? target : target[..hash];
                var anchor = hash < 0 ? null : target[(hash + 1)..];
                var resolved = path.Length == 0 ? file : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(path)));

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}: '{target}' — no such file");
                    continue;
                }

                if (anchor is not null && resolved.EndsWith(".md", StringComparison.Ordinal) &&
                    !Headings(File.ReadAllText(resolved)).Contains(anchor))
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}: '{target}' — no such heading");
                }
            }
        }

        Assert.True(problems.Count == 0, "links that resolve nowhere:\n  " + string.Join("\n  ", problems));
    }

    // GitHub's heading anchors: lower case, punctuation dropped, spaces to hyphens.
    private static HashSet<string> Headings(string markdown) =>
        HeadingLine().Matches(markdown)
            .Select(m => Slug(m.Groups["text"].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string Slug(string heading) =>
        NotSlug().Replace(heading.Trim().ToLowerInvariant().Replace("`", string.Empty, StringComparison.Ordinal), string.Empty).Replace(' ', '-');

    // ── T-4 (G-6): the stale comments the survey named are gone ──

    [Theory]
    [InlineData("called from Program.cs")]
    [InlineData("Called from Program.cs")]
    [InlineData("becomes inert")]
    [InlineData("a missing API key")]
    [InlineData("Providers__YourProvider__ApiKey")]
    public void T4_a_stale_comment_the_survey_named_is_gone(string phrase)
    {
        var offenders = SourceFiles()
            .Where(f => Path.GetFileName(f) != $"{nameof(DocumentationTests)}.cs")
            .Where(f => File.ReadAllText(f).Contains(phrase, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.True(offenders.Length == 0, $"'{phrase}' is still in: {string.Join(", ", offenders)}");
    }

    [GeneratedRegex(@"<!--\s*retired\s*-->.*?<!--\s*/retired\s*-->", RegexOptions.Singleline)]
    private static partial Regex RetiredBlock();

    [GeneratedRegex(@"\b(Authentication|Providers|Limits|Confirmation|Development|RateLimit)(:|__)[A-Za-z0-9_{}*]+((:|__)[A-Za-z0-9_{}*]+)*")]
    private static partial Regex GovernedKey();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^#{1,6}\s+(?<text>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"[^\p{L}\p{N}\s_-]")]
    private static partial Regex NotSlug();
}
