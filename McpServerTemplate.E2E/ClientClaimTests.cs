using System.Net;
using McpServerTemplate.E2E.Harness;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-11 (3) (G-12 (3) · UC-11) — ClientIdClaim is the claim a token must carry.
///
/// Identity providers name the calling application differently — Keycloak and Entra v2 azp, Okta
/// cid, Entra v1 appid — and each identity provider's ClientIdClaim says which one it uses. The check
/// used to accept client_id or azp whatever ClientIdClaim said, so the setting was read and never
/// applied. Here Keycloak's provider is told its client claim is client_id; a Keycloak
/// client-credentials token carries azp and no client_id, and must be refused. A ClientIdClaim that
/// every token carries (a registered claim) or that means something else (the scope claim) would
/// switch the requirement off, so it refuses startup.
/// </summary>
public sealed class ClientClaimTests(ClientClaimTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<ClientClaimTests.Server>
{
    /// <summary>Keycloak's provider told that its client claim is client_id, which Keycloak's tokens do not carry.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Set(ClientIdClaimKey, "client_id")
        .Sabotaged(KeycloakClaimLeftAsAzp, d => d.Set(ClientIdClaimKey, "azp")));

    private const string ClientIdClaimKey = $"Authentication:IdentityProviders:{E2EEnvironment.KeycloakIssuer}:ClientIdClaim";
    private const string KeycloakClaimLeftAsAzp = "t11-3-keycloak-client-claim-left-as-azp";

    [Fact]
    [Sabotage(KeycloakClaimLeftAsAzp, SabotageActs.ContainerEnvironment,
        "The class's server keeps Keycloak's ClientIdClaim as the environment has it, azp, the claim Keycloak's tokens carry.")]
    public async Task T11_3_a_token_without_the_claim_that_ClientIdClaim_names_is_refused()
    {
        using var http = fixture.CreateClient();
        var keycloak = await fixture.KeycloakTokenAsync();

        // The token is what the claim needs it to be: the client in azp, no client_id.
        var claims = new JsonWebToken(keycloak);
        Assert.True(
            claims.TryGetClaim("azp", out _) && !claims.TryGetClaim("client_id", out _),
            $"the Keycloak token was expected to carry azp and no client_id; it carries {string.Join(", ", claims.Claims.Select(c => c.Type).Distinct())}.");

        // Positive control: a token that carries client_id is accepted by this same server, so the
        // refusal below is the claim's, not a server that refuses everything.
        var carrying = await TestIssuerService.MintAsync(http, "idp-a.e2e.test", "valid", ServerUnderTest.Resource, ["weather:read"]);
        using (var accepted = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, carrying)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // The same Keycloak token is accepted where Keycloak's ClientIdClaim is azp (every other class
        // in the run), so the one thing this server changes is the claim required. The refusal's
        // reason is not logged: a missing required claim fails in OnTokenValidated, which no log line
        // reports yet — T-3's "with its reason in the log" is that clause, and it is not this phase's.
        using var response = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, keycloak));
        output.WriteLine($"The Keycloak token got {(int)response.StatusCode}.");

        Claim.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"with ClientIdClaim=client_id, a Keycloak token carrying only azp got {(int)response.StatusCode}, not 401: the claim "
            + "ClientIdClaim names was not the one required.");
    }

    /// <summary>
    /// sub is a registered claim every accepted token carries; scope is this provider's ScopeClaim; typ
    /// is on every Keycloak access token (Bearer) and is neither, so it passed the check that named only
    /// those — ClientIdClaim is now one of the four claims identity providers name the client in.
    /// </summary>
    [Theory]
    [InlineData("sub")]
    [InlineData("scope")]
    [InlineData("typ")]
    [Sabotage("t11-3-client-claim-left-out", SabotageActs.ContainerEnvironment, Sabotage.MisconfigurationLeftOut)]
    public async Task T11_3_a_client_claim_that_is_always_present_or_means_something_else_refuses_to_start(string claim)
    {
        await using var outcome = await fixture.Environment.StartupAsync(
            $"client-claim-{claim}", SettingsDelta.None.Set(ClientIdClaimKey, claim).Sabotaged("t11-3-client-claim-left-out", _ => SettingsDelta.None));
        output.WriteLine(outcome.Describe());

        Claim.True(
            outcome.ExitCode == 78 && outcome.RefusalLine?.Contains(ClientIdClaimKey, StringComparison.Ordinal) == true,
            $"{ClientIdClaimKey}={claim} names a claim that would switch the client requirement off, and {outcome.Describe()}");
    }
}
