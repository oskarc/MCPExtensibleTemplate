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
/// The test issuer container, and the test's door into it.
///
/// contract-005 · G-6 — a container built from McpServerTemplate.TestIssuer, on the network under every
/// host name the issuer registry gives it (idp-a.e2e.test and idp-b.e2e.test). It is its own container
/// rather than a port on the test machine exposed into the network, so Windows with Docker Desktop and
/// the Linux runner reach it by the same mechanism, with no SSH sidecar. Its signing keys are made
/// when it starts and never leave it; the test asks it for tokens through its admin endpoint, under
/// the same name the server uses, so the token's issuer is the one the server pins.
/// </summary>
public sealed class TestIssuerService : IAsyncDisposable
{
    public const int Port = 443;

    private readonly IDockerClient _docker;
    private readonly IContainer _container;

    private TestIssuerService(IDockerClient docker, IContainer container, IReadOnlyList<string> hosts, NameMap names)
    {
        _docker = docker;
        _container = container;
        Hosts = hosts;
        Names = names;
    }

    /// <summary>The issuer host names this container answers under.</summary>
    public IReadOnlyList<string> Hosts { get; }

    /// <summary>The name map entries for each of those names.</summary>
    public NameMap Names { get; }

    public IContainer Container => _container;

    internal static async Task<TestIssuerService> StartAsync(
        IDockerClient docker,
        string image,
        INetwork network,
        TestPki pki,
        NameMap names,
        IReadOnlyList<string> hosts,
        string runId,
        CancellationToken cancellationToken)
    {
        var tls = pki.LeafDirectory("issuer");
        var builder = new ContainerBuilder(image)
            .WithNetwork(network)
            .WithNetworkAliases([.. hosts])
            .WithLabel(E2ENetwork.RunLabel, runId)
            .WithPortBinding(Port, true)
            .WithBindMount(Path.Combine(tls, TestPki.CertificateFile), "/e2e/tls/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(tls, TestPki.KeyFile), "/e2e/tls/tls.key", AccessMode.ReadOnly)
            .WithEnvironment("TestIssuer__Certificate", "/e2e/tls/tls.crt")
            .WithEnvironment("TestIssuer__Key", "/e2e/tls/tls.key")
            .WithEnvironment("TestIssuer__Port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        for (var i = 0; i < hosts.Count; i++)
        {
            builder = builder.WithEnvironment($"TestIssuer__Names__{i}", hosts[i]);
        }

        var container = builder.Build();
        try
        {
            await container.StartAsync(cancellationToken);

            foreach (var host in hosts)
            {
                names = names.With(host, Port, container.Hostname, container.GetMappedPublicPort(Port));
            }

            var service = new TestIssuerService(docker, container, hosts, names);
            await service.WaitUntilReadyAsync(cancellationToken);
            return service;
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A token from the admin endpoint of issuer <paramref name="host"/>.
    /// </summary>
    /// <param name="http">A client made from the environment's name map.</param>
    /// <param name="host">The issuer name, e.g. idp-a.e2e.test.</param>
    /// <param name="kind">valid, wrong-audience, expired, alg-none, hs256, cross-signed, missing-claim or stale-iat.</param>
    /// <param name="audience">The audience; the server's resource for all but wrong-audience.</param>
    /// <param name="scopes">Scopes for the scope claim.</param>
    /// <param name="extra">Further request fields: subject, clientId, claim, issuedSecondsAgo, signedBy.</param>
    public static async Task<string> MintAsync(
        HttpClient http,
        string host,
        string kind,
        string audience,
        IReadOnlyList<string>? scopes = null,
        IReadOnlyDictionary<string, object>? extra = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        var request = new Dictionary<string, object> { ["kind"] = kind, ["audience"] = audience };
        if (scopes is not null)
        {
            request["scopes"] = scopes;
        }

        foreach (var (key, value) in extra ?? new Dictionary<string, object>())
        {
            request[key] = value;
        }

        using var response = await http.PostAsJsonAsync(new Uri($"https://{host}/admin/mint"), request, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The test issuer refused to mint '{kind}' at {host}: {(int)response.StatusCode} {body}");
        }

        return body.GetProperty("token").GetString()!;
    }

    /// <summary>Requests counted per issuer name and path — discovery and JWKS among them.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> CountsAsync(
        HttpClient http, string anyHost, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        var counts = await http.GetFromJsonAsync<Dictionary<string, Dictionary<string, int>>>(new Uri($"https://{anyHost}/admin/counts"), cancellationToken);
        return counts!.ToDictionary(
            c => c.Key,
            c => (IReadOnlyDictionary<string, int>)c.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

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
                throw new InvalidOperationException($"The test issuer exited with code {exitCode} during startup: {stdout}{stderr}");
            }

            try
            {
                // Every name, not just the first: a name missing from the certificate or the aliases
                // is found here rather than in whichever test first uses it.
                foreach (var host in Hosts)
                {
                    using var response = await http.GetAsync(new Uri($"https://{host}/admin/clock"), cancellationToken);
                    response.EnsureSuccessStatusCode();
                }

                return;
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                throw new EnvironmentFaultException("issuer", $"CA trust missing: the test issuer's certificate did not validate against the run's test CA ({ex.InnerException.Message}).", ex);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new EnvironmentFaultException("issuer", $"The test issuer was not ready within a minute ({last?.Message}).", last);
    }
}
