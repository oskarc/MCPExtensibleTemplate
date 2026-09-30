using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit.Sdk;

namespace McpServerTemplate.E2E.Harness;

/// <summary>Where a sabotage acts. contract-005 · G-11 allows three places, all in the end-to-end fixture.</summary>
public enum SabotageActs
{
    /// <summary>What the test sends or is given: a token, a request, a document, a signal, how long it waits.</summary>
    Inputs,

    /// <summary>A container's environment: a server's settings, what the container holds, whether it runs.</summary>
    ContainerEnvironment,

    /// <summary>The network: which container a name reaches, and by which route a request goes.</summary>
    Network,
}

/// <summary>
/// contract-005 · G-11, T-12 — a named sabotage, declared on the end-to-end test it targets: the one thing the fixture
/// weakens for that test when MCP_E2E_SABOTAGE names it (<see cref="Sabotage"/>). On a theory it is declared once and
/// named for each row, <c>{name}.{row}</c>, where the row is its leading arguments, as few as tell the rows apart; each
/// row's is a sabotage of its own, run on its own.
/// </summary>
/// <param name="name">Lower-case words joined by hyphens; unique in the suite.</param>
/// <param name="acts">Where it acts.</param>
/// <param name="weakens">What it does, in one sentence the record keeps.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class SabotageAttribute(string name, SabotageActs acts, string weakens) : Attribute
{
    public string Name { get; } = name;

    public SabotageActs Acts { get; } = acts;

    public string Weakens { get; } = weakens;
}

/// <summary>
/// contract-005 · T-12 — a test class whose tests are not end-to-end tests of the contract's claims, with the reason:
/// they have no sabotage, and the record names them as such. contract-005 · G-17 round 1 (HAR-R1-03) — allowed on the
/// harness's own self-tests alone (HarnessSelfTests, which start no container); HarnessSelfTests fails on any other class
/// that carries it, and on any test the runner lists that the registry does not cover.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NotEndToEndAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The sabotages, and the one a run applies.
///
/// contract-005 · G-11, T-12 — every end-to-end test has a named sabotage (<see cref="SabotageAttribute"/>) that acts
/// only in the end-to-end fixture — the test's inputs, a container's environment or the network — and weakens one
/// thing for that one test. It is applied when MCP_E2E_SABOTAGE names it, with no code edited: the place where it acts
/// asks <see cref="Applies"/>, which is true only in a run of that sabotage. Such a run runs the one test it targets and
/// nothing else, and records how it ended (<see cref="E2ETestFramework"/>); scripts/e2e-sabotage.sh runs every
/// sabotage in turn, checks each red is a <see cref="ClaimException"/>, and writes the record, SABOTAGE-RECORD.md.
///
/// CI refuses it, on every run: with CI set, a run that names a sabotage does not start (<see cref="Refusal"/>), and the e2e
/// job checks the variable is unset before it runs anything. The variable is the suite's own, and no product or test-host
/// file reads it (T-13). The per-push check proves that every test has a sabotage and that each recorded red was taken on its
/// test's file as it is; the full retake, run by hand (scripts/e2e-sabotage.sh --all), proves the reds. contract-005 · G-17
/// round 1 (HAR-R1-01, HAR-R1-02) added a scheduled retake in CI, the one CI run that admitted a sabotage; the pioneer's
/// decision of 2026-09-29 (contract-005's revision of that date, "3. B") removed it, and with it that admission.
///
/// contract-005 · G-17 round 1 (HAR-R1-07) — and a control run: <see cref="ControlVariable"/> names sabotages whose tests are
/// run with no sabotage at all, so scripts/e2e-sabotage.sh can require each test it retakes to pass without its sabotage
/// before a red with it counts, and record both.
/// </summary>
public static partial class Sabotage
{
    /// <summary>The variable that names the one sabotage a run applies.</summary>
    public const string Variable = "MCP_E2E_SABOTAGE";

