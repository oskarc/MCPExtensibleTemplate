using Docker.DotNet;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using McpServerTemplate.Testing;
using Microsoft.IdentityModel.Tokens;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// A real identity provider: Keycloak, shared by every test class of a run.
///
/// contract-005 · G-5 — Keycloak 26.7.4, pinned by digest, run through the generic container builder
/// in start mode (production mode), serving HTTPS with a leaf of the run's test CA. Its hostname is
/// pinned to https://keycloak.e2e.test:8443, so a token carries the same issuer whether it was fetched
/// from inside the network or from the test process through a published port; without the pin, iss
/// follows whichever address the token was fetched from and the server refuses it.
///
/// One realm is imported, with a client-credentials client per test class, an audience mapper equal
/// to Authentication:Resource, and an explicit basic scope: a realm file that declares client scopes
/// gets none of Keycloak's built-in ones, sub included. The client secrets are generated when the run
/// starts and live only in the run directory, which is deleted after the run.
///
/// It starts once per run. xUnit 2 has no assembly fixtures, so it is part of the lazily started
/// <see cref="E2EEnvironment"/>, and startup — 18 to 51 seconds measured — is paid once.
/// </summary>
public sealed class KeycloakService : IAsyncDisposable
{
    /// <summary>quay.io/keycloak/keycloak:26.7.4, by the digest of its multi-arch index (resolved 2026-09-26).</summary>
    public const string Image = "quay.io/keycloak/keycloak:26.7.4@sha256:82a77884f3af238beab1e7afd63b5f530e1b5c0590bd7aa60b40a40463e29b2c";

    public const string Host = "keycloak.e2e.test";
    public const int Port = 8443;
    public const int ManagementPort = 9000;
    public const string Realm = "mcp";

    /// <summary>The issuer every Keycloak token carries, wherever it was fetched from.</summary>
    public static readonly string Issuer = $"https://{Host}:{Port}/realms/{Realm}";

