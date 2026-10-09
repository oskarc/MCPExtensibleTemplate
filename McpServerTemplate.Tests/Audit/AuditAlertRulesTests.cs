using System.Text.RegularExpressions;
using McpServerTemplate.Infrastructure.Audit;
using YamlDotNet.RepresentationModel;

namespace McpServerTemplate.Tests.Audit;

/// <summary>
/// contract-006 · T-14 — the alert rules shipped with the repository (docs/siem/alerts; roadmap P3.7) are read by this
/// test as a monitoring system would read them: each is YAML, each is a rule, and each event it listens for is one the
/// server can write. A rule for an event nobody can write never fires, and nobody finds out.
/// </summary>
public sealed partial class AuditAlertRulesTests
{
    /// <summary>The roadmap's four alerts, each of which must have a rule: named in a rule's x-roadmap-case.</summary>
    private static readonly string[] RoadmapCases = ["first-tool-use", "refusals-in-ten-minutes", "critical-event", "chain-gap"];

    private static string AlertsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpServerTemplate.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root from the test output directory");
        return Path.Combine(dir!.FullName, "docs", "siem", "alerts");
    }

    /// <summary>Every rule in every file: a file may hold more than one YAML document.</summary>
    private static List<(string File, YamlMappingNode Rule)> Rules()
    {
        var rules = new List<(string File, YamlMappingNode Rule)>();
        foreach (var file in Directory.GetFiles(AlertsDirectory(), "*.yml").Order(StringComparer.Ordinal))
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(File.ReadAllText(file)))
            {
                stream.Load(reader);
            }

            Assert.NotEmpty(stream.Documents);
            rules.AddRange(stream.Documents.Select(document => (Path.GetFileName(file), Assert.IsType<YamlMappingNode>(document.RootNode))));
        }

        return rules;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    /// <summary>Every scalar under <paramref name="node"/>, with the key it sits under.</summary>
    private static IEnumerable<(string Key, string Value)> Scalars(YamlNode node, string key = "")
    {
        switch (node)
        {
            case YamlScalarNode scalar when scalar.Value is not null:
                yield return (key, scalar.Value);
                break;
            case YamlSequenceNode sequence:
                foreach (var found in sequence.Children.SelectMany(child => Scalars(child, key)))
                {
                    yield return found;
                }

                break;
            case YamlMappingNode mapping:
                foreach (var found in mapping.Children.SelectMany(child => Scalars(child.Value, ((YamlScalarNode)child.Key).Value ?? string.Empty)))
                {
                    yield return found;
                }

                break;
        }
    }

    [Fact]
    public void T14_every_alert_file_is_a_rule_a_monitoring_system_can_load()
    {
        var rules = Rules();
        Assert.True(rules.Count >= 5, $"found {rules.Count} rules under {AlertsDirectory()}");

        Assert.All(rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(Scalar(rule.Rule, "title")), $"{rule.File}: a rule has no title");
            Assert.True(Guid.TryParse(Scalar(rule.Rule, "id"), out _), $"{rule.File}: a rule's id is not a UUID");
            Assert.False(string.IsNullOrWhiteSpace(Scalar(rule.Rule, "level")), $"{rule.File}: a rule has no level");

            // A rule either detects on records or correlates other rules.
            var detects = rule.Rule.Children.ContainsKey(new YamlScalarNode("detection")) && rule.Rule.Children.ContainsKey(new YamlScalarNode("logsource"));
            var correlates = rule.Rule.Children.ContainsKey(new YamlScalarNode("correlation"));
            Assert.True(detects ^ correlates, $"{rule.File}: '{Scalar(rule.Rule, "title")}' is neither a detection with a logsource nor a correlation");
        });

        // No two rules share an id, and every rule a correlation names is here.
        var ids = rules.Select(rule => Scalar(rule.Rule, "id")!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var (file, rule) in rules.Where(r => r.Rule.Children.ContainsKey(new YamlScalarNode("correlation"))))
        {
            var named = Scalars(rule.Children[new YamlScalarNode("correlation")]).Where(s => s.Key == "rules").Select(s => s.Value).ToArray();
            Assert.NotEmpty(named);
            Assert.All(named, id => Assert.True(ids.Contains(id, StringComparer.Ordinal), $"{file}: the correlation names rule {id}, which is not among the alerts"));
        }
    }

    [Fact]
    public void T14_every_event_a_rule_listens_for_is_one_the_server_can_write()
    {
        var listenedFor = new List<(string File, string Event)>();
        foreach (var (file, rule) in Rules().Where(r => r.Rule.Children.ContainsKey(new YamlScalarNode("detection"))))
        {
            foreach (var (key, value) in Scalars(rule.Children[new YamlScalarNode("detection")]).Where(s => s.Key.StartsWith("event", StringComparison.Ordinal)))
            {
                // The one shape a rule may match an event by: its name, whole, as the text before the first colon.
                var match = EventPattern().Match(value);
                Assert.True(match.Success, $"{file}: '{key}: {value}' does not match an event by its whole name, as ^name(:|$)");
                listenedFor.Add((file, match.Groups["name"].Value));
            }
        }

        Assert.NotEmpty(listenedFor);
        Assert.All(listenedFor, each => Assert.True(
            AuditEvents.IsKnown(each.Event),
            $"{each.File} listens for '{each.Event}', which is not an event the server can write (AuditEvents)."));

        // And the shape matches what the writer writes: the name, then nothing or a colon.
        Assert.Matches("^authz_fail(:|$)", "authz_fail:sub=alice,resource=get_forecast");
        Assert.Matches("^authz_fail(:|$)", "authz_fail");
        Assert.DoesNotMatch("^mcp_tool_call(:|$)", "mcp_tool_called:sub=alice");
    }

    [Fact]
    public void T14_each_of_the_roadmaps_four_alerts_has_a_rule()
    {
        var cases = Rules().Select(rule => Scalar(rule.Rule, "x-roadmap-case")).Where(c => c is not null).ToArray();

        Assert.Equal(RoadmapCases.Order(StringComparer.Ordinal), cases.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"^\^(?<name>[a-z_]+)\(:\|\$\)$")]
    private static partial Regex EventPattern();
}
