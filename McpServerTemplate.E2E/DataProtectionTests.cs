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

    [Fact]
    public async Task T17_the_image_writes_no_key_and_loads_no_key_ring()
    {
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
        Assert.True(
            written.Count == 0 && logged.Count == 0 && answered.StatusCode == HttpStatusCode.OK,
            $"the image wrote under /home/app/.aspnet: [{string.Join(", ", written)}]; its log said: [{string.Join(" | ", logged)}]; "
            + $"GET /readyz answered {(int)answered.StatusCode}.");
    }
}
