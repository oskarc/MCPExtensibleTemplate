using System.Collections.Frozen;
using System.Net;
using System.Reflection;
using McpServerTemplate.Infrastructure.Identity;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// The frame's own frozen copy of every policy, matched against what the server actually serves,
/// and checked once at startup (contract-003 · G-1, G-2). Nothing is served that no enabled module
/// declared, and nothing is declared that is not served.
/// </summary>
public sealed class PolicyRegistry
{
    /// <summary>A tool, the provider that owns it, the identity provider it answers to, and its policy.</summary>
    public sealed record ToolEntry(string Provider, string IdentityProvider, ToolPolicy Policy, ArgumentGuard Guard);

    /// <summary>A resource or prompt, its provider, identity provider and required scope.</summary>
    public sealed record ScopedEntry(string Provider, string IdentityProvider, string Scope);

    private PolicyRegistry(
        FrozenDictionary<string, ToolEntry> tools,
        FrozenDictionary<string, ScopedEntry> resources,
        FrozenDictionary<string, ScopedEntry> prompts)
    {
        Tools = tools;
        Resources = resources;
        Prompts = prompts;
    }

    /// <summary>Every served tool by wire name.</summary>
    public FrozenDictionary<string, ToolEntry> Tools { get; }

    /// <summary>Every served resource by URI or URI template.</summary>
    public FrozenDictionary<string, ScopedEntry> Resources { get; }

    /// <summary>Every served prompt by name.</summary>
    public FrozenDictionary<string, ScopedEntry> Prompts { get; }

    /// <summary>The key a resource is governed under.</summary>
    public static string KeyOf(McpServerResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.ProtocolResourceTemplate?.UriTemplate ?? resource.ProtocolResource?.Uri ?? string.Empty;
    }

    /// <summary>
    /// Builds the registry, or throws a <see cref="ConfigurationException"/> naming every way the
    /// declaration and the served surface disagree.
    /// </summary>
    public static PolicyRegistry Build(
        IServiceProvider services,
        IReadOnlyList<IProviderModule> modules,
        AuthenticationConfig identity,
        IConfiguration configuration,
        McpServerOptions options,
        bool confirmationConfigured)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        var toolOwner = new Dictionary<string, IProviderModule>(StringComparer.Ordinal);
        var resourceOwner = new Dictionary<string, IProviderModule>(StringComparer.Ordinal);
        var promptOwner = new Dictionary<string, IProviderModule>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            var bound = configuration[$"Providers:{module.Name}:IdentityProvider"];
            if (string.IsNullOrWhiteSpace(bound))
            {
                problems.Add($"Providers:{module.Name}:IdentityProvider is required. Every provider answers to exactly one identity provider.");
            }
            else if (!identity.IdentityProviders.ContainsKey(bound))
            {
                problems.Add($"Providers:{module.Name}:IdentityProvider names '{bound}', which is not configured. Configured: {string.Join(", ", identity.IdentityProviders.Keys)}.");
            }
            else
            {
                bindings[module.Name] = bound;
            }

            Claim(module, NamesOf(module.ToolTypes, typeof(McpServerToolAttribute), m => McpServerTool.Create(m, (object?)null, new McpServerToolCreateOptions { Services = services }).ProtocolTool.Name, problems), toolOwner, "tool", problems);
            Claim(module, NamesOf(module.ResourceTypes, typeof(McpServerResourceAttribute), m => KeyOf(McpServerResource.Create(m, (object?)null, new McpServerResourceCreateOptions { Services = services })), problems), resourceOwner, "resource", problems);
            Claim(module, NamesOf(module.PromptTypes, typeof(McpServerPromptAttribute), m => McpServerPrompt.Create(m, (object?)null, new McpServerPromptCreateOptions { Services = services }).ProtocolPrompt.Name, problems), promptOwner, "prompt", problems);

