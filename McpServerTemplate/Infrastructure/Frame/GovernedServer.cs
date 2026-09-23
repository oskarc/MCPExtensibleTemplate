using System.Reflection;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Frame;

/// <summary>
/// The one place the MCP server is composed (contract-003 · G-3). The HTTP server, the stdio
/// Development mode and the tests call this and nothing else, so the checks a test exercises are
/// the checks the shipped server runs. The test server that used to declare its own copy of the
/// filters is gone; that copy had already drifted from the real one.
///
/// Order is part of the guarantee: settings are checked, then each enabled provider registers
/// under watch, then the frame registers itself last, so no provider runs after it.
/// </summary>
public static class GovernedServer
{
    /// <summary>The limit, per caller per minute, when <c>Limits:PerPrincipalPerMinute</c> is not set.</summary>
    public const int DefaultPerPrincipalPerMinute = 120;

    /// <summary>Composes the governed MCP server and returns its builder for the transport to be added.</summary>
    public static IMcpServerBuilder AddGovernedMcpServer(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IReadOnlyList<IProviderModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(modules);

        SettingsAllowlist.Validate(configuration, modules);
        var enabled = SelectEnabled(configuration, environment, modules);

        foreach (var module in enabled)
        {
            FrameIntegrity.Register(module, services, configuration);
        }

        // ── The frame, registered last ──
        var manifest = new FrameManifest();
        services.AddSingleton(manifest);
        services.AddSingleton(new EnabledProviders(enabled));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(LimitStoreFor(configuration, environment));
        var confirmation = ConfirmationFor(configuration);
        if (confirmation is not null)
        {
            services.AddSingleton(confirmation);
        }

        var perPrincipal = ConfigurationGuard.IntegerInRange(
            configuration, "Limits:PerPrincipalPerMinute", minimum: 1, maximum: 1_000_000,
            fallback: DefaultPerPrincipalPerMinute, because: "requests per caller per minute");

        services.AddSingleton(sp => PolicyRegistry.Build(
            sp,
            enabled,
            sp.GetRequiredService<AuthenticationConfig>(),
            configuration,
            sp.GetRequiredService<IOptions<McpServerOptions>>().Value,
            confirmationConfigured: sp.GetService<ConfirmationService>() is not null));

        services.AddSingleton(sp => new RequestGate(
            sp.GetRequiredService<PolicyRegistry>(),
            sp.GetRequiredService<ILimitStore>(),
            sp.GetRequiredService<TimeProvider>(),
            perPrincipal,
            sp.GetService<ConfirmationService>(),
            sp.GetRequiredService<ILogger<RequestGate>>()));

        var builder = services.AddMcpServer(options => options.ServerInfo = new()
        {
            Name = "McpServerTemplate",
            Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0",
        });

        foreach (var module in enabled)
        {
            // The casts choose the overload that reads the types' methods. Each of these has a
            // generic sibling taking any object as the tool target, and a list of types binds to it
            // more closely: it registers the list's own methods — none — and serves nothing, with
            // no error. The startup check below now refuses that outcome as well.
            builder
                .WithTools((IEnumerable<Type>)module.ToolTypes)
                .WithResources((IEnumerable<Type>)module.ResourceTypes)
                .WithPrompts((IEnumerable<Type>)module.PromptTypes);
        }

        return builder
            .WithMessageFilters(filters => filters.AddIncomingFilter(manifest.OwnGate(RequestGate.RequestKinds())))
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(manifest.Own(ToolCallLoggingFilter.Create()));
                filters.AddCallToolFilter(manifest.Own(RequestGate.CallTool()));
                filters.AddListToolsFilter(manifest.Own(RequestGate.ListTools()));
                filters.AddListResourcesFilter(manifest.Own(RequestGate.ListResources()));
                filters.AddListResourceTemplatesFilter(manifest.Own(RequestGate.ListResourceTemplates()));
                filters.AddReadResourceFilter(manifest.Own(RequestGate.ReadResource()));
                filters.AddListPromptsFilter(manifest.Own(RequestGate.ListPrompts()));
                filters.AddGetPromptFilter(manifest.Own(RequestGate.GetPrompt()));
                filters.AddCompleteFilter(manifest.Own(RequestGate.Complete()));
            });
    }

    /// <summary>
    /// Builds and checks everything that can only be checked once the container exists, and logs
    /// what was installed. Call at startup, before the server accepts anything; a failure is a
    /// <see cref="ConfigurationException"/>, which the host turns into exit code 78.
    /// </summary>
    public static string ValidateAtStartup(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var manifest = services.GetRequiredService<FrameManifest>();
        var enabled = services.GetRequiredService<EnabledProviders>();

        FrameIntegrity.VerifyInstalled(options, manifest, enabled.Modules);
        var registry = services.GetRequiredService<PolicyRegistry>();
        services.GetRequiredService<RequestGate>();

        var description = FrameManifest.Describe(options, manifest, registry);
        services.GetRequiredService<ILogger<RequestGate>>().LogInformation(
            "Frame installed: limits={Limits} providers={Providers} :: {Manifest}",
            services.GetRequiredService<ILimitStore>().Description,
            string.Join(",", enabled.Modules.Select(m => m.Name)),
            description);
        return description;
    }

    private static IReadOnlyList<IProviderModule> SelectEnabled(IConfiguration configuration, IHostEnvironment environment, IReadOnlyList<IProviderModule> modules)
    {
        var names = configuration.GetSection("Providers:Enabled").Get<string[]>();
        if (names is null)
        {
            if (environment.IsDevelopment())
            {
                return modules;
            }

            throw new ConfigurationException(
                "Providers:Enabled is required outside Development: name the providers this deployment serves. "
                + $"Available: {string.Join(", ", modules.Select(m => m.Name))}.");
        }

        var unknown = names.Where(n => modules.All(m => m.Name != n)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ConfigurationException(
                $"Providers:Enabled names {string.Join(", ", unknown)}, which is not a provider this server has. "
                + $"Available: {string.Join(", ", modules.Select(m => m.Name))}.");
        }

        return [.. modules.Where(m => names.Contains(m.Name, StringComparer.Ordinal))];
    }

    private static ILimitStore LimitStoreFor(IConfiguration configuration, IHostEnvironment environment)
    {
        var redis = configuration["Limits:Redis"];
        if (!string.IsNullOrWhiteSpace(redis))
        {
            return RedisLimitStore.Connect(redis);
        }

        if (environment.IsDevelopment())
        {
            return new InMemoryLimitStore(TimeProvider.System);
        }

        throw new ConfigurationException(
            "Limits:Redis is required outside Development. Per-caller limits must hold on every instance; "
            + "in memory they hold per instance only, and a caller escapes them by spreading requests.");
    }

    private static ConfirmationService? ConfirmationFor(IConfiguration configuration)
    {
        var key = configuration["Confirmation:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            return new ConfirmationService(Convert.FromBase64String(key), TimeProvider.System);
        }
        catch (FormatException)
        {
            throw new ConfigurationException("Confirmation:Key must be base64, for example the output of 'openssl rand -base64 32'.");
        }
    }
}

/// <summary>The provider modules this deployment serves.</summary>
public sealed record EnabledProviders(IReadOnlyList<IProviderModule> Modules);
