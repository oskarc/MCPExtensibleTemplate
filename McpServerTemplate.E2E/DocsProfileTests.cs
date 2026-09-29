using System.Net;
using McpServerTemplate.E2E.Harness;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-9 (G-13 · UC-9) — the documented deployment runs as written.
///
/// docs/04's compose file, with the production configuration it deploys, is run verbatim through the docs profile's
/// declared table (<see cref="DocsProfile.Table"/>) — host names and addresses, and test trust, each with its reason —
/// with this run's server image tagged mcp-server:latest, the name the file uses. It starts, accepts a real token —
/// Keycloak's, through the TLS front the document tells an operator to put in front of it — and lists tools, each one
/// the image's own startup line says it serves.
///
/// The document is the repository's docs/04, or in a run outside CI the copy <see cref="DocsProfile.DocumentVariable"/>
/// names: how the profile is run against docs/04 as it stood at an earlier commit, without rewriting history.
/// </summary>
[KeycloakClient]
public sealed class DocsProfileTests(ITestOutputHelper output)
{
    /// <summary>
    /// Sabotage (G-11) and T-9's red: run it against docs/04 as it stood when the fixes landed — commit 6f370e2, before
    /// phase 2's edits — by naming that text in MCP_E2E_DOCS04. Its resource is https://mcp.example.com/, and nothing
    /// else about the run changes; the server refuses to start, and the claim's assertion goes red.
    /// </summary>
    [Fact]
    public async Task T9_the_documented_deployment_starts_accepts_a_real_token_and_lists_tools()
    {
        var environment = await E2EEnvironment.GetAsync();
        var document = DocsProfile.DocumentPath(environment.RepositoryRoot);
        output.WriteLine($"The document: {document}");

        await using var profile = await environment.StartDocsProfileAsync(document);
        output.WriteLine("The declared table, as it acted on this document:");
        foreach (var row in profile.Applied)
        {
            output.WriteLine($"  {row}");
        }

        output.WriteLine(profile.Describe());

        HttpStatusCode? status = null;
        IReadOnlyList<string> tools = [];
        IReadOnlyList<string> unserved = [];
        var answered = string.Empty;
        var refusal = "(no authn_login_fail line)";
        if (profile.Started)
        {
            // A real token: Keycloak's, for this class's own client, for the resource the document names.
            using var keycloak = environment.Names.CreateClient();
            var token = await environment.Keycloak.ClientCredentialsTokenAsync(keycloak, ServerFixture.ClientIdFor(typeof(DocsProfileTests)));
            Assert.Equal(KeycloakService.Issuer, new JsonWebToken(token).Issuer);

            using var http = profile.CreateClient(ClientAddresses.Next());
            using var response = await http.SendAsync(McpRequests.Rpc(ServerUnderTest.Endpoint, token, "tools/list"));
            status = response.StatusCode;
            answered = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                var message = await McpRequests.MessageOfAsync(response);
                tools = message.TryGetProperty("result", out var result) && result.TryGetProperty("tools", out var list)
                    ? [.. list.EnumerateArray().Select(t => t.GetProperty("name").GetString()!)]
                    : [];

                // Each tool listed is one the image's own startup line says it serves (G-11).
                var line = FrameLine.Parse(await profile.StartupLineAsync());
                unserved = [.. tools.Where(t => !line.Manifest.Any(e => e.Contains($"/{t}:", StringComparison.Ordinal)))];
            }

            if (status != HttpStatusCode.OK)
            {
                // Why it refused, as the server's log says (the token itself is never logged).
                refusal = string.Join(" | ", (await profile.StderrAsync()).Split('\n')
                    .Where(l => l.Contains("authn_login_fail", StringComparison.Ordinal)).Select(l => l.Trim()));
            }

            output.WriteLine($"tools/list with a Keycloak token through the front: {(int?)status}, [{string.Join(", ", tools)}]");
        }

        Assert.True(
            profile.Started && status == HttpStatusCode.OK && tools.Count > 0 && unserved.Count == 0,
            profile.Started
                ? $"the documented deployment ({document}) started, and tools/list with a Keycloak token through the front got "
                    + $"{(int?)status}, listing [{string.Join(", ", tools)}]{(unserved.Count > 0 ? $", of which [{string.Join(", ", unserved)}] are not on its startup line" : string.Empty)}. "
                    + $"{(status == HttpStatusCode.OK ? string.Empty : $"It answered: '{Shorten(answered)}'; its log: {refusal}")}"
                : $"the documented deployment did not start: {profile.Describe()}");
    }

    private static string Shorten(string text) => text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 400), "…");
}
