using System.Text.RegularExpressions;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
using YamlDotNet.RepresentationModel;

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

    // contract-005, following contract-004 · G-3 — the same mechanisms in the plain words a sentence uses, in any case. The
    // list above holds their code names alone, so a comment saying a probe carries no such credential passed it, as the
    // documentation contract's verifier found.
    private static readonly string[] RetiredInPlainWords = ["API key", "API-key", "assembly scanning"];

    /// <summary>
    /// The retired terms that begin on <paramref name="line"/>, read together with the line after it, so that a term broken
    /// across two lines is found, and found once.
    /// </summary>
    private static IEnumerable<string> RetiredTermsStartingOn(string line, string? next)
    {
        var text = next is null ? line : line + " " + next;
        foreach (var term in Retired)
        {
            var at = text.IndexOf(term, StringComparison.Ordinal);
            if (at >= 0 && at < line.Length)
            {
                yield return term;
            }
        }

        foreach (var term in RetiredInPlainWords)
        {
            var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at < line.Length)
            {
                yield return term;
            }
        }
    }

    /// <summary>A line's comment, without the slashes that open it; null when the line has none.</summary>
    private static string? CommentOn(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? null : line[at..].TrimStart('/').Trim();
    }

    [Fact]
    public void T2_no_document_describes_a_removed_mechanism_outside_a_retired_block()
    {
        var problems = new List<string>();
        foreach (var file in CurrentDocuments())
        {
            var lines = OutsideRetiredBlocks(File.ReadAllText(file)).Split('\n').Select(l => l.Trim()).ToArray();
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var term in RetiredTermsStartingOn(lines[i], i + 1 < lines.Length ? lines[i + 1] : null))
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}: '{term}' in \"{lines[i]}\"");
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
                if (CommentOn(lines[i]) is not { } comment)
                {
                    continue;
                }

                foreach (var term in RetiredTermsStartingOn(comment, i + 1 < lines.Length ? CommentOn(lines[i + 1]) : null))
                {
                    problems.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}:{i + 1}: '{term}'");
                }
            }
        }

        Assert.True(problems.Count == 0, "comments describe removed mechanisms:\n  " + string.Join("\n  ", problems));
    }

    // contract-005 — two rules a reader must be told, each said where they look for it.
    [Theory]
    [InlineData("README.md", "A provider registers its own services and nothing of the server's")]
    [InlineData("docs/01-ARCHITECTURE.md", "What a provider may register")]
    [InlineData("docs/04-CONFIGURATION.md", "In Development only a loopback address is accepted")]
    public void The_documents_say_what_a_provider_may_register_and_where_development_listens(string document, string says)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), document.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains(says, text, StringComparison.Ordinal);
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

    // ── contract-005 · G-17 round 2 (DOC-R2-01, DOC-R2-03): the documented deployment's front, services and start line ──
    //
    // docs/04's compose file and nginx.conf are "the production deployment, whole", which the docs profile (T-9) runs
    // verbatim. These read the same blocks the same way, without containers: the first yaml block after the Docker Compose
    // line, and the first fenced block after the line below it that opens with **nginx.conf**.

    /// <summary>docs/04, with its line ends as \n.</summary>
    private static string ConfigurationGuide() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "04-CONFIGURATION.md")).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The first fenced block at or after <paramref name="from"/>, whose fence opens with <paramref name="fence"/>.</summary>
    private static string FencedBlock(string document, int from, string fence, string what)
    {
        Assert.True(from >= 0, $"docs/04 has no {what}.");
        var open = document.IndexOf(fence, from, StringComparison.Ordinal);
        var body = open < 0 ? -1 : document.IndexOf('\n', open) + 1;
        var end = body <= 0 ? -1 : document.IndexOf("```", body, StringComparison.Ordinal);
        Assert.True(end > 0, $"docs/04 gives no fenced block for {what}.");
        return document[body..end];
    }

    private static string DocumentedComposeFile(string document) =>
        FencedBlock(document, document.IndexOf("**Docker Compose**:", StringComparison.Ordinal), "```yaml", "the compose file");

    private static string DocumentedNginxConf(string document) =>
        FencedBlock(
            document,
            document.IndexOf("\n**nginx.conf**", document.IndexOf("**Docker Compose**:", StringComparison.Ordinal), StringComparison.Ordinal),
            "```",
            "nginx.conf");

    /// <summary>An nginx configuration's simple directives, comments removed, each name with its arguments, in order.</summary>
    private static List<(string Name, string Arguments)> NginxDirectives(string conf) =>
        [.. conf.Split('\n')
            .Select(line => (line.IndexOf('#', StringComparison.Ordinal) is var hash and >= 0 ? line[..hash] : line).Trim())
            .Where(line => line.EndsWith(';'))
            .Select(line => line.TrimEnd(';').Trim())
            .Select(line => line.IndexOfAny([' ', '\t']) is var space and > 0
                ? (line[..space], line[space..].Trim())
                : (line, string.Empty))];

    /// <summary>An nginx time, as seconds: 10s, 1m, or a bare number of seconds.</summary>
    private static int? NginxSeconds(string? time) => time is null ? null
        : NginxTime().Match(time) is { Success: true } match
            ? int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) * (match.Groups["unit"].Value == "m" ? 60 : 1)
            : null;

    /// <summary>
    /// DOC-R2-01 — the documented nginx.conf replaced the image's own with a bare events {}: one worker of 512 connections, no
    /// cap per client and nginx's 60-second handshake and header timeouts, so one unauthenticated client holding about 512
    /// half-open connections locked every other client out. It keeps the image's worker defaults, a connection cap per client
    /// address, and timeouts below nginx's own (60, 60 and 75 seconds).
    /// </summary>
    [Fact]
    public void G17_the_documented_front_keeps_its_workers_caps_each_client_and_drops_the_slow()
    {
        var directives = NginxDirectives(DocumentedNginxConf(ConfigurationGuide()));
        string? Value(string name) => directives.FirstOrDefault(d => d.Name == name).Arguments;

        Assert.Equal("auto", Value("worker_processes"));
        Assert.True(
            int.TryParse(Value("worker_connections"), System.Globalization.CultureInfo.InvariantCulture, out var connections) && connections >= 1024,
            $"the documented front holds {Value("worker_connections") ?? "nginx's own 512"} connections a worker, fewer than the image's own 1024.");

        var zone = PerClientZone().Match(Value("limit_conn_zone") ?? string.Empty);
        Assert.True(zone.Success, $"the documented front keys no connection zone by the client's address: limit_conn_zone is '{Value("limit_conn_zone")}'.");
        var cap = (Value("limit_conn") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(
            cap is [var name, var count] && name == zone.Groups["zone"].Value && int.TryParse(count, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0,
            $"the documented front caps no client on its zone '{zone.Groups["zone"].Value}': limit_conn is '{Value("limit_conn")}'.");

        Assert.True(NginxSeconds(Value("client_header_timeout")) is > 0 and < 60, $"client_header_timeout is '{Value("client_header_timeout")}', not under nginx's own 60 s.");
        Assert.True(NginxSeconds(Value("client_body_timeout")) is > 0 and < 60, $"client_body_timeout is '{Value("client_body_timeout")}', not under nginx's own 60 s.");
        Assert.True(NginxSeconds(Value("keepalive_timeout")) is > 0 and < 75, $"keepalive_timeout is '{Value("keepalive_timeout")}', not under nginx's own 75 s.");
    }

    /// <summary>
    /// DOC-R2-03 — the documented front sent its nginx version in every response, and under TLS 1.2 accepted CBC suites and a
    /// static-RSA exchange with no forward secrecy for the bearer tokens it carries. It names no version, and every TLS 1.2
    /// suite it offers is an ECDHE exchange with an AEAD cipher.
    /// </summary>
    [Fact]
    public void G17_the_documented_front_names_no_version_and_offers_only_forward_secret_authenticated_ciphers()
    {
        var directives = NginxDirectives(DocumentedNginxConf(ConfigurationGuide()));
        string? Value(string name) => directives.FirstOrDefault(d => d.Name == name).Arguments;

        Assert.Equal("off", Value("server_tokens"));
        var suites = (Value("ssl_ciphers") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(suites);
        Assert.All(suites, suite => Assert.True(
            suite.StartsWith("ECDHE-", StringComparison.Ordinal)
                && (suite.Contains("-GCM-", StringComparison.Ordinal) || suite.EndsWith("-CHACHA20-POLY1305", StringComparison.Ordinal)),
            $"the documented front offers '{suite}', which is not an ECDHE exchange with an AEAD cipher."));

        // contract-005 · G-17 round 3 (DOC-R3-02) — and it issues no TLS session tickets. nginx issues them by default, each
        // holding its session's secret under a key kept as long as nginx runs, and under TLS 1.2 the ticket is sent in the
        // clear: that one key would open every recorded connection given one, whatever the cipher suite.
        Assert.True(
            Value("ssl_session_tickets") == "off",
            $"the documented front's ssl_session_tickets is '{Value("ssl_session_tickets") ?? "nginx's own, on"}', not off.");
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (DOC-R3-01) — the documented deployment kept its logs only inside the server's container:
    /// the file logs, security events included, went with the container on every recreate — every new image, every changed
    /// setting — and on every down, and Docker's own copy of each container's output grew without bound. The server's logs/
    /// is a named volume, declared at the top level, and every service's own output is rotated: json-file, with a size and a
    /// count.
    /// </summary>
    [Fact]
    public void G17_the_documented_deployment_keeps_the_servers_logs_on_a_volume_and_rotates_every_services_output()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(DocumentedComposeFile(ConfigurationGuide())));
        var root = stream.Documents[0].RootNode;

        static YamlNode? Child(YamlNode? node, string key) =>
            node is YamlMappingNode mapping ? mapping.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == key).Value : null;
        static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

        var services = Assert.IsType<YamlMappingNode>(Child(root, "services"));
        Assert.Equal(["redis", "mcp-server", "proxy"], services.Children.Keys.Select(Scalar));

        // The server's logs/ on a named volume, declared at the top level.
        string?[] mounts = (Child(Child(services, "mcp-server"), "volumes") as YamlSequenceNode)?.Children.Select(Scalar).ToArray() ?? [];
        if (mounts.SingleOrDefault(m => m?.EndsWith(":/app/logs", StringComparison.Ordinal) == true) is not { } logs)
        {
            Assert.Fail($"the documented server mounts nothing at /app/logs: {string.Join(", ", mounts)}.");
            return;
        }

        var volume = logs[..logs.IndexOf(':', StringComparison.Ordinal)];
        Assert.False(
            volume.StartsWith('.') || volume.StartsWith('/') || volume.StartsWith('~'),
            $"the documented server's /app/logs is '{volume}', a directory of the host's, not a named volume.");
        Assert.True(
            Child(root, "volumes") is YamlMappingNode declared && declared.Children.Keys.Any(k => Scalar(k) == volume),
            $"the documented compose file declares no volume '{volume}' at its top level.");

        // Every service's own output, rotated.
        foreach (var (key, service) in services.Children)
        {
            var name = Scalar(key);
            var logging = Child(service, "logging");
            var options = Child(logging, "options");
            Assert.True(
                Scalar(Child(logging, "driver")) == "json-file",
                $"the documented '{name}' service's logging driver is '{Scalar(Child(logging, "driver")) ?? "Docker's default"}', not json-file named with its rotation.");
            Assert.True(
                !string.IsNullOrWhiteSpace(Scalar(Child(options, "max-size"))) && !string.IsNullOrWhiteSpace(Scalar(Child(options, "max-file"))),
                $"the documented '{name}' service's output is not rotated: its logging options name no max-size and max-file.");
        }
    }

    /// <summary>
    /// DOC-R2-03 — no service of the documented deployment had a restart policy, so a daemon restart or a reboot left it down;
    /// and its start line set MCP_HOST for one command, so every later Compose command on the file (ps, logs, stop, down)
    /// stopped asking for it. Each service restarts unless stopped, and the host name is kept in .env beside the file.
    /// </summary>
    [Fact]
    public void G17_the_documented_deployment_restarts_each_service_and_keeps_its_host_name_for_every_compose_command()
    {
        var document = ConfigurationGuide();

        var restart = new Dictionary<string, string?>(StringComparer.Ordinal);
        var inServices = false;
        string? service = null;
        foreach (var line in DocumentedComposeFile(document).Split('\n'))
        {
            var text = line.TrimStart();
            var indent = line.Length - text.Length;
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            if (indent == 0)
            {
                inServices = text == "services:";
                service = null;
            }
            else if (inServices && indent == 2 && text.EndsWith(':'))
            {
                service = text.TrimEnd(':');
                restart[service] = null;
            }
            else if (inServices && indent == 4 && service is not null && text.StartsWith("restart:", StringComparison.Ordinal))
            {
                restart[service] = text["restart:".Length..].Trim();
            }
        }

        Assert.Equal(["redis", "mcp-server", "proxy"], restart.Keys);
        Assert.All(restart, entry => Assert.True(entry.Value == "unless-stopped", $"the documented '{entry.Key}' service restarts '{entry.Value ?? "no"}'."));

        var after = document[document.IndexOf("\n**nginx.conf**", StringComparison.Ordinal)..];
        Assert.Contains("a file named `.env` beside the compose file", after, StringComparison.Ordinal);
        Assert.Contains("`MCP_HOST=mcp.example.com`", after, StringComparison.Ordinal);
        Assert.DoesNotContain("MCP_HOST=mcp.example.com docker compose", document, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\$binary_remote_addr\s+zone=(?<zone>[A-Za-z0-9_]+):\d+[km]?$")]
    private static partial Regex PerClientZone();

    [GeneratedRegex(@"^(?<value>\d+)(?<unit>[sm]?)$")]
    private static partial Regex NginxTime();

    [GeneratedRegex(@"<!--\s*retired\s*-->.*?<!--\s*/retired\s*-->", RegexOptions.Singleline)]
    private static partial Regex RetiredBlock();

    // The sections SettingsAllowlist.GovernedSections names; HttpTransport and Kestrel since contract-005 ·
    // G-12 (2). No Kestrel key is a setting the server reads, so a document naming one fails here.
    [GeneratedRegex(@"\b(Authentication|Providers|Limits|Confirmation|Development|RateLimit|HttpTransport|Kestrel)(:|__)[A-Za-z0-9_{}*]+((:|__)[A-Za-z0-9_{}*]+)*")]
    private static partial Regex GovernedKey();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^#{1,6}\s+(?<text>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"[^\p{L}\p{N}\s_-]")]
    private static partial Regex NotSlug();
}
