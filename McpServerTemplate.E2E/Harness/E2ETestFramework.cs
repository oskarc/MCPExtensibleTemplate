using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("McpServerTemplate.E2E.Harness.E2ETestFramework", "McpServerTemplate.E2E")]

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// xUnit's own test framework, with three additions: when every test in the assembly has run, the shared environment is
/// torn down; a run that names a sabotage runs its one test and records how it ended; and a run that must not start
/// runs nothing.
///
/// contract-005 · G-3, G-14 — xUnit 2 has no assembly fixture, and the environment is started lazily by
/// whichever class needs it first. What it may not do is outlive the run: its diagnostics bundle is
/// written, its containers and network are removed, and the run directory — every key of the test
/// PKI — is deleted here, before the assembly reports that it finished.
///
/// contract-005 · G-11, T-12 — a sabotage weakens one thing for one test, so a run of it (MCP_E2E_SABOTAGE) runs that
/// test and no other, whatever the runner's filter let through, and its result is written for scripts/e2e-sabotage.sh
/// (<see cref="SabotageRecorder"/>). A run the sabotage variable refuses — set in CI, or naming no sabotage — does not
/// start: every test it was given fails with the refusal, before any fixture's server can start
/// (<see cref="E2EEnvironment"/> refuses too).
/// </summary>
public sealed class E2ETestFramework(IMessageSink messageSink) : XunitTestFramework(messageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(
            IEnumerable<IXunitTestCase> testCases,
            IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            if (Sabotage.Refusal is null && Sabotage.Active is { } sabotage)
            {
                testCases = [.. testCases.Where(t => sabotage.Targets(t.TestMethod.TestClass.Class.Name, t.TestMethod.Method.Name, t.TestMethodArguments))];
                executionMessageSink = new SabotageRecorder(
                    sabotage, Path.Combine(E2EEnvironment.FindRepositoryRoot(), "TestResults", "sabotage"), executionMessageSink);
            }

            using var runner = new AssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class AssemblyRunner(
        ITestAssembly testAssembly,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        protected override async Task AfterTestAssemblyStartingAsync()
        {
            await base.AfterTestAssemblyStartingAsync();

            // contract-005 · G-11 — every test the run was given fails with the refusal, and none of them runs.
            if (Sabotage.Refusal is { } refusal)
            {
                Aggregator.Add(new EnvironmentFaultException("sabotage", refusal));
            }
        }

        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            await E2EEnvironment.ShutdownAsync();
            await base.BeforeTestAssemblyFinishedAsync();
        }
    }
}
