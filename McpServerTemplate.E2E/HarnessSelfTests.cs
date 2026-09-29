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
    + "record, the held decisions, what constructs a claim's failure, and how an exhausted address pool is named: they start "
    + "no container and send nothing to the image, so there is no fixture input, container environment or network for a "
    + "sabotage to act on, and no claim about the image for it to break.")]
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

        // The runner's own list, method by method, against what the registry read.
        var listed = (await RunnerListAsync())
            .Where(t => !t.StartsWith($"{typeof(HarnessSelfTests).FullName}.", StringComparison.Ordinal))
            .GroupBy(t => t.Split('(')[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var read = tests
            .GroupBy(t => $"{t.Class.FullName}.{t.Method.Name}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Positive control: the runner listed the suite, rows included.
        Assert.Contains($"{typeof(WalkingSkeletonTests).FullName}.{nameof(WalkingSkeletonTests.T1_a_keycloak_token_lists_tools_through_the_front)}", listed.Keys);

        problems.AddRange(listed
            .Where(l => read.GetValueOrDefault(l.Key) != l.Value)
            .Select(l => read.TryGetValue(l.Key, out var n)
                ? $"{l.Key}: the runner lists {l.Value} tests, and the registry read {n}."
                : $"{l.Key}: the runner lists {l.Value} test{(l.Value == 1 ? string.Empty : "s")}, and the registry read none, so none has a sabotage: "
                    + "a static fact, a fact declared on a base class, or a class marked [NotEndToEnd] reads so."));
        problems.AddRange(read
            .Where(r => !listed.ContainsKey(r.Key))
            .Select(r => $"{r.Key}: the registry read {r.Value} tests, and the runner lists none."));

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
    /// </summary>
    [Fact]
    public void Every_skipped_end_to_end_test_waits_on_a_decision_held_md_names()
    {
        var held = HeldDecisions();

        // Positive control: the list was read, T-5's tool row among it.
        Assert.Contains(held, h => h.Id == "t5-tool-refusal-words");

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
    /// was taken: a fingerprint anyone can compute is no proof of a run, and CI's push runs refuse sabotages. The scheduled
    /// retake (<see cref="Sabotage.RetakeWorkflow"/>) proves the reds: it takes every one afresh on a clean checkout, and
    /// fails when one is not red on its claim or its verdict differs from the one committed here.
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

        var held = HeldDecisions();
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
    /// names no sabotage of the suite is refused anywhere; a registered one, named outside CI, is the one applied.
    ///
    /// contract-005 · G-17 round 1 — and in CI, the scheduled retake (HAR-R1-01) is the one run that may name one: on its own
    /// workflow, started on its schedule or by hand; a push or a pull request is refused whichever workflow it starts, and so
    /// is another workflow's schedule. A control run (HAR-R1-07) is refused and admitted the same way, runs only tests that
    /// run, and never with a sabotage.
    /// </summary>
    [Fact]
    public void A_sabotage_is_refused_in_ci_and_one_the_suite_does_not_have_is_refused_anywhere()
    {
        IReadOnlyList<Sabotage.Entry> Registry() => Sabotage.All;
        var registered = Sabotage.All.First(e => e.Held is null);
        var other = Sabotage.All.First(e => e.Held is null && e.Name != registered.Name);
        var heldOne = Sabotage.All.First(e => e.Held is not null);
        const string retake = $"owner/repo/{Sabotage.RetakeWorkflow}@refs/heads/main";

        // Positive controls: none named, none applied and nothing refused; a registered one named outside CI, applied; and
        // in the scheduled retake, on its schedule and by hand, applied.
        var none = Sabotage.Decide(new(null, null, null), Registry);
        Assert.Null(none.Active);
        Assert.Empty(none.Controlled);
        Assert.Null(none.Refusal);
        Assert.Same(registered, Sabotage.Decide(new(registered.Name, null, null), Registry).Active);
        foreach (var started in new[] { "schedule", "workflow_dispatch" })
        {
            var inRetake = Sabotage.Decide(new(registered.Name, null, "true", started, retake), Registry);
            Assert.Null(inRetake.Refusal);
            Assert.Same(registered, inRetake.Active);
        }

        foreach (var named in new[] { registered.Name, string.Empty })
        {
            var inCi = Sabotage.Decide(new(named, null, "true"), Registry);
            Assert.Null(inCi.Active);
            Assert.Contains($"{Sabotage.Variable} is set ('{named}'), and so is CI", inCi.Refusal, StringComparison.Ordinal);
        }

        // In CI, anything but the retake's own schedule or hand is refused: a push to the retake's workflow, another workflow's schedule.
        foreach (var (started, workflow) in new[] { ("push", retake), ("pull_request", retake), ("schedule", "owner/repo/.github/workflows/ci.yml@refs/heads/main") })
        {
            var refused = Sabotage.Decide(new(registered.Name, null, "true", started, workflow), Registry);
            Assert.Null(refused.Active);
            Assert.Contains("and so is CI", refused.Refusal, StringComparison.Ordinal);
        }

        var unknown = Sabotage.Decide(new("t0-no-such-sabotage", null, null), Registry);
        Assert.Null(unknown.Active);
        Assert.Contains("'t0-no-such-sabotage', which is no sabotage of this suite", unknown.Refusal, StringComparison.Ordinal);

        // A control run: the named sabotages' tests, none applied; refused in CI but for the retake, for a name that is no
        // sabotage or whose test is held, and with a sabotage beside it.
        var control = Sabotage.Decide(new(null, $"{registered.Name},{other.Name}", null), Registry);
        Assert.Null(control.Refusal);
        Assert.Null(control.Active);
        Assert.Equal(new[] { registered.Name, other.Name }.Order(StringComparer.Ordinal), control.Controlled.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Null(Sabotage.Decide(new(null, registered.Name, "true", "workflow_dispatch", retake), Registry).Refusal);
        Assert.Contains(
            $"{Sabotage.ControlVariable} is set ('{registered.Name}'), and so is CI",
            Sabotage.Decide(new(null, registered.Name, "true", "push", retake), Registry).Refusal,
            StringComparison.Ordinal);
        Assert.Contains("'t0-no-such-sabotage'", Sabotage.Decide(new(null, "t0-no-such-sabotage", null), Registry).Refusal, StringComparison.Ordinal);
        Assert.Contains($"'{heldOne.Name}'", Sabotage.Decide(new(null, heldOne.Name, null), Registry).Refusal, StringComparison.Ordinal);
        Assert.Contains("are both set", Sabotage.Decide(new(registered.Name, other.Name, null), Registry).Refusal, StringComparison.Ordinal);
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

    /// <summary>The held decisions, as McpServerTemplate.E2E/HELD.md lists them.</summary>
    private static IReadOnlyList<Sabotage.HeldDecision> HeldDecisions()
    {
        var path = Path.Combine(E2EEnvironment.FindRepositoryRoot(), "McpServerTemplate.E2E", Sabotage.HeldFile);
        Assert.True(File.Exists(path), $"{path} is missing: it lists the decisions a skipped end-to-end test may wait on.");
        return Sabotage.HeldDecisions(File.ReadAllText(path));
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
        return dotnet.ExitCode == 0
            ? [.. (await output).Split('\n').Where(l => l.StartsWith("    McpServerTemplate.E2E.", StringComparison.Ordinal)).Select(l => l.Trim())]
            : throw new InvalidOperationException($"dotnet test --list-tests failed ({dotnet.ExitCode}): {await errors}{await output}");
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

    /// <summary>A ClaimException constructed: new ClaimException(…), or new(…) given to one.</summary>
    [GeneratedRegex(@"\bnew\s+ClaimException\s*\(|\bClaimException\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex ConstructsClaimException();

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
