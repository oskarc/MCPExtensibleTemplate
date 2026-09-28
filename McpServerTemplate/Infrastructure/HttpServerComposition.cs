using System.Buffers;
using System.Text;
using System.Threading.RateLimiting;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
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

        // ── Data protection: in memory, and no key ring (contract-005 · G-18) ──
        KeepNoKeyRing(builder.Services);

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
        var trusted = TrustedProxies(configuration);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            // contract-002 · G-12 — the proxies an operator declared. Until this was wired,
            // HttpTransport:KnownProxies and :KnownNetworks were read by the transport guard and
            // by nothing else: declaring a trusted proxy satisfied the startup check and left the
            // middleware trusting only its defaults. A setting that is checked but never applied
            // is worse than an absent one, because it answers a question falsely.
            if (trusted.Proxies.Count > 0 || trusted.Networks.Count > 0)
            {
                // Defaults trust loopback. An operator who names their proxies means those, so the
                // defaults are replaced rather than added to.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
            }

            foreach (var proxy in trusted.Proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in trusted.Networks)
            {
                options.KnownIPNetworks.Add(network);
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
    /// contract-005 · G-18 — data protection, configured explicitly. Authentication registers it as a side effect,
    /// and nothing in this server protects data with it: JWT bearer, host filtering, CORS, rate limiting, the health
    /// endpoints and the MCP SDK reference none of it (in the framework, only the cookie and remote sign-in ticket
    /// formats do, unused here). Left as it came, the framework loaded a key ring at startup — on Windows the user's
    /// own, from the profile; in the image a key written unencrypted under /home/app/.aspnet, with two warnings saying
    /// so. An in-memory, ephemeral provider instead, with keys of its own that no disk sees, and no key ring loaded at
    /// startup: the framework's hosted service that loads one (DataProtectionHostedService) is all that ever read a key
    /// repository here, so it goes.
    /// </summary>
    private static void KeepNoKeyRing(IServiceCollection services)
    {
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        foreach (var loadsKeyRing in services
            .Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService
                && d.ImplementationType?.Assembly == typeof(DataProtectionOptions).Assembly)
            .ToArray())
        {
            services.Remove(loadsKeyRing);
        }
    }

    /// <summary>
    /// contract-002 · G-12 — the proxies and networks forwarded headers are trusted from, HttpTransport:KnownProxies and
    /// HttpTransport:KnownNetworks, read once, at composition.
    ///
    /// contract-005 · G-12 (2), review round 9 — every entry read at startup, and one that is not what its key says
    /// refused naming the key and index: they used to be parsed only as the pipeline was built, and stopped the server
    /// with exit 70 and a stack trace. An IPv4 address, alone or as a network's, is held to the standard form, as the bind
    /// address is (010.0.0.1 would be read as 8.0.0.1). A network's prefix length is 0-32 for IPv4 and 0-128 for IPv6. An
    /// IPv4 address alone is that one address, as before; an IPv6 address alone was read with the IPv4 default, /32, and
    /// IPNetwork clears the bits past a prefix without a word, so fd00::1 stood for all of fd00::/32 — it is refused, and
    /// asked for its prefix length.
    /// </summary>
    /// <exception cref="ConfigurationException">An entry is not an IP address, or a network, as its key says.</exception>
    public static (IReadOnlyList<System.Net.IPAddress> Proxies, IReadOnlyList<System.Net.IPNetwork> Networks) TrustedProxies(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var proxies = new List<System.Net.IPAddress>();
        foreach (var entry in configuration.GetSection("HttpTransport:KnownProxies").GetChildren())
        {
            var value = entry.Value ?? string.Empty;
            if (!System.Net.IPAddress.TryParse(value, out var address))
            {
                throw new ConfigurationException(
                    $"{entry.Path} is '{value}', which is not an IP address. Write the address of the proxy in front of this "
                    + "server, as 10.0.0.2, or its network in HttpTransport:KnownNetworks, as 10.0.0.0/8.");
            }

            if (NotInStandardForm(value, address) is { } reading)
            {
                throw new ConfigurationException($"{entry.Path} is '{value}', which {reading}. {StandardIPv4}");
            }

            proxies.Add(address);
        }

        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in configuration.GetSection("HttpTransport:KnownNetworks").GetChildren())
        {
            var value = entry.Value ?? string.Empty;
            var slash = value.IndexOf('/', StringComparison.Ordinal);
            var written = slash < 0 ? value : value[..slash];
            if (!System.Net.IPAddress.TryParse(written, out var address))
            {
                throw new ConfigurationException(
                    $"{entry.Path} is '{value}', whose address, '{written}', is not an IP address. Write a network in CIDR "
                    + "form, as 10.0.0.0/8.");
            }

            if (NotInStandardForm(written, address, slash < 0 ? string.Empty : value[slash..]) is { } reading)
            {
                throw new ConfigurationException($"{entry.Path} is '{value}', whose address {reading}. {StandardIPv4}");
            }

            var bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            int prefix;
            if (slash < 0)
            {
                if (bits == 128)
                {
                    throw new ConfigurationException(
                        $"{entry.Path} is '{value}', an IPv6 address with no prefix length, which a network needs: read with "
                        + $"the IPv4 default, /32, it stood for far more than this address. Write {value}/128 for the one "
                        + "address, or its network, as fd00::/8.");
                }

                prefix = 32;
            }
            else if (!int.TryParse(value.AsSpan(slash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out prefix)
                || prefix > bits)
            {
                throw new ConfigurationException(
                    $"{entry.Path} is '{value}', whose prefix length is outside 0-{bits}. Write a network in CIDR form, as "
                    + $"{(bits == 32 ? "10.0.0.0/8" : "fd00::/8")}.");
            }

            networks.Add(new System.Net.IPNetwork(address, prefix));
        }

        return (proxies, networks);
    }

    /// <summary>What <see cref="NotInStandardForm"/> is followed by in a refusal.</summary>
    private const string StandardIPv4 = "An IPv4 address is written in its standard form: four decimal parts, without leading zeros.";

    /// <summary>
    /// contract-005 · G-12 (2), review rounds 8 and 9 — how <paramref name="text"/>, an IPv4 address not written in its
    /// standard form, would be read, completing "…, which": "would be read as 8.0.0.1; write 8.0.0.1, or 10.0.0.1 if that
    /// is what you meant", each written form followed by <paramref name="suffix"/>; or null when it is in its standard
    /// form, or IPv6. The parser reads the other spellings as some address, not always the one meant (010.0.0.1 is octal).
    /// </summary>
    private static string? NotInStandardForm(string text, System.Net.IPAddress parsed, string suffix = "")
    {
        if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || text == parsed.ToString())
        {
            return null;
        }

        var read = parsed.ToString();
        var meant = DecimalReading(text);
        return $"would be read as {read}; write {read}{suffix}"
            + (meant is not null && meant != read ? $", or {meant}{suffix} if that is what you meant" : string.Empty);
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
    ///
    /// Review round 5 — the opposite mistake, an entry no request can match, left a server that answered
    /// every request 400, its own name included, without saying why: a setting it would never act on,
    /// which the frame refuses wherever it finds one. An unset variable in a compose file leaves an entry
    /// empty, and an entry with a port, with whitespace around it or an unbracketed IPv6 address never
    /// matches either. So each entry must also be one a request can match, which
    /// <see cref="NoRequestMatches"/> asks of Kestrel's rule and of the filter itself.
    ///
    /// Review round 5, addendum — these rules hold for the list the filter runs with, wherever it came from:
    /// one written out, or the one a loopback bind gives (<see cref="LoopbackDefault"/>), which is refused
    /// naming HttpTransport:BindAddress. And a bind counts as loopback only where Kestrel binds it to
    /// loopback (<see cref="IsLoopback"/>).
    ///
    /// Review round 6 — the bind address is refused first, whatever the allowed hosts, where the server would not
    /// act on it as written. Review round 7 — which is to say unless it is one of the forms declared for it
    /// (<see cref="BindAddressRefusal"/>): an IP address Kestrel reads as one, localhost, or an explicit all-interfaces
    /// spelling; the refusal of anything else says what Kestrel would do with it.
    /// </summary>
    /// <exception cref="ConfigurationException">The bind address is not one of its accepted forms, a non-loopback bind names no host, or an entry does not name exactly one host a request can match.</exception>
    public static string[] AllowedHosts(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var bindAddress = configuration.GetValue("HttpTransport:BindAddress", "localhost") ?? "localhost";
        if (BindAddressRefusal(bindAddress) is { } refusal)
        {
            throw new ConfigurationException(refusal);
        }

        // Each entry the filter will run with, how a refusal names it, and what it tells the operator to set.
        (string Host, string Named, string Remedy)[] entries;
        var configured = configuration.GetSection("HttpTransport:AllowedHosts").GetChildren().ToArray();
        if (configured.Length > 0)
        {
            entries = [.. configured.Select(entry => (
                entry.Value ?? string.Empty,
                $"{entry.Path} is '{entry.Value}'",
                "Name the host names clients reach this server by"))];
        }
        else if (IsLoopback(bindAddress))
        {
            entries = [.. LoopbackDefault(bindAddress).Select(host => (
                host,
                $"HttpTransport:BindAddress is '{bindAddress}', a loopback address, so the host filter allows '{host}' by default",
                "Set HttpTransport:AllowedHosts to the host names clients reach this server by"))];
        }
        else
        {
            // Only an accepted form gets here: an IP address that is not loopback, or an all-interfaces spelling.
            var which = bindAddress is "*" or "+"
                || (System.Net.IPAddress.TryParse(bindAddress, out var any)
                    && (any.Equals(System.Net.IPAddress.Any) || any.Equals(System.Net.IPAddress.IPv6Any)))
                ? "which listens on every interface"
                : "which is not a loopback address";
            throw new ConfigurationException(
                $"HttpTransport:AllowedHosts must name the host names clients reach this server by, because "
                + $"HttpTransport:BindAddress is '{bindAddress}', {which}. With none, every "
                + "Host header would be answered, and DNS rebinding could turn a browser into a client of this "
                + "server. For example HttpTransport:AllowedHosts:0=mcp.example.com.");
        }

        foreach (var (host, named, remedy) in entries)
        {
            if (NotOneHostName(host) is { } why)
            {
                throw new ConfigurationException(
                    $"{named}, which {why} {remedy}, for example mcp.example.com. Without them DNS rebinding can turn a "
                    + "browser into a client of this server.");
            }

            if (NoRequestMatches(host) is { } never)
            {
                throw new ConfigurationException(
                    $"{named}, which no request can match: {never} {remedy}, each on its own and without a port, for "
                    + "example mcp.example.com.");
            }
        }

        string[] allowedHosts = [.. entries.Select(entry => entry.Host)];
        RefuseHostsNobodyNamed(allowedHosts);
        return allowedHosts;
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 7 — why the server refuses <paramref name="bindAddress"/>, whatever the
    /// allowed hosts, or null when it accepts it. A positive allowlist, declared rather than inferred: exactly
    ///   (a) an IP address Kestrel reads as one — IPAddress.TryParse, which its address binder applies to the same
    ///       text: IPv4, dotted or in a short form it reads as the same address (127.1), and IPv6, bare or bracketed;
    ///       with a zone only on a link-local address, where Kestrel binds that interface's address (a wrong zone fails
    ///       to bind). On any other address a zone is not what Kestrel binds by: on Linux ::1%1 binds ::1 and the zone
    ///       is ignored, and on Windows it cannot be bound at all (10049);
    ///   (b) localhost, in any case;
    ///   (c) an explicit all-interfaces spelling: 0.0.0.0, ::, [::], * or +.
    /// Within them, as round 6 and its addendum left it: an empty value; one that carries a port, since the server
    /// listens on the port HttpTransport:Port sets, and IPAddress reads [::1]:9999 as ::1, dropping 9999 without a word;
    /// and an IPv4-mapped address, bracketed or not, which Kestrel binds on an IPv6-only socket
    /// (SocketTransportOptions.CreateDefaultBoundListenSocket) that cannot take one. Anything else is refused by
    /// <see cref="NotAnAcceptedForm"/>, saying what Kestrel would do with it.
    ///
    /// Review round 8 — an IPv4 address is accepted in its standard form only, as IPAddress writes it: four decimal
    /// parts without leading zeros. The parser reads the others as some address, not always the one meant (010.0.0.1 is
    /// octal, 8.0.0.1), and the refusal shows how it would be read. And a link-local address needs its interface's zone:
    /// the parser drops one it cannot resolve without a word (%0, an unknown name, a percent-encoded one, and on Windows
    /// any name at all), leaving an address Linux cannot bind (invalid argument) and Windows places on whichever
    /// interface holds it. A failure to bind an accepted address is refused too, once Kestrel meets it
    /// (<see cref="BindFailure"/>).
    /// </summary>
    private static string? BindAddressRefusal(string bindAddress)
    {
        // Review round 6, addendum — an unset variable in a compose file leaves the address empty, and the server
        // passed its checks and stopped in Kestrel instead ("Invalid url", exit 70); whitespace alone reads as nothing.
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return $"HttpTransport:BindAddress is empty{(bindAddress.Length == 0 ? string.Empty : " but for whitespace")}, as an unset "
                + "variable in a compose file leaves it: HttpTransport__BindAddress=${MCP_BIND} with MCP_BIND not set. Write the "
                + "address to listen on: 127.0.0.1 for this machine alone, or 0.0.0.0 behind a proxy, with "
                + "HttpTransport:AllowedHosts set.";
        }

        if (AddressBeforeAPort(bindAddress) is { } address)
        {
            return $"HttpTransport:BindAddress is '{bindAddress}', which carries a port. The server listens on the port "
                + $"HttpTransport:Port sets, and would ignore this one. Write the address alone, as {address}, and set the "
                + "port in HttpTransport:Port.";
        }

        if (System.Net.IPAddress.TryParse(bindAddress, out var parsed))
        {
            if (NotInStandardForm(bindAddress, parsed) is { } reading)
            {
                return $"HttpTransport:BindAddress is '{bindAddress}', which {reading}. {StandardIPv4}";
            }

            // Review round 9 — an address no connection can be accepted on: the server bound 255.255.255.255 and logged
            // "Now listening on http://255.255.255.255:3001", and nobody could connect.
            var broadcast = parsed.Equals(System.Net.IPAddress.Broadcast);
            if (broadcast || parsed.IsIPv6Multicast
                || (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (parsed.GetAddressBytes()[0] & 0xF0) == 0xE0))
            {
                return $"HttpTransport:BindAddress is '{bindAddress}', a {(broadcast ? "broadcast" : "multicast")} address, "
                    + "which cannot accept connections: a server listens on a unicast address. Write one: 127.0.0.1, or "
                    + "[::1], for this machine alone, or 0.0.0.0, or [::], for every interface, behind a proxy, with "
                    + "HttpTransport:AllowedHosts set.";
            }

            if (parsed.IsIPv4MappedToIPv6)
            {
                return $"HttpTransport:BindAddress is '{bindAddress}', an IPv4-mapped IPv6 address, which Kestrel cannot listen "
                    + "on: it binds any IPv6 address but [::] on an IPv6-only socket, which cannot take an IPv4 one. Write the "
                    + $"IPv4 address itself, {parsed.MapToIPv4()}.";
            }

            if (parsed.IsIPv6LinkLocal && parsed.ScopeId == 0)
            {
                var alone = parsed.ToString();
                var percent = bindAddress.IndexOf('%', StringComparison.Ordinal);
                var zone = percent < 0
                    ? " with no zone"
                    : $", whose zone {bindAddress[percent..].TrimEnd(']')} the address parser dropped, unable to resolve it";
                return $"HttpTransport:BindAddress is '{bindAddress}', a link-local address{zone}. A link-local address needs "
                    + $"its interface's zone, as {alone}%eth0 or {alone}%2, to say which interface's address it is. Write it "
                    + "with the zone of the interface that holds it.";
            }

            if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && parsed.ScopeId != 0 && !parsed.IsIPv6LinkLocal)
            {
                // Review round 8 — Kestrel dual-binds :: itself alone, so ::%1 is bound for IPv6 alone.
                var alone = new System.Net.IPAddress(parsed.GetAddressBytes());
                var onLinux = alone.Equals(System.Net.IPAddress.IPv6Any)
                    ? "on Linux it binds :: for IPv6 alone and ignores the zone, where :: without one listens on IPv4 as well"
                    : $"on Linux it binds {alone} and ignores the zone";
                return $"HttpTransport:BindAddress is '{bindAddress}', an address with a zone, which is not what Kestrel binds "
                    + $"it by: {onLinux}, and on Windows it cannot bind it at all. A zone belongs on a link-local address "
                    + $"alone. Write the address without it, {alone}.";
            }

            return null;
        }

        return bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) || bindAddress is "*" or "+"
            ? null
            : NotAnAcceptedForm(bindAddress);
    }

    /// <summary>
    /// The address <paramref name="bindAddress"/> gives before a port, or null when it carries none: an IPv6 address in
    /// brackets followed by a colon, or text that is not an address itself before its only colon, the port being digits
    /// or nothing. IPEndPoint splits a port off the same way; an unbracketed IPv6 address, such as ::1:9999, is an
    /// address, its colons its own.
    /// </summary>
    private static string? AddressBeforeAPort(string bindAddress)
    {
        int colon;
        if (bindAddress.StartsWith('['))
        {
            colon = bindAddress.IndexOf("]:", StringComparison.Ordinal) is var close and >= 0 ? close + 1 : -1;
        }
        else
        {
            colon = bindAddress.IndexOf(':');
            if (System.Net.IPAddress.TryParse(bindAddress, out _) || colon != bindAddress.LastIndexOf(':'))
            {
                colon = -1;
            }
        }

        return colon > 0 && bindAddress.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0 ? bindAddress[..colon] : null;
    }

    /// <summary>
    /// The address <paramref name="text"/> would be as four decimal parts, leading zeros read as nothing, or null when
    /// it is not four parts of decimal digits each of at most 255.
    /// </summary>
    private static string? DecimalReading(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)))
        {
            return null;
        }

        var values = parts.Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return values.All(value => value <= 255) ? string.Join('.', values) : null;
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 8 — a failure to bind the configured address, as a refusal to start, or null
    /// when <paramref name="exception"/> is not one. Kestrel's socket transport raises what the operating system said as
    /// it binds: the address and port in use (which Kestrel wraps: "Failed to bind to address ...: address already in
    /// use"), an address not available on this machine, an invalid argument (Linux, for a link-local address with no
    /// interface), or permission denied. The server used to exit 70 on each, "MCP Server terminated unexpectedly", with
    /// a stack trace. Only a socket error raised inside Kestrel's socket transport counts; anything else stays
    /// unexpected, exit 70.
    /// </summary>
    public static ConfigurationException? BindFailure(Exception exception, string bindAddress, int port)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.AspNetCore.Connections.AddressInUseException)
            {
                return new ConfigurationException(InUse(bindAddress, port));
            }

            if (e is System.Net.Sockets.SocketException socket && RaisedByKestrelsTransport(socket))
            {
                const string Instead = "Write an address this machine holds: 127.0.0.1, or [::1], for this machine alone, or "
                    + "0.0.0.0 for every interface, behind a proxy, with HttpTransport:AllowedHosts set.";
                return new ConfigurationException(socket.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.AddressAlreadyInUse => InUse(bindAddress, port),
                    System.Net.Sockets.SocketError.AddressNotAvailable =>
                        $"HttpTransport:BindAddress is '{bindAddress}', and the server cannot listen there: the address is not "
                        + $"available on this machine, which holds no interface with it. {Instead}",
                    System.Net.Sockets.SocketError.InvalidArgument =>
                        $"HttpTransport:BindAddress is '{bindAddress}', and the server cannot listen there: the operating system "
                        + $"refused the address as an invalid argument. {Instead}",
                    System.Net.Sockets.SocketError.AccessDenied =>
                        $"HttpTransport:Port is {port}, and the server may not listen on it at HttpTransport:BindAddress "
                        + $"'{bindAddress}': permission denied. A port below 1024 needs privileges this process does not have, "
                        + "and the operating system can reserve others. Set HttpTransport:Port to a free port of 1024 or above, "
                        + "such as 3001.",
                    _ => $"HttpTransport:BindAddress is '{bindAddress}' and HttpTransport:Port is {port}, and the server cannot "
                        + $"listen there: the operating system refused it ({socket.SocketErrorCode}: {socket.Message}).",
                });
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="exception"/> is a failure to bind that <see cref="BindFailure"/> refuses.</summary>
    public static bool IsBindFailure(Exception exception) => BindFailure(exception, string.Empty, 0) is not null;

    private static string InUse(string bindAddress, int port) =>
        $"HttpTransport:Port is {port}, and the server cannot listen on it at HttpTransport:BindAddress '{bindAddress}': "
        + "the address and port are already in use, by another process or another server. Stop that one, or set "
        + "HttpTransport:Port to a free port.";

    /// <summary>Whether <paramref name="socket"/> was raised inside Kestrel's socket transport, binding.</summary>
    private static bool RaisedByKestrelsTransport(System.Net.Sockets.SocketException socket) =>
        new System.Diagnostics.StackTrace(socket, fNeedFileInfo: false).GetFrames()
            .Any(frame => frame.GetMethod()?.DeclaringType?.Assembly
                == typeof(Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions).Assembly);

    /// <summary>
    /// contract-005 · G-12 (2), review round 7 — the refusal of text that is none of the accepted forms, saying what
    /// Kestrel would do with it, as its address binder reads http://{bindAddress}:{port} (AddressBinder.ParseAddress,
    /// BindingAddress.Parse): a Unix socket or a named pipe for unix:/ or pipe:/, and a path for whatever follows a /,
    /// none of which it starts on; loopback for a name under .localhost; and every interface for anything else, a host
    /// name included, which it does not look up.
    /// </summary>
    private static string NotAnAcceptedForm(string bindAddress)
    {
        const string Instead = "For this machine alone, write 127.0.0.1, or [::1]; for every interface, write 0.0.0.0, behind a "
            + "proxy, with HttpTransport:AllowedHosts naming the hosts clients use; and put the port in HttpTransport:Port.";
        var what = $"HttpTransport:BindAddress is '{bindAddress}'";

        if (bindAddress.StartsWith("unix:/", StringComparison.Ordinal))
        {
            return $"{what}. Kestrel would read it as the path of a Unix socket, not an address, and would not start. {Instead}";
        }

        if (bindAddress.StartsWith("pipe:/", StringComparison.Ordinal))
        {
            return $"{what}. Kestrel would read it as the name of a named pipe, not an address, and would not start. {Instead}";
        }

        BindingAddress kestrel;
        try
        {
            kestrel = BindingAddress.Parse($"http://{bindAddress}:1");
        }
        catch (FormatException)
        {
            return $"{what}. Kestrel cannot read it as an address, and would not start. {Instead}";
        }

        if (!string.IsNullOrEmpty(kestrel.PathBase))
        {
            return $"{what}. Kestrel would read what follows its / as a path, and would not start: a bind address has no path, "
                + $"and no scheme. {Instead}";
        }

        if (kestrel.Host.Length > ".localhost".Length && kestrel.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"{what}, a name under .localhost. Kestrel reads it as localhost and listens on loopback, not on the name. "
                + "Write localhost, or 127.0.0.1, or [::1].";
        }

        var name = Uri.CheckHostName(bindAddress) == UriHostNameType.Dns
            ? " It does not look a host name up, so it would not listen on that name's address."
            : string.Empty;
        return $"{what}. Kestrel does not read '{bindAddress}' as an address and would listen on every interface.{name} {Instead}";
    }

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 addendum — the host allowlist a loopback bind gives when none is set.
    /// Where Kestrel binds the addresses the loopback names reach — localhost, or 127.0.0.1 or ::1 however written
    /// (0::1, [::1], 127.1) — it is those names. It used to be them only for the spellings localhost, 127.0.0.1 and
    /// ::1, and otherwise the address as written, which for 0::1, unbracketed, no request can match: the server
    /// started and answered every request 400.
    ///
    /// Review round 5, addendum 2 — any other IPv4 loopback address the names do not reach: Kestrel binds that
    /// address alone, and a request reaching it carries it as its Host in its standard form, so it is that alone,
    /// as IPAddress writes it (127.2 gives 127.0.0.2). The one other IPv6 loopback address, ::ffff:127.0.0.1, never
    /// reaches here: review round 6 refuses an IPv4-mapped bind address, bracketed or not, first
    /// (<see cref="BindAddressRefusal"/>); were one to, it would stay as written, and the entry rules would refuse it.
    /// </summary>
    private static string[] LoopbackDefault(string bindAddress)
    {
        if (bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(bindAddress, out var address)
                && (address.Equals(System.Net.IPAddress.Loopback) || address.Equals(System.Net.IPAddress.IPv6Loopback))))
        {
            return ["localhost", "127.0.0.1", "[::1]"];
        }

        return address?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? [address.ToString()] : [bindAddress];
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
        var filter = HostFilter([.. allowedHosts], () => admitted = true);

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

    /// <summary>The host filter's options, as the server runs it and as <see cref="HostFilter"/> builds it to be tried.</summary>
    private static void ConfigureHostFilter(HostFilteringOptions options, IList<string> allowedHosts)
    {
        options.AllowedHosts = allowedHosts;
        options.AllowEmptyHosts = false;
        options.IncludeFailureMessage = false;
    }

    /// <summary>
    /// ASP.NET Core's host-filtering middleware over <paramref name="allowedHosts"/>, with the options the server
    /// runs it with, calling <paramref name="admit"/> for each request it lets through. A refusal completes at
    /// once: 400, and no body (IncludeFailureMessage is off).
    /// </summary>
    private static HostFilteringMiddleware HostFilter(IList<string> allowedHosts, Action admit)
    {
        var options = new HostFilteringOptions();
        ConfigureHostFilter(options, allowedHosts);
        return new HostFilteringMiddleware(
            _ =>
            {
                admit();
                return Task.CompletedTask;
            },
            NullLogger<HostFilteringMiddleware>.Instance,
            new FixedOptions<HostFilteringOptions>(options));
    }

    /// <summary>
    /// Why no request can match <paramref name="entry"/>, completing "…, which no request can match:", or null
    /// when one can. contract-005 · G-12 (2), review round 5 — decided by what decides it at run time: Kestrel,
    /// which refuses a request whose Host breaks its rule before any middleware sees it, and then the host
    /// filter itself, given this entry alone and handed a request whose Host header is exactly it. The filter
    /// refuses a request with no host; it compares a request's host without its port; and it puts an
    /// unbracketed IPv6 address in brackets before it compares. Asked in-process, the filter would match an
    /// entry with whitespace around it, a path or a user name, which Kestrel never lets a request carry; so
    /// Kestrel's rule is asked first.
    /// </summary>
    private static string? NoRequestMatches(string entry)
    {
        if (!KestrelLetsThrough(entry))
        {
            var trimmed = entry.Trim(' ', '\t');
            if (trimmed.Length == 0)
            {
                return "it is only whitespace, which HTTP drops from around a Host header, leaving no host, and the "
                    + "host filter refuses a request with no host.";
            }

            if (trimmed.Length != entry.Length)
            {
                return "it begins or ends with whitespace, which HTTP drops from around a Host header, so no "
                    + "request's Host is ever this.";
            }

            return "Kestrel refuses a request, before the host filter sees it, unless its Host is a name of letters, "
                + "digits and !$&'()-._~, or an IPv6 address in brackets, as [::1], either followed by a colon and a "
                + "port.";
        }

        var admitted = false;
        var context = new DefaultHttpContext();
        context.Request.Headers.Host = entry;

        // Nothing here throws: the entry rules before this refuse whatever the filter cannot convert, and
        // Kestrel's rule a port that is not digits.
        HostFilter([entry], () => admitted = true).Invoke(context).GetAwaiter().GetResult();
        if (admitted)
        {
            return null;
        }

        if (entry.Length == 0)
        {
            return "it is empty, and the host filter refuses a request with no host. An unset variable leaves an "
                + "entry empty: HttpTransport__AllowedHosts__0=${MCP_HOST} in a compose file, with MCP_HOST not set.";
        }

        var hasPort = entry.StartsWith('[') ? entry.Contains("]:", StringComparison.Ordinal) : entry.Contains(':', StringComparison.Ordinal);
        return hasPort
            ? "the host filter compares a request's host without its port, so an entry with a port matches nothing."
            : "ASP.NET Core's host filter, given it alone, refuses a request whose Host is exactly it.";
    }

    /// <summary>The characters Kestrel lets a Host header's name be made of.</summary>
    private static readonly SearchValues<char> HostNameChars =
        SearchValues.Create("!$&'()-._~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>The characters Kestrel lets stand between the brackets of an IPv6 address in a Host header.</summary>
    private static readonly SearchValues<char> IPv6AddressChars = SearchValues.Create(".0123456789:ABCDEFabcdef");

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 — whether Kestrel lets a request whose Host is <paramref name="host"/>
    /// through to the middleware. On every protocol it answers 400, before any middleware runs, unless the Host
    /// is empty, a name of letters, digits and !$&amp;'()-._~, or an IPv6 address in brackets, either followed by
    /// a colon and a port of digits; and HTTP drops the spaces and tabs around a header's value first. This is
    /// Kestrel's own rule (HttpUtilities.IsHostHeaderValid), written out because Kestrel keeps it internal; a
    /// test asks Kestrel's, by reflection, about every ASCII character in each place, and holds this to it.
    ///
    /// Review round 6 — a documented limit: that comparison runs on the runtime the tests run on (10.0.1, from the
    /// SDK global.json pins), and the image ships 10.0.12. When either runtime moves to another patch, run the
    /// comparison again on the shipped runtime, as the review did. Should the two rules drift apart, the cost is
    /// precision only, and it fails closed: an entry Kestrel would let through is refused, or one it would not is
    /// accepted and matches no request.
    /// </summary>
    private static bool KestrelLetsThrough(string host)
    {
        if (host.Length == 0)
        {
            return true;
        }

        int rest;
        if (host[0] == '[')
        {
            // [::1] is the shortest: the closing bracket comes fourth at the earliest.
            var close = host.AsSpan(1).IndexOfAnyExcept(IPv6AddressChars) + 1;
            if (close < 4 || host[close] != ']')
            {
                return false;
            }

            rest = close + 1;
        }
        else
        {
            rest = host.AsSpan().IndexOfAnyExcept(HostNameChars);
            if (rest < 0)
            {
                return true;
            }

            if (rest == 0)
            {
                return false;
            }
        }

        return rest == host.Length
            || (host[rest] == ':' && rest + 1 < host.Length && host.AsSpan(rest + 1).IndexOfAnyExceptInRange('0', '9') < 0);
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

    /// <summary>
    /// contract-005 · G-12 (2), review round 5 addendum — whether Kestrel binds <paramref name="bindAddress"/> to
    /// loopback: the name localhost, or text IPAddress.TryParse reads, as written, as a loopback address. That is
    /// the parse Kestrel's address binder applies to the same text, and it binds every interface for text it
    /// cannot read. The brackets used to be taken off first, so [127.0.0.1], [::1 and [[::1]] counted as loopback,
    /// with no allowed host asked for, while Kestrel listened on every interface; [::1] parses with its brackets.
    /// </summary>
    private static bool IsLoopback(string bindAddress) =>
        bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(bindAddress, out var address) && System.Net.IPAddress.IsLoopback(address));

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
