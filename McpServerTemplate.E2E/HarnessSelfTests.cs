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
    + "record, the held decisions, what constructs a claim's failure, how an exhausted address pool is named, how every "
    + "container joins the run's network, how a run's liveness and a dead run are judged, when an OAuth flow's failure is the "
    + "environment's, and how a witness is read by address: they start no container and send nothing to the image, so there "
    + "is no fixture input, container environment or network for a sabotage to act on, and no claim about the image for it to "
    + "break. Their source names nothing that starts one (checked here).")]
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
    ///
    /// contract-005 · G-17 round 1 (HAR-R1-03) — "as the runner reports it" is the runner's own list: `dotnet test
    /// --list-tests` of this assembly, compared with the registry test method by test method. The registry reads the suite
    /// by reflection, which missed shapes the runner runs — a static fact, a fact inherited from a base class — so such a
    /// test passed here with no sabotage, and only the local script's count noticed. And [NotEndToEnd] exempts this class
    /// alone: on any other class it failed nothing, and nothing checked the class started no container.
    ///
    /// contract-005 · G-17 round 2 (HAR-R2-05) — both ways out closed. The runner's list is read in every namespace, and every
    /// test on it is this class's own or one the registry read, so a static or inherited fact anywhere in the assembly fails
    /// here. And this class holds no end-to-end test: its own source names nothing that starts the environment, a server, a
    /// class's fixture or a container (<see cref="StartsAContainer"/>), which the words "start no container" only said.
    /// </summary>
    [Fact]
    public async Task Every_end_to_end_test_has_a_named_sabotage()
    {
        var assembly = typeof(HarnessSelfTests).Assembly;
        var tests = Sabotage.EndToEndTests(assembly);
        var entries = Sabotage.All;

        // Positive controls: the reading found the suite's tests, a theory's rows among them, and not this class's.
        Assert.Contains(tests, t => t.Class == typeof(WalkingSkeletonTests));
        Assert.Contains(tests, t => t.Row is ["no-allowed-hosts", ..]);
        Assert.DoesNotContain(tests, t => t.Class == typeof(HarnessSelfTests));

        var problems = Sabotage.Problems(tests, entries).ToList();

        // [NotEndToEnd] is this class's, and no other's.
        problems.AddRange(Sabotage.NotEndToEnd(assembly)
            .Where(n => n.Class != typeof(HarnessSelfTests))
            .Select(n => $"{n.Class.Name} is marked [NotEndToEnd], which only {nameof(HarnessSelfTests)} may be: its tests would have no sabotage."));

        // The runner's own list, in every namespace, method by method, against what the registry read and this class's own tests.
        var listed = (await RunnerListAsync())
            .GroupBy(t => t.Split('(')[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var read = tests
            .GroupBy(t => $"{t.Class.FullName}.{t.Method.Name}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var own = typeof(HarnessSelfTests).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.IsDefined(typeof(FactAttribute), inherit: false))
            .Select(m => $"{typeof(HarnessSelfTests).FullName}.{m.Name}")
            .ToHashSet(StringComparer.Ordinal);

        // Positive controls: the runner listed the suite, rows included, and this class's own tests, this one among them.
        Assert.Contains($"{typeof(WalkingSkeletonTests).FullName}.{nameof(WalkingSkeletonTests.T1_a_keycloak_token_lists_tools_through_the_front)}", listed.Keys);
        Assert.Contains($"{typeof(HarnessSelfTests).FullName}.{nameof(Every_end_to_end_test_has_a_named_sabotage)}", listed.Keys);

        problems.AddRange(listed
            .Where(l => !own.Contains(l.Key) && read.GetValueOrDefault(l.Key) != l.Value)
            .Select(l => read.TryGetValue(l.Key, out var n)
                ? $"{l.Key}: the runner lists {l.Value} tests, and the registry read {n}."
                : $"{l.Key}: the runner lists {l.Value} test{(l.Value == 1 ? string.Empty : "s")}, and the registry read none, so none has a sabotage: "
                    + "a static fact, a fact declared on a base class — in any namespace — or a class marked [NotEndToEnd] reads so."));
        problems.AddRange(read
            .Where(r => !listed.ContainsKey(r.Key))
            .Select(r => $"{r.Key}: the registry read {r.Value} tests, and the runner lists none."));

        // contract-005 · G-17 round 2 (HAR-R2-05) — the exempt class starts nothing: its own source names nothing that would.
        var exempt = File.ReadAllText(Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E", $"{nameof(HarnessSelfTests)}.cs"));
        Assert.Contains("class HarnessSelfTests", exempt, StringComparison.Ordinal);
        problems.AddRange(StartsAContainer().Matches(exempt).Select(m => m.Value).Distinct(StringComparer.Ordinal)
            .Select(n => $"{nameof(HarnessSelfTests)}.cs names {n}, which starts the environment, a server, a class's fixture or a container: "
                + "the one class exempt from sabotages holds no end-to-end test."));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var directory = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "TestResults", "sabotage");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "registry.tsv"), Sabotage.Listing(entries));
        File.WriteAllText(
            Path.Combine(directory, "not-end-to-end.tsv"),
            string.Concat(Sabotage.NotEndToEnd(assembly).Select(n => $"{n.Class.Name}\t{n.Tests}\t{n.Reason}\n")));
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (HAR-R1-03) — a skipped end-to-end test is held, and runs no sabotage, only while it waits
    /// on a decision McpServerTemplate.E2E/HELD.md names: its Skip reason says "held: {id}" of an entry there, and that entry
    /// names the test. Any skip counted as held before; now a skip that names no decision fails here, and so does an entry no
    /// skipped test names, which would outlive its decision.
    ///
    /// contract-005 · T-5 — the list may be empty, and is: the pioneer's decision of 2026-09-29 (contract-005's revision of
    /// that date, "1: A") released its one entry, T-5's tool row. The file and this check stay for the next held test.
    /// </summary>
    [Fact]
    public void Every_skipped_end_to_end_test_waits_on_a_decision_held_md_names()
    {
        var (text, held) = HeldFile();

        // Positive controls: the file read is the list — its table's head is there — and the list's reader reads a row written
        // as the file writes one, so an empty list is an empty file, not a reader that reads nothing.
        Assert.Contains("| Id | Test | Waits on | Held since |", text, StringComparison.Ordinal);
        Assert.Equal(
            ["t0-example"],
            Sabotage.HeldDecisions("| `t0-example` | `TrustDomainTests.T5_example(\"tool\")` | a decision | 2026-09-29 |\n").Select(h => h.Id));

        var problems = HeldProblems(held).ToList();
        problems.AddRange(held
            .Where(h => !Sabotage.All.Any(e => e.Held is { } skip && Sabotage.HeldIdIn(skip) == h.Id))
            .Select(h => $"{Sabotage.HeldFile} holds '{h.Id}' ({h.Test}), and no skipped test names it: remove the entry with its Skip."));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// contract-005 · T-12 — and each has its recorded red, kept with its message in SABOTAGE-RECORD.md: a red on the
    /// assertion that carries the test's claim, taken on the test's file as it is now (<see cref="Sabotage.Fingerprint"/>),
    /// or, for a test skipped by decision, held. A sabotage added without its red, one whose test changed since its red was
    /// taken, or a record naming one the suite no longer has, fails here until scripts/e2e-sabotage.sh is run again — which
    /// retakes those, and only those.
    ///
    /// contract-005 · G-17 round 1 (HAR-R1-01, HAR-R1-02) — what this check proves, on every push, is coverage and freshness:
    /// every sabotage has an entry, and each entry's red was taken on its test's file as that file is now. It reads each
    /// entry's own text too, not only its row: the entry is there, its result line is the row's, its fingerprint is the
    /// row's, and its message is kept — an entry's message deleted, or its row edited alone, fails here. It cannot prove a red
    /// was taken: a fingerprint anyone can compute is no proof of a run, and CI refuses sabotages on every run. The full
    /// retake, run by hand (scripts/e2e-sabotage.sh --all), proves the reds: it takes every one afresh, each beside its
    /// control run. contract-005 · the pioneer's decision of 2026-09-29 ("3. B") removed the scheduled retake that ran it in CI.
    /// </summary>
    [Fact]
    public void Every_named_sabotage_has_a_recorded_red_on_its_claim()
    {
        var path = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E", Sabotage.RecordFile);
        Assert.True(File.Exists(path), $"{path} is missing: run scripts/e2e-sabotage.sh, which writes it.");
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var recorded = RecordRow().Matches(text)
            .ToDictionary(
                m => m.Groups["name"].Value,
                m => (Fingerprint: m.Groups["fingerprint"].Success ? m.Groups["fingerprint"].Value : null, Result: m.Groups["result"].Value.Trim()),
                StringComparer.Ordinal);
        var blocks = RecordBlocks(text);

        // Positive control: the record was read as one, its rows and its entries.
        Assert.NotEmpty(recorded);
        Assert.NotEmpty(blocks);

        var (_, held) = HeldFile();
        var problems = new List<string>();
        foreach (var entry in Sabotage.All)
        {
            if (!recorded.TryGetValue(entry.Name, out var row))
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): not in the record");
            }
            else if (!blocks.TryGetValue(entry.Name, out var block))
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): in the record's table, and its entry is gone");
            }
            else if (entry.Held is { } skip)
            {
                if (!row.Result.StartsWith("held", StringComparison.Ordinal) || !block.Contains("\n- Held: ", StringComparison.Ordinal))
                {
                    problems.Add($"{entry.Name} ({entry.Test.Name}): held, and recorded as '{row.Result}'");
                }
                else if (Sabotage.HeldIdIn(skip) is not { } id || held.All(h => h.Id != id))
                {
                    problems.Add($"{entry.Name} ({entry.Test.Name}): recorded as held, and its Skip names no decision {Sabotage.HeldFile} holds");
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
            else if (!block.Contains($"\n- Result: red on its claim, with `{typeof(ClaimException).FullName}`", StringComparison.Ordinal))
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): its row says red on its claim, and its entry's result line does not");
            }
            else if (!block.Contains($"; test file `{row.Fingerprint}`", StringComparison.Ordinal))
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): its row's test file is {row.Fingerprint}, and its entry's is not");
            }
            else if (KeptMessage().Match(block) is not { Success: true } kept || string.IsNullOrWhiteSpace(kept.Groups["message"].Value))
            {
                problems.Add($"{entry.Name} ({entry.Test.Name}): its entry keeps no message");
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
    /// names no sabotage of the suite is refused anywhere; a registered one, named outside CI, is the one applied. A control
    /// run (G-17 round 1, HAR-R1-07) is refused in CI the same way, runs only tests that run, and never with a sabotage.
    ///
    /// contract-005 · the pioneer's decision of 2026-09-29 (contract-005's revision of that date, "3. B") — in CI, on every
    /// run: G-17 round 1 (HAR-R1-01) had admitted one CI run, the scheduled retake, on its own workflow's schedule or by hand,
    /// and this test held both sides of that line. The retake is gone, the harness reads no workflow or event any more, and CI
    /// set is refused whatever started the run. The full retake is run by hand (scripts/e2e-sabotage.sh --all).
    /// </summary>
    [Fact]
    public void A_sabotage_is_refused_in_ci_and_one_the_suite_does_not_have_is_refused_anywhere()
    {
        IReadOnlyList<Sabotage.Entry> Registry() => Sabotage.All;
        var registered = Sabotage.All.First(e => e.Held is null);
        var other = Sabotage.All.First(e => e.Held is null && e.Name != registered.Name);

        // A held test's sabotage, made here: HELD.md holds no test today (T-5's tool row was released on 2026-09-29), and a
        // control run must still refuse one.
        var heldOne = registered with { Name = "t0-held-example", Declared = "t0-held-example", Test = registered.Test with { Skip = "held: t0-example" } };
        IReadOnlyList<Sabotage.Entry> WithHeld() => [.. Sabotage.All, heldOne];

        // Positive controls: none named, none applied and nothing refused; a registered one named outside CI, applied.
        var none = Sabotage.Decide(new(null, null, null), Registry);
        Assert.Null(none.Active);
        Assert.Empty(none.Controlled);
        Assert.Null(none.Refusal);
        Assert.Same(registered, Sabotage.Decide(new(registered.Name, null, null), Registry).Active);

        // In CI, on every run, even named empty and even with CI set empty.
        foreach (var (named, ci) in new[] { (registered.Name, "true"), (string.Empty, "true"), (registered.Name, string.Empty) })
        {
            var inCi = Sabotage.Decide(new(named, null, ci), Registry);
            Assert.Null(inCi.Active);
            Assert.Contains($"{Sabotage.Variable} is set ('{named}'), and so is CI", inCi.Refusal, StringComparison.Ordinal);
            Assert.Contains("never in CI, on any run", inCi.Refusal, StringComparison.Ordinal);
        }

        var unknown = Sabotage.Decide(new("t0-no-such-sabotage", null, null), Registry);
        Assert.Null(unknown.Active);
        Assert.Contains("'t0-no-such-sabotage', which is no sabotage of this suite", unknown.Refusal, StringComparison.Ordinal);

        // A control run: the named sabotages' tests, none applied; refused in CI, for a name that is no sabotage or whose test
        // is held, and with a sabotage beside it.
        var control = Sabotage.Decide(new(null, $"{registered.Name},{other.Name}", null), Registry);
        Assert.Null(control.Refusal);
        Assert.Null(control.Active);
        Assert.Equal(new[] { registered.Name, other.Name }.Order(StringComparer.Ordinal), control.Controlled.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Contains(
            $"{Sabotage.ControlVariable} is set ('{registered.Name}'), and so is CI",
            Sabotage.Decide(new(null, registered.Name, "true"), Registry).Refusal,
            StringComparison.Ordinal);
        Assert.Contains("'t0-no-such-sabotage'", Sabotage.Decide(new(null, "t0-no-such-sabotage", null), Registry).Refusal, StringComparison.Ordinal);
        Assert.Null(Sabotage.Decide(new(null, registered.Name, null), WithHeld).Refusal);
        Assert.Contains($"'{heldOne.Name}'", Sabotage.Decide(new(null, heldOne.Name, null), WithHeld).Refusal, StringComparison.Ordinal);
        Assert.Contains("are both set", Sabotage.Decide(new(registered.Name, other.Name, null), Registry).Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · UC-6 — the pioneer's decision of 2026-09-29 (contract-005's revision of that date, "2. yes, cut it and
    /// mock any calls"): every container the harness starts joins the run's network one way, E2ENetwork.OnTheRunNetwork, which
    /// gives it its own loopback address as its only name server and no search domain, so no container of a run can resolve a
    /// real name. Read from the suite's own source: no file but Harness/E2ENetwork.cs joins a container to a network
    /// (.WithNetwork, .WithNetworkMode), and a container the engine is asked to create directly has no network at all
    /// (NetworkMode "none") — ShippedImageContentsTests', which are created, read and never started. What the engine then gave
    /// each container is checked where the containers run: at the environment's start ("environment: no real names") and in
    /// T-6 (UpstreamNamesTests).
    ///
    /// contract-005 · G-17 round 2 (HAR-R2-08) — and as each starts: E2ENetwork.OnTheRunNetwork makes the builder, and has the
    /// container's own record checked the moment the engine starts it. So no file but Harness/E2ENetwork.cs makes a container
    /// builder (a builder given no network runs on the default bridge, with the host's resolver) or sets a startup callback
    /// (a later one would replace the check), and no file but Harness/E2EEnvironment.cs joins a running container to a network
    /// (its RejoinAsync, which moves a container already checked from name to name).
    /// </summary>
    [Fact]
    public void Every_container_joins_the_run_network_the_one_way_that_resolves_no_real_name()
    {
        var suite = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E");
        var sources = Directory.EnumerateFiles(suite, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(d => d is "bin" or "obj"))
            .ToDictionary(f => Path.GetRelativePath(suite, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
        string[] Matching(Regex pattern) => [.. sources.Where(s => pattern.IsMatch(s.Value)).Select(s => s.Key).Order(StringComparer.Ordinal)];
        var joining = Matching(JoinsANetwork());
        var creating = Matching(CreatesAContainer());
        var building = Matching(MakesABuilder());
        var connecting = Matching(ConnectsARunningContainer());
        var calling = Matching(SetsAStartupCallback());

        // T-13's digest check (the fast suite's RepositoryInvariantsTests) reads every image a builder is given as a literal, and
        // every *Image constant; a builder is made only by the one way now, so an image given it as a literal would pass that
        // check unread. An image is a named constant, or one the run builds.
        var literal = Matching(GivesTheOneWayALiteralImage());

        // Positive controls: the reading sees the one way's own join, builder and callback, the one container made through the
        // engine directly, and the one rejoin.
        Assert.Contains("Harness/E2ENetwork.cs", joining);
        Assert.Contains("ShippedImageContentsTests.cs", creating);
        Assert.Contains("Harness/E2ENetwork.cs", building);
        Assert.Contains("Harness/E2ENetwork.cs", calling);
        Assert.Contains("Harness/E2EEnvironment.cs", connecting);

        var theOneWay = $"{nameof(E2ENetwork)}.{nameof(E2ENetwork.OnTheRunNetwork)}";
        var problems = joining.Where(f => f != "Harness/E2ENetwork.cs")
            .Select(f => $"{f} joins a container to a network itself; every container joins the run's network through {theOneWay}, which "
                + "gives it no way to resolve a real name.")
            .Concat(creating.Where(f => !WithNoNetwork().IsMatch(sources[f]))
                .Select(f => $"{f} has the engine create a container with a network; only one with none (NetworkMode \"none\") may be made that way."))
            .Concat(building.Where(f => f != "Harness/E2ENetwork.cs")
                .Select(f => $"{f} makes a container builder itself; every container of the run is made through {theOneWay}, which joins it "
                    + "to the run's network and checks it as it starts."))
            .Concat(calling.Where(f => f != "Harness/E2ENetwork.cs")
                .Select(f => $"{f} sets a container's startup callback, which would replace the check {theOneWay} sets."))
            .Concat(connecting.Where(f => f != "Harness/E2EEnvironment.cs")
                .Select(f => $"{f} joins a running container to a network; only E2EEnvironment's RejoinAsync may, with a container {theOneWay} made."))
            .Concat(literal
                .Select(f => $"{f} gives {theOneWay} an image as a literal, which T-13's digest check does not read: name it as a const "
                    + "string …Image, which the check reads, pinned by digest."))
            .ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (HAR-R1-04) — only <see cref="Claim"/> constructs a <see cref="ClaimException"/>: the record,
    /// the script and Claim itself say a red on a claim is one "only Claim throws", and a test that built one of its own
    /// around any exception had its environment faults recorded as reds on its claim. Read from the suite's own source.
    /// </summary>
    [Fact]
    public void Only_Claim_constructs_a_ClaimException()
    {
        var suite = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E");
        var sources = Directory.EnumerateFiles(suite, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(d => d is "bin" or "obj"))
            .ToArray();
        var constructing = sources
            .Where(f => ConstructsClaimException().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(suite, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Positive control: the reading sees Claim's own.
        Assert.Contains("Harness/Claim.cs", constructing);
        Assert.True(
            constructing.SequenceEqual(["Harness/Claim.cs"]),
            $"These files construct a ClaimException, which only Claim may: {string.Join(", ", constructing.Where(f => f != "Harness/Claim.cs"))}. "
            + $"State a claim through Claim.True or Claim.Holds, and fold into it only the server's answer (Claim.{nameof(Claim.IsTheServersAnswer)}).");
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (FLA-R1-02) — Docker refusing to start a container because the run's network has no address
    /// left is the environment's fault, named with the pool, in whichever phase it meets — a server's own start, measured as
    /// the product's, included; any other failure of such a phase passes as it is. Given as the engine words it, through the
    /// client the harness uses.
    /// </summary>
    [Fact]
    public async Task An_address_pool_with_nothing_left_is_named_the_environments_fault()
    {
        var timings = new Timings();
        var exhausted = new Docker.DotNet.DockerApiException(
            System.Net.HttpStatusCode.InternalServerError,
            """{"message":"failed to set up container networking: no available IPv4 addresses on this network's address pools: mcp-e2e-x (0123)"}""");

        // Positive control: another failure of the engine, in the same phase, passes as it is — the product's to explain.
        var other = new Docker.DotNet.DockerApiException(System.Net.HttpStatusCode.NotFound, """{"message":"No such image: mcp-e2e-server:x"}""");
        Assert.Same(other, await Assert.ThrowsAsync<Docker.DotNet.DockerApiException>(() => timings.MeasureAsync<bool>("x: server ready", () => throw other)));

        var fault = await Assert.ThrowsAsync<EnvironmentFaultException>(() => timings.MeasureAsync<bool>("x: server ready", () => throw exhausted));
        Assert.Equal("x: server ready", fault.Phase);
        Assert.Contains($"address pool exhausted: the run's network has no address left to give this container. Its dynamic range, {E2ENetwork.DynamicRange}, holds {E2ENetwork.DynamicAddresses}", fault.Message, StringComparison.Ordinal);
        Assert.Same(exhausted, fault.InnerException);

        // And wrapped, as a library may hand it on.
        var wrapped = new InvalidOperationException("the container did not start", exhausted);
        Assert.Equal("x: redis", (await Assert.ThrowsAsync<EnvironmentFaultException>(() => timings.MeasureEnvironmentAsync<bool>("x: redis", () => throw wrapped))).Phase);
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (FLA-R2-04) — a run directory this account cannot enter is judged live, however old: another
    /// account's, in a temporary directory accounts share (Linux's /tmp), whose mark this account cannot read. Judged by its
    /// unreadable mark as unmarked, and by its age as dead, its live run's containers and network were removed mid-run. Two
    /// directories made alike, unmarked and judged as ten minutes old; one closed to this account (mode 000 on Unix, a deny on
    /// Windows).
    /// </summary>
    [Fact]
    public void A_run_directory_this_account_cannot_enter_is_judged_live()
    {
        var parent = Directory.CreateTempSubdirectory("e2e-liveness-").FullName;
        var open = Directory.CreateDirectory(Path.Combine(parent, "mcp-e2e-open")).FullName;
        var closed = Directory.CreateDirectory(Path.Combine(parent, "mcp-e2e-closed")).FullName;
        var tenMinutesOn = DateTime.UtcNow.AddMinutes(10);
        try
        {
            // Positive control: an unmarked directory ten minutes old, which this account can enter, is a dead run's.
            Assert.False(E2EEnvironment.RunDirectories.OwnerIsAlive(open, tenMinutesOn), $"{open}, unmarked and ten minutes old, was judged live.");

            Close(closed);

            // Positive control: closed, it cannot be entered — this is not an account that enters everything.
            Assert.False(E2EEnvironment.RunDirectories.CanEnter(closed), $"{closed} was closed to this account and could still be entered: run as an account that cannot enter what it closes.");
            Assert.True(E2EEnvironment.RunDirectories.OwnerIsAlive(closed, tenMinutesOn), $"{closed}, which this account cannot enter, was judged dead at ten minutes old.");
        }
        finally
        {
            Reopen(closed);
            Directory.Delete(parent, recursive: true);
        }

        static void Close(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectoryInfo(directory).GetAccessControl();
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                    System.Security.AccessControl.FileSystemRights.ListDirectory,
                    System.Security.AccessControl.AccessControlType.Deny));
                new DirectoryInfo(directory).SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(directory, UnixFileMode.None);
            }
        }

        static void Reopen(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectoryInfo(directory).GetAccessControl();
                security.RemoveAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                    System.Security.AccessControl.FileSystemRights.ListDirectory,
                    System.Security.AccessControl.AccessControlType.Deny));
                new DirectoryInfo(directory).SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-06) — the next run's sweep removes a dead run of this owner (its machine, operating
    /// system, account and temporary directory, on its owner label), and no other: a run of another owner, or of no owner, whose
    /// directory is not here, may be alive where this process does not look. Judged on labels as the engine gives them, for runs
    /// no directory here names.
    /// </summary>
    [Fact]
    public void A_run_is_swept_as_dead_only_when_it_is_this_owners()
    {
        var thisRun = $"t0-this-{Guid.NewGuid():N}";
        Dictionary<string, string> Labels(string run, string? owner) => owner is null
            ? new() { [E2ENetwork.RunLabel] = run }
            : new() { [E2ENetwork.RunLabel] = run, [E2ENetwork.OwnerLabel] = owner };

        // Positive control: a run of this owner, whose directory is gone, is dead.
        Assert.True(E2EEnvironment.IsDeadRun(Labels($"t0-gone-{Guid.NewGuid():N}", E2ENetwork.ThisOwner), thisRun, out _), "a run of this owner with no directory was not judged dead.");

        Assert.False(E2EEnvironment.IsDeadRun(Labels(thisRun, E2ENetwork.ThisOwner), thisRun, out _), "this run was judged dead by itself.");
        Assert.False(
            E2EEnvironment.IsDeadRun(Labels($"t0-elsewhere-{Guid.NewGuid():N}", E2ENetwork.ThisOwner.Replace("temp=", "temp=/elsewhere", StringComparison.Ordinal)), thisRun, out _),
            "a run of another temporary directory — another owner, whose run directory is not where this process looks — was judged dead.");
        Assert.False(E2EEnvironment.IsDeadRun(Labels($"t0-unowned-{Guid.NewGuid():N}", owner: null), thisRun, out _), "a run of no owner, with no directory here, was judged dead.");
        Assert.False(E2EEnvironment.IsDeadRun(new Dictionary<string, string>(), thisRun, out _), "a container of no run was judged a dead run's.");
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-03) — an OAuth flow's failure is the server's answer only if every name of the
    /// environment it asked answered. The SDK's OAuth client throws an McpException of its own for a connection or TLS failure at
    /// the authorization server, with no inner exception, and a claim took it for the server's; the name map's log tells the two
    /// apart. Given as the log records requests: a mapped name that gave no answer is the environment's fault, naming the name; a
    /// name the map refused is where the flow went, a claim's to judge; a flow whose every request was answered is the server's.
    /// And every file that makes the SDK's OAuth client reads that log before it folds a failure into a claim.
    /// </summary>
    [Fact]
    public void An_oauth_flows_failure_where_a_name_of_the_environment_gave_no_answer_is_the_environments_fault()
    {
        var failure = new ModelContextProtocol.McpException("Failed to find .well-known/openid-configuration or .well-known/oauth-authorization-server metadata");

        // Positive controls: every request answered, or the one unanswered to a name the map refused, is no fault of the environment.
        var answered = new NameMapLog();
        answered.Request(HttpMethod.Post, new Uri("https://mcp.e2e.test/mcp"), 401);
        answered.Request(HttpMethod.Get, new Uri("https://idp-a.e2e.test/.well-known/oauth-authorization-server"), 200);
        answered.ThrowIfAMappedNameGaveNoAnswer("x: flow", failure);

        var refused = new NameMapLog();
        refused.Request(HttpMethod.Post, new Uri("https://mcp.e2e.test/mcp"), 401);
        refused.Refuse(new System.Net.DnsEndPoint("idp-a.example.com", 443));
        refused.Request(HttpMethod.Get, new Uri("https://idp-a.example.com/.well-known/oauth-authorization-server"), 0);
        Assert.Empty(refused.Unanswered);
        refused.ThrowIfAMappedNameGaveNoAnswer("x: flow", failure);

        var unanswered = new NameMapLog();
        unanswered.Request(HttpMethod.Post, new Uri("https://mcp.e2e.test/mcp"), 401);
        unanswered.Request(HttpMethod.Get, new Uri("https://idp-a.e2e.test/.well-known/oauth-authorization-server"), 0);
        var fault = Assert.Throws<EnvironmentFaultException>(() => unanswered.ThrowIfAMappedNameGaveNoAnswer("x: flow", failure));
        Assert.Equal("x: flow", fault.Phase);
        Assert.Contains("idp-a.e2e.test:443", fault.Message, StringComparison.Ordinal);
        Assert.Same(failure, fault.InnerException);

        // Wherever the SDK's OAuth client is made, the log is read.
        var suite = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E");
        var making = Directory.EnumerateFiles(suite, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .Where(s => MakesTheOAuthClient().IsMatch(s.Text))
            .ToArray();
        Assert.Contains(making, s => s.File == "StandardClientTests.cs");
        var unread = making.Where(s => !ReadsTheNameMapLog().IsMatch(s.Text)).Select(s => s.File).ToArray();
        Assert.True(
            unread.Length == 0,
            $"these files make the SDK's OAuth client and never read the name map's log before folding its failure into a claim: {string.Join(", ", unread)}.");
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-02) — every test reads a witness by address only through its server's
    /// <see cref="RequestSource"/>, bounded by when the server started: no test file asks for its server's bare address or start.
    /// Read by address alone, the fake's journal and the stand-in's record counted an earlier server's requests at the same
    /// address as this one's.
    /// </summary>
    [Fact]
    public void Every_witness_read_by_address_is_bounded_by_its_servers_start()
    {
        var suite = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E");
        var tests = Directory.EnumerateFiles(suite, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .ToArray();
        var harness = File.ReadAllText(Path.Combine(suite, "Harness", "ServerUnderTest.cs"));

        // Positive controls: the reading sees the tests, and the harness's own use of what they may not use.
        Assert.Contains(tests, t => t.File == "UpstreamTests.cs" && t.Text.Contains(nameof(ServerUnderTest.RequestSourceAsync), StringComparison.Ordinal));
        Assert.Matches(BareAddressOrStart(), harness);

        var bare = tests.Where(t => BareAddressOrStart().IsMatch(t.Text)).Select(t => t.File).ToArray();
        Assert.True(
            bare.Length == 0,
            $"these test files ask for their server's bare address or start, and read a witness by it: {string.Join(", ", bare)}. Read one "
            + $"through the server's {nameof(ServerUnderTest.RequestSourceAsync)}, which bounds it by when the server started.");
    }

    /// <summary>McpServerTemplate.E2E/HELD.md as it stands, and the held decisions it lists.</summary>
    private static (string Text, IReadOnlyList<Sabotage.HeldDecision> Held) HeldFile()
    {
        var path = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E", Sabotage.HeldFile);
        Assert.True(File.Exists(path), $"{path} is missing: it lists the decisions a skipped end-to-end test may wait on.");
        var text = File.ReadAllText(path);
        return (text, Sabotage.HeldDecisions(text));
    }

    /// <summary>Every skipped end-to-end test whose Skip names no held decision, or one whose entry does not name the test.</summary>
    private static IEnumerable<string> HeldProblems(IReadOnlyList<Sabotage.HeldDecision> held)
    {
        foreach (var test in Sabotage.EndToEndTests(typeof(HarnessSelfTests).Assembly).Where(t => t.Skip is not null))
        {
            var id = Sabotage.HeldIdIn(test.Skip!);
            var decision = held.FirstOrDefault(h => h.Id == id);
            if (decision is null)
            {
                yield return $"{test.Name} is skipped, and its Skip names no decision {Sabotage.HeldFile} holds ('held: {{id}}'): '{test.Skip}'.";
            }
            else if (!decision.Test.Contains($"{test.Class.Name}.{test.Method.Name}", StringComparison.Ordinal))
            {
                yield return $"{test.Name} names '{decision.Id}', whose entry in {Sabotage.HeldFile} holds another test: {decision.Test}.";
            }
        }
    }

    /// <summary>
    /// The runner's own list of this assembly's tests: `dotnet test --list-tests`, each fact and each theory row it lists, as
    /// Namespace.Class.Method, with a row's arguments after it. Run in English, which is how its lines are read.
    ///
    /// contract-005 · G-17 round 2 (HAR-R2-05) — every test it lists, in whatever namespace: each indented line after its "The
    /// following Tests are available:" line. Only lines in the suite's own namespace were kept, so a static fact, or one a class
    /// inherits from an abstract base, declared in any other namespace was listed and run, and neither this list nor the registry
    /// saw it (a probe on a scratch clone ran one, and this check passed).
    /// </summary>
    private static async Task<IReadOnlyList<string>> RunnerListAsync()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", ["test", typeof(HarnessSelfTests).Assembly.Location, "--list-tests"])
        {
            WorkingDirectory = E2EEnvironment.FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["VSLANG"] = "1033";

        using var dotnet = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = dotnet.StandardOutput.ReadToEndAsync();
        var errors = dotnet.StandardError.ReadToEndAsync();
        await dotnet.WaitForExitAsync();
        if (dotnet.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet test --list-tests failed ({dotnet.ExitCode}): {await errors}{await output}");
        }

        var lines = (await output).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var header = Array.FindIndex(lines, l => l.Trim() == "The following Tests are available:");
        return header < 0
            ? throw new InvalidOperationException($"dotnet test --list-tests printed no 'The following Tests are available:' line: {await output}")
            : [.. lines.Skip(header + 1).Where(l => l.StartsWith("    ", StringComparison.Ordinal) && l.Trim().Length > 0).Select(l => l.Trim())];
    }

    /// <summary>
    /// The record's entries, each by its sabotage's name: the text from its heading to the next, a heading inside a message's
    /// fence being part of the message, as scripts/e2e-sabotage.sh reads them.
    /// </summary>
    private static Dictionary<string, string> RecordBlocks(string record)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var block = new System.Text.StringBuilder();
        var fenced = false;
        foreach (var line in record.Split('\n'))
        {
            if (line.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }

            if (!fenced && EntryHeading().Match(line) is { Success: true } heading)
            {
                if (name is not null)
                {
                    blocks[name] = block.ToString();
                }

                name = heading.Groups["name"].Value;
                block.Clear();
            }

            if (name is not null)
            {
                block.Append(line).Append('\n');
            }
        }

        if (name is not null)
        {
            blocks[name] = block.ToString();
        }

        return blocks;
    }

    /// <summary>
    /// A row of the record's index: the sabotage, its test, where it acts, the fingerprint of the test file its red was
    /// taken on (- for a held one), and its result.
    /// </summary>
    [GeneratedRegex(@"^\| `(?<name>[a-z0-9.-]+)` \| [^|\n]* \| [^|\n]* \| (?:`(?<fingerprint>[0-9a-f]{12})`|-) \| (?<result>[^|\n]+) \|[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex RecordRow();

    /// <summary>An entry's heading in the record.</summary>
    [GeneratedRegex(@"^### `(?<name>[a-z0-9.-]+)`$")]
    private static partial Regex EntryHeading();

    /// <summary>An entry's kept message: its ~~~text fence and what is inside it.</summary>
    [GeneratedRegex(@"\n~~~text\n(?<message>.*?)\n~~~", RegexOptions.Singleline)]
    private static partial Regex KeptMessage();

    /// <summary>
    /// A ClaimException constructed: a new-expression naming the type, or a target-typed new given to one. Worded without
    /// the construction itself, so this comment is not a match.
    /// </summary>
    [GeneratedRegex(@"\bnew\s+ClaimException\s*\(|\bClaimException\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex ConstructsClaimException();

    /// <summary>
    /// contract-005 · the pioneer's decision of 2026-09-29 — a container builder given a network, or a network mode. Written
    /// with character classes, so the pattern's own text is not a match.
    /// </summary>
    [GeneratedRegex(@"[.]WithNetwork(?:Mode)?\s*[(]")]
    private static partial Regex JoinsANetwork();

    /// <summary>contract-005 · the pioneer's decision of 2026-09-29 — the engine asked to create a container directly.</summary>
    [GeneratedRegex(@"\bCreateContainerAsync\s*[(]")]
    private static partial Regex CreatesAContainer();

    /// <summary>contract-005 · the pioneer's decision of 2026-09-29 — a container created with no network at all.</summary>
    [GeneratedRegex(@"NetworkMode\s*=\s*""none""")]
    private static partial Regex WithNoNetwork();

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-08) — a container builder made: a new-expression naming the type, or a target-typed new
    /// given to one. Written with character classes, so the pattern's own text is not a match.
    /// </summary>
    [GeneratedRegex(@"\bnew\s+Container[B]uilder\s*[(<]|\bContainer[B]uilder\s+\w+\s*=\s*new\s*[(]")]
    private static partial Regex MakesABuilder();

    /// <summary>contract-005 · G-17 round 2 (HAR-R2-08) — a running container joined to a network through the engine.</summary>
    [GeneratedRegex(@"\bConnect[N]etworkAsync\s*[(]")]
    private static partial Regex ConnectsARunningContainer();

    /// <summary>contract-005 · G-17 round 2 (HAR-R2-08) — a container's startup callback set.</summary>
    [GeneratedRegex(@"[.]WithStartup[C]allback\s*[(]")]
    private static partial Regex SetsAStartupCallback();

    /// <summary>contract-005 · G-17 round 2 (HAR-R2-08) — the one way given its image as a string literal, of any form.</summary>
    [GeneratedRegex(@"\bOnTheRun[N]etwork\s*[(]\s*[$@]*""")]
    private static partial Regex GivesTheOneWayALiteralImage();

    /// <summary>contract-005 · G-17 round 2 (HAR-R2-03) — the SDK's OAuth client made, as the harness makes it. Written so the pattern's own text is not a match.</summary>
    [GeneratedRegex(@"\bStandardClient[.]Transport\s*[(]")]
    private static partial Regex MakesTheOAuthClient();

    /// <summary>contract-005 · G-17 round 2 (HAR-R2-03) — the name map's log read before an OAuth flow's failure is folded into a claim.</summary>
    [GeneratedRegex(@"[.]ThrowIfAMapped[N]ameGaveNoAnswer\s*[(]")]
    private static partial Regex ReadsTheNameMapLog();

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-02) — a server's bare address or start asked for, by the harness's names for them.
    /// Written so the pattern's own text is not a match.
    /// </summary>
    [GeneratedRegex(@"\bNetwork[A]ddressAsync\b|\bStarted[A]tAsync\b")]
    private static partial Regex BareAddressOrStart();

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-05) — what starts the environment, a server, a class's fixture or a container, by the
    /// harness's and xUnit's names for it. Written so the pattern's own text is not a match.
    /// </summary>
    [GeneratedRegex(@"E2EEnvironment[.]GetAsync|\bStart(?:Server|up|DocsProfile|Redis)Async\b|\bServerFixture\b|\bI(?:Class|Collection)Fixture\b|\bContainerBuilder\b|\bCreateContainerAsync\b")]
    private static partial Regex StartsAContainer();

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
