using Docker.DotNet;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using McpServerTemplate.Testing;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The recording fake of the server's upstreams.
///
/// contract-005 · G-7 — one WireMock.Net container answering, over HTTPS with a leaf of the run's test
/// CA, under every host name the upstream registry gives it: the real provider hosts
/// (opendata-download-metfcst.smhi.se, opendata-download-metobs.smhi.se, jsonplaceholder.typicode.com)
/// and the neutral alias wiremock.e2e.test. Inside the network those names resolve to this container,
/// so the server under test calls its upstreams by their real names and reaches the fake; with its
/// trust narrowed to the test CA, a name with no fake fails TLS instead of reaching the internet.
///
/// Its request journal is read through WireMock's admin REST API with a plain HttpClient from the name
/// map — no WireMock client package, so nothing about the fake is taken on trust from a library.
/// </summary>
public sealed class WireMockService : IUpstreamService
{
    /// <summary>sheyenrath/wiremock.net-alpine:2.14.0, by the digest of its multi-arch index (resolved 2026-09-26).</summary>
    public const string Image = "sheyenrath/wiremock.net-alpine:2.14.0@sha256:b0e6698daff17215232317ed6cf4a007ae72808dcc35f191eb4efffeb6622737";

    /// <summary>The fake's own name, for reading its journal and for tests that call it directly.</summary>
    public const string Alias = "wiremock.e2e.test";

    public const int Port = 443;

    /// <summary>contract-005 · G-16 — WireMock as an upstream owner: the one the registry gives every provider host today.</summary>
    public static UpstreamOwner Owner { get; } = new WireMockOwner();

    private readonly IDockerClient _docker;
    private readonly IContainer _container;

    private WireMockService(IDockerClient docker, IContainer container, NameMap names)
    {
        _docker = docker;
        _container = container;
        Names = names;
    }

    public NameMap Names { get; }

    public IContainer Container => _container;

    internal static async Task<WireMockService> StartAsync(
        IDockerClient docker,
        INetwork network,
        TestPki pki,
        NameMap names,
        IReadOnlyList<string> hosts,
        string runId,
        CancellationToken cancellationToken)
    {
        var tls = pki.LeafDirectory(Owner.Name);
        var container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithNetworkAliases([.. hosts])
            .WithLabel(E2ENetwork.RunLabel, runId)
            .WithPortBinding(Port, true)
            .WithLoopbackPortsOnly()
            // One PEM holding the certificate and its key, so no password has to be passed around.
            .WithBindMount(Path.Combine(tls, TestPki.BundleFile), "/e2e/tls/tls.pem", AccessMode.ReadOnly)
            // The image's entrypoint listens on http://*:80; the later --Urls replaces it, so the fake
            // answers over HTTPS only and a plaintext call to a provider host finds nothing.
            .WithCommand(
                "--Urls", $"https://*:{Port}",
                "--X509CertificateFilePath", "/e2e/tls/tls.pem",
                "--WireMockLogger", "WireMockConsoleLogger")
            .Build();

        try
        {
            await container.StartAsync(cancellationToken);
            names = names.With(Alias, Port, container.Hostname, container.GetMappedPublicPort(Port));

            var service = new WireMockService(docker, container, names);
            await service.WaitUntilReadyAsync(cancellationToken);
            return service;
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>Every request the fake has recorded, as WireMock's admin API returns them.</summary>
    public static async Task<JsonElement> JournalAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        return await http.GetFromJsonAsync<JsonElement>(new Uri($"https://{Alias}/__admin/requests"), cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class WireMockOwner() : UpstreamOwner("wiremock")
    {
        internal override async Task<IUpstreamService> StartAsync(UpstreamStart start, IReadOnlyList<string> hosts, CancellationToken cancellationToken) =>
            await WireMockService.StartAsync(start.Docker, start.Network, start.Pki, start.Names, hosts, start.RunId, cancellationToken);
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var http = Names.CreateClient();
        var deadline = DateTime.UtcNow.AddMinutes(1);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            if (await DockerEngine.ExitCodeIfStoppedAsync(_docker, _container.Id, cancellationToken) is { } exitCode)
            {
                var (stdout, stderr) = await _container.GetLogsAsync(ct: cancellationToken);
                throw new EnvironmentFaultException("wiremock", $"WireMock exited with code {exitCode} during startup: {stdout}{stderr}");
            }

            try
            {
                using var response = await http.GetAsync(new Uri($"https://{Alias}/__admin/requests"), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                throw new EnvironmentFaultException("wiremock", $"CA trust missing: WireMock's certificate did not validate against the run's test CA ({ex.InnerException.Message}).", ex);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new EnvironmentFaultException("wiremock", $"WireMock was not ready within a minute ({last?.Message}).", last);
    }
}
