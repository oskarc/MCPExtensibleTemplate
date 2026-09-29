using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace McpServerTemplate.Tests;

/// <summary>
/// contract-001 · T-9 (G-8) and T-11 (G-10) — guarantees about the repository itself.
///
/// The secret scan also runs in CI over the whole tree; this is the part that can be asserted
/// in-process, so it holds on a developer's machine before a push rather than after one.
///
/// contract-005 · T-13 (G-1, G-2, G-3, G-10) — and the repository's invariants for the shipped image and the suites
/// around it: what the product and the end-to-end suite reference, what the image is built from, that every image is
/// pinned by digest, that the suite's own switches and the identity test hook stay where they belong, that no workflow
/// pushes an image, and that no key or certificate is tracked.
/// </summary>
public partial class RepositoryInvariantsTests
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

    // ── contract-005 · T-13 (G-1, G-2, G-3, G-10): what ships, what the suites reach, and what is kept ─────
    // Cheap file checks only: nothing here builds, starts or pulls anything.

    /// <summary>The project files a project reaches through its ProjectReferences, directly or through another's.</summary>
    private static HashSet<string> ProjectReferenceClosure(string project)
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([Path.GetFullPath(project)]);
        while (pending.TryPop(out var current))
        {
            foreach (var reference in XDocument.Load(current).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (include is not null)
                {
                    var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, include.Replace('\\', Path.DirectorySeparatorChar)));
                    if (reached.Add(full))
                    {
                        pending.Push(full);
                    }
                }
            }
        }

        return reached;
    }

    /// <summary>
    /// The repository's own source under <paramref name="directory"/>: build output, logs, test results, git's and the
    /// editor's state, and the kit, which is not source, left out.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string directory, string pattern)
    {
        string[] skipped = ["bin", "obj", "logs", "TestResults", ".git", ".vs", ".vscode", ".claude"];
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory).Where(d => !skipped.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            foreach (var file in SourceFiles(child, pattern))
            {
                yield return file;
            }
        }
    }

    /// <summary>A path relative to the repository, with / between names, as a message names it.</summary>
    private static string Relative(string path) => Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/');

    /// <summary>
    /// A Dockerfile's instructions, one per entry: comments dropped, and a line continued with a backslash joined to the
    /// next, as the builder reads them.
    /// </summary>
    private static List<string> DockerfileInstructions(string path) => DockerfileInstructions(File.ReadAllLines(path));

    /// <summary>The instructions of a Dockerfile's <paramref name="lines"/>, read as <see cref="DockerfileInstructions(string)"/> reads a file.</summary>
    private static List<string> DockerfileInstructions(IEnumerable<string> lines)
    {
        var instructions = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || (line.Length == 0 && current.Length == 0))
            {
                continue;
            }

            var continued = line.EndsWith('\\');
            current.Append(continued ? line[..^1] : line).Append(' ');
            if (!continued)
            {
                instructions.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            instructions.Add(current.ToString().Trim());
        }

        return instructions;
    }

    [Fact]
    public void T13_the_product_project_has_no_project_reference()
    {
        // G-10 — the product references nothing: no test host, no test library, nothing a later project could reach it through.
        var project = Path.Combine(RepositoryRoot(), "McpServerTemplate", "McpServerTemplate.csproj");
        var references = ProjectReferenceClosure(project);

        Assert.True(
            references.Count == 0,
            $"McpServerTemplate.csproj has a ProjectReference to {string.Join(", ", references.Select(Relative))}: the product must reference no project.");
    }

    [Fact]
    public void T13_the_end_to_end_suite_references_neither_the_product_nor_the_test_host()
    {
        // G-2 — it sees the server only through its network edges, its logs and its exit codes: not directly, and not through
        // any project it references.
        var root = RepositoryRoot();
        var reached = ProjectReferenceClosure(Path.Combine(root, "McpServerTemplate.E2E", "McpServerTemplate.E2E.csproj"));

        // Positive control: the walk finds what the suite does reference, the shared Testing library.
        Assert.Contains(Path.Combine(root, "McpServerTemplate.Testing", "McpServerTemplate.Testing.csproj"), reached);

        var forbidden = new[]
        {
            Path.Combine(root, "McpServerTemplate", "McpServerTemplate.csproj"),
            Path.Combine(root, "McpServerTemplate.E2EHost", "McpServerTemplate.E2EHost.csproj"),
        };
        var offenders = reached.Where(p => forbidden.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(Relative).ToArray();
        Assert.True(
            offenders.Length == 0,
            $"McpServerTemplate.E2E reaches {string.Join(" and ", offenders)} through its ProjectReferences: it must reference neither the product nor the test host.");
    }

    [Fact]
    public void T13_the_root_dockerfile_has_no_test_stage_and_copies_no_test_material()
    {
        // G-1 — the image that ships is built from the product and nothing else. What the build may copy from the context is
        // the product's project and the three files it builds with; the test projects, the test results and the context as a
        // whole never, and McpServerTemplate/init.json, the test material in the product's folder, stays out of the context.
        var root = RepositoryRoot();
        string[] productInputs = ["global.json", "Directory.Build.props", ".editorconfig", "McpServerTemplate"];

        var problems = new List<string>();
        var instructions = DockerfileInstructions(Path.Combine(root, "Dockerfile"));
        foreach (var instruction in instructions)
        {
            var words = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var keyword = words[0].ToUpperInvariant();

            if (keyword == "FROM" && words.Length >= 4 && words[^2].Equals("AS", StringComparison.OrdinalIgnoreCase)
                && words[^1].Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"a test stage: '{instruction}'");
            }

            if (keyword == "RUN" && (instruction.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) || instruction.Contains("vstest", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"a step that runs tests: '{instruction}'");
            }

            if (keyword is "COPY" or "ADD" && !words.Any(w => w.StartsWith("--from", StringComparison.OrdinalIgnoreCase)))
            {
                // Every source but the last word, the destination; flags (--chown and the like) are not sources.
                foreach (var source in words[1..^1].Where(w => !w.StartsWith("--", StringComparison.Ordinal)))
                {
                    var path = source.Replace('\\', '/');
                    while (path.StartsWith("./", StringComparison.Ordinal))
                    {
                        path = path[2..];
                    }

                    path = path.TrimEnd('/');
                    if (path is "" or "." || path.Contains('*', StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal)
                        || !productInputs.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal)))
                    {
                        problems.Add($"'{source}' copied from the context, which is not the product's: '{instruction}'");
                    }
                }
            }
        }

        // Positive control: the file was read as a Dockerfile, and it does copy the product.
        Assert.Contains(instructions, i => i.StartsWith("COPY McpServerTemplate/", StringComparison.Ordinal));

        var ignored = File.ReadAllLines(Path.Combine(root, ".dockerignore")).Select(l => l.Trim());
        if (File.Exists(Path.Combine(root, "McpServerTemplate", "init.json")) && !ignored.Contains("McpServerTemplate/init.json"))
        {
            problems.Add("McpServerTemplate/init.json, test material in the product's folder, is no longer kept out of the build context (.dockerignore)");
        }

        Assert.True(problems.Count == 0, "The root Dockerfile builds more than the product: " + string.Join("; ", problems));
    }

    [Fact]
    public void T13_every_image_reference_is_pinned_by_digest()
    {
        // G-1, G-8 — a tag can move on its registry; a digest cannot. An image reference is every image a Dockerfile names
        // (ImagesNamedIn: every FROM that is not an earlier stage, and — contract-005 · G-17 round 1, CON-R1-01 — every
        // COPY --from and RUN --mount from that names no earlier stage), and, in the end-to-end harness and this suite, every
        // Image constant and every literal a container builder is given. The images the harness builds are tagged by their
        // context's hash, and their FROM lines are the Dockerfiles'.
        var root = RepositoryRoot();
        var references = new List<(string Where, string Image)>();

        foreach (var dockerfile in SourceFiles(root, "Dockerfile*"))
        {
            references.AddRange(ImagesNamedIn(DockerfileInstructions(dockerfile)).Select(image => (Relative(dockerfile), image)));
        }

        var constant = new Regex(@"const\s+string\s+\w*Image\s*=\s*""(?<image>[^""]+)""");
        var literal = new Regex(@"(?:new\s+\w*Builder|\.WithImage)\(\s*""(?<image>[^""]+)""");
        string[] suites = ["McpServerTemplate.E2E", "McpServerTemplate.Tests"];
        foreach (var file in suites.SelectMany(d => SourceFiles(Path.Combine(root, d), "*.cs")))
        {
            var text = File.ReadAllText(file);
            references.AddRange(constant.Matches(text).Concat(literal.Matches(text)).Select(m => (Relative(file), m.Groups["image"].Value)));
        }

        // Positive control: the references this repository is known to hold were found.
        Assert.Contains(references, r => r.Where == "Dockerfile");
        Assert.Contains(references, r => r.Where.EndsWith("Harness/TlsFront.cs", StringComparison.Ordinal));
        Assert.Contains(references, r => r.Where.EndsWith("TestRedis.cs", StringComparison.Ordinal));

        var unpinned = references.Where(r => !PinnedByDigest().IsMatch(r.Image)).Select(r => $"{r.Where}: {r.Image}").ToArray();
        Assert.True(unpinned.Length == 0, "Image references not pinned by digest: " + string.Join("; ", unpinned));
    }

    /// <summary>An image reference that ends in a digest: the one form a registry cannot move.</summary>
    [GeneratedRegex(@"@sha256:[0-9a-f]{64}$")]
    private static partial Regex PinnedByDigest();

    /// <summary>
    /// Every image a Dockerfile's <paramref name="instructions"/> name, as the builder pulls it: each FROM that is not an
    /// earlier stage, and — contract-005 · G-17 round 1 (CON-R1-01) — each COPY --from and each RUN --mount's from= that names
    /// neither an earlier stage nor an earlier stage's index. The builder pulls an image named there as it pulls a FROM's, so
    /// a tag there can move on its registry as a FROM's can; the digest check read the FROM lines alone, and
    /// COPY --from=busybox:latest passed it.
    /// </summary>
    private static List<string> ImagesNamedIn(IReadOnlyList<string> instructions)
    {
        var images = new List<string>();
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = 0;

        // A stage by its name, or by its index among the stages declared so far, as COPY --from takes either.
        bool IsStage(string from) =>
            stages.Contains(from)
            || (int.TryParse(from, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < declared);

        foreach (var instruction in instructions)
        {
            var words = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var flags = words.Skip(1).TakeWhile(w => w.StartsWith("--", StringComparison.Ordinal)).ToArray();
            switch (words[0].ToUpperInvariant())
            {
                case "FROM":
                    var operands = words.Skip(1).Where(w => !w.StartsWith("--", StringComparison.Ordinal)).ToArray();
                    if (!stages.Contains(operands[0]))
                    {
                        images.Add(operands[0]);
                    }

                    if (operands.Length >= 3 && operands[^2].Equals("AS", StringComparison.OrdinalIgnoreCase))
                    {
                        stages.Add(operands[^1]);
                    }

                    declared++;
                    break;

                case "COPY":
                    images.AddRange(flags
                        .Where(f => f.StartsWith("--from=", StringComparison.OrdinalIgnoreCase))
                        .Select(f => f["--from=".Length..])
                        .Where(from => !IsStage(from)));
                    break;

                case "RUN":
                    images.AddRange(flags
                        .Where(f => f.StartsWith("--mount=", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(f => f["--mount=".Length..].Split(','))
                        .Where(option => option.StartsWith("from=", StringComparison.OrdinalIgnoreCase))
                        .Select(option => option["from=".Length..])
                        .Where(from => !IsStage(from)));
                    break;

                default:
                    break;
            }
        }

        return images;
    }

    /// <summary>A digest of the right form, for the probe Dockerfiles below.</summary>
    private const string ProbeDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A probe Dockerfile's two stages, as the root Dockerfile declares them, each pinned by digest.</summary>
    private const string ProbeStages =
        $"FROM mcr.microsoft.com/dotnet/sdk:10.0.101-noble@sha256:{ProbeDigest} AS build\n"
        + "RUN mkdir -p /runtime/logs\n"
        + $"FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:{ProbeDigest} AS final\n";

    /// <summary>
    /// contract-005 · G-17 round 1 (CON-R1-01) — each way a Dockerfile names an image besides FROM, which the digest check must
    /// see: the probe, and the unpinned image it names. The first is the one the round named: an image copied from by tag.
    /// </summary>
    public static TheoryData<string, string, string> WaysToNameAnImage() => new()
    {
        { "COPY --from an image by tag", ProbeStages + "COPY --from=nginx:latest /etc/nginx/nginx.conf /etc/nginx/nginx.conf\n", "nginx:latest" },
        { "COPY --from an image, after another flag", ProbeStages + "COPY --chown=1654:1654 --from=busybox:latest /bin/busybox /busybox\n", "busybox:latest" },
        { "COPY --from an image by name alone", ProbeStages + "COPY --from=busybox /bin/busybox /busybox\n", "busybox" },
        { "COPY --from the test host's own image", ProbeStages + "COPY --from=mcp-e2e-testhost:4ff0f487 /app/McpServerTemplate.E2EHost.dll ./\n", "mcp-e2e-testhost:4ff0f487" },
        {
            "COPY --from a name no earlier stage has",
            $"FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:{ProbeDigest} AS final\nCOPY --from=build /app ./\n"
                + $"FROM mcr.microsoft.com/dotnet/sdk:10.0.101-noble@sha256:{ProbeDigest} AS build\n",
            "build"
        },
        { "RUN --mount from an image", ProbeStages + "RUN --mount=type=bind,from=busybox:latest,source=/bin/busybox,target=/busybox /busybox true\n", "busybox:latest" },
    };

    [Theory]
    [MemberData(nameof(WaysToNameAnImage))]
    public void T13_the_digest_check_sees_every_image_a_dockerfile_names(string way, string dockerfile, string image)
    {
        // Positive control: the same stages named every other way a Dockerfile may — by name, by index, in a mount — and an
        // image copied from by digest are not refused.
        var clean = ProbeStages
            + "COPY --from=build /app ./\n"
            + "COPY --from=0 --chown=1654:1654 /runtime/logs ./logs\n"
            + $"COPY --from=nginx:1.30.5-alpine@sha256:{ProbeDigest} /etc/nginx/mime.types /etc/mime.types\n"
            + "RUN --mount=type=bind,from=build,source=/app,target=/b true\n";
        Assert.Empty(UnpinnedImagesIn(clean));

        // The row: the image the probe names, unpinned, is found.
        var found = UnpinnedImagesIn(dockerfile);
        Assert.True(found.Count > 0, $"{way}: the digest check found no image in{Environment.NewLine}{dockerfile}");
        Assert.Contains(image, found);
    }

    /// <summary>The images a Dockerfile's text names that are not pinned by digest, as the digest check reads them.</summary>
    private static List<string> UnpinnedImagesIn(string dockerfile) =>
        [.. ImagesNamedIn(DockerfileInstructions(dockerfile.Split('\n'))).Where(i => !PinnedByDigest().IsMatch(i))];

    [Fact]
    public void T13_no_product_or_test_host_file_reads_an_end_to_end_variable()
    {
        // G-10, G-11 — the end-to-end suite's own switches (MCP_E2E_*) belong to the suite: a product or test-host file that
        // read one could behave differently under test than it ships. Assembled, as elsewhere, so the name is whole only here.
        var marker = "MCP_" + "E2E_";
        var root = RepositoryRoot();
        string[] projects = ["McpServerTemplate", "McpServerTemplate.E2EHost"];
        var read = projects.SelectMany(d => SourceFiles(Path.Combine(root, d), "*")).ToArray();

        // Positive control: both projects' files were read.
        Assert.Contains(read, f => f.EndsWith($"McpServerTemplate{Path.DirectorySeparatorChar}Program.cs", StringComparison.Ordinal));
        Assert.Contains(read, f => f.EndsWith($"McpServerTemplate.E2EHost{Path.DirectorySeparatorChar}Program.cs", StringComparison.Ordinal));

        var offenders = read.Where(f => File.ReadAllText(f).Contains(marker, StringComparison.Ordinal)).Select(Relative).ToArray();
        Assert.True(offenders.Length == 0, $"These product or test-host files name an end-to-end variable ({marker}*): {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void T13_nothing_outside_the_fast_suite_passes_the_identity_test_hook()
    {
        // G-10 — AddHttpServer's configureIdentityForTests gives a test an in-process backchannel to each identity provider;
        // the fast suite's in-process server is the one caller it is for. Outside it, AddHttpServer is called with two
        // arguments and AddIdentity with one, the product's own forwarding of the hook it was given aside.
        var root = RepositoryRoot();
        var tests = Path.Combine(root, "McpServerTemplate.Tests") + Path.DirectorySeparatorChar;
        var offenders = SourceFiles(root, "*.cs")
            .Where(f => !f.StartsWith(tests, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => IdentityHookPassedIn(f))
            .ToArray();

        // Positive control: the same reading finds the one caller meant to pass it.
        Assert.Contains(SourceFiles(tests, "*.cs").SelectMany(f => IdentityHookPassedIn(f)), o => o.StartsWith("McpServerTemplate.Tests/Identity/InProcessServer.cs", StringComparison.Ordinal));

        Assert.True(offenders.Length == 0, "Outside the fast suite, the identity test hook is passed at: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// Every call in <paramref name="file"/> that hands AddHttpServer or AddIdentity the identity test hook: by name, or as
    /// an argument past the ones a deployment passes. A declaration of either is not a call; the product's forwarding of the
    /// parameter it was given, in HttpServerComposition, is not a caller passing one.
    /// </summary>
    private static IEnumerable<string> IdentityHookPassedIn(string file)
    {
        var code = Regex.Replace(Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);
        var relative = Relative(file);
        foreach (Match call in Regex.Matches(code, @"\b(?<name>AddHttpServer|AddIdentity)\s*\("))
        {
            // What stands before it in its statement: a modifier there makes it a declaration, not a call.
            var statement = code.LastIndexOfAny(['\n', ';', '{', '}'], Math.Max(call.Index - 1, 0)) + 1;
            if (Regex.IsMatch(code[statement..call.Index], @"\b(public|private|internal|protected|static)\b"))
            {
                continue;
            }

            var arguments = ArgumentsFrom(code, call.Index + call.Length);
            var name = call.Groups["name"].Value;
            var forwarded = name == "AddIdentity" && relative == "McpServerTemplate/Infrastructure/HttpServerComposition.cs"
                && arguments.Count == 2 && arguments[1] == "configureIdentityForTests";
            if (!forwarded && (arguments.Count > (name == "AddHttpServer" ? 2 : 1)
                || arguments.Any(a => a.StartsWith("configureIdentityForTests:", StringComparison.Ordinal) || a.StartsWith("configureForTests:", StringComparison.Ordinal))))
            {
                yield return $"{relative}: {name}({string.Join(", ", arguments)})";
            }
        }
    }

    /// <summary>The top-level arguments of the call whose argument list starts at <paramref name="start"/>, trimmed.</summary>
    private static List<string> ArgumentsFrom(string code, int start)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        char? quote = null;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (quote is not null)
            {
                current.Append(c);
                if (c == '\\')
                {
                    current.Append(code[++i]);
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    current.Append(c);
                    break;
                case '(' or '[' or '{':
                    depth++;
                    current.Append(c);
                    break;
                case ')' when depth == 0:
                    if (current.ToString().Trim().Length > 0 || arguments.Count > 0)
                    {
                        arguments.Add(current.ToString().Trim());
                    }

                    return arguments;
                case ')' or ']' or '}':
                    depth--;
                    current.Append(c);
                    break;
                case ',' when depth == 0:
                    arguments.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        return arguments;
    }

    [Fact]
    public void T13_no_workflow_pushes_an_image()
    {
        // G-10, G-14 — the test images are never pushed anywhere, and nor is the product's from here. A push is any step that
        // sends an image to a registry: docker push (or image push, or compose push), a build with --push or push: true, and
        // the other tools' push and copy.
        //
        // contract-005 · G-17 round 1 (CON-R1-02) — read as YAML, as GitHub reads it (WorkflowPushes), not line by line: every
        // .yml and .yaml under .github, and every local action or reusable workflow a step uses, wherever it is. The line
        // regex passed a build-push-action whose push was an expression, buildx's --output type=registry and push=true,
        // imagetools create, a registry login, packages: write, and a docker push inside a local composite action.
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, ".github"), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Positive control: the workflows were found, and read as workflows.
        Assert.Contains(".github/workflows/ci.yml", files);
        Assert.Contains(".github/workflows/sabotage-retake.yml", files);
        Assert.All(files.Where(f => f.StartsWith(".github/workflows/", StringComparison.Ordinal)), f => Assert.Contains("jobs:", File.ReadAllText(Path.Combine(root, f)), StringComparison.Ordinal));

        string? Read(string relative) => File.Exists(Path.Combine(root, relative)) ? File.ReadAllText(Path.Combine(root, relative)) : null;
        var offenders = files.SelectMany(f => WorkflowPushes(f, Read)).ToArray();
        Assert.True(offenders.Length == 0, "A workflow pushes an image, or could: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (CON-R1-02) — each way a workflow can send an image to a registry, or be let to, that the
    /// line regex passed: the check must see it. The first file is the workflow; a second, when there is one, is a local
    /// action or reusable workflow it uses.
    /// </summary>
    public static TheoryData<string, string, string?> WaysToPushAnImage()
    {
        const string Head = "on:\n  push:\npermissions:\n  contents: read\njobs:\n  publish:\n    runs-on: ubuntu-latest\n    steps:\n";
        const string Step = "      - uses: actions/checkout@v4\n";
        return new()
        {
            { "docker push in a run block", Head + Step + "      - run: |\n          docker build -t ghcr.io/o/r:1 .\n          docker push ghcr.io/o/r:1\n", null },
            { "build-push-action, push an expression", Head + Step + "      - uses: docker/build-push-action@v6\n        with:\n          file: McpServerTemplate.E2EHost/Dockerfile\n          push: ${{ github.event_name != 'pull_request' }}\n", null },
            { "build-push-action, push true", Head + Step + "      - uses: docker/build-push-action@v6\n        with:\n          push: true\n", null },
            { "build-push-action, registry outputs", Head + Step + "      - uses: docker/build-push-action@v6\n        with:\n          outputs: type=registry\n", null },
            { "buildx, --output type=registry", Head + Step + "      - run: docker buildx build --output type=registry -t ghcr.io/o/r:1 -f McpServerTemplate.TestIssuer/Dockerfile .\n", null },
            { "buildx, -o push=true", Head + Step + "      - run: docker buildx build -o type=image,name=ghcr.io/o/r:1,push=true .\n", null },
            { "buildx, --push", Head + Step + "      - run: docker buildx build --push -t ghcr.io/o/r:1 .\n", null },
            { "imagetools create", Head + Step + "      - run: docker buildx imagetools create -t ghcr.io/o/r:1 ghcr.io/o/r:0\n", null },
            { "docker login", Head + Step + "      - run: echo \"$T\" | docker login ghcr.io -u o --password-stdin\n", null },
            { "a registry login action", Head + Step + "      - uses: docker/login-action@v3\n        with:\n          registry: ghcr.io\n", null },
            { "a push action", Head + Step + "      - uses: redhat-actions/push-to-registry@v2\n", null },
            { "packages: write for the workflow", Head.Replace("  contents: read\n", "  contents: read\n  packages: write\n", StringComparison.Ordinal) + Step, null },
            { "packages: write for a job", Head.Replace("    runs-on: ubuntu-latest\n", "    runs-on: ubuntu-latest\n    permissions:\n      packages: write\n", StringComparison.Ordinal) + Step, null },
            { "write-all", Head.Replace("permissions:\n  contents: read\n", "permissions: write-all\n", StringComparison.Ordinal) + Step, null },
            { "no permissions stated", Head.Replace("permissions:\n  contents: read\n", string.Empty, StringComparison.Ordinal) + Step, null },
            { "a local composite action that pushes", Head + Step + "      - uses: ./.github/actions/publish\n",
                "name: publish\nruns:\n  using: composite\n  steps:\n    - run: docker push ghcr.io/o/r:1\n      shell: bash\n" },
            { "a local reusable workflow that logs in", "on:\n  push:\npermissions:\n  contents: read\njobs:\n  publish:\n    uses: ./.github/workflows/publish.yml\n",
                "on:\n  workflow_call:\npermissions:\n  contents: read\njobs:\n  x:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: docker/login-action@v3\n" },
        };
    }

    [Theory]
    [MemberData(nameof(WaysToPushAnImage))]
    public void T13_the_workflow_check_sees_every_way_to_push_an_image(string way, string workflow, string? used)
    {
        const string Probe = ".github/workflows/publish-probe.yml";
        var usedPath = workflow.Contains("./.github/actions/publish", StringComparison.Ordinal) ? ".github/actions/publish/action.yml" : ".github/workflows/publish.yml";
        string? Read(string relative) => relative == Probe ? workflow : relative == usedPath ? used : null;

        // Positive control: the same shape with nothing that pushes, or could, is not refused — a trigger named push among it.
        const string Clean = "on:\n  push:\n  pull_request:\npermissions:\n  contents: read\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n"
            + "      - uses: actions/checkout@v4\n      - uses: docker/build-push-action@v6\n        with:\n          push: false\n"
            + "      - run: |\n          dotnet build McpServerTemplate.sln -c Release\n          docker build -t mcp-server:latest .\n";
        Assert.Empty(WorkflowPushes(Probe, relative => relative == Probe ? Clean : null));

        var found = WorkflowPushes(Probe, Read);
        Assert.True(found.Count > 0, $"{way}: the workflow check found nothing in{Environment.NewLine}{workflow}{used}");
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (CON-R1-02) — what the workflow or action at <paramref name="relative"/> (a path under the
    /// repository, / between names) does that sends an image to a registry, or lets one be sent, read as YAML, one line each:
    /// - a command in any value that pushes (docker, podman or buildah push, --push, an --output or -o of type=registry or
    ///   push=true, imagetools create, crane, oras or skopeo copying to a registry) or logs in to a registry;
    /// - an action that logs in to a registry or pushes (docker/login-action and the like), and any action's push input that is
    ///   not false (docker/build-push-action pushes on push: true, or an expression);
    /// - packages: write, or write-all, as a workflow's or a job's permissions; and a workflow that states no permissions,
    ///   whose token then has the repository's default ones, which can include packages: write;
    /// - a local action or reusable workflow it uses (uses: ./…) that does any of these, read the same way, wherever it is.
    /// <paramref name="read"/> gives a file's text by its path, or null when there is none.
    /// </summary>
    private static List<string> WorkflowPushes(string relative, Func<string, string?> read, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();
        if (!seen.Add(relative))
        {
            return found;
        }

        if (read(relative) is not { } text)
        {
            found.Add($"{relative} is used, and is not in the repository");
            return found;
        }

        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            found.Add($"{relative} could not be read as YAML, so what it does is unknown: {ex.Message}");
            return found;
        }

        foreach (var document in stream.Documents)
        {
            if (relative.StartsWith(".github/workflows/", StringComparison.Ordinal)
                && (document.RootNode is not YamlDotNet.RepresentationModel.YamlMappingNode top || ValueOf(top, "permissions") is null))
            {
                found.Add($"{relative}: states no permissions, so its token has the repository's default ones, which can include packages: write");
            }

            Walk(document.RootNode, null);
        }

        return found;

        void Walk(YamlDotNet.RepresentationModel.YamlNode node, string? key)
        {
            var at = $"{relative}:{node.Start.Line}";
            switch (node)
            {
                case YamlDotNet.RepresentationModel.YamlScalarNode { Value: { } value }:
                    if (PushOrRegistryLogin().Match(value) is { Success: true } command)
                    {
                        found.Add($"{at}: '{command.Value}'");
                    }

                    break;

                case YamlDotNet.RepresentationModel.YamlSequenceNode sequence:
                    foreach (var item in sequence)
                    {
                        Walk(item, key);
                    }

                    break;

                case YamlDotNet.RepresentationModel.YamlMappingNode mapping:
                    foreach (var (name, value) in mapping.Children.Select(c => ((c.Key as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value, c.Value)))
                    {
                        var scalar = (value as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value;
                        if (name == "uses" && scalar is not null)
                        {
                            Uses(scalar, $"{relative}:{value.Start.Line}");
                        }

                        if (key == "with" && name == "push" && scalar != "false")
                        {
                            found.Add($"{relative}:{value.Start.Line}: push: {scalar ?? "(not a plain value)"}, an input that pushes unless it is false");
                        }

                        if (name == "permissions" && (scalar == "write-all"
                            || (value is YamlDotNet.RepresentationModel.YamlMappingNode granted
                                && (ValueOf(granted, "packages") as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value == "write")))
                        {
                            found.Add($"{relative}:{value.Start.Line}: permissions {(scalar ?? "packages: write")}, which lets its token push to the repository's registry");
                        }

                        Walk(value, name);
                    }

                    break;
            }
        }

        void Uses(string uses, string at)
        {
            var action = uses.Split('@')[0];
            if (RegistryActions.Any(a => action.Equals(a, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add($"{at}: uses {uses}, which logs in to a registry or pushes to one");
            }
            else if (uses.StartsWith("./", StringComparison.Ordinal))
            {
                // A local action is a directory holding action.yml (or .yaml); a local reusable workflow is the file itself.
                var target = uses[2..].TrimEnd('/');
                var file = target.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    ? target
                    : read($"{target}/action.yml") is not null ? $"{target}/action.yml" : $"{target}/action.yaml";
                found.AddRange(WorkflowPushes(file, read, seen).Select(f => $"{at}: uses {uses}, and {f}"));
            }
        }
    }

    /// <summary>The value <paramref name="mapping"/> holds under the plain key <paramref name="key"/>, or null.</summary>
    private static YamlDotNet.RepresentationModel.YamlNode? ValueOf(YamlDotNet.RepresentationModel.YamlMappingNode mapping, string key) =>
        mapping.Children.FirstOrDefault(c => (c.Key as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value == key).Value;

    /// <summary>The actions that log in to a registry or push to one, by owner/name, whatever version.</summary>
    private static readonly string[] RegistryActions =
    [
        "docker/login-action", "aws-actions/amazon-ecr-login", "azure/docker-login", "redhat-actions/podman-login",
        "redhat-actions/push-to-registry",
    ];

    /// <summary>A command that sends an image to a registry, or logs in to one.</summary>
    [GeneratedRegex(
        @"\bdocker\s+(image\s+|compose\s+|manifest\s+)?push\b|\bdocker-compose\s+push\b|--push\b|\btype=registry\b|\bpush=true\b|\bimagetools\s+create\b"
        + @"|\b(podman|buildah)\s+(manifest\s+)?push\b|\b(crane|oras)\s+(push|copy|cp)\b|\bskopeo\s+copy\b|\bhelm\s+push\b"
        + @"|\b(docker|podman|buildah|nerdctl|oras|skopeo)\s+login\b|\bcrane\s+auth\s+login\b|\bhelm\s+registry\s+login\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PushOrRegistryLogin();

    [Fact]
    public void T13_no_key_or_certificate_is_tracked_by_git()
    {
        // G-3 — the end-to-end PKI is made for each run and deleted after it; a key or certificate in the repository is a
        // credential or a trust anchor in every clone. Tracked files are asked of git itself. A key or certificate is found
        // by its file's kind — whatever its content, since DER and PKCS#12 are binary — or by a PEM block in any file: the
        // marker line followed by its base64, which a script or a test naming the marker does not have.
        var tracked = TrackedFiles();

        // Positive control: git answered, with this repository's files.
        Assert.Contains("McpServerTemplate.sln", tracked);

        string[] kinds = [".pem", ".crt", ".cer", ".der", ".key", ".pfx", ".p12", ".p7b", ".p7c", ".jks", ".keystore", ".csr"];
        var pem = new Regex("-----BEGIN (?:[A-Z0-9]+ )*(?:PRIVATE KEY|CERTIFICATE)-----\\s*\\n[A-Za-z0-9+/=]{20,}");
        var root = RepositoryRoot();
        var offenders = tracked
            .Where(f => kinds.Contains(Path.GetExtension(f).ToLowerInvariant())
                || (File.Exists(Path.Combine(root, f)) && pem.IsMatch(File.ReadAllText(Path.Combine(root, f)).Replace("\r\n", "\n", StringComparison.Ordinal))))
            .ToArray();

        Assert.True(offenders.Length == 0, "Keys or certificates tracked by git: " + string.Join(", ", offenders));
    }

    /// <summary>Every file git tracks in this repository, as git ls-files names it.</summary>
    private static string[] TrackedFiles()
    {
        var start = new System.Diagnostics.ProcessStartInfo("git", ["-C", RepositoryRoot(), "ls-files", "-z"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var git = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var output = git.StandardOutput.ReadToEndAsync();
        var errors = git.StandardError.ReadToEndAsync();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git ls-files failed: {errors.Result}");
        return output.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
