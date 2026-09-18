namespace McpServerTemplate.Infrastructure.Identity;

/// <summary>
/// Refuses to serve bearer tokens over a connection that is not protected.
///
/// contract-002 · G-12 — everything else in this phase protects a credential that anyone on the
/// path can read and replay if the transport is plaintext. Pinning the audience, routing by
/// issuer, allowlisting algorithms: all of it is theatre over cleartext HTTP. So in Production
/// the server refuses to start unless one of two things is true — it terminates TLS itself, or
/// it sits behind a proxy it has been told to trust, which is the deployment where plaintext on
/// the loopback hop is the intended design.
///
/// This was nearly deferred to a later phase on the reasoning that TLS is a deployment concern
/// and no exit criterion tested it. Both halves of that were wrong, and the contract records the
/// withdrawal.
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

        var servesHttps = !string.IsNullOrWhiteSpace(configuration["HttpTransport:Certificate:Path"]) ||
                          !string.IsNullOrWhiteSpace(configuration["HttpTransport:Certificate:Subject"]);

        var behindKnownProxy =
            (configuration.GetSection("HttpTransport:KnownProxies").Get<string[]>() ?? []).Length > 0 ||
            (configuration.GetSection("HttpTransport:KnownNetworks").Get<string[]>() ?? []).Length > 0;

        if (servesHttps || behindKnownProxy)
        {
            return;
        }

        throw new ConfigurationException(
            "In Production this server must either terminate TLS itself or sit behind a proxy it "
            + "trusts explicitly, and neither is configured. Set HttpTransport:Certificate:Path (or "
            + ":Subject) to serve HTTPS, or HttpTransport:KnownProxies / :KnownNetworks to name the "
            + "proxy in front of it. Bearer tokens over plaintext can be read and replayed by anyone "
            + "on the path, which would make every other identity control in this server decorative.");
    }
}