    /// <summary>
    /// contract-005 · G-17 round 1 (HAR-R1-07) — the variable that names, comma-separated, the sabotages whose tests a control
    /// run runs: each with no sabotage applied, and no other test.
    /// </summary>
    public const string ControlVariable = "MCP_E2E_CONTROL";

    /// <summary>The record the sabotages' reds are kept in, under the end-to-end project.</summary>
    public const string RecordFile = "SABOTAGE-RECORD.md";

    /// <summary>
    /// contract-005 · G-17 round 1 (HAR-R1-03) — the held decisions, under the end-to-end project: a test skipped by decision
    /// counts as held only when its Skip reason names one of them ("held: {id}").
    /// </summary>
    public const string HeldFile = "HELD.md";

    /// <summary>
    /// What every startup refusal's sabotage does: the server the test starts is started without the misconfiguration it
    /// is refused for, so it comes up — the red its claim, exit 78 naming the cause, must see.
    /// </summary>
    public const string MisconfigurationLeftOut =
        "The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.";

    private static readonly Lazy<IReadOnlyList<Entry>> Registry = new(() => Build(EndToEndTests(typeof(Sabotage).Assembly)));

    private static readonly Lazy<Decision> State = new(() => Decide(
        new Run(
            Environment.GetEnvironmentVariable(Variable),
            Environment.GetEnvironmentVariable(ControlVariable),
            Environment.GetEnvironmentVariable("CI")),
        () => All));

    private static int _applied;

    /// <summary>Every sabotage in the suite: one per declaration on a fact, one per row on a theory.</summary>
    public static IReadOnlyList<Entry> All => Registry.Value;

    /// <summary>The sabotage this run applies, or null in an ordinary run.</summary>
    public static Entry? Active => State.Value.Active;

    /// <summary>contract-005 · G-17 round 1 (HAR-R1-07) — the sabotages whose tests this control run runs, unsabotaged; empty in every other run.</summary>
    public static IReadOnlyList<Entry> Controlled => State.Value.Controlled;

    /// <summary>Why this run must not start, or null when it may.</summary>
    public static string? Refusal => State.Value.Refusal;

    /// <summary>Whether this run's sabotage has acted: some place asked for it by name, and was told it applies.</summary>
    public static bool Acted => Volatile.Read(ref _applied) == 1;

    /// <summary>
    /// Whether the sabotage declared as <paramref name="name"/> applies here: true only in a run of it (of one of its
    /// rows, on a theory; a run runs only the row it targets). The place that asks is where it acts.
    /// </summary>
    public static bool Applies(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Active?.Declared != name)
        {
            return false;
        }

