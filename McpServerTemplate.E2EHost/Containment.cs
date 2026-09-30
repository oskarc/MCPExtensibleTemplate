using System.Reflection;
using McpServerTemplate.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;

namespace McpServerTemplate.E2EHost;

/// <summary>
/// contract-005 · G-10 — containment by identity, not by environment name.
///
/// The test host serves tools no deployment has, among them an irreversible one, so it must never serve a real
/// caller. Refusing the environment name "Production" was the first draw, and naming the environment "Staging" walked
/// round it. What makes a caller real is the identity provider that vouches for it, so that is what is held: the test
/// host starts only if the resource it serves and every identity provider it trusts are hosts under .test — a name no
/// real client resolves and no real identity provider is issued for (RFC 6761) — under any environment name.
///
/// Checked on the fully built configuration, once every source has been read, and then on the server as it will run,
/// before it serves anything. A refusal is a <see cref="ConfigurationException"/>: exit 78, the reason on stderr. What is
/// checked, exactly:
///   - the settings: Authentication:Resource, and each identity provider's Authority and Issuer, are hosts under .test;
///   - every bearer scheme, as its options were finally built: its Authority and MetadataAddress are under .test, and so is
///     every issuer it accepts (ValidIssuer, ValidIssuers);
///   - what clients are told: the resource the metadata publishes, and each authorization server it sends them to;
///   - contract-005 · G-17 round 3 (CON-R3-03) — what decides whether a token is accepted, which the lines above did not
///     read, so a value that reached a scheme some other way than the settings passed (<see cref="Unaccepting"/>):
///       each bearer scheme fetches its keys from its metadata and nowhere else — no signing key, no key resolver, no
///       signature validator, no discovery document or configuration manager of its own, no backchannel handler but the
///       product's or the framework's; it requires signed tokens and validates issuer, audience and lifetime; it forwards
///       authentication to no other scheme; and every validator, event and token handler it holds is the product's or the
///       framework's (the product's own assembly, or one named Microsoft.*, System.* or ModelContextProtocol*);
///       every authentication scheme's handler, the routing scheme's selector, and the services a request's principal is
///       decided by — the authentication service, its handler and scheme providers, the policy evaluator, the claims
///       transformations and the HTTP context factory — are the product's or the framework's;
///       and every step that configures the options authentication and authorization are built from (bearer, policy scheme,
///       authentication, authorization, the MCP scheme's), and the options machinery of each, is the product's or the
///       framework's: a Configure or PostConfigure of JwtBearerOptions in this host's own code is refused, by its origin.
/// What this cannot see is a value set on the built options after this runs, which nothing in this host does.
/// </summary>
public static class Containment
{
    /// <summary>The one suffix the test host's identity may be under.</summary>
    public const string Suffix = ".test";

    /// <summary>The product's own assembly: the one besides the framework's that may shape how a token is accepted.</summary>
    private static readonly Assembly Product = typeof(ConfigurationException).Assembly;

