using System.Net;
using System.Text.Json;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-11 (1) (G-12 (1) · UC-11) — the endpoint and the resource agree.
///
/// Authentication:Resource names https://mcp.e2e.test/mcp, and every token's audience is that URL;
/// MCP has to answer there, or a standard client that is given the resource URL finds nothing (T-4 is
/// that red). Two variants are held here: a Resource whose path is not /mcp refuses to start, so the
/// two cannot drift apart again; and the challenge at the resource URL names a metadata document that
/// answers, as does RFC 9728's well-known location for that resource, with the same document.
/// </summary>
public sealed class EndpointAndResourceTests(EndpointAndResourceTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<EndpointAndResourceTests.Server>
{
    /// <summary>The server exactly as the environment configures it.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None);

    /// <summary>RFC 9728 §3.1: the well-known location for https://mcp.e2e.test/mcp.</summary>
    private static readonly Uri WellKnownForResource = new($"https://{TlsFront.Host}/.well-known/oauth-protected-resource/mcp");

    [Fact]
    public async Task T11_1_a_resource_whose_path_is_not_mcp_refuses_to_start()
    {
        await using var outcome = await fixture.Environment.StartupAsync(
            "resource-at-root", SettingsDelta.None.Set("Authentication:Resource", $"https://{TlsFront.Host}/"));
        output.WriteLine(outcome.Describe());

        Assert.True(
            outcome.ExitCode == 78 && outcome.RefusalLine?.Contains("Authentication:Resource", StringComparison.Ordinal) == true,
            $"Authentication:Resource=https://{TlsFront.Host}/ names a resource MCP does not answer at, and {outcome.Describe()}");
    }

    [Fact]
    public async Task T11_1_the_challenge_at_the_resource_url_and_the_rfc9728_location_answer_with_the_same_document()
    {
        using var http = fixture.CreateClient();

        // The resource URL itself, with no token: what a standard client sends first.
        using var challenged = await http.SendAsync(McpRequests.Initialize(new Uri(ServerUnderTest.Resource)));
        var named = McpRequests.ResourceMetadataOf(challenged);
        Assert.True(
            challenged.StatusCode == HttpStatusCode.Unauthorized && named is not null,
            $"an unauthenticated initialize to the resource URL {ServerUnderTest.Resource} got {(int)challenged.StatusCode}"
            + $" {(named is null ? "with no resource_metadata in its challenge" : $"naming {named}")}, not a 401 naming its metadata.");
        output.WriteLine($"The challenge at {ServerUnderTest.Resource} names {named}.");

        using var fromChallenge = await http.GetAsync(new Uri(named!));
        using var fromWellKnown = await http.GetAsync(WellKnownForResource);
        Assert.Equal(HttpStatusCode.OK, fromChallenge.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fromWellKnown.StatusCode);

        var challengeDocument = await fromChallenge.Content.ReadAsStringAsync();
        var wellKnownDocument = await fromWellKnown.Content.ReadAsStringAsync();
        Assert.Equal(wellKnownDocument, challengeDocument);
        Assert.Equal(ServerUnderTest.Resource, JsonDocument.Parse(wellKnownDocument).RootElement.GetProperty("resource").GetString());
    }
}
