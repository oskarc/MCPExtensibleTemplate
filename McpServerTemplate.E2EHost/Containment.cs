using McpServerTemplate.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
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
/// Checked on the fully built configuration, once every source has been read, and then on the server as it will run:
/// the options each bearer scheme was finally built with, where it fetches keys and which issuer it pins, and the
/// metadata that sends clients to an authorization server. A value that reached the server any other way than the
/// settings this reads is caught there. A refusal is a <see cref="ConfigurationException"/>: exit 78, the reason on
/// stderr.
/// </summary>
public static class Containment
{
    /// <summary>The one suffix the test host's identity may be under.</summary>
    public const string Suffix = ".test";

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

        if (outside.Count > 0)
        {
            throw new ConfigurationException(
                $"The test host serves only identity under {Suffix}, whatever its environment: it serves tools no deployment "
                + "has, and must never serve a real caller. " + string.Join("; ", outside.Distinct(StringComparer.Ordinal))
                + ". The test host is for the end-to-end environment alone; the shipped server is McpServerTemplate.");
        }
    }

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
