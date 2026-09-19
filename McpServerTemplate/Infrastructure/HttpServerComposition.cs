using System.Threading.RateLimiting;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
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
    /// Registers everything the HTTP server needs and returns the validated identity
    /// configuration.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="configureMcpServer">
    /// Registers the MCP server itself. Passed in because the tool, prompt and resource
    /// registration is provider-specific and belongs beside the providers.
    /// </param>
    /// <param name="configureIdentityForTests">
    /// A hook onto each identity provider's bearer options. Its only intended use is giving a
    /// test an in-process backchannel; nothing in the shipped server passes it.
    /// </param>
    public static AuthenticationConfig AddHttpServer(
        WebApplicationBuilder builder,
        Func<IServiceCollection, IConfiguration, IMcpServerBuilder> configureMcpServer,
        Action<string, JwtBearerOptions>? configureIdentityForTests = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureMcpServer);

        var configuration = builder.Configuration;

        // The HTTP transport's services must be registered before MapMcp can route to them;
        // without this the host builds and then throws on the first route mapping.
        configureMcpServer(builder.Services, configuration)
            // contract-002 · G-11 — stateless streamable HTTP: no session affinity, no
            // Mcp-Session-Id, so any instance can serve any request.
            .WithHttpTransport(options => options.Stateless = true)
            // contract-002 · G-4 — the SDK filters list-tools and call-tool against the caller's
            // authorization, so a principal is shown only what it may use.
            .AddAuthorizationFilters();

        // contract-002 · G-12 — refuse plaintext in Production before anything binds.
        TransportSecurityGuard.Validate(configuration, builder.Environment.IsProduction());

        // Identity is configured and validated before anything binds. A deployment that cannot
        // verify a token must not come up and discover that on its first request.
        var identity = IdentityConfigurationBinder.Bind(configuration);
        builder.Services.AddIdentity(identity, configureIdentityForTests);
        builder.Services.AddAuthorization();

        // contract-002 · G-5 — the binding needs the registered tools, so it is resolved from the
        // container; callers force it at startup rather than on the first call.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(services => ProviderBinding.Create(
            configuration, identity, services.GetServices<McpServerTool>()));

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
        var bindAddress = configuration.GetValue("HttpTransport:BindAddress", "localhost") ?? "localhost";
        var allowedHosts = configuration.GetSection("HttpTransport:AllowedHosts").Get<string[]>();
        if (allowedHosts is not { Length: > 0 })
        {
            allowedHosts = bindAddress is "localhost" or "127.0.0.1" or "::1"
                ? ["localhost", "127.0.0.1", "[::1]"]
                : [bindAddress];
        }

        builder.Services.AddHostFiltering(options =>
        {
            options.AllowedHosts = allowedHosts;
            options.AllowEmptyHosts = false;
            options.IncludeFailureMessage = false;
        });

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

        // Resolved here on purpose: a provider that declares no identity provider, or a tool
        // that declares no provider, stops the server now rather than when a call is wrongly
        // allowed.
        app.Services.GetRequiredService<ProviderBinding>();

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
        app.MapMcp().RequireAuthorization();

        return app;
    }
}
