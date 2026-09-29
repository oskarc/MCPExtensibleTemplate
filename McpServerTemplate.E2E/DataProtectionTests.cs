using System.Net;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-17 (G-18) — the shipped image keeps no data-protection key material and loads no key ring.
///
/// Data protection came with authentication, and nothing in the server protects data with it; yet at startup the
/// framework loaded a key ring for it, which in the image meant a key written, unencrypted, under
/// /home/app/.aspnet, and two warnings saying so. The container's file system is read as docker diff reads it, and
/// its log as it wrote it; the server still answers.
/// </summary>
public sealed class DataProtectionTests(DataProtectionTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<DataProtectionTests.Server>
{
    /// <summary>The environment's server, as it ships.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None);

    private const string KeyWrittenInTheProfile = "t17-key-written-in-the-profile";

    /// <summary>The line the server logs a refused token with, at Warning (contract-002 revision; contract-005 · T-3).</summary>
    private const string AuthenticationFailed = "authn_login_fail";

    /// <summary>
    /// Nothing in the environment can make the product write a key again (G-18 left it no key repository to write to), so
    /// the sabotage writes one where the framework did, through Docker's archive API — the way anything with write access
    /// to the container could — and the claim, read as docker diff reads the container, must see it.
    ///
    /// contract-005 · G-11, G-17 round 1 (HAR-R1-06) — the claim is two absences, so each is asserted only on a witness seen
    /// recording something earlier in this same test. The file system's: a file this test copies into the container at a
    /// path of its own, under /tmp, is listed by docker diff. The log's: a token the server cannot read, sent through the
    /// front, is refused with an authn_login_fail line on the server's stderr, at Warning — the level the framework wrote its
    /// data-protection warnings at.
    /// </summary>
    [Fact]
    [Sabotage(KeyWrittenInTheProfile, SabotageActs.ContainerEnvironment,
        "A key file is written into the class's server container at /home/app/.aspnet/DataProtection-Keys, where the framework "
        + "wrote its unencrypted key before G-18, through Docker's archive API.")]
    public async Task T17_the_image_writes_no_key_and_loads_no_key_ring()
    {
        if (Sabotage.Applies(KeyWrittenInTheProfile))
        {
            await fixture.Server.Container.CopyAsync(
                "<!-- contract-005 · T-12 sabotage: stands for the key the framework wrote here before G-18. -->\n"u8.ToArray(),
                "/home/app/.aspnet/DataProtection-Keys/key-e2e-sabotage.xml");
        }

        // The file system's witness, seen recording: a file of this test's own, where the image keeps nothing.
        var witness = $"/tmp/e2e-witness-{Guid.NewGuid():N}.txt";
        await fixture.Server.Container.CopyAsync("contract-005 · T-17: the witness that docker diff sees what is written.\n"u8.ToArray(), witness);

        using var docker = await DockerEngine.ConnectAsync(CancellationToken.None);
        var changes = await docker.Containers.InspectChangesAsync(fixture.Server.Container.Id, CancellationToken.None);
        Assert.True(
            changes.Any(c => c.Path == witness),
            $"docker diff of the server container does not list {witness}, which this test wrote there: the witness of what the image writes "
            + $"saw nothing. It listed: [{string.Join(", ", changes.Select(c => $"{c.Kind} {c.Path}"))}].");
        var written = changes
            .Where(c => c.Path.StartsWith("/home/app/.aspnet", StringComparison.Ordinal))
            .Select(c => $"{c.Kind} {c.Path}")
            .ToList();

        // The log's witness, seen recording: a refusal the server logs at Warning, for a token it cannot read.
        var before = (await fixture.Server.StderrLinesAsync(AuthenticationFailed)).Count;
        using (var http = fixture.CreateClient())
        using (var refused = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, $"e2e-unreadable-{Guid.NewGuid():N}")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var warned = await fixture.Server.StderrLinesAfterAsync(AuthenticationFailed, before);
        Assert.True(
            warned.Count > 0,
            $"the server refused a token it cannot read, and its stderr has no {AuthenticationFailed} line for it: the witness of what the "
            + "image logs at Warning saw nothing.");

        var logged = (await fixture.Server.StderrAsync()).Split('\n')
            .Where(l => l.Contains("DataProtection", StringComparison.Ordinal) || l.Contains("key repository", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim())
            .ToList();
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var readiness = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.Server.DirectEndpoint, "/readyz"));
        readiness.Headers.Host = TlsFront.Host;
        using var answered = await direct.SendAsync(readiness);

        output.WriteLine($"written: {string.Join(" | ", written)}");
        output.WriteLine($"logged: {string.Join(" | ", logged)}");
        Claim.True(
            written.Count == 0 && logged.Count == 0 && answered.StatusCode == HttpStatusCode.OK,
            $"the image wrote under /home/app/.aspnet: [{string.Join(", ", written)}]; its log said: [{string.Join(" | ", logged)}]; "
            + $"GET /readyz answered {(int)answered.StatusCode}.");
    }
}
