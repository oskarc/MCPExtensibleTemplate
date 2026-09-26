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
// ============================================================================

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("TestIssuer");

var names = settings.GetSection("Names").Get<string[]>() ?? [];
if (names.Length == 0)
{
    throw new InvalidOperationException("TestIssuer:Names must name at least one issuer host, for example idp-a.e2e.test.");
}

var certificatePath = settings["Certificate"]
    ?? throw new InvalidOperationException("TestIssuer:Certificate must name the PEM certificate this issuer serves.");
var keyPath = settings["Key"]
    ?? throw new InvalidOperationException("TestIssuer:Key must name the PEM key of that certificate.");
var port = settings.GetValue("Port", 443);

// The certificate is a leaf of the run's test CA; the harness mounts it read-only.
var certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port, listen => listen.UseHttps(certificate)));

using var issuers = new IssuerSet(names);

var app = builder.Build();
issuers.Map(app);
await app.RunAsync();
