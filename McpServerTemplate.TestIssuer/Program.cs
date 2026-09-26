using System.Security.Cryptography.X509Certificates;
using McpServerTemplate.TestIssuer;

// ============================================================================
// McpServerTemplate.TestIssuer — test only (contract-005 · G-6)
//
// An identity provider the end-to-end environment controls completely. One container answers
// under several issuer names (idp-a.e2e.test and idp-b.e2e.test), and each name is a separate
// issuer with its own signing key, generated when the container starts and never sent anywhere.
//
// Per name it serves:
//   /.well-known/openid-configuration and /jwks — what a bearer handler fetches, counted per name,
//     so a test can see a key lookup that did not happen
//   /.well-known/oauth-authorization-server — RFC 8414 metadata advertising S256
//   /authorize — approves at once and redirects with code, state and iss
//   /token — checks PKCE and mints a token whose audience is the resource parameter
//   /admin/* — the test's own door: tokens Keycloak will not mint, the counts, and the clock
//
// It is reached under the same names from the server under test and from the test process, so a
// token minted through /admin carries exactly the issuer the server pins.
//
// EXIT CODES: 0 normal shutdown · 78 configuration it will not start with, named on stderr — the
// product's code for the same thing (contract-001 · G-2), so a refusal never reads as a crash.
// ============================================================================

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("TestIssuer");

var names = settings.GetSection("Names").Get<string[]>() ?? [];
if (names.Length == 0)
{
    return Refuse("TestIssuer:Names must name at least one issuer host, for example idp-a.e2e.test.");
}

// contract-005 · G-6, G-10 — containment by identity, as the test host has it. This issuer mints
// whatever it is asked to through an unauthenticated door, so the only issuers it may ever claim to
// be are hosts under .test: names no real client resolves and no real deployment trusts. Any other
// name stops it here, before it listens: exit 78, naming the name.
foreach (var name in names)
{
    if (Uri.CheckHostName(name) != UriHostNameType.Dns
        || !name.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
        || name.Length <= ".test".Length)
    {
        return Refuse(
            $"TestIssuer:Names holds '{name}', which is not a host under .test. The test issuer mints any token it is "
            + "asked for, so it answers only as a .test issuer, for example idp-a.e2e.test.");
    }
}

if (settings["Certificate"] is not { } certificatePath)
{
    return Refuse("TestIssuer:Certificate must name the PEM certificate this issuer serves.");
}

if (settings["Key"] is not { } keyPath)
{
    return Refuse("TestIssuer:Key must name the PEM key of that certificate.");
}

var port = settings.GetValue("Port", 443);

// The certificate is a leaf of the run's test CA; the harness mounts it read-only.
X509Certificate2 certificate;
try
{
    certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
{
    return Refuse($"the certificate {certificatePath} with key {keyPath} could not be read: {ex.Message}");
}

builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port, listen => listen.UseHttps(certificate)));

using var issuers = new IssuerSet(names);

var app = builder.Build();
issuers.Map(app);
await app.RunAsync();
return 0;

// A refusal of its configuration: the reason on stderr, and 78, never an unhandled exception.
static int Refuse(string reason)
{
    Console.Error.WriteLine($"The test issuer cannot start: {reason}");
    return 78;
}
