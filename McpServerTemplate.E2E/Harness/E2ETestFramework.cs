using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("McpServerTemplate.E2E.Harness.E2ETestFramework", "McpServerTemplate.E2E")]

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// xUnit's own test framework, with one addition: when every test in the assembly has run, the shared
/// environment is torn down.
///
/// contract-005 · G-3, G-14 — xUnit 2 has no assembly fixture, and the environment is started lazily by
/// whichever class needs it first. What it may not do is outlive the run: its diagnostics bundle is
/// written, its containers and network are removed, and the run directory — every key of the test
/// PKI — is deleted here, before the assembly reports that it finished.
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
        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            await E2EEnvironment.ShutdownAsync();
            await base.BeforeTestAssemblyFinishedAsync();
        }
    }
}
