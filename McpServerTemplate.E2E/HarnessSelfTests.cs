using System.Text.RegularExpressions;
using McpServerTemplate.E2E.Harness;

namespace McpServerTemplate.E2E;

/// <summary>
/// The harness's own checks that need no environment: nothing here starts a container.
///
/// contract-005 · T-12 — so they are not end-to-end tests of the contract's claims, and have no sabotage. A sabotage acts
/// only in the end-to-end fixture — a test's inputs, a container's environment or the network — and weakens one thing
/// the image is claimed to do; these run in the test process against the harness's own code, which the end-to-end tests
/// stand on. The record names them, with this reason.
/// </summary>
[NotEndToEnd(
    "In-process checks of the harness's own code — the settings delta, the revision label, the sabotage registry and its "
    + "record: they start no container and send nothing to the image, so there is no fixture input, container environment "
    + "or network for a sabotage to act on, and no claim about the image for it to break.")]
public sealed partial class HarnessSelfTests
{
    /// <summary>
    /// contract-005 · G-8 — a key outside the sections the server's own startup check governs is one
    /// the server would ignore silently, so the delta refuses it before any container starts, naming
    /// the key and the nearest key the server is declared to read. Serilog:WriteTo:1:Args:pth is one
    /// that left a server running as if it were not there; the others are misspellings of the rest of
    /// what the server reads outside the governed sections.
    /// </summary>
    [Theory]
    [InlineData("Serilog:WriteTo:1:Args:pth", "Serilog:WriteTo:{n}:Args:path")]
    [InlineData("Serilog__MinimumLevel__Defualt", "Serilog:MinimumLevel:Default")]
    [InlineData("Transprot", "Transport")]
    [InlineData("SSL_CERT_FIEL", "SSL_CERT_FILE")]
    public void A_misspelt_key_outside_the_governed_sections_is_refused_naming_the_nearest_declared_key(string key, string nearest)
    {
        // Positive controls: a declared key and a governed key both pass, so the refusal below is
        // this key's and not a delta that refuses everything.
        SettingsDelta.None.Set("Serilog:WriteTo:1:Args:path", "logs/e2e-.log");
        SettingsDelta.None.Set("Authentication:Resourse", "left for the server's own check");

        var set = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Set(key, "x"));
        Assert.Contains($"'{key}'", set.Message, StringComparison.Ordinal);
        Assert.Contains($"Did you mean '{nearest}'?", set.Message, StringComparison.Ordinal);

