// contract-005 · G-17 round 3 — declared in a namespace that holds InnerNamespaceModule's, on purpose.
namespace McpServerTemplate.Tests.Frame.Nesting;

/// <summary>
/// A provider whose namespace holds another provider's in the same assembly. What a provider owns is its namespace and those
/// under it (FrameIntegrity), so this one would own <see cref="Inner.InnerNamespaceModule"/>'s types.
/// </summary>
public sealed class OuterNamespaceModule() : TestModule("Outer")
{
}
