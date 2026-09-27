using System.Text;
using System.Threading.RateLimiting;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Builds the HTTP server: its services, and the middleware order they run in.
///
/// It lives here rather than inside Program.cs so a test can compose the same server in-process
/// and reach the parts a spawned process cannot — chiefly the bearer handler's backchannel,
/// which is how identity is tested without a network (contract-002 · G-10).
///
/// That matters more than tidiness. Half of contract-002's acceptance tests describe what happens
/// to a token, and a token cannot be minted for a server whose signing keys come from a real
/// authority. Either this composition is reachable from a test, or those guarantees are checked
/// by reading.
/// </summary>
public static class HttpServerComposition
{
    /// <summary>
    /// Where MCP answers. contract-005 · G-12 (1) — the resource a token is issued for is
    /// https://{host}/mcp (Authentication:Resource, whose path startup holds to this), and MCP answers
    /// at that same URL. They used to disagree — MCP at the root, the resource at /mcp — and a standard
    /// OAuth client, which checks that the metadata's resource is the URL it connected to, could not
    /// connect at all.
    /// </summary>
    public const string McpPath = "/mcp";

    /// <summary>
    /// Registers everything the HTTP server needs and returns the validated identity
    /// configuration.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="modules">
    /// The provider modules the server may serve. The shipped server and the tests pass them to the
    /// same composition (contract-003 · G-3); only the list differs.
    /// </param>
    /// <param name="configureIdentityForTests">
    /// A hook onto each identity provider's bearer options. Its only intended use is giving a
    /// test an in-process backchannel; nothing in the shipped server passes it.
    /// </param>
    public static AuthenticationConfig AddHttpServer(
        WebApplicationBuilder builder,
        IReadOnlyList<IProviderModule> modules,
        Action<string, JwtBearerOptions>? configureIdentityForTests = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(modules);

        var configuration = builder.Configuration;

        // Identity is validated first: a deployment that cannot verify a token must not come up,
        // and that is the first thing an operator needs to hear. Its services are registered after
        // the MCP server, because providers register before anything of the frame's (G-12).
        var identity = IdentityConfigurationBinder.Bind(configuration);

        // The HTTP transport's services must be registered before MapMcp can route to them;
        // without this the host builds and then throws on the first route mapping.
        builder.Services.AddGovernedMcpServer(configuration, builder.Environment, modules)
            // contract-002 · G-11 — stateless streamable HTTP: no session affinity, no
            // Mcp-Session-Id, so any instance can serve any request.
            .WithHttpTransport(options => options.Stateless = true)
            // contract-002 · G-4 — the SDK's own [Authorize] filters, beneath the frame's gate.
            .AddAuthorizationFilters();

        // contract-002 · G-12 — refuse plaintext in Production before anything binds.
        TransportSecurityGuard.Validate(configuration, builder.Environment.IsProduction());

        builder.Services.AddIdentity(identity, configureIdentityForTests);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(identity);

        // ── Kestrel hardening ──
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 1_048_576; // 1 MB
            kestrel.Limits.MaxConcurrentConnections = 100;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
        });

        // ── Trust proxy headers only from a proxy ──
        // Known networks and proxies default to loopback, so a forwarded header from anywhere
        // else is ignored. Without that, any caller could set X-Forwarded-For and choose which
        // bucket of the per-client rate limiter to spend.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            // contract-002 · G-12 — the proxies an operator declared. Until this was wired,
            // HttpTransport:KnownProxies and :KnownNetworks were read by the transport guard and
            // by nothing else: declaring a trusted proxy satisfied the startup check and left the
            // middleware trusting only its defaults. A setting that is checked but never applied
            // is worse than an absent one, because it answers a question falsely.
            var proxies = configuration.GetSection("HttpTransport:KnownProxies").Get<string[]>() ?? [];
            var networks = configuration.GetSection("HttpTransport:KnownNetworks").Get<string[]>() ?? [];

            if (proxies.Length > 0 || networks.Length > 0)
            {
                // Defaults trust loopback. An operator who names their proxies means those, so the
                // defaults are replaced rather than added to.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
            }

            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
            }

            foreach (var network in networks)
            {
                var parts = network.Split('/', 2);
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                    System.Net.IPAddress.Parse(parts[0]),
                    parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 32));
            }
        });

        // ── Host allowlist ──
        // Rejects requests whose Host header this server does not answer for, which is what
        // stops DNS rebinding from turning a browser on the operator's machine into a client.
        var allowedHosts = AllowedHosts(configuration);

        builder.Services.AddHostFiltering(options => ConfigureHostFilter(options, allowedHosts));

        // ── Per-client (IP) rate limiting ──
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true
                    }));
        });

        // ── Restrictive CORS — deny all cross-origin by default ──
        var allowedOrigins = configuration.GetSection("HttpTransport:AllowedOrigins").Get<string[]>();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (allowedOrigins is { Length: > 0 })
                    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
                else
                    policy.SetIsOriginAllowed(_ => false);
            });
        });

        return identity;
    }

    /// <summary>
    /// The Host header values this server answers for, or a refusal to start.
    ///
    /// contract-005 · G-12 (2) — ASP.NET Core's host filter reads *, 0.0.0.0 and [::] as "any host",
    /// and the server used to fall back to the bind address when no AllowedHosts was set. So a 0.0.0.0
    /// bind — every container deployment — ran with no host filtering at all, and so did an
    /// AllowedHosts that named a wildcard. Both now refuse to start, naming the key. :: is refused with
    /// them: it is the IPv6 any-address written without brackets, and names no host. On a loopback
    /// bind the loopback names are still the default, since nothing off the machine reaches it.
    ///
    /// Refusing those four spellings was not enough, because the filter does not match an entry as
    /// written. It honours *.example.com as every name under example.com, so *.com and *. admitted
    /// nearly any host; it converts each entry to punycode first, and where the runtime has ICU that
    /// folds a full-width asterisk (U+FF0A), or * beside a soft hyphen, into * and switches filtering off; and it
    /// compares names exactly, so mcp.example.com. is another name than mcp.example.com. So an entry
    /// must name one host exactly as the filter will match it: ASCII (an internationalised name in its
    /// punycode form), no * anywhere, no trailing dot, and unchanged by the filter's own conversion.
    /// Then the filter itself is built over the final list and asked whether it lets through a request
    /// for a name nobody could have configured, in case a spelling gets past all of that.
    /// </summary>
    /// <exception cref="ConfigurationException">A non-loopback bind names no host, or an entry does not name exactly one host.</exception>
    public static string[] AllowedHosts(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var bindAddress = configuration.GetValue("HttpTransport:BindAddress", "localhost") ?? "localhost";
        var section = configuration.GetSection("HttpTransport:AllowedHosts");

        foreach (var entry in section.GetChildren())
        {
            if (NotOneHostName(entry.Value ?? string.Empty) is { } why)
            {
                throw new ConfigurationException(
                    $"{entry.Path} is '{entry.Value}', which {why} Name the host names clients reach this server by, "
                    + "for example mcp.example.com. Without them DNS rebinding can turn a browser into a client of "
                    + "this server.");
            }
        }

        var allowedHosts = section.Get<string[]>();
        if (allowedHosts is not { Length: > 0 })
        {
            if (!IsLoopback(bindAddress))
            {
                throw new ConfigurationException(
                    $"HttpTransport:AllowedHosts must name the host names clients reach this server by, because "
                    + $"HttpTransport:BindAddress is '{bindAddress}', which is not a loopback address. With none, every "
                    + "Host header would be answered, and DNS rebinding could turn a browser into a client of this "
                    + "server. For example HttpTransport:AllowedHosts:0=mcp.example.com.");
            }

            allowedHosts = bindAddress is "localhost" or "127.0.0.1" or "::1"
                ? ["localhost", "127.0.0.1", "[::1]"]
                : [bindAddress];
        }

        RefuseHostsNobodyNamed(allowedHosts);
        return allowedHosts;
    }

    /// <summary>
    /// contract-005 · G-12 (2) — the last check on the host allowlist, belt and braces: ASP.NET Core's
    /// host-filtering middleware itself is built over the final list, with the options the server runs it
    /// with, and handed a request for a random name under .invalid, with and without a trailing dot, which
    /// nobody could have meant to allow; it must refuse each with 400. The entry rules in
    /// <see cref="AllowedHosts"/> refuse every spelling known to widen the filter; this refuses one they
    /// do not know. Review round 2 — it used to ask <see cref="HostString.MatchesAny"/> alone, which the
    /// middleware calls only after reading 0.0.0.0, [::] and, through its punycode conversion, a
    /// full-width asterisk as "any host": asked directly, it let those three through.
    /// </summary>
    /// <param name="allowedHosts">The list the host filter is given.</param>
    /// <exception cref="ConfigurationException">The filter would admit a host nobody named.</exception>
    public static void RefuseHostsNobodyNamed(IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);

        var admitted = false;
        var options = new HostFilteringOptions();
        ConfigureHostFilter(options, [.. allowedHosts]);
        var filter = new HostFilteringMiddleware(
            _ =>
            {
                admitted = true;
                return Task.CompletedTask;
            },
            NullLogger<HostFilteringMiddleware>.Instance,
            new FixedOptions<HostFilteringOptions>(options));

        var nobody = $"{Guid.NewGuid():N}.invalid";
        foreach (var probe in new[] { nobody, nobody + "." })
        {
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString(probe);

            // A refusal completes at once: 400, and no body (IncludeFailureMessage is off).
            filter.Invoke(context).GetAwaiter().GetResult();
            if (admitted || context.Response.StatusCode != StatusCodes.Status400BadRequest)
            {
                throw new ConfigurationException(
                    $"HttpTransport:AllowedHosts ({string.Join(", ", allowedHosts)}) admits '{probe}', a name nobody "
                    + "configured: ASP.NET Core's host filter let a request for it through, and would let through any "
                    + "other host the same way. Name the host names clients reach this server by, each exactly, for "
                    + "example mcp.example.com.");
            }
        }
    }

    /// <summary>The host filter's options, as the server runs it and as <see cref="RefuseHostsNobodyNamed"/> tries it.</summary>
    private static void ConfigureHostFilter(HostFilteringOptions options, IList<string> allowedHosts)
    {
        options.AllowedHosts = allowedHosts;
        options.AllowEmptyHosts = false;
        options.IncludeFailureMessage = false;
    }

    /// <summary>Options that are what they are: the host filter asks for a monitor, and nothing here changes.</summary>
    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Why <paramref name="entry"/> does not name exactly one host as the host filter will match it,
    /// completing "…, which", or null when it does.
    /// </summary>
    private static string? NotOneHostName(string entry)
    {
        const string NotAsMatched = "does not name one host as ASP.NET Core's host filter matches it:";

        if (entry.Trim() is "*" or "0.0.0.0" or "[::]" or "::")
        {
            return "is not a host name: *, 0.0.0.0 and [::] switch ASP.NET Core's host filtering off, and :: is the "
                + "IPv6 any-address.";
        }

        if (!Ascii.IsValid(entry))
        {
            // Named by code point: these characters are chosen for looking like others, and a console
            // that cannot show them shows the character they imitate.
            var first = entry.First(c => !char.IsAscii(c));
            return $"{NotAsMatched} it is not ASCII (it holds U+{(int)first:X4}). The filter converts an entry to its "
                + "punycode form before it matches, and where the runtime has ICU the conversion folds characters into "
                + "others: a full-width asterisk, U+FF0A, becomes *, which switches filtering off. Write an "
                + "internationalised name in its punycode form, which begins xn--.";
        }

        if (entry.Contains('*', StringComparison.Ordinal))
        {
            return $"{NotAsMatched} it contains *. The filter reads *.example.com as every name under example.com, so "
                + "*.com, or *., admits nearly any host.";
        }

        if (entry.EndsWith('.'))
        {
            return $"{NotAsMatched} it ends with a dot. The filter compares names exactly, so this is another name "
                + "than the one without the dot, and *. admitted every name written with one.";
        }

        string matched;
        try
        {
            matched = new HostString(entry).ToUriComponent();
        }
        catch (ArgumentException ex)
        {
            return $"{NotAsMatched} the filter cannot convert it to a host name ({ex.Message}).";
        }

        return string.Equals(matched, entry, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{NotAsMatched} the filter converts it to '{matched}' before it matches, so it would not match the name written.";
    }

    private static bool IsLoopback(string bindAddress) =>
        bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(bindAddress.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));

    /// <summary>
    /// The middleware order (contract-001 · G-3), fixed, each stage depending on the ones before:
    ///   forwarded headers  — establishes the real client address and scheme
    ///   HTTPS redirection  — acts on that scheme (a no-op when no HTTPS port is configured)
    ///   host allowlist     — rejects a Host this server does not answer for
    ///   CORS               — answers preflight before anything spends a rate-limit permit
    ///   rate limiter       — partitions on the address forwarded headers established
    ///   health endpoints   — probes carry no credential, and disclose nothing
    ///   origin guard       — a browser-driven request is refused for being cross-origin,
    ///                        before any token is examined
    ///   authentication     — the last gate before any tool is reachable; a caller without a
    ///                        token is challenged with the metadata document rather than refused
    /// </summary>
    public static WebApplication UseHttpServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // contract-003 · G-2, G-12 — every policy, binding and installed filter is checked now, so a
        // mistake stops the server rather than surfacing when a call is wrongly allowed.
        GovernedServer.ValidateAtStartup(app.Services);

        app.UseForwardedHeaders();

        // contract-002 revision, 2026-09-20 — roadmap P1.6 names UseHsts() and it was absent from
        // the source entirely: in no clause, no test and no verification pass. It emits
        // Strict-Transport-Security on HTTPS responses only, so a deployment terminating TLS at a
        // proxy tells the browser never to try plaintext again, and the loopback tests that run
        // Production over http are unaffected.
        app.UseHsts();
        app.UseHttpsRedirection();
        app.UseHostFiltering();
        app.UseCors();
        app.UseRateLimiter();
        app.UseHealthEndpoints(app.Lifetime);
        app.UseMiddleware<OriginGuardMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        // contract-005 · G-12 (1) — at the resource's own path. The challenge names the metadata at
        // /.well-known/oauth-protected-resource/mcp, which is also RFC 9728's location for the
        // resource, so the two cannot name different documents.
        app.MapMcp(McpPath).RequireAuthorization();

        return app;
    }
}
