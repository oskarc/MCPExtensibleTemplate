namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Refuses to serve bearer tokens over a connection that is not protected.
///
/// contract-002 · G-12 — everything else in this phase protects a credential that anyone on the
/// path can read and replay if the transport is plaintext. Pinning the audience, routing by
/// issuer, allowlisting algorithms: all of it is theatre over cleartext HTTP. So in Production
/// the server refuses to start unless it sits behind a proxy it has been told to trust, which is
/// the deployment where plaintext on the loopback hop is the intended design.
///
/// <para><b>Narrowed 2026-09-20, by the pioneer's decision.</b> This guard used to accept a second
/// branch: a certificate path or subject, taken as evidence that the server terminates TLS itself.
/// It never did. Nothing read either key, and Program.cs binds http:// unconditionally — so
/// setting HttpTransport:Certificate:Path satisfied the check and the server went on serving
/// bearer tokens in cleartext. A control that is checked and never applied is worse than an absent
/// one, because it answers a question falsely.</para>
///
/// <para>The Kestrel HTTPS listener moves to the phase that handles it, where P6.4 already covers
/// TLS termination at the ingress or in Kestrel. The branch comes back when the listener does, and
/// not before: this guard promises only what this server can do today.</para>
///
/// <para>contract-005 · G-17 round 1 — the guard ran only in an environment named Production, while the
/// frame's other deployment rules (Limits:Redis, Providers:Enabled) apply in every environment but
/// Development. Under any other name — Staging, or a misnamed Production — a server with no proxy
/// started and read bearer tokens over plain http. It applies wherever they do now: in every
/// environment but Development, which runs on the developer's own loopback.</para>
/// </summary>
public static class TransportSecurityGuard
{
    /// <summary>
    /// Checks the transport configuration of a server running in <paramref name="environment"/>: in every environment but
    /// Development, a proxy it trusts explicitly must be named (contract-005 · G-17 round 1). The HTTP composition calls this.
    /// </summary>
    /// <param name="configuration">Configuration to read.</param>
    /// <param name="environment">The server's environment.</param>
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Validate(configuration, isProduction: !environment.IsDevelopment());
    }

    /// <summary>
    /// Checks the transport configuration for a deployment.
    /// </summary>
    /// <param name="configuration">Configuration to read.</param>
    /// <param name="isProduction">
    /// Whether the guard applies: whether the server runs as a deployment rather than on a developer's
    /// loopback. Development runs over plaintext loopback by design, and refusing there would only
    /// teach developers to set a flag that turns the check off. The name is contract-002's, from when
    /// only an environment named Production was guarded; since contract-005 · G-17 round 1 the
    /// composition passes true for every environment but Development
    /// (<see cref="Validate(IConfiguration, IHostEnvironment)"/>).
    /// </param>
    public static void Validate(IConfiguration configuration, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!isProduction)
        {
            return;
        }

        // The one branch this server can honour. Declaring a proxy is not a formality: the same
        // values reach ForwardedHeadersOptions, so the address the rate limiter partitions on is
        // the one that proxy forwarded.
        //
        // contract-005 · G-17 round 1 — counted as the composition reads them (TrustedProxies), not as entries: any entry
        // used to count, and 0.0.0.0/0, which names no proxy and trusts every client, satisfied the guard. An entry that
        // is not a proxy's is refused there, naming its key, and satisfies nothing.
        var (proxies, networks) = HttpServerComposition.TrustedProxies(configuration);
        var behindKnownProxy = proxies.Count > 0 || networks.Count > 0;

        if (behindKnownProxy)
        {
            return;
        }

        // A certificate setting is named here only to refuse it. An operator who sets one and gets
        // a generic message will reasonably conclude the check is broken; they need to be told that
        // this server does not yet serve HTTPS, so the setting they reached for does nothing.
        // contract-005 · G-12 (2) — the settings allowlist now retires both keys with the same reason,
        // so the shipped server refuses them in every environment, before this guard runs.
        var reachedForACertificate =
            !string.IsNullOrWhiteSpace(configuration["HttpTransport:Certificate:Path"]) ||
            !string.IsNullOrWhiteSpace(configuration["HttpTransport:Certificate:Subject"]);

        throw new ConfigurationException(
            "Outside Development this server must sit behind a proxy it trusts explicitly, and none is "
            + "configured. Set HttpTransport:KnownProxies or :KnownNetworks to name the proxy in "
            + "front of it, and terminate TLS there. Bearer tokens over plaintext can be read and "
            + "replayed by anyone on the path, which would make every other identity control in "
            + "this server decorative."
            + (reachedForACertificate
                ? " HttpTransport:Certificate:Path and :Subject are set and are not read: this "
                  + "server does not terminate TLS itself. The Kestrel HTTPS listener belongs to a "
                  + "later phase, and until it exists a certificate setting here would claim a "
                  + "protection the server does not provide."
                : string.Empty));
    }
}
