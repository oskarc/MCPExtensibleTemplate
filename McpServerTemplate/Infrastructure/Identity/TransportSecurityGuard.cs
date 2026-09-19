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
/// </summary>
public static class TransportSecurityGuard
{
    /// <summary>
    /// Checks the transport configuration for a Production deployment.
    /// </summary>
    /// <param name="configuration">Configuration to read.</param>
    /// <param name="isProduction">
    /// Whether this is a Production environment. Development and test environments run over
    /// plaintext loopback by design, and refusing there would only teach developers to set a
    /// flag that turns the check off.
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
        var behindKnownProxy =
            (configuration.GetSection("HttpTransport:KnownProxies").Get<string[]>() ?? []).Length > 0 ||
            (configuration.GetSection("HttpTransport:KnownNetworks").Get<string[]>() ?? []).Length > 0;

        if (behindKnownProxy)
        {
            return;
        }

        // A certificate setting is named here only to refuse it. An operator who sets one and gets
        // a generic message will reasonably conclude the check is broken; they need to be told that
        // this server does not yet serve HTTPS, so the setting they reached for does nothing.
        var reachedForACertificate =
            !string.IsNullOrWhiteSpace(configuration["HttpTransport:Certificate:Path"]) ||
            !string.IsNullOrWhiteSpace(configuration["HttpTransport:Certificate:Subject"]);

        throw new ConfigurationException(
            "In Production this server must sit behind a proxy it trusts explicitly, and none is "
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