        Interlocked.Exchange(ref _applied, 1);
        return true;
    }

    /// <summary><paramref name="sabotaged"/> in a run of the sabotage declared as <paramref name="name"/>; otherwise <paramref name="value"/>.</summary>
    public static T Choose<T>(string name, T value, T sabotaged) => Applies(name) ? sabotaged : value;

    /// <summary>
    /// What a run is given: the sabotage variable, the control variable and CI's variable. contract-005 · the pioneer's
    /// decision of 2026-09-29 ("3. B") — no longer the event that started a GitHub workflow, nor the workflow: they told the
    /// scheduled retake apart, and no CI run is admitted now, whatever started it.
    /// </summary>
    internal sealed record Run(string? Sabotage, string? Control, string? Ci);

    /// <summary>What a run does: apply one sabotage, run some tests as a control, run as it is, or not start (and why).</summary>
    internal sealed record Decision(Entry? Active, IReadOnlyList<Entry> Controlled, string? Refusal);

    /// <summary>
    /// What a run does, given what it is given (<paramref name="run"/>) and the registry: no sabotage, the one named, a
    /// control run of the named sabotages' tests, or a refusal to start. Set at all in CI, even empty, either variable is
    /// refused, on every run — contract-005 · the pioneer's decision of 2026-09-29 ("3. B") removed the scheduled retake, the
    /// one CI run G-17 round 1 (HAR-R1-01) had admitted. Outside CI, a name no sabotage has is refused rather than run as an
    /// ordinary run, and so is a run given both variables.
    /// </summary>
    internal static Decision Decide(Run run, Func<IReadOnlyList<Entry>> registry)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(registry);

        static Decision Refused(string why) => new(null, [], why);

        if (run.Sabotage is not null && run.Control is not null)
        {
            return Refused(
                $"{Variable} ('{run.Sabotage}') and {ControlVariable} ('{run.Control}') are both set. A run applies one sabotage, or runs "
                + "tests as a control with none, not both, so this run does not start.");
        }

        var (variable, named) = run.Sabotage is not null ? (Variable, run.Sabotage) : (ControlVariable, run.Control);
        if (named is not null && run.Ci is not null)
        {
            return Refused(
                $"{variable} is set ('{named}'), and so is CI. A sabotage weakens the environment on purpose, to see one test go red, and "
                + "a control run runs a handful of tests; either is run by scripts/e2e-sabotage.sh, by hand, and never in CI, on any "
                + "run, where a run of one test must never stand for the suite. So this run does not start.");
        }

        if (string.IsNullOrEmpty(named))
        {
            return new(null, [], null);
        }

        var entries = registry();
        if (run.Sabotage is not null)
        {
            var entry = entries.FirstOrDefault(e => e.Name == named);
            return entry is not null
                ? new(entry, [], null)
                : Refused(
                    $"{Variable} names '{named}', which is no sabotage of this suite, so this run does not start. The sabotages are "
                    + $"declared on the tests they target ([Sabotage]); {RecordFile} lists them.");
        }

        var names = named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = names.Where(n => entries.All(e => e.Name != n || e.Held is not null)).ToArray();
        return unknown.Length == 0
            ? new(null, [.. entries.Where(e => names.Contains(e.Name, StringComparer.Ordinal))], null)
            : Refused(
                $"{ControlVariable} names {string.Join(", ", unknown.Select(n => $"'{n}'"))}, which {(unknown.Length == 1 ? "is" : "are")} no "
                + "sabotage of this suite whose test runs, so this run does not start.");
    }

    /// <summary>
    /// contract-005 · G-17 round 1 (HAR-R1-03) — the held decisions <see cref="HeldFile"/> lists: each one's id, the test it
    /// holds, the decision it waits on, and since when. A table row: | `id` | test | waits on | yyyy-mm-dd |.
    /// </summary>
    public static IReadOnlyList<HeldDecision> HeldDecisions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. HeldRow().Matches(text).Select(m => new HeldDecision(
            m.Groups["id"].Value, m.Groups["test"].Value.Trim(), m.Groups["waits"].Value.Trim(), m.Groups["since"].Value))];
    }

    /// <summary>The held decision a Skip reason names ("held: {id}"), or null when it names none.</summary>
    public static string? HeldIdIn(string skip)
    {
        ArgumentNullException.ThrowIfNull(skip);
        return HeldName().Match(skip) is { Success: true } named ? named.Groups["id"].Value : null;
    }

    /// <summary>
    /// The end-to-end tests of <paramref name="assembly"/> as the runner reports them: each fact, and each row of each
    /// theory, with the reason it is skipped, if it is. A class marked <see cref="NotEndToEndAttribute"/> has none.
    /// </summary>
    public static IReadOnlyList<EndToEndTest> EndToEndTests(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return [.. assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsDefined(typeof(NotEndToEndAttribute), inherit: false))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.IsDefined(typeof(FactAttribute), inherit: false))
                .OrderBy(m => m.MetadataToken)
                .SelectMany(m => TestsOf(t, m)))];
    }

    /// <summary>
    /// The tests of <paramref name="assembly"/> that are not end-to-end tests, by class, with the reason each class gives:
    /// what the record names instead of a sabotage.
    /// </summary>
    public static IReadOnlyList<(Type Class, int Tests, string Reason)> NotEndToEnd(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return [.. assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<NotEndToEndAttribute>() is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (t, t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.IsDefined(typeof(FactAttribute), inherit: false))
                .Sum(m => TestsOf(t, m).Count()), t.GetCustomAttribute<NotEndToEndAttribute>()!.Reason))];
    }

    /// <summary>
    /// What is wrong with the registry against <paramref name="tests"/>: a test no sabotage targets, a name that is not one
    /// name, a name or a declaration used twice, a description the record cannot hold. Empty when there is nothing.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<EndToEndTest> tests, IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(entries);

        var problems = new List<string>();
        problems.AddRange(tests
            .Where(t => !entries.Any(e => e.Test == t))
            .Select(t => $"{t.Name} has no named sabotage: declare one with [Sabotage] on the test (contract-005 · G-11)."));

        problems.AddRange(entries
            .Where(e => !NamePattern().IsMatch(e.Name))
            .Select(e => $"'{e.Name}' ({e.Test.Name}) is not a sabotage name: lower-case words joined by hyphens, and a row after a dot."));

        problems.AddRange(entries
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' names {g.Count()} sabotages: {string.Join(", ", g.Select(e => e.Test.Name))}."));

        problems.AddRange(entries
            .GroupBy(e => e.Declared, StringComparer.Ordinal)
            .Where(g => g.Select(e => e.Test.Method).Distinct().Count() > 1)
            .Select(g => $"'{g.Key}' is declared on more than one test: {string.Join(", ", g.Select(e => e.Test.Method.Name).Distinct())}."));

        problems.AddRange(entries
            .Where(e => string.IsNullOrWhiteSpace(e.Weakens) || e.Weakens.IndexOfAny(['\t', '\r', '\n']) >= 0)
            .Select(e => $"'{e.Name}' says what it weakens in no words, or in more than one line."));

        // A red is taken on its test's own file (Fingerprint), which is found by the class's name.
        var root = E2EEnvironment.FindRepositoryRoot();
        problems.AddRange(tests
            .Select(t => t.Class).Distinct()
            .Where(c => !File.Exists(Path.Combine(root, $"McpServerTemplate.E2E/{c.Name}.cs"))
                || !File.ReadAllText(Path.Combine(root, $"McpServerTemplate.E2E/{c.Name}.cs")).Contains($"class {c.Name}", StringComparison.Ordinal))
            .Select(c => $"{c.Name} is not declared in McpServerTemplate.E2E/{c.Name}.cs, the file its reds are taken on."));

        // contract-005 · G-17 round 3 (HAR-R3-02) — and compiled from that file alone, as the compiler recorded it: a part of the class
        // in another file — a partial class's second part, with a test or a helper its claim rests on — could change the claim with
        // every red still counted fresh, since a red is taken on the class's own file alone (Fingerprint). UpstreamTests was partial.
        if (SourceDocuments.Missing is { } missing)
        {
            problems.Add($"The files each test class is compiled from cannot be read ({missing}), so a test class in more than one file would pass unseen.");
        }
        else
        {
            problems.AddRange(tests
                .Select(t => t.Class).Distinct()
                .Select(c => (Class: c, Own: $"/McpServerTemplate.E2E/{c.Name}.cs", Documents: SourceDocuments.Of(c)))
                .Where(c => !c.Documents.Any(d => d.EndsWith(c.Own, StringComparison.Ordinal)) || c.Documents.Any(d => !d.EndsWith(c.Own, StringComparison.Ordinal)))
                .Select(c => $"{c.Class.Name} is compiled from [{string.Join(", ", c.Documents.Order(StringComparer.Ordinal))}], not from "
                    + $"McpServerTemplate.E2E/{c.Class.Name}.cs alone: its reds are taken on that one file, so a change in any other would leave "
                    + "them counted fresh. Declare a test class whole, in its own file, and not partial."));
        }

        return problems;
    }

    /// <summary>The registry as scripts/e2e-sabotage.sh reads it: a header, then one tab-separated line per sabotage.</summary>
    public static string Listing(IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Appended, not passed to the constructor: T-13 reads a string given to a new …Builder as an image reference.
        var text = new StringBuilder().Append("name\tclass\tmethod\tacts\theld\tfingerprint\ttest\tweakens\n");
        foreach (var entry in entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"{entry.Name}\t{entry.Test.Class.FullName}\t{entry.Test.Method.Name}\t{ActsIn(entry.Acts)}\t")
                .Append(CultureInfo.InvariantCulture, $"{OneLine(entry.Held ?? "-")}\t{Fingerprint(entry.Test)}\t{OneLine(entry.Test.Name)}\t{entry.Weakens}\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// contract-005 · T-12 — what a recorded red was taken on: the first 12 hex digits of the SHA-256 of the test's own file
    /// (<see cref="EndToEndTest.SourceFile"/>), read as text with its line endings made \n, so a checkout's line endings
    /// change nothing. A red taken on another version of the file is stale: the test may no longer make the claim it made.
    /// contract-005 · G-17 round 1 (HAR-R1-01) — this is what the per-push check holds the record to, and all it can: that each
    /// test file's reds were taken on it as it is. A change to the harness or the product changes no fingerprint, and a
    /// fingerprint anyone can compute is no proof that a red was taken; the full retake, run by hand (scripts/e2e-sabotage.sh
    /// --all), proves the reds — contract-005 · the pioneer's decision of 2026-09-29 ("3. B"), which removed the scheduled one.
    /// contract-005 · G-17 round 3 (HAR-R3-02) — one file is the whole test class: <see cref="Problems"/> refuses a test class the
    /// compiler built from any other file as well (<see cref="SourceDocuments"/>), so no part of it lies outside what this hashes.
    /// </summary>
    public static string Fingerprint(EndToEndTest test)
    {
        ArgumentNullException.ThrowIfNull(test);

        var text = File.ReadAllText(Path.Combine(E2EEnvironment.FindRepositoryRoot(), test.SourceFile)).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }

    /// <summary>Where a sabotage acts, in the record's words.</summary>
    public static string ActsIn(SabotageActs acts) => acts switch
    {
        SabotageActs.Inputs => "inputs",
        SabotageActs.ContainerEnvironment => "container environment",
        SabotageActs.Network => "network",
        _ => throw new ArgumentOutOfRangeException(nameof(acts), acts, "inputs, container environment or network"),
    };

    private static IEnumerable<EndToEndTest> TestsOf(Type type, MethodInfo method)
    {
        var fact = method.GetCustomAttribute<FactAttribute>()!;

        // A fact, or a theory skipped whole: the runner reports one test, not one per row.
        if (fact is not TheoryAttribute || fact.Skip is not null)
        {
            yield return new EndToEndTest(type, method, null, fact.Skip);
            yield break;
        }

        foreach (var data in method.GetCustomAttributes<DataAttribute>())
        {
            foreach (var row in data.GetData(method))
            {
                yield return new EndToEndTest(type, method, row, data.Skip);
            }
        }
    }

    private static List<Entry> Build(IReadOnlyList<EndToEndTest> tests)
    {
        var entries = new List<Entry>();
        foreach (var method in tests.GroupBy(t => t.Method))
        {
            var cases = method.ToArray();
            var rows = cases[0].Row is null ? null : RowKeys(method.Key, [.. cases.Select(c => c.Row!)]);
            foreach (var declared in method.Key.GetCustomAttributes<SabotageAttribute>())
            {
                for (var i = 0; i < cases.Length; i++)
                {
                    entries.Add(new Entry(rows is null ? declared.Name : $"{declared.Name}.{rows[i]}", declared.Name, declared.Acts, declared.Weakens, cases[i]));
                }
            }
        }

        return entries;
    }

    /// <summary>Each row's part of its sabotages' names: its leading arguments, as few as tell the rows apart.</summary>
    private static string[] RowKeys(MethodInfo method, object?[][] rows)
    {
        var width = rows.Max(r => r.Length);
        for (var leading = 1; leading <= width; leading++)
        {
            var keys = rows.Select(r => Slug(string.Join("-", r.Take(leading).Where(a => a is not null).Select(a => Convert.ToString(a, CultureInfo.InvariantCulture))))).ToArray();
            if (keys.All(k => k.Length > 0) && keys.Distinct(StringComparer.Ordinal).Count() == keys.Length)
            {
                return keys;
            }
        }

        throw new InvalidOperationException(
            $"The rows of {method.DeclaringType?.Name}.{method.Name} are not told apart by their arguments, so no sabotage can be named for each.");
    }

    private static string Slug(string text) => NotANamePart().Replace(text.ToLowerInvariant(), "-").Trim('-');

    private static string OneLine(string text) => WhiteSpace().Replace(text, " ");

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotANamePart();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)?$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"^\| `(?<id>[a-z0-9]+(-[a-z0-9]+)*)` \| (?<test>[^|\n]+) \| (?<waits>[^|\n]+) \| (?<since>\d{4}-\d{2}-\d{2}) \|[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex HeldRow();

    [GeneratedRegex(@"\bheld: (?<id>[a-z0-9]+(-[a-z0-9]+)*)")]
    private static partial Regex HeldName();

    /// <summary>contract-005 · G-17 round 1 (HAR-R1-03) — one held decision: its id, the test it holds, the decision it waits on, and since when.</summary>
    public sealed record HeldDecision(string Id, string Test, string WaitsOn, string Since);

    /// <summary>One sabotage: its name, the declaration it comes from, where it acts, what it weakens, and its test.</summary>
    /// <param name="Name">The name MCP_E2E_SABOTAGE gives it: the declared name, and on a theory its row's.</param>
    /// <param name="Declared">The name it is declared under, which the place where it acts asks for.</param>
    /// <param name="Acts">Where it acts.</param>
    /// <param name="Weakens">What it does.</param>
    /// <param name="Test">The one test it targets.</param>
    public sealed record Entry(string Name, string Declared, SabotageActs Acts, string Weakens, EndToEndTest Test)
    {
        /// <summary>Why its test is skipped, when it is: a held test runs no sabotage, and the record says so.</summary>
        public string? Held => Test.Skip;

        /// <summary>Whether it targets the test case <paramref name="className"/>.<paramref name="methodName"/>(<paramref name="arguments"/>).</summary>
        public bool Targets(string className, string methodName, IReadOnlyList<object?>? arguments) =>
            Test.Is(className, methodName, arguments);
    }
}

