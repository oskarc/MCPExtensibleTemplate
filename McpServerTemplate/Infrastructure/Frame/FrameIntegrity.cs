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
/// contract-005 · G-17 round 3 (SEC-R3-01, SEC-R3-02) — declared, not inferred. The check named what a provider may not
/// register, and everything it did not name was allowed: a provider replaced the host's HttpContextFactory, and every
/// request with no token ran as a principal of the provider's choosing; one replaced the ILoggerFactory, and the whole
/// log, the frame's security events among it, went round the line-safe pipeline to the provider; one registered an
/// IOptionsFactory for ForwardedHeadersOptions, whose namespace no one had named, and trusted every address's forwarded
/// headers. A list of what is forbidden is never finished. So it is turned round: every service a provider adds is checked,
/// by its type and by every type argument, against what a provider may register — its own types, and the plumbing its HTTP
/// clients are built from (<see cref="ProviderPlumbing"/>), sized from what the built-in providers actually register — and
/// anything else refuses startup, naming the provider and the type. The list above stays, checked first: nothing there is a
/// provider's to register, whatever namespace the provider itself is in.
///
/// What it does not cover: a provider is code in the same process, and code can reach anything by
/// reflection at run time. This check governs what a provider registers, which is how every
/// ordinary extension point is reached; it is not a sandbox.
/// </summary>
public static class FrameIntegrity
{
    // Service types a provider may not register, by namespace, whatever else would allow them: the frame's, the SDK's, the
    // identity layer's and the host pipeline's. contract-005 · G-17 round 3 — checked first; what a provider may register
    // is the allowlist below.
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

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-01) — the plumbing a provider's HTTP clients are built from, which a provider may
    /// register besides its own types: every type these assemblies define. Declared from what the built-in providers'
    /// registrations add, as an enumeration of each provider's registrations showed — alone and in order, in the HTTP host,
    /// the stdio host and the test compositions, where they are the same — and the fast suite holds each built-in provider,
    /// alone, to it. By assembly rather than namespace: the HTTP client factory and the resilience handler register marker
    /// types of their own in Microsoft.Extensions.DependencyInjection, which is also the namespace of the container's own
    /// services, and those stay refused. A provider that needs more has it added here, on purpose, never by inference.
    /// </summary>
    private static readonly string[] ProviderPlumbing =
    [
        // HttpClient, HttpResponseMessage: AddHttpClient, and the resilience pipeline's result type.
        "System.Net.Http",

        // IHttpClientFactory and IHttpMessageHandlerFactory (in System.Net.Http), typed and named clients and their
        // HttpClientFactoryOptions, the handler builder and its filters, and the factory's two registration trackers
        // (HttpClientMappingRegistry, DefaultHttpClientConfigurationTracker): AddHttpClient.
        "Microsoft.Extensions.Http",

        // The standard resilience handler, its options and their validators, its pipeline key and its marker:
        // AddStandardResilienceHandler.
        "Microsoft.Extensions.Http.Resilience",

        // The resilience pipeline, its builder, its registry and the registry's options: AddStandardResilienceHandler.
        "Polly.Core",

        // The registry's configuration and marker, and the telemetry options: AddStandardResilienceHandler.
        "Polly.Extensions",

        // IExceptionSummarizer and its HTTP provider, which the resilience handler's telemetry uses: AddStandardResilienceHandler.
        "Microsoft.Extensions.Diagnostics.ExceptionSummarization",
    ];

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-01) — the options machinery (IConfigureOptions, IPostConfigureOptions,
    /// IValidateOptions, IOptionsChangeTokenSource, IOptionsFactory and the rest): a provider may register it closed over
    /// types it may register — its own options, its clients' and its pipelines' — and never open, nor over anyone else's
    /// options. IOptionsFactory&lt;ForwardedHeadersOptions&gt; was the frame's forwarded-headers trust.
    /// </summary>
    private const string OptionsMachinery = "Microsoft.Extensions.Options";

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-01) — the one options type of the framework's own a provider's registrations close
    /// the options machinery over: what ValidateOnStart configures, which AddHttpClient (through AddMetrics) and the standard
    /// resilience handler call each time they run, so it is in every such provider's registrations however much was laid
    /// before. It is internal to Microsoft.Extensions.Options: no provider's code can name it.
    /// </summary>
    private const string StartupValidation = "Microsoft.Extensions.Options.StartupValidatorOptions";

    /// <summary>Runs one module's registration and refuses what it must not do.</summary>
    public static void Register(IProviderModule module, IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(services);

        LayTheFrameworksPlumbing(services);

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
        var refused = services
            .Where(d => !beforeSet.Contains(d))
            .Select(d => d.ServiceType)
            .Where(type => !MayRegister(module, type))
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (refused.Length > 0)
        {
            throw new ConfigurationException(
                $"The provider '{module.Name}' registered services that belong to the frame, the MCP SDK, the identity layer, "
                + $"the host or the log pipeline, which no provider may register: {string.Join(", ", refused)}. A provider "
                + "registers its own types — its client, its options — and the HTTP client and resilience plumbing its client is "
                + "built from; tools, resources and prompts are declared by the module's types and policy, and request checks, "
                + "identity, the host and the log are the frame's.");
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 3 — the framework's own plumbing, laid before a provider registers: logging, options and
    /// metrics, each only where it is missing, as every host lays it; and the memory cache. A provider's HTTP client asks for
    /// the first three (AddHttpClient calls AddLogging, AddOptions and AddMetrics, each adding only what is not there yet), a
    /// built-in provider asks for the cache (AddMemoryCache, the same way), and none of it is a provider's to register: laid
    /// first, it adds nothing inside the provider's registrations, and a provider that registers any of it itself — its own
    /// logger factory, its own cache over everyone's — is refused. Every host this server runs in lays the first three before
    /// a provider runs, so those add nothing there; a bare service collection gets them here, once. The log pipeline itself is
    /// registered before the providers too (Program.cs), and so is never inside a provider's registrations.
    /// </summary>
    private static void LayTheFrameworksPlumbing(IServiceCollection services)
    {
        services.AddLogging();
        services.AddOptions();
        if (!services.Any(d => d.ServiceType == typeof(System.Diagnostics.Metrics.IMeterFactory)))
        {
            services.AddMetrics();
        }

        services.AddMemoryCache();
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-01) — whether <paramref name="module"/> may register <paramref name="type"/>: never
    /// a type the frame, the SDK, the identity layer or the host pipeline owns (<see cref="Protected"/>); otherwise its own
    /// type, or its HTTP clients' plumbing (<see cref="ProviderPlumbing"/>), with every type argument one it may register as
    /// well; or the options machinery closed over types it may register. Nothing else.
    /// </summary>
    private static bool MayRegister(IProviderModule module, Type type) => !TouchesProtected(type) && Allowed(module, type);

    private static bool Allowed(IProviderModule module, Type type)
    {
        // An open generic's parameter stands for every type it could be closed over, the frame's among them.
        if (type.IsGenericParameter)
        {
            return false;
        }

        if (type.HasElementType)
        {
            return Allowed(module, type.GetElementType()!);
        }

        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        Type[] arguments = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericArguments() : [];

        if (Owns(module, definition) || ProviderPlumbing.Contains(definition.Assembly.GetName().Name, StringComparer.Ordinal))
        {
            return arguments.All(argument => Allowed(module, argument));
        }

        // Open (IConfigureOptions<>), it would configure every options type there is.
        return IsOptionsMachinery(definition)
            && arguments.Length > 0
            && arguments.All(argument => Allowed(module, argument) || IsStartupValidation(argument));
    }

    private static bool IsOptionsMachinery(Type type) =>
        type.Assembly == typeof(Microsoft.Extensions.Options.IOptions<>).Assembly && type.Namespace == OptionsMachinery;

    private static bool IsStartupValidation(Type type) =>
        type.Assembly == typeof(Microsoft.Extensions.Options.IOptions<>).Assembly && type.FullName == StartupValidation;

    /// <summary>
    /// Whether <paramref name="type"/> is <paramref name="module"/>'s own: declared in the module's own assembly, in the
    /// module's namespace or one under it. The assembly as well as the namespace, because a namespace is only a name: a module
    /// declared in Microsoft.AspNetCore.Http owns nothing of ASP.NET Core's.
    /// </summary>
    private static bool Owns(IProviderModule module, Type type) =>
        type.Assembly == module.GetType().Assembly
        && module.GetType().Namespace is { Length: > 0 } own
        && type.Namespace is { } ns
        && (ns == own || ns.StartsWith(own + ".", StringComparison.Ordinal));

    /// <summary>
    /// contract-005 · G-17 round 3 — what a provider owns is its namespace and those under it, in its own assembly
    /// (<see cref="Owns"/>); so no provider's namespace may hold another provider's in the same assembly, or the outer one
    /// would own the inner one's types and could register over them. Refused at startup, naming both. Two modules in one
    /// namespace of one assembly are one provider's parts, compiled together, and own the same types by design.
    /// </summary>
    public static void VerifyOwnership(IEnumerable<IProviderModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var declared = modules.ToArray();
        var nested = new List<string>();
        foreach (var outer in declared)
        {
            var outerType = outer.GetType();
            if (outerType.Namespace is not { Length: > 0 } own)
            {
                continue;
            }

            foreach (var inner in declared)
            {
                var innerType = inner.GetType();
                if (innerType.Assembly == outerType.Assembly
                    && innerType.Namespace is { } theirs
                    && theirs.StartsWith(own + ".", StringComparison.Ordinal))
                {
                    nested.Add($"'{outer.Name}' ({own}) holds '{inner.Name}' ({theirs})");
                }
            }
        }

        if (nested.Count > 0)
        {
            throw new ConfigurationException(
                "A provider is declared in a namespace that holds another provider's in the same assembly, so it would own that "
                + $"provider's types: {string.Join(", ", nested.Distinct(StringComparer.Ordinal))}. Give each provider a namespace "
                + "of its own that holds no other provider's.");
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

    /// <summary>
    /// A type as a refusal names it: with its namespace, since a name alone is ambiguous (ILogger is Serilog's and
    /// Microsoft's), and its type arguments. contract-005 · G-17 round 3 — a nested type of a generic one has no arity in its
    /// own name, which used to throw here, where startup should have refused naming it.
    /// </summary>
    private static string Describe(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        return type.IsGenericType
            ? $"{Qualified(type)}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>"
            : Qualified(type);
    }

    private static string Qualified(Type type) =>
        type.DeclaringType is { } outer ? $"{Qualified(outer)}.{WithoutArity(type.Name)}"
        : type.Namespace is { Length: > 0 } ns ? $"{ns}.{WithoutArity(type.Name)}"
        : WithoutArity(type.Name);

    private static string WithoutArity(string name) =>
        name.IndexOf('`', StringComparison.Ordinal) is var tick and >= 0 ? name[..tick] : name;
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
