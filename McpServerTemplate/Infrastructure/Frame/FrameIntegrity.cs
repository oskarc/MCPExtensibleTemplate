using System.Reflection;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// Stops a provider from switching the frame off (contract-003 · G-12).
///
/// A provider registers its services into the same collection the frame lives in, so nothing but
/// this check stops it removing the gate, replacing the identity layer, or adding a tool through a
/// side door. Providers register first and the frame last, so there is nothing of the frame's to
/// remove when a provider runs; what a provider removes, or adds in a namespace that belongs to the
/// frame, the SDK, the identity layer or the host pipeline, refuses startup with its name.
///
/// What it does not cover: a provider is code in the same process, and code can reach anything by
/// reflection at run time. This check governs what a provider registers, which is how every
/// ordinary extension point is reached; it is not a sandbox.
/// </summary>
public static class FrameIntegrity
{
    // Service types a provider may not register, by namespace. Anything a provider legitimately
    // needs — its client, its options, the HTTP client factory, resilience — lives elsewhere.
    private static readonly string[] Protected =
    [
        "ModelContextProtocol",
        "McpServerTemplate.Infrastructure",
        "Microsoft.AspNetCore.Authentication",
        "Microsoft.AspNetCore.Authorization",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server",
        "Microsoft.AspNetCore.HostFiltering",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Cors",
        "Microsoft.AspNetCore.HttpOverrides",
        "Microsoft.AspNetCore.Routing",
        "System.Security.Claims",
        "StackExchange.Redis",
    ];

    /// <summary>Runs one module's registration and refuses what it must not do.</summary>
    public static void Register(IProviderModule module, IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(services);

        var before = services.ToArray();
        module.Register(services, configuration);
        var after = services.ToHashSet(ReferenceEqualityComparer.Instance);

        var removed = before.Where(d => !after.Contains(d)).ToArray();
        if (removed.Length > 0)
        {
            throw new ConfigurationException(
                $"The provider '{module.Name}' removed service registrations it did not make: "
                + string.Join(", ", removed.Select(d => d.ServiceType.Name).Distinct(StringComparer.Ordinal))
                + ". A provider adds its own services and changes no one else's.");
        }

        var beforeSet = before.ToHashSet(ReferenceEqualityComparer.Instance);
        var trespass = services
            .Where(d => !beforeSet.Contains(d))
            .Select(d => d.ServiceType)
            .Where(TouchesProtected)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (trespass.Length > 0)
        {
            throw new ConfigurationException(
                $"The provider '{module.Name}' registered services that belong to the frame, the MCP SDK, the "
                + $"identity layer or the host: {string.Join(", ", trespass)}. Tools, resources and prompts are "
                + "declared by the module's types and policy; request checks are the frame's.");
        }
    }

    /// <summary>
    /// Compares the request and message filters actually installed with the frame's own. Anything
    /// that is neither the frame's nor the SDK's own refuses startup, naming where it came from.
    /// </summary>
    public static void VerifyInstalled(McpServerOptions options, FrameManifest manifest, IEnumerable<IProviderModule> modules)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifest);

        var foreign = new List<string>();
        foreach (var (list, filters) in InstalledFilters(options))
        {
            foreach (var filter in filters)
            {
                if (!manifest.Owns(filter) && !IsSdk(filter))
                {
                    var origin = filter.Method.DeclaringType?.FullName ?? "an unknown type";
                    var owner = modules.FirstOrDefault(m => OwnsNamespace(m, filter.Method.DeclaringType));
                    foreign.Add($"{list}: {origin}" + (owner is null ? string.Empty : $" (provider '{owner.Name}')"));
                }
            }
        }

        var missing = manifest.Filters.Where(f => !InstalledFilters(options).Any(l => l.Filters.Contains(f))).ToArray();
        if (missing.Length > 0)
        {
            foreign.Add($"{missing.Length} of the frame's own filters are not installed");
        }

        var incoming = options.Filters.Message.IncomingFilters;
        if (incoming.Count == 0 || !ReferenceEquals(incoming[0], manifest.Gate))
        {
            foreign.Add("the frame's request-kind gate is not the first incoming message filter");
        }

        if (foreign.Count > 0)
        {
            throw new ConfigurationException(
                "The MCP server's request checks are not the frame's: " + string.Join("; ", foreign)
                + ". The frame refuses to serve with checks it did not install.");
        }
    }

    /// <summary>Every filter list the SDK consults, by name, in a stable order.</summary>
    public static IEnumerable<(string List, IReadOnlyList<Delegate> Filters)> InstalledFilters(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var holder in new object[] { options.Filters.Request, options.Filters.Message })
        {
            foreach (var property in holder.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (property.GetValue(holder) is System.Collections.IEnumerable list and not string)
                {
                    yield return ($"{holder.GetType().Name}.{property.Name}", list.OfType<Delegate>().ToArray());
                }
            }
        }
    }

    private static bool IsSdk(Delegate filter) =>
        filter.Method.DeclaringType?.Namespace?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true ||
        filter.Method.DeclaringType?.Namespace?.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) == true &&
        filter.Method.DeclaringType.Assembly.GetName().Name?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true;

    private static bool OwnsNamespace(IProviderModule module, Type? type) =>
        type?.Namespace is { } ns && module.GetType().Namespace is { } moduleNs && ns.StartsWith(moduleNs, StringComparison.Ordinal);

    private static bool TouchesProtected(Type type)
    {
        if (type.Namespace is { } ns && Protected.Any(p => ns.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }

        // IConfigureOptions<JwtBearerOptions>, IPostConfigureOptions<McpServerOptions> and the like
        // reach a protected part through a generic argument.
        return type.IsGenericType && type.GetGenericArguments().Any(TouchesProtected);
    }

    private static string Describe(Type type) =>
        type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>"
            : type.Name;
}

/// <summary>
/// What the frame installed, kept so it can be compared with what is actually installed — at
/// startup, and by the test that checks the tested server is the shipped one (contract-003 · T-10).
/// </summary>
public sealed class FrameManifest
{
    private readonly HashSet<Delegate> _owned = new(ReferenceEqualityComparer.Instance);

    /// <summary>The request-kind gate, which must be the first incoming message filter.</summary>
    public Delegate? Gate { get; private set; }

    /// <summary>Every filter the frame installed.</summary>
    public IReadOnlyCollection<Delegate> Filters => _owned;

    /// <summary>Records a filter as the frame's and returns it.</summary>
    public T Own<T>(T filter) where T : Delegate
    {
        _owned.Add(filter);
        return filter;
    }

    /// <summary>Records the request-kind gate.</summary>
    public T OwnGate<T>(T gate) where T : Delegate
    {
        Gate = Own(gate);
        return gate;
    }

    /// <summary>Whether the frame installed this filter.</summary>
    public bool Owns(Delegate filter) => _owned.Contains(filter);

    /// <summary>
    /// A description that is identical for two servers built the same way: which filters sit in
    /// which list and whose they are, and every policy in force. Logged at startup.
    /// </summary>
    public static string Describe(McpServerOptions options, FrameManifest manifest, PolicyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registry);

        var lists = FrameIntegrity.InstalledFilters(options)
            .Where(l => l.Filters.Count > 0)
            .Select(l => $"{l.List}=[{string.Join(",", l.Filters.Select(f => manifest.Owns(f) ? "frame" : "sdk"))}]");
        return string.Join(" ", lists) + " | " + registry.Describe();
    }
}
