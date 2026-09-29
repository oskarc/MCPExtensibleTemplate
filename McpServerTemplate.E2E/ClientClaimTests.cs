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
///
/// contract-005 · G-17 round 2 (HAR-R2-04) — refused for that claim and nothing else: the same server takes the same client's
/// Keycloak token once it carries client_id (asked for with <see cref="KeycloakService.ClientIdScope"/>, which adds that claim
/// alone), and the refusal of the token without it is logged naming client_id as the missing client claim, and not the token.
/// The positive control was idp-a's token, which showed the server takes tokens, not that it would take Keycloak's but for the
/// claim; and the log said the reason, which this test had not read.
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

    /// <summary>The server's log line for a refused token.</summary>
    private const string AuthenticationFailed = "authn_login_fail";

    /// <summary>What the server logs for a token missing the claim ClientIdClaim names (IdentityRegistration, OnTokenValidated), as T-3 reads it.</summary>
    private const string MissingClientClaim = "missing client_id (the client claim)";

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

        // contract-005 · G-17 round 2 (HAR-R2-04) — positive control: the same client's Keycloak token, asked for with the one scope
        // that adds client_id and nothing else, is taken by this same server. It is the token below but for that claim — same
        // issuer, same subject — so the refusal below is for the claim ClientIdClaim names, and for nothing else about it.
        var carrying = await fixture.KeycloakTokenAsync(KeycloakService.ClientIdScope);
        var carried = new JsonWebToken(carrying);
        Assert.True(
            carried.TryGetClaim("client_id", out _) && carried.Issuer == claims.Issuer && carried.Subject == claims.Subject,
            $"the Keycloak token asked for with {KeycloakService.ClientIdScope} was expected to be the same client's with client_id added; "
            + $"it is {carried.Issuer}'s for {carried.Subject}, carrying {string.Join(", ", carried.Claims.Select(c => c.Type).Distinct())}.");
        using (var accepted = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, carrying)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // The same Keycloak token is accepted where Keycloak's ClientIdClaim is azp (every other class
        // in the run), so the one thing this server changes is the claim required.
        var before = (await fixture.Server.StderrLinesAsync(AuthenticationFailed)).Count;
        using var response = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, keycloak));
        var logged = await fixture.Server.StderrLinesAfterAsync(AuthenticationFailed, before);
        output.WriteLine($"The Keycloak token got {(int)response.StatusCode}; logged: {string.Join(" | ", logged)}");

        Claim.True(
            response.StatusCode == HttpStatusCode.Unauthorized,
            $"with ClientIdClaim=client_id, a Keycloak token carrying only azp got {(int)response.StatusCode}, not 401: the claim "
            + "ClientIdClaim names was not the one required.");

        // contract-005 · G-17 round 2 (HAR-R2-04) — refused for that claim, said so, and the token not written down, as T-3 holds of
        // idp-a's tokens.
        Claim.True(
            logged.Count > 0 && logged.All(line => line.Contains(MissingClientClaim, StringComparison.Ordinal)),
            $"a Keycloak token carrying only azp was refused with 401, and the server's log "
            + $"{(logged.Count == 0 ? $"has no {AuthenticationFailed} line for it" : $"says '{string.Join(" | ", logged)}'")}, not its reason ('{MissingClientClaim}').");
        var stderr = await fixture.Server.StderrAsync();
        Claim.True(
            !(stderr.Contains(keycloak, StringComparison.Ordinal) || stderr.Contains(keycloak.Split('.')[1], StringComparison.Ordinal)),
            "the server's log holds the Keycloak token it refused.");
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
