using System.Globalization;
using System.Text;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · T-12 (G-11) — how a sabotage run's one test ended, for scripts/e2e-sabotage.sh to judge and record.
///
/// Every message of the run passes through on its way to the runner; the test's result is kept, and when the assembly
/// finishes two files are written under TestResults/sabotage/: {name}.outcome — the test as the runner names it, where
/// the sabotage acts, whether it acted, how the test ended and the type of the exception it failed with — and
/// {name}.message, the failure message as the runner reports it. The script, not this class, decides whether the red
/// counts: it counts only when the exception is a <see cref="ClaimException"/>.
///
/// contract-005 · G-17 round 1 (HAR-R1-07) — and how a control run's tests ended: for each sabotage the control run names,
/// {name}.control.outcome and {name}.control.message, the same fields for its test run with no sabotage applied. The script
/// counts a red only when its test passed there.
///
/// Nothing a run mints is kept: a JSON web token in a message is replaced by [token] before it is written.
/// </summary>
internal sealed class SabotageRecorder(IReadOnlyList<Sabotage.Entry> entries, bool control, string directory, IMessageSink inner)
    : LongLivedMarshalByRefObject, IMessageSink
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ITestResultMessage> _results = new(StringComparer.Ordinal);

    public bool OnMessage(IMessageSinkMessage message)
    {
        switch (message)
        {
            case ITestPassed or ITestFailed or ITestSkipped:
                var result = (ITestResultMessage)message;
                lock (_gate)
                {
                    foreach (var entry in entries.Where(e => Ran(e, result)))
                    {
                        _results[entry.Name] = result;
                    }
                }

                break;

            case ITestAssemblyFinished:
                Write();
                break;
        }

        return inner.OnMessage(message);
    }

    /// <summary>A message with every JSON web token in it replaced: what a failure may be kept as.</summary>
    internal static string Redacted(string message) =>
        JsonWebTokens.Pattern().Replace(message.Replace("\r\n", "\n", StringComparison.Ordinal), "[token]");

    /// <summary>
    /// Whether <paramref name="result"/> is the test <paramref name="entry"/> targets. A sabotage run is given its one test
    /// alone; a control run several, each told apart by its class, method and arguments.
    /// </summary>
    private bool Ran(Sabotage.Entry entry, ITestResultMessage result) =>
        !control
        || entry.Targets(result.TestCase.TestMethod.TestClass.Class.Name, result.TestCase.TestMethod.Method.Name, result.TestCase.TestMethodArguments);

    private void Write()
    {
        Directory.CreateDirectory(directory);
        foreach (var entry in entries)
        {
            ITestResultMessage? result;
            lock (_gate)
            {
                result = _results.GetValueOrDefault(entry.Name);
            }

            var (outcome, exception, message) = result switch
            {
                ITestFailed failed => ("failed", failed.ExceptionTypes.FirstOrDefault() ?? "(none)", ExceptionUtility.CombineMessages(failed)),
                ITestSkipped skipped => ("skipped", "-", skipped.Reason),
                ITestPassed => ("passed", "-", control
                    ? "The test passed with no sabotage applied."
                    : "The test passed: the sabotage did not make its claim fail."),
                _ => ("not-run", "-", "The test the sabotage targets did not run: it was not among the tests this run was given."),
            };

            var name = control ? $"{entry.Name}.control" : entry.Name;
            File.WriteAllText(
                Path.Combine(directory, $"{name}.outcome"),
                new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"name: {entry.Name}\n")
                    .Append(CultureInfo.InvariantCulture, $"test: {(result?.Test.DisplayName ?? entry.Test.Name).ReplaceLineEndings(" ")}\n")
                    .Append(CultureInfo.InvariantCulture, $"acts: {Sabotage.ActsIn(entry.Acts)}\n")
                    .Append(CultureInfo.InvariantCulture, $"acted: {(Sabotage.Acted ? "yes" : "no")}\n")
                    .Append(CultureInfo.InvariantCulture, $"outcome: {outcome}\n")
                    .Append(CultureInfo.InvariantCulture, $"exception: {exception}\n")
                    .Append(CultureInfo.InvariantCulture, $"seconds: {result?.ExecutionTime ?? 0:0.0}\n")
                    .ToString());
            File.WriteAllText(Path.Combine(directory, $"{name}.message"), Redacted(message).TrimEnd() + "\n");
        }
    }
}
