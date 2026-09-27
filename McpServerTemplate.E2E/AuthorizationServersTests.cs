using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-11 (4) (G-12 (4) · UC-11) — the metadata sends clients to each issuer.
///
/// A client reads authorization_servers and fetches each one's RFC 8414 metadata, whose issuer must
/// be exactly the URL it was sent to (§3.3); the SDK's client refuses anything else. The server used
/// to list each identity provider's Authority — where it fetches keys from — rather than its Issuer.
/// They are the same for most providers, and not for idp-b here, whose issuer carries a trailing slash
/// (see <see cref="E2EEnvironment.Issuers"/>). Since clients are sent to it, an Issuer must also be an
/// absolute https URI, the rule an Authority already had.
/// </summary>
public sealed class AuthorizationServersTests(AuthorizationServersTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<AuthorizationServersTests.Server>
{
    /// <summary>The server exactly as the environment configures it.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None);

    [Fact]
    public async Task T11_4_the_metadata_lists_each_identity_providers_issuer()
    {
        using var http = fixture.CreateClient();
        var idpB = E2EEnvironment.Issuers[E2EEnvironment.IdpB];
        Assert.NotEqual(idpB.Authority.ToString().TrimEnd('/'), idpB.Issuer); // the case the claim needs

        // RFC 9728's location for the resource: it answers on the current code and after the fix alike.
        using var response = await http.GetAsync(new Uri($"https://{TlsFront.Host}/.well-known/oauth-protected-resource/mcp"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()!).ToArray();
        output.WriteLine($"authorization_servers: {string.Join(", ", listed)}");

        var issuers = E2EEnvironment.Issuers.Entries.Select(e => e.Issuer).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            listed.Order(StringComparer.Ordinal).SequenceEqual(issuers),
            $"authorization_servers is [{string.Join(", ", listed)}], not the identity providers' issuers [{string.Join(", ", issuers)}]: "
            + $"idp-b's issuer is {idpB.Issuer} and its authority {idpB.Authority.ToString().TrimEnd('/')}.");

        // And a client sent to idp-b's entry finds metadata naming that exact issuer (RFC 8414 §3.3).
        var metadata = await http.GetFromJsonAsync<JsonElement>(new Uri($"https://{idpB.Host}/.well-known/oauth-authorization-server"));
        Assert.Equal(idpB.Issuer, metadata.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task T11_4_an_http_issuer_refuses_to_start()
    {
        var key = $"Authentication:IdentityProviders:{E2EEnvironment.IdpB}:Issuer";
        await using var outcome = await fixture.Environment.StartupAsync("issuer-http", SettingsDelta.None.Set(key, "http://idp-b.e2e.test/"));
        output.WriteLine(outcome.Describe());

        Assert.True(
            outcome.ExitCode == 78 && outcome.RefusalLine?.Contains(key, StringComparison.Ordinal) == true,
            $"{key}=http://idp-b.e2e.test/ would send clients to a plaintext issuer, and {outcome.Describe()}");
    }
}