    /// <summary>The scopes the realm defines; the test classes' clients get the first four by default.</summary>
    public static readonly string[] Scopes = ["weather:read", "observations:read", "demo:read", "demo:write"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly IDockerClient _docker;
    private readonly IContainer _container;
    private readonly IReadOnlyDictionary<string, string> _secrets;

    private KeycloakService(IDockerClient docker, IContainer container, IReadOnlyDictionary<string, string> secrets, NameMap names)
    {
        _docker = docker;
        _container = container;
        _secrets = secrets;
        Names = names;
    }

    /// <summary>The name map entries for Keycloak's HTTPS and management ports.</summary>
    public NameMap Names { get; }

    public IContainer Container => _container;

    /// <summary>The client ids the realm was imported with, one per test class.</summary>
    public IEnumerable<string> ClientIds => _secrets.Keys;

    internal static async Task<KeycloakService> StartAsync(
        IDockerClient docker,
        INetwork network,
        TestPki pki,
        NameMap names,
        string resource,
        IReadOnlyList<string> clientIds,
        string runId,
        CancellationToken cancellationToken)
    {
        var secrets = clientIds.ToDictionary(
            id => id,
            _ => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            StringComparer.Ordinal);

        // In the run directory, so the secrets are deleted with the keys when the run ends.
        var realmPath = pki.WriteFile("keycloak-realm.json", RealmJson(resource, secrets));

        var tls = pki.LeafDirectory("keycloak");
        var container = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithNetworkAliases(Host)
            .WithLabel(E2ENetwork.RunLabel, runId)
            .WithPortBinding(Port, true)
            .WithPortBinding(ManagementPort, true)
            .WithLoopbackPortsOnly()
            // contract-005 · G-3 — key material by read-only bind mount, never copied into the container.
            .WithBindMount(Path.Combine(tls, TestPki.CertificateFile), "/e2e/tls/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(tls, TestPki.KeyFile), "/e2e/tls/tls.key", AccessMode.ReadOnly)
            .WithBindMount(realmPath, "/opt/keycloak/data/import/realm.json", AccessMode.ReadOnly)
            .WithCommand(
                "start",
                "--import-realm",
                $"--hostname=https://{Host}:{Port}",
                "--https-certificate-file=/e2e/tls/tls.crt",
                "--https-certificate-key-file=/e2e/tls/tls.key",
                "--http-enabled=false",
                "--health-enabled=true",
                "--db=dev-file")
            .Build();

        try
        {
            await container.StartAsync(cancellationToken);

            names = names
                .With(Host, Port, container.Hostname, container.GetMappedPublicPort(Port))
                .With(Host, ManagementPort, container.Hostname, container.GetMappedPublicPort(ManagementPort));

            var service = new KeycloakService(docker, container, secrets, names);
            await service.WaitUntilReadyAsync(cancellationToken);
            return service;
        }
        catch
        {
            // Its log is in the fault already; the container must not outlive the failure.
            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>A client-credentials token for a test class's own client.</summary>
    public async Task<string> ClientCredentialsTokenAsync(HttpClient http, string clientId, string? scope = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!_secrets.TryGetValue(clientId, out var secret))
        {
            throw new InvalidOperationException($"The realm has no client '{clientId}'. Clients are imported per test class at start.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(new Uri($"{Issuer}/protocol/openid-connect/token"), content, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!response.IsSuccessStatusCode || !body.TryGetProperty("access_token", out var token))
        {
            throw new InvalidOperationException($"Keycloak refused a client-credentials token for '{clientId}': {(int)response.StatusCode} {body}");
        }

        return token.GetString()!;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Ready when the management interface says so — which is after the realm import — and when the
    /// realm's discovery document names the pinned issuer. A Keycloak that answers with another
    /// issuer would mint tokens the server refuses, and that is the environment's fault.
    /// </summary>
    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var http = Names.CreateClient();
        var deadline = DateTime.UtcNow.AddMinutes(3);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DockerEngine.ExitCodeIfStoppedAsync(_docker, _container.Id, cancellationToken) is { } exitCode)
            {
                var (stdout, _) = await _container.GetLogsAsync(ct: cancellationToken);
                throw new EnvironmentFaultException("keycloak", $"Keycloak exited with code {exitCode} during startup: {Tail(stdout)}");
            }

            try
            {
                using var health = await http.GetAsync(new Uri($"https://{Host}:{ManagementPort}/health/ready"), cancellationToken);
                if (health.IsSuccessStatusCode)
                {
                    var discovery = await http.GetFromJsonAsync<JsonElement>(new Uri($"{Issuer}/.well-known/openid-configuration"), cancellationToken);
                    var issuer = discovery.GetProperty("issuer").GetString();
                    if (issuer != Issuer)
                    {
                        throw new EnvironmentFaultException("keycloak", $"Keycloak's realm names issuer '{issuer}', not the pinned '{Issuer}'.");
                    }

                    return;
                }
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                throw new EnvironmentFaultException("keycloak", $"CA trust missing: Keycloak's certificate did not validate against the run's test CA ({ex.InnerException.Message}).", ex);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new EnvironmentFaultException("keycloak", $"Keycloak was not ready within 3 minutes ({last?.Message ?? "health never answered 200"}).", last);
    }

    private static string Tail(string text) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(8));

    private static string RealmJson(string resource, IReadOnlyDictionary<string, string> secrets)
    {
        static Dictionary<string, object> Scope(string name, bool inToken) => new()
        {
            ["name"] = name,
            ["protocol"] = "openid-connect",
            ["attributes"] = new Dictionary<string, string>
            {
                ["include.in.token.scope"] = inToken ? "true" : "false",
                ["display.on.consent.screen"] = "false",
            },
        };

        // A realm that declares clientScopes gets none of Keycloak's built-in scopes, so the one that
        // puts sub in an access token is declared here.
        var basic = Scope("basic", inToken: false);
        basic["protocolMappers"] = new[]
        {
            new Dictionary<string, object>
            {
                ["name"] = "sub",
                ["protocol"] = "openid-connect",
                ["protocolMapper"] = "oidc-sub-mapper",
                ["consentRequired"] = false,
                ["config"] = new Dictionary<string, string>
                {
                    ["access.token.claim"] = "true",
                    ["introspection.token.claim"] = "true",
                },
            },
        };

        var clients = secrets.Select(client => new Dictionary<string, object>
        {
            ["clientId"] = client.Key,
            ["enabled"] = true,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["clientAuthenticatorType"] = "client-secret",
            ["secret"] = client.Value,
            ["serviceAccountsEnabled"] = true,
            ["standardFlowEnabled"] = false,
            ["implicitFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["fullScopeAllowed"] = false,
            ["attributes"] = new Dictionary<string, string> { ["access.token.signed.response.alg"] = "RS256" },
            ["defaultClientScopes"] = new[] { "basic", Scopes[0], Scopes[1], Scopes[2] },
            ["optionalClientScopes"] = new[] { Scopes[3] },
            ["protocolMappers"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = "audience",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-audience-mapper",
                    ["consentRequired"] = false,
                    ["config"] = new Dictionary<string, string>
                    {
                        // The server pins its audience to Authentication:Resource, exactly.
                        ["included.custom.audience"] = resource,
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "false",
                        ["introspection.token.claim"] = "true",
                    },
                },
            },
        }).ToArray();

        return JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["realm"] = Realm,
                ["enabled"] = true,
                ["sslRequired"] = "all",
                ["accessTokenLifespan"] = 600,
                ["clientScopes"] = new[] { basic }.Concat(Scopes.Select(s => Scope(s, inToken: true))).ToArray(),
                ["clients"] = clients,
            },
            Indented);
    }
}