    /// <summary>Refuses to start unless everything identity rests on is under .test.</summary>
    /// <exception cref="ConfigurationException">A resource, authority, issuer or published server is not under .test.</exception>
    public static void Verify(IConfiguration configuration, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(services);

        var outside = new List<string>();
        void Check(string what, string? value)
        {
            if (NotUnderTest(value) is { } why)
            {
                outside.Add($"{what} is '{value}', {why}");
            }
        }

        // The settings, as every source left them.
        Check("Authentication:Resource", configuration["Authentication:Resource"]);
        foreach (var provider in configuration.GetSection("Authentication:IdentityProviders").GetChildren())
        {
            Check($"{provider.Path}:Authority", provider["Authority"]);
            Check($"{provider.Path}:Issuer", provider["Issuer"]);
        }

        // The server, as it will run: every bearer scheme, whatever registered it, after every step that configured it.
        var schemes = services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync().GetAwaiter().GetResult();
        var bearer = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        foreach (var scheme in schemes.Where(s => typeof(JwtBearerHandler).IsAssignableFrom(s.HandlerType)))
        {
            var options = bearer.Get(scheme.Name);
            Check($"The {scheme.Name} scheme's Authority", options.Authority);
            Check($"The {scheme.Name} scheme's MetadataAddress", options.MetadataAddress);
            foreach (var issuer in new[] { options.TokenValidationParameters.ValidIssuer }.Concat(options.TokenValidationParameters.ValidIssuers ?? []))
            {
                Check($"An issuer the {scheme.Name} scheme accepts", issuer);
            }
        }

        // What clients are told: the resource, and the authorization servers they are sent to.
        var metadata = services.GetRequiredService<IOptionsMonitor<McpAuthenticationOptions>>()
            .Get(McpAuthenticationDefaults.AuthenticationScheme).ResourceMetadata;
        Check("The resource the metadata publishes", metadata?.Resource);
        foreach (var server in metadata?.AuthorizationServers ?? [])
        {
            Check("An authorization server the metadata publishes", server);
        }

        // contract-005 · G-17 round 3 (CON-R3-03) — and what decides whether a token is accepted at all.
        var unaccepting = Unaccepting(services, schemes);

        if (outside.Count > 0 || unaccepting.Count > 0)
        {
            throw new ConfigurationException(
                $"The test host serves only identity under {Suffix}, whatever its environment: it serves tools no deployment "
                + "has, and must never serve a real caller. " + string.Join("; ", outside.Concat(unaccepting).Distinct(StringComparer.Ordinal))
                + ". The test host is for the end-to-end environment alone; the shipped server is McpServerTemplate.");
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-03) — every way the server, as it will run, would decide a token's acceptance by
    /// something other than the product's own registration over the settings Verify reads: keys, validators, configuration or a
    /// backchannel of a bearer scheme's own, a scheme forwarding authentication elsewhere, a handler, a service or an options step
    /// from anywhere but the product and the framework. Each is named; none is the test host's to change.
    /// </summary>
    private static List<string> Unaccepting(IServiceProvider services, IEnumerable<AuthenticationScheme> schemes)
    {
        var found = new List<string>();

        // The options authentication and authorization are built from: every step that configures them, and their machinery.
        Configured<JwtBearerOptions>(services, found);
        Configured<PolicySchemeOptions>(services, found);
        Configured<AuthenticationSchemeOptions>(services, found);
        Configured<AuthenticationOptions>(services, found);
        Configured<AuthorizationOptions>(services, found);
        Configured<McpAuthenticationOptions>(services, found);

        // The services a request's principal is decided by.
        using (var scope = services.CreateScope())
        {
            var provider = scope.ServiceProvider;
            foreach (var service in new[]
            {
                typeof(IAuthenticationService), typeof(IAuthenticationHandlerProvider), typeof(IAuthenticationSchemeProvider),
                typeof(IPolicyEvaluator), typeof(Microsoft.AspNetCore.Http.IHttpContextFactory),
            })
            {
                if (provider.GetService(service) is { } implementation && !Ours(implementation.GetType().Assembly))
                {
                    found.Add($"{service.Name} is {Named(implementation.GetType())}");
                }
            }

            foreach (var transformation in provider.GetServices<IClaimsTransformation>().Where(t => !Ours(t.GetType().Assembly)))
            {
                found.Add($"a claims transformation is {Named(transformation.GetType())}");
            }
        }

        // Every scheme's handler; the routing scheme's selector; and every bearer scheme as it will run.
        var policies = services.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>();
        var bearer = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        foreach (var scheme in schemes)
        {
            if (!Ours(scheme.HandlerType.Assembly))
            {
                found.Add($"the {scheme.Name} scheme's handler is {Named(scheme.HandlerType)}");
            }

            if (typeof(PolicySchemeHandler).IsAssignableFrom(scheme.HandlerType) && Foreign(policies.Get(scheme.Name).ForwardDefaultSelector) is { } selector)
            {
                found.Add($"the {scheme.Name} scheme chooses where to forward by {selector}");
            }

            if (typeof(JwtBearerHandler).IsAssignableFrom(scheme.HandlerType))
            {
                found.AddRange(BearerUnaccepting(scheme.Name, bearer.Get(scheme.Name)));
            }
        }

        return found;
    }

    /// <summary>contract-005 · G-17 round 3 (CON-R3-03) — why the bearer scheme <paramref name="scheme"/> would accept a token by anything but its metadata's keys and the product's rules.</summary>
    private static IEnumerable<string> BearerUnaccepting(string scheme, JwtBearerOptions options)
    {
        var parameters = options.TokenValidationParameters;
        if (parameters.IssuerSigningKey is not null || parameters.IssuerSigningKeys?.Any() == true)
        {
            yield return $"the {scheme} scheme is given signing keys of its own, where its keys come from its metadata alone";
        }

        if (parameters.IssuerSigningKeyResolver is not null || parameters.IssuerSigningKeyResolverUsingConfiguration is not null)
        {
            yield return $"the {scheme} scheme resolves its signing keys itself";
        }

        if (parameters.SignatureValidator is not null || parameters.SignatureValidatorUsingConfiguration is not null)
        {
            yield return $"the {scheme} scheme validates signatures itself";
        }

        if (!parameters.RequireSignedTokens || !parameters.ValidateIssuer || !parameters.ValidateAudience || !parameters.ValidateLifetime)
        {
            yield return $"the {scheme} scheme does not require signed tokens, or does not validate their issuer, audience or lifetime";
        }

        if (options.Configuration is not null)
        {
            yield return $"the {scheme} scheme is given a discovery document of its own, and fetches none";
        }

        if (options.ConfigurationManager is { } manager && manager.GetType() != typeof(ConfigurationManager<OpenIdConnectConfiguration>))
        {
            yield return $"the {scheme} scheme's configuration comes from {Named(manager.GetType())}";
        }

        if (options.BackchannelHttpHandler is { } handler && !Ours(handler.GetType().Assembly))
        {
            yield return $"the {scheme} scheme fetches its metadata and keys through {Named(handler.GetType())}";
        }

        if (options.Backchannel is { } backchannel && backchannel.GetType() != typeof(HttpClient))
        {
            yield return $"the {scheme} scheme fetches its metadata and keys through {Named(backchannel.GetType())}";
        }

        if (options.ForwardAuthenticate is not null || options.ForwardDefault is not null || options.ForwardDefaultSelector is not null)
        {
            yield return $"the {scheme} scheme forwards authentication to another scheme";
        }

        if (options.EventsType is { } eventsType && !Ours(eventsType.Assembly))
        {
            yield return $"the {scheme} scheme's events are {Named(eventsType)}";
        }

        if (options.Events is { } events && !Ours(events.GetType().Assembly))
        {
            yield return $"the {scheme} scheme's events are {Named(events.GetType())}";
        }

        foreach (var tokenHandler in options.TokenHandlers.Where(h => !Ours(h.GetType().Assembly)))
        {
            yield return $"the {scheme} scheme reads tokens with {Named(tokenHandler.GetType())}";
        }

        // Every delegate the scheme holds — its token validation's validators and resolvers, its events, its selector — the
        // product's or the framework's.
        foreach (var (holder, what) in new (object? Holder, string What)[] { (parameters, "token validation"), (options.Events, "events"), (options, "options") })
        {
            if (holder is null)
            {
                continue;
            }

            foreach (var property in holder.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0 && p.CanRead))
            {
                if (Foreign(property.GetValue(holder) as Delegate) is { } origin)
                {
                    yield return $"the {scheme} scheme's {what} {property.Name} is {origin}";
                }
            }
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-03) — every step that configures <typeparamref name="TOptions"/> (its Configure and its
    /// PostConfigure steps, by the delegate each runs or, where it runs none, by its own type), and the machinery that builds and
    /// caches them, is the product's or the framework's; each that is not is added to <paramref name="found"/>.
    /// </summary>
    private static void Configured<TOptions>(IServiceProvider services, List<string> found)
        where TOptions : class
    {
        var what = typeof(TOptions).Name;
        foreach (var step in services.GetServices<IConfigureOptions<TOptions>>().Cast<object>().Concat(services.GetServices<IPostConfigureOptions<TOptions>>()))
        {
            var origin = step.GetType().GetProperty("Action")?.GetValue(step) is Delegate action
                ? Foreign(action)
                : Ours(step.GetType().Assembly) ? null : Named(step.GetType());
            if (origin is not null)
            {
                found.Add($"{what} is configured by {origin}");
            }
        }

        foreach (var (machinery, instance) in new (string, object?)[]
        {
            ("factory", services.GetService<IOptionsFactory<TOptions>>()),
            ("monitor", services.GetService<IOptionsMonitor<TOptions>>()),
            ("cache", services.GetService<IOptionsMonitorCache<TOptions>>()),
        })
        {
            if (instance is not null && !Ours(instance.GetType().Assembly))
            {
                found.Add($"{what}'s options {machinery} is {Named(instance.GetType())}");
            }
        }
    }

    /// <summary>Where <paramref name="handler"/>'s code is declared, when any of it is declared outside the product and the framework; null otherwise, and for none.</summary>
    private static string? Foreign(Delegate? handler) =>
        handler?.GetInvocationList()
            .Select(d => d.Method.DeclaringType)
            .FirstOrDefault(t => !Ours(t?.Assembly)) is { } type
            ? $"code of {Named(type)}"
            : null;

    /// <summary>Whether <paramref name="assembly"/> is the product's or the framework's: the product itself, or one named Microsoft.*, System.* or ModelContextProtocol*.</summary>
    private static bool Ours(Assembly? assembly) =>
        assembly is not null
        && (assembly == Product
            || assembly.GetName().Name is { } name
                && (name.StartsWith("Microsoft.", StringComparison.Ordinal)
                    || name.StartsWith("System.", StringComparison.Ordinal)
                    || name is "System" or "mscorlib" or "netstandard"
                    || name.StartsWith("ModelContextProtocol", StringComparison.Ordinal)));

    /// <summary>A type as a refusal names it: its full name and its assembly's.</summary>
    private static string Named(Type type) => $"{type.FullName} ({type.Assembly.GetName().Name})";

    /// <summary>Why <paramref name="value"/> does not name a host under .test, completing "{what} is '{value}', …", or null when it does.</summary>
    private static string? NotUnderTest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "which names no host";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "which is not an absolute URI";
        }

        return Uri.CheckHostName(uri.Host) == UriHostNameType.Dns
            && uri.Host.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Length > Suffix.Length
            ? null
            : $"whose host, {uri.Host}, is not under {Suffix}";
    }
}
