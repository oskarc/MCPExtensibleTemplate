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

    /// <summary>
    /// Nothing in the environment can make the product write a key again (G-18 left it no key repository to write to), so
    /// the sabotage writes one where the framework did, through Docker's archive API — the way anything with write access
    /// to the container could — and the claim, read as docker diff reads the container, must see it.
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

        using var docker = await DockerEngine.ConnectAsync(CancellationToken.None);
        var changes = await docker.Containers.InspectChangesAsync(fixture.Server.Container.Id, CancellationToken.None);
        var written = changes
            .Where(c => c.Path.StartsWith("/home/app/.aspnet", StringComparison.Ordinal))
            .Select(c => $"{c.Kind} {c.Path}")
            .ToList();

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