        var removed = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Remove(key));
        Assert.Contains($"'{key}'", removed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — HttpTransport is governed by the server's own check now, so a
    /// misspelt transport key is passed through untouched: refusing it is the server's job, and a test
    /// of that refusal needs the key to arrive. These two were refused here before.
    /// </summary>
    [Theory]
    [InlineData("HttpTransport:AlowedHosts:0")]
    [InlineData("HttpTransport__KnownProxys__0")]
    public void A_misspelt_transport_key_reaches_the_server(string key)
    {
        var delta = SettingsDelta.None.Set(key, "x");
        Assert.Equal("x", delta.Changes[key.Replace("__", ":", StringComparison.Ordinal)]);
    }

    /// <summary>contract-005 · G-8 — a key under no section at all is refused the same way.</summary>
    [Fact]
    public void An_invented_top_level_key_is_refused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Set("Bogus:Key", "1"));
        Assert.Contains("'Bogus:Key'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-8 — an image's revision label names the commit it was built from, and an image
    /// built from a working tree whose build context differs from that commit is not the commit's: its
    /// label says so with -dirty. A change the context does not carry (docs/, which .dockerignore
    /// excludes) leaves the label as the commit. Run on a repository made for the test, so the
    /// checkout the suite builds from is never touched.
    /// </summary>
    [Fact]
    public async Task The_revision_label_marks_a_build_context_that_differs_from_its_commit()
    {
        var repository = Directory.CreateTempSubdirectory("e2e-revision-").FullName;
        try
        {
            await GitAsync(repository, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, ".dockerignore"), "docs/\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// one\n");
            Directory.CreateDirectory(Path.Combine(repository, "docs"));
            await File.WriteAllTextAsync(Path.Combine(repository, "docs", "guide.md"), "one\n");
            await GitAsync(repository, "add", "--all");
            await GitAsync(repository, "-c", "user.name=e2e", "-c", "user.email=e2e@example.invalid", "commit", "--quiet", "--message", "base");
            var commit = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();

            // Positive control: the tree as committed is labelled with the commit alone.
            Assert.Equal(commit, await Images.CheckoutRevisionAsync(repository, CancellationToken.None));

            // Changes the build context does not carry leave it so.
            await File.WriteAllTextAsync(Path.Combine(repository, "docs", "guide.md"), "two\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "docs", "new.md"), "new\n");
            Assert.Equal(commit, await Images.CheckoutRevisionAsync(repository, CancellationToken.None));

            // A change it carries — an edit, a new file, a deletion — and the image is not the commit's.
            var changes = new (string What, Func<Task> Make, Func<Task> Undo)[]
            {
                ("an edited file", () => File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// two\n"),
                    () => File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// one\n")),
                ("an untracked file", () => File.WriteAllTextAsync(Path.Combine(repository, "Added.cs"), "// new\n"),
                    () => Task.Run(() => File.Delete(Path.Combine(repository, "Added.cs")))),
                ("a deleted file", () => Task.Run(() => File.Delete(Path.Combine(repository, "Program.cs"))),
                    () => File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// one\n")),
            };

            foreach (var (what, make, undo) in changes)
            {
                await make();
                var revision = await Images.CheckoutRevisionAsync(repository, CancellationToken.None);
                Assert.True(
                    revision == $"{commit}-dirty",
                    $"a build context with {what} not in its commit was labelled '{revision}', not '{commit}-dirty'.");
                await undo();
                Assert.Equal(commit, await Images.CheckoutRevisionAsync(repository, CancellationToken.None));
            }
        }
        finally
        {
            // Git writes its objects read-only, and Windows will not delete them so.
            foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>
    /// contract-005 · T-12 (G-11) — every end-to-end test, as the runner reports it (a theory's every row), has a named
    /// sabotage; each name is one sabotage's, and each says in one line what it weakens. The registry, as the harness reads
    /// it, is written to TestResults/sabotage/ for scripts/e2e-sabotage.sh, which runs each sabotage in turn from it.
    /// </summary>
    [Fact]
    public void Every_end_to_end_test_has_a_named_sabotage()
    {
        var assembly = typeof(HarnessSelfTests).Assembly;
        var tests = Sabotage.EndToEndTests(assembly);
        var entries = Sabotage.All;

        // Positive controls: the reading found the suite's tests, a theory's rows among them, and not this class's.
        Assert.Contains(tests, t => t.Class == typeof(WalkingSkeletonTests));
        Assert.Contains(tests, t => t.Row is ["no-allowed-hosts", ..]);
        Assert.DoesNotContain(tests, t => t.Class == typeof(HarnessSelfTests));

        var problems = Sabotage.Problems(tests, entries);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var directory = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "TestResults", "sabotage");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "registry.tsv"), Sabotage.Listing(entries));
        File.WriteAllText(
            Path.Combine(directory, "not-end-to-end.tsv"),
            string.Concat(Sabotage.NotEndToEnd(assembly).Select(n => $"{n.Class.Name}\t{n.Tests}\t{n.Reason}\n")));
    }

    /// <summary>
    /// contract-005 · T-12 — and each has its recorded red, kept with its message in SABOTAGE-RECORD.md: a red on the
    /// assertion that carries the test's claim, taken on the test's file as it is now (<see cref="Sabotage.Fingerprint"/>),
    /// or, for a test skipped by decision, held. A sabotage added without its red, one whose test changed since its red was
    /// taken, or a record naming one the suite no longer has, fails here until scripts/e2e-sabotage.sh is run again — which
    /// retakes those, and only those.
    /// </summary>
    [Fact]
    public void Every_named_sabotage_has_a_recorded_red_on_its_claim()
    {
        var path = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E", Sabotage.RecordFile);
        Assert.True(File.Exists(path), $"{path} is missing: run scripts/e2e-sabotage.sh, which writes it.");
        var recorded = RecordRow().Matches(File.ReadAllText(path))
            .ToDictionary(
                m => m.Groups["name"].Value,
                m => (Fingerprint: m.Groups["fingerprint"].Success ? m.Groups["fingerprint"].Value : null, Result: m.Groups["result"].Value.Trim()),
                StringComparer.Ordinal);

        // Positive control: the record was read as one.
        Assert.NotEmpty(recorded);

        var problems = new List<string>();
        foreach (var entry in Sabotage.All)
        {
            if (!recorded.TryGetValue(entry.Name, out var row))
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): not in the record");
            }
            else if (entry.Held is not null)
            {
                if (!row.Result.StartsWith("held", StringComparison.Ordinal))
                {
                    problems.Add($"{entry.Name} ({entry.Test.Name}): held, and recorded as '{row.Result}'");
                }
            }
            else if (row.Result != "red on its claim")
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): recorded as '{row.Result}'");
            }
            else if (row.Fingerprint != Sabotage.Fingerprint(entry.Test))
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): its red was taken on {entry.Test.SourceFile} as {row.Fingerprint ?? "(none)"}, "
                    + $"and the file is {Sabotage.Fingerprint(entry.Test)} now");
            }
        }

        problems.AddRange(recorded.Keys.Where(n => Sabotage.All.All(e => e.Name != n)).Select(n => $"{n}: in the record, but no sabotage of the suite"));
        Assert.True(
            problems.Count == 0,
            $"{Sabotage.RecordFile} does not hold a red on its claim, taken on its test as it is, for every sabotage; run "
            + $"scripts/e2e-sabotage.sh, which retakes these. {string.Join("; ", problems)}");
    }

    /// <summary>
    /// contract-005 · G-11, T-12 — a run that names a sabotage is refused in CI, even with the variable empty, and one that
    /// names no sabotage of the suite is refused anywhere; a registered one, named outside CI, is the one applied.
    /// </summary>
    [Fact]
    public void A_sabotage_is_refused_in_ci_and_one_the_suite_does_not_have_is_refused_anywhere()
    {
        IReadOnlyList<Sabotage.Entry> Registry() => Sabotage.All;
        var registered = Sabotage.All.First(e => e.Held is null);

        // Positive controls: none named, none applied and nothing refused; a registered one named outside CI, applied.
        var none = Sabotage.Decide(null, null, Registry);
        Assert.Null(none.Active);
        Assert.Null(none.Refusal);
        Assert.Same(registered, Sabotage.Decide(registered.Name, null, Registry).Active);

        foreach (var named in new[] { registered.Name, string.Empty })
        {
            var inCi = Sabotage.Decide(named, "true", Registry);
            Assert.Null(inCi.Active);
            Assert.Contains($"{Sabotage.Variable} is set ('{named}'), and so is CI", inCi.Refusal, StringComparison.Ordinal);
        }

        var unknown = Sabotage.Decide("t0-no-such-sabotage", null, Registry);
        Assert.Null(unknown.Active);
        Assert.Contains("'t0-no-such-sabotage', which is no sabotage of this suite", unknown.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row of the record's index: the sabotage, its test, where it acts, the fingerprint of the test file its red was
    /// taken on (- for a held one), and its result.
    /// </summary>
    [GeneratedRegex(@"^\| `(?<name>[a-z0-9.-]+)` \| [^|\n]* \| [^|\n]* \| (?:`(?<fingerprint>[0-9a-f]{12})`|-) \| (?<result>[^|\n]+) \|[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex RecordRow();

    private static async Task<string> GitAsync(string repository, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git", ["-C", repository, .. arguments])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var git = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var output = git.StandardOutput.ReadToEndAsync();
        var errors = git.StandardError.ReadToEndAsync();
        await git.WaitForExitAsync();
        return git.ExitCode == 0
            ? await output
            : throw new InvalidOperationException($"git {string.Join(" ", arguments)} failed in {repository}: {await errors}");
    }
}
