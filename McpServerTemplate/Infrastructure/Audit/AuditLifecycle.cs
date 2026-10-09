using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using McpServerTemplate.Infrastructure.Frame;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// The server's own life on the audit trail (contract-006 · G-9; roadmap P3.x exit criteria): sys_startup once the host has
/// started everything it starts — the listener bound, for the HTTP host — and sys_shutdown once it has stopped. Hung on the
/// host's own lifetime, so a composition that starts no host (the tests' bare harness) writes nothing, and a server that
/// failed to bind never claims to have started.
///
/// sys_startup carries what an inventory needs to tell one running server from another (roadmap §7, MCP09): the version,
/// the SHA-256 of the server's own assembly file, and the SHA-256 of the installed frame and every policy in force. The
/// tool-lock hash the roadmap names beside them is Phase 5's to supply; it is written as absent until then.
/// </summary>
internal sealed class AuditLifecycle(AuditTrail trail, IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(AuditLifecycle).Assembly;
        var detail = new Dictionary<string, string?>
        {
            ["version"] = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["build_sha256"] = BuildHash(assembly),
            ["frame_sha256"] = FrameHash(),
            ["tool_lock_sha256"] = null,
            ["limits"] = services.GetService<ILimitStore>()?.Description,
            ["providers"] = services.GetService<EnabledProviders>() is { } enabled ? string.Join(",", enabled.Modules.Select(m => m.Name)) : null,
            ["audit"] = trail.Description,
        };

        // The first record of this instance's chain. A server whose start cannot be recorded could record nothing after it
        // either, and would refuse every change it is asked for: it does not start.
        if (!trail.Record(AuditEvent.System(AuditEvents.SysStartup, "The server began serving.", detail)))
        {
            throw new ConfigurationException(
                "The audit trail could not record the server's start (the reason is in the sys_monitor_disabled line above). "
                + "A server that cannot record a decision refuses it, so it does not start without a trail it can write. "
                + "Check Audit:Path.");
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        trail.Record(AuditEvent.System(AuditEvents.SysShutdown, "The server stopped; it accepts no more requests."));
        return Task.CompletedTask;
    }

    /// <summary>The SHA-256 of the server's own assembly file; null where the assembly has no file of its own.</summary>
    private static string? BuildHash(Assembly assembly)
    {
        try
        {
            var location = assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(location)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 of the frame's own account of itself: which filters sit where, and every policy in force.</summary>
    private string? FrameHash()
    {
        var options = services.GetService<IOptions<McpServerOptions>>()?.Value;
        var manifest = services.GetService<FrameManifest>();
        var registry = services.GetService<PolicyRegistry>();
        return options is null || manifest is null || registry is null
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FrameManifest.Describe(options, manifest, registry))));
    }
}

/// <summary>
/// The HTTP side of the audit trail (contract-006 · G-8, G-10): the request's facts captured once, after forwarded headers
/// have established the real client address, and the HTTP layer's own refusals — a refused token, a refused origin, the
/// per-address limit — recorded through the same trail as the gate's.
/// </summary>
public static class AuditHttpCapture
{
    /// <summary>How long one address's rejections by the per-address limit are recorded as one.</summary>
    private static readonly TimeSpan RejectionWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Captures each request's HTTP facts for everything downstream, and makes the caller's trace current when a
    /// traceparent arrived and the host has not already done so. Place it straight after forwarded headers.
    /// </summary>
    public static IApplicationBuilder UseAuditHttpCapture(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            AuditHttp.Enter(context);

            // The web host makes the caller's trace current itself when anything listens to its diagnostics. When nothing
            // does, it reads no traceparent at all, and the request's records would start a trace of their own.
            using var trace = Activity.Current is null
                && ActivityContext.TryParse(context.Request.Headers.TraceParent, context.Request.Headers.TraceState, isRemote: true, out var parent)
                    ? new Activity("mcp.http").SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start()
                    : null;

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Records a refusal the HTTP layer made before any MCP message was read. Nobody is signed in at that point.</summary>
    public static void Refused(HttpContext context, string name, string description, string rule, IReadOnlyDictionary<string, string?>? detail = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RequestServices.GetService<AuditTrail>()?.Record(new AuditEvent(name, description)
        {
            TraceId = (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString(),
            Http = AuditHttp.Current ?? AuditHttp.From(context),
            Decision = new AuditDecision(AuditOutcome.Deny, rule),
            Detail = detail,
        });
    }

    /// <summary>
    /// Records that the per-address limit turned a request away — once per address per minute, however many it turns away:
    /// a caller past the limit is not signed in and can send as many requests as the network carries, and each one written
    /// to disk would be a way to fill it.
    /// </summary>
    public static void RateLimited(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var seen = context.RequestServices.GetService<RejectedAddresses>();
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (seen is null || seen.FirstThisWindow(address, RejectionWindow))
        {
            Refused(
                context,
                AuditEvents.ExcessRateLimitExceeded,
                "The address has reached the limit of requests per minute that applies before sign-in. Further requests from it in this minute are refused and not recorded one by one.",
                "address-rate");
        }
    }

    /// <summary>Which addresses have had a rejection recorded, and when. Bounded: full, it is emptied and starts again.</summary>
    public sealed class RejectedAddresses(TimeProvider clock)
    {
        private const int Capacity = 10_000;

        private readonly ConcurrentDictionary<string, DateTimeOffset> _recorded = new(StringComparer.Ordinal);

        /// <summary>Whether this is the first rejection of <paramref name="address"/> within <paramref name="window"/>.</summary>
        public bool FirstThisWindow(string address, TimeSpan window)
        {
            var now = clock.GetUtcNow();
            if (_recorded.Count >= Capacity)
            {
                _recorded.Clear();
            }

            var first = false;
            _recorded.AddOrUpdate(
                address,
                _ =>
                {
                    first = true;
                    return now;
                },
                (_, last) =>
                {
                    if (now - last < window)
                    {
                        return last;
                    }

                    first = true;
                    return now;
                });
            return first;
        }
    }
}
