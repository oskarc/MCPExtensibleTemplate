using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
/// Nothing a run mints is kept: a JSON web token in a message is replaced by [token] before it is written.
/// </summary>
internal sealed partial class SabotageRecorder(Sabotage.Entry sabotage, string directory, IMessageSink inner) : LongLivedMarshalByRefObject, IMessageSink
{
    private readonly Lock _gate = new();
    private ITestResultMessage? _result;

    public bool OnMessage(IMessageSinkMessage message)
    {
        switch (message)
        {
            case ITestPassed or ITestFailed or ITestSkipped:
                lock (_gate)
                {
                    _result = (ITestResultMessage)message;
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
        JsonWebToken().Replace(message.Replace("\r\n", "\n", StringComparison.Ordinal), "[token]");

    private void Write()
    {
        ITestResultMessage? result;
        lock (_gate)
        {
            result = _result;
        }

        var (outcome, exception, message) = result switch
        {
            ITestFailed failed => ("failed", failed.ExceptionTypes.FirstOrDefault() ?? "(none)", ExceptionUtility.CombineMessages(failed)),
            ITestSkipped skipped => ("skipped", "-", skipped.Reason),
            ITestPassed => ("passed", "-", "The test passed: the sabotage did not make its claim fail."),
            _ => ("not-run", "-", "The test the sabotage targets did not run: it was not among the tests this run was given."),
        };

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{sabotage.Name}.outcome"),
            new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"name: {sabotage.Name}\n")
                .Append(CultureInfo.InvariantCulture, $"test: {(result?.Test.DisplayName ?? sabotage.Test.Name).ReplaceLineEndings(" ")}\n")
                .Append(CultureInfo.InvariantCulture, $"acts: {Sabotage.ActsIn(sabotage.Acts)}\n")
                .Append(CultureInfo.InvariantCulture, $"acted: {(Sabotage.Acted ? "yes" : "no")}\n")
                .Append(CultureInfo.InvariantCulture, $"outcome: {outcome}\n")
                .Append(CultureInfo.InvariantCulture, $"exception: {exception}\n")
                .Append(CultureInfo.InvariantCulture, $"seconds: {result?.ExecutionTime ?? 0:0.0}\n")
                .ToString());
        File.WriteAllText(Path.Combine(directory, $"{sabotage.Name}.message"), Redacted(message).TrimEnd() + "\n");
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*\.[A-Za-z0-9_-]*")]
    private static partial Regex JsonWebToken();
}