            CheckEgress(module, configuration, problems);
        }

        // What is served is what the container holds and what the options already carry: the SDK
        // copies registered primitives into the options only when a server is created, and a
        // primitive added either way is served. Both are read, so neither is a way around the policy.
        var served = Distinct(services.GetServices<McpServerTool>().Concat(options.ToolCollection ?? []), t => t.ProtocolTool.Name, "tool", problems);
        var tools = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);
        foreach (var tool in served)
        {
            var name = tool.ProtocolTool.Name;
            if (!toolOwner.TryGetValue(name, out var owner))
            {
                problems.Add($"The tool '{name}' is served but no enabled provider module declares it. Tools are served only from a module's ToolTypes.");
                continue;
            }

            if (!owner.Policy.Tools.TryGetValue(name, out var policy))
            {
                problems.Add($"The tool '{name}' of provider '{owner.Name}' has no policy. Add it to the provider's ProviderPolicy.Tools.");
                continue;
            }

            CheckScope(owner, bindings, identity, policy.Scope, $"tool '{name}'", problems);
            CheckAnnotations(tool.ProtocolTool, policy, problems);
            if (policy.Risk == RiskClass.Irreversible && !confirmationConfigured)
            {
                problems.Add($"The tool '{name}' is Irreversible, and Confirmation:Key is not set. An irreversible tool cannot run without the key that signs its confirmations.");
            }

            if (policy.PerPrincipalPerMinute < 1 || policy.MaxOutputBytes < 1 || policy.MaxStringArgLength < 1)
            {
                problems.Add($"The tool '{name}' has a limit below 1 in its policy, which would refuse every call.");
            }

            if (bindings.TryGetValue(owner.Name, out var idp))
            {
                tools[name] = new ToolEntry(owner.Name, idp, policy, ArgumentGuard.For(tool.ProtocolTool.InputSchema, policy.MaxStringArgLength));
            }
        }

        var resources = Scoped(
            Distinct(services.GetServices<McpServerResource>().Concat(options.ResourceCollection ?? []), KeyOf, "resource", problems).Select(KeyOf),
            resourceOwner, m => m.Policy.Resources, "resource", bindings, identity, problems);
        var prompts = Scoped(
            Distinct(services.GetServices<McpServerPrompt>().Concat(options.PromptCollection ?? []), p => p.ProtocolPrompt.Name, "prompt", problems).Select(p => p.ProtocolPrompt.Name),
            promptOwner, m => m.Policy.Prompts, "prompt", bindings, identity, problems);

        // Declared but not served: the module names it and the server would not answer for it.
        foreach (var (name, owner) in toolOwner.Where(t => !tools.ContainsKey(t.Key) && !served.Any(s => s.ProtocolTool.Name == t.Key)))
        {
            problems.Add($"The provider '{owner.Name}' declares the tool '{name}', and the server does not serve it. Its type is not registered with the MCP server.");
        }

        foreach (var (key, owner) in resourceOwner.Where(r => !resources.ContainsKey(r.Key)))
        {
            problems.Add($"The provider '{owner.Name}' declares the resource '{key}', and the server does not serve it.");
        }

        foreach (var (key, owner) in promptOwner.Where(p => !prompts.ContainsKey(p.Key)))
        {
            problems.Add($"The provider '{owner.Name}' declares the prompt '{key}', and the server does not serve it.");
        }

        foreach (var module in modules)
        {
            Orphans(module.Policy.Tools.Keys, toolOwner, module, "tool", problems);
            Orphans(module.Policy.Resources.Keys, resourceOwner, module, "resource", problems);
            Orphans(module.Policy.Prompts.Keys, promptOwner, module, "prompt", problems);
        }

        if (!string.IsNullOrEmpty(identity.AdminIdentityProvider) &&
            (!identity.IdentityProviders.TryGetValue(identity.AdminIdentityProvider, out var admin) ||
             !admin.ScopeCatalog.Contains("mcp:admin", StringComparer.Ordinal)))
        {
            problems.Add($"Authentication:AdminIdentityProvider names '{identity.AdminIdentityProvider}', which is not a configured identity provider whose catalog contains mcp:admin.");
        }

        if (problems.Count > 0)
        {
            throw new ConfigurationException(
                "The providers' policies do not match what the server would serve:\n  " + string.Join("\n  ", problems));
        }

        return new PolicyRegistry(tools.ToFrozenDictionary(StringComparer.Ordinal), resources, prompts);
    }

    /// <summary>One line per governed primitive, sorted, for the startup manifest.</summary>
    public string Describe() => string.Join(
        ";",
        Tools.Select(t => $"tool:{t.Value.Provider}/{t.Key}:{t.Value.Policy.Scope}:{t.Value.Policy.Risk}")
            .Concat(Resources.Select(r => $"resource:{r.Value.Provider}/{r.Key}:{r.Value.Scope}"))
            .Concat(Prompts.Select(p => $"prompt:{p.Value.Provider}/{p.Key}:{p.Value.Scope}"))
            .Order(StringComparer.Ordinal));

    // The same instance may be held twice; two different instances under one name may not, or the
    // undeclared one could answer under the declared one's name and policy.
    private static T[] Distinct<T>(IEnumerable<T> items, Func<T, string> key, string kind, List<string> problems)
        where T : class
    {
        var result = new List<T>();
        foreach (var group in items.Distinct(ReferenceEqualityComparer.Instance).Cast<T>().GroupBy(key, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                problems.Add($"The {kind} '{group.Key}' is served by {group.Count()} different implementations. A name answers to one declared implementation.");
            }

            result.Add(group.First());
        }

        return [.. result];
    }

    private static IEnumerable<string> NamesOf(IReadOnlyList<Type> types, Type marker, Func<MethodInfo, string> nameOf, List<string> problems)
    {
        foreach (var type in types)
        {
            if (!(type.IsAbstract && type.IsSealed))
            {
                problems.Add($"'{type.FullName}' is not a static class. The frame learns each primitive's name at startup without constructing anything, so primitive types must be static.");
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.IsDefined(marker)))
            {
                yield return nameOf(method);
            }
        }
    }

    private static void Claim(IProviderModule module, IEnumerable<string> names, Dictionary<string, IProviderModule> owners, string kind, List<string> problems)
    {
        foreach (var name in names)
        {
            if (owners.TryGetValue(name, out var other))
            {
                problems.Add($"The {kind} '{name}' is declared by both '{other.Name}' and '{module.Name}'.");
            }
            else
            {
                owners[name] = module;
            }
        }
    }

    private static FrozenDictionary<string, ScopedEntry> Scoped<TPolicy>(
        IEnumerable<string>? served,
        Dictionary<string, IProviderModule> owners,
        Func<IProviderModule, IReadOnlyDictionary<string, TPolicy>> policiesOf,
        string kind,
        Dictionary<string, string> bindings,
        AuthenticationConfig identity,
        List<string> problems)
    {
        var result = new Dictionary<string, ScopedEntry>(StringComparer.Ordinal);
        foreach (var key in served ?? [])
        {
            if (!owners.TryGetValue(key, out var owner))
            {
                problems.Add($"The {kind} '{key}' is served but no enabled provider module declares it.");
                continue;
            }

            if (!policiesOf(owner).TryGetValue(key, out var policy))
            {
                problems.Add($"The {kind} '{key}' of provider '{owner.Name}' has no policy. Add it to the provider's ProviderPolicy.");
                continue;
            }

            var scope = policy switch
            {
                ResourcePolicy r => r.Scope,
                PromptPolicy p => p.Scope,
                _ => string.Empty,
            };
            CheckScope(owner, bindings, identity, scope, $"{kind} '{key}'", problems);
            if (bindings.TryGetValue(owner.Name, out var idp))
            {
                result[key] = new ScopedEntry(owner.Name, idp, scope);
            }
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Orphans(IEnumerable<string> declared, Dictionary<string, IProviderModule> owners, IProviderModule module, string kind, List<string> problems)
    {
        foreach (var key in declared)
        {
            if (!owners.TryGetValue(key, out var owner) || !ReferenceEquals(owner, module))
            {
                problems.Add($"The provider '{module.Name}' has a policy for the {kind} '{key}', which none of its types declares. A policy for nothing is a mistake waiting to be read as protection.");
            }
        }
    }

    private static void CheckScope(IProviderModule owner, Dictionary<string, string> bindings, AuthenticationConfig identity, string scope, string what, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Contains('*', StringComparison.Ordinal))
        {
            problems.Add($"The {what} requires the scope '{scope}'. A scope must be a single name, with no wildcard.");
            return;
        }

        if (bindings.TryGetValue(owner.Name, out var idp) &&
            !identity.IdentityProviders[idp].ScopeCatalog.Contains(scope, StringComparer.Ordinal))
        {
            problems.Add($"The {what} requires the scope '{scope}', which its identity provider '{idp}' cannot issue. Its catalog is {string.Join(", ", identity.IdentityProviders[idp].ScopeCatalog)}.");
        }
    }

    private static void CheckAnnotations(Tool tool, ToolPolicy policy, List<string> problems)
    {
        var expected = new ToolAnnotations
        {
            ReadOnlyHint = policy.Risk == RiskClass.Read,
            DestructiveHint = policy.Risk == RiskClass.Irreversible,
            IdempotentHint = policy.Idempotent,
            OpenWorldHint = true,
        };

        var declared = tool.Annotations;
        if (declared is not null &&
            ((declared.ReadOnlyHint is { } ro && ro != expected.ReadOnlyHint) ||
             (declared.DestructiveHint is { } d && d != expected.DestructiveHint)))
        {
            problems.Add($"The tool '{tool.Name}' declares hints that contradict its {policy.Risk} risk class. Remove the hints from its attribute; the frame derives them from the policy.");
            return;
        }

        tool.Annotations = expected;
    }

    private static void CheckEgress(IProviderModule module, IConfiguration configuration, List<string> problems)
    {
        var egress = module.Policy.Egress;
        foreach (var host in egress.Hosts)
        {
            if (host.Contains('*', StringComparison.Ordinal) || IPAddress.TryParse(host.Trim('[', ']'), out _) ||
                Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.', StringComparison.Ordinal))
            {
                problems.Add($"The provider '{module.Name}' allows the host '{host}'. Hosts must be exact domain names: no wildcards and no IP addresses.");
            }
        }

        if (egress.Hosts.Count == 0 || egress.Methods.Count == 0 || egress.MaxResponseBytes < 1 ||
            egress.MaxConcurrentUpstreamCalls < 1 || egress.DailyCallBudget < 1 || egress.MaxRetryAttempts < 0)
        {
            problems.Add($"The provider '{module.Name}' has an egress policy with no hosts, no methods, or a limit below 1.");
        }

        var worstCase = egress.AttemptTimeout * (1 + egress.MaxRetryAttempts);
        if (egress.TotalTimeout <= worstCase)
        {
            problems.Add($"The provider '{module.Name}' has a total timeout of {egress.TotalTimeout.TotalSeconds:0.##}s, which cannot fit {1 + egress.MaxRetryAttempts} attempts of {egress.AttemptTimeout.TotalSeconds:0.##}s.");
        }

        var baseUrl = configuration[$"Providers:{module.Name}:BaseUrl"];
        if (baseUrl is not null)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                problems.Add($"Providers:{module.Name}:BaseUrl is '{baseUrl}'; it must be an absolute HTTPS address.");
            }
            else if (!egress.Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"Providers:{module.Name}:BaseUrl points at '{uri.Host}', which the provider's policy does not allow. Allowed: {string.Join(", ", egress.Hosts)}.");
            }
        }
    }
}