/// <summary>
/// One end-to-end test as the runner reports it: a fact, or one row of a theory.
/// </summary>
/// <param name="Class">Its test class.</param>
/// <param name="Method">Its test method.</param>
/// <param name="Row">A theory row's arguments; null for a fact.</param>
/// <param name="Skip">Why it is skipped, when it is.</param>
public sealed record EndToEndTest(Type Class, MethodInfo Method, object?[]? Row, string? Skip)
{
    /// <summary>The test, as the record names it: Class.Method, and a theory row's arguments.</summary>
    public string Name => Row is null
        ? $"{Class.Name}.{Method.Name}"
        : $"{Class.Name}.{Method.Name}({string.Join(", ", Row.Select(a => a is null ? "null" : $"\"{a}\""))})";

    /// <summary>contract-005 · T-12 — the test's own file, under the repository: every test class is in a file named after it.</summary>
    public string SourceFile => $"McpServerTemplate.E2E/{Class.Name}.cs";

    /// <summary>Whether it is the test case <paramref name="className"/>.<paramref name="methodName"/>(<paramref name="arguments"/>).</summary>
    public bool Is(string className, string methodName, IReadOnlyList<object?>? arguments) =>
        Class.FullName == className
        && Method.Name == methodName
        && (Row is null ? arguments is null || arguments.Count == 0 : arguments is not null && Row.SequenceEqual(arguments));

    public bool Equals(EndToEndTest? other) =>
        other is not null && Class == other.Class && Method == other.Method && Is(other.Class.FullName!, other.Method.Name, other.Row) && Skip == other.Skip;

    public override int GetHashCode() => HashCode.Combine(Class, Method, Row?.Length);
}
