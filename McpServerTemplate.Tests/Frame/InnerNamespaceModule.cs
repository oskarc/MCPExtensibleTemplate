// contract-005 · G-17 round 3 — declared inside OuterNamespaceModule's namespace, on purpose.
namespace McpServerTemplate.Tests.Frame.Nesting.Inner;

/// <summary>A provider declared in a namespace another provider's holds (<see cref="OuterNamespaceModule"/>).</summary>
public sealed class InnerNamespaceModule() : TestModule("Inner")
{
}
