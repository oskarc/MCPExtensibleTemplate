namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// A provider, as the frame sees it (contract-003 · G-1): a name, a policy, the types that declare
/// its tools, resources and prompts, and the services it needs.
///
/// The frame registers the primitive types itself — nothing is found by scanning the assembly — so
/// a class that is not named by a module is not served. Primitive types must be static classes:
/// the frame has to learn each primitive's wire name at startup without constructing anything.
///
/// <see cref="Register"/> is the provider's only way into the service collection, and the frame
/// watches it: a module that removes a registration, or adds one that belongs to the frame, the SDK
/// or the identity layer, stops the server from starting (contract-003 · G-12).
/// </summary>
public interface IProviderModule
{
    /// <summary>The provider's name, matching <c>Providers:{Name}</c> in configuration.</summary>
    string Name { get; }

    /// <summary>What the provider is allowed to do.</summary>
    ProviderPolicy Policy { get; }

    /// <summary>Static classes carrying <c>[McpServerTool]</c> methods.</summary>
    IReadOnlyList<Type> ToolTypes { get; }

    /// <summary>Static classes carrying <c>[McpServerResource]</c> methods.</summary>
    IReadOnlyList<Type> ResourceTypes { get; }

    /// <summary>Static classes carrying <c>[McpServerPrompt]</c> methods.</summary>
    IReadOnlyList<Type> PromptTypes { get; }

    /// <summary>
    /// Keys the provider reads under <c>Providers:{Name}</c>, besides the two the frame owns
    /// (<c>IdentityProvider</c> and <c>BaseUrl</c>). Any other key there refuses startup.
    /// </summary>
    IReadOnlyCollection<string> Settings { get; }

    /// <summary>Registers the services the provider's primitives need, such as its API client.</summary>
    void Register(IServiceCollection services, IConfiguration configuration);
}
