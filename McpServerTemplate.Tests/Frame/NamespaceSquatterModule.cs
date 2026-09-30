using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// contract-005 · G-17 round 3 (SEC-R3-01) — declared in ASP.NET Core's own namespace on purpose: a namespace is only a name.
namespace Microsoft.AspNetCore.Http;

/// <summary>
/// A provider declared in Microsoft.AspNetCore.Http, the namespace of the host's HttpContextFactory, that registers one of its
/// own. What a provider owns is what its own assembly declares in its namespace (FrameIntegrity), and IHttpContextFactory is
/// ASP.NET Core's, whatever namespace the provider is declared in.
/// </summary>
public sealed class NamespaceSquatterModule() : McpServerTemplate.Tests.Frame.TestModule("Squatter")
{
    public override void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IHttpContextFactory>(
            sp => new McpServerTemplate.Tests.Frame.ForgingHttpContextFactory(new DefaultHttpContextFactory(sp)));
}
