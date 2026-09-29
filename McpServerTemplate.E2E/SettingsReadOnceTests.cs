using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Docker.DotNet.Models;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-003 · G-11, contract-005 review round 2 — the server reads its settings once, at startup,
/// where they are checked.
///
/// The settings allowlist refuses, at startup, any key it does not know in the sections it governs,
/// Authentication:Schemes among them: ASP.NET Core binds each bearer scheme's options from
/// Authentication:Schemes:{scheme} as well as from the server's own registration, so a MetadataAddress
/// there sends the scheme to another provider's keys. The host also watched appsettings*.json and read
/// in what they gained while it ran, where no check reads them; a scheme's options are built when it is
/// first used, so a scheme first used after the change was built from it. Here the shipped
/// appsettings.Production.json gains that key for idp-b's scheme, which nothing has used yet — copied in
/// through Docker's archive API, as anything with write access to the file could — and a token naming
/// idp-b but signed with idp-a's key must still be refused. A restart reads the file as it now is, and
/// refuses to start.
///
/// No server of its own: the one it starts has its settings changed under it, so no other test uses it.
/// </summary>
public sealed class SettingsReadOnceTests(ITestOutputHelper output)
{
    private const string IdpAHost = "idp-a.e2e.test";

    private const string IdpBHost = "idp-b.e2e.test";

    private const string IdpBTrustsIdpAKeys = "settings-read-once-idp-b-keys-from-idp-a";

    /// <summary>
    /// Its sabotage gives the server, from its start, what the changed file would give it if it were read: idp-b's scheme
    /// fetching idp-a's keys.
    /// </summary>
    [Fact]
    [Sabotage(IdpBTrustsIdpAKeys, SabotageActs.ContainerEnvironment,
        "The server is started with idp-b's Authority at https://idp-a.e2e.test, so idp-b's scheme takes idp-a's keys from the "
        + "start, as it would had the running server read the changed file.")]
    public async Task A_settings_file_changed_while_the_server_runs_changes_nothing_until_it_restarts()
    {
        var environment = await E2EEnvironment.GetAsync();
        await using var server = await environment.StartServerAsync(
            "settings-read-once",
            SettingsDelta.None.Sabotaged(IdpBTrustsIdpAKeys, d => d.Set($"Authentication:IdentityProviders:{E2EEnvironment.IdpB}:Authority", $"https://{IdpAHost}")));
        using var http = server.CreateClient(ClientAddresses.Next());

        // Positive control: this server accepts the test issuer's tokens. idp-a's, so idp-b's scheme is not
        // used, and its options not built, until after the change.
        var idpA = await TestIssuerService.MintAsync(http, IdpAHost, "valid", ServerUnderTest.Resource, ["weather:read"]);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(http, idpA));

        // The shipped file, as the image carries it, with idp-b's scheme sent to idp-a's metadata and keys.
        const string key = "Authentication:Schemes:idp:idp-b:MetadataAddress";
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(environment.RepositoryRoot, "McpServerTemplate", "appsettings.Production.json")))!.AsObject();
        settings["Authentication"] = new JsonObject
        {
            ["Schemes"] = new JsonObject
            {
                ["idp"] = new JsonObject
                {
                    ["idp-b"] = new JsonObject { ["MetadataAddress"] = $"https://{IdpAHost}/.well-known/openid-configuration" },
                },
            },
        };
        await server.Container.CopyAsync(Encoding.UTF8.GetBytes(settings.ToJsonString()), "/app/appsettings.Production.json");
        output.WriteLine($"/app/appsettings.Production.json now carries {key}.");

        // A watched file is read again 250 ms after it changes; three seconds is well past that, so idp-b's
        // scheme, used for the first time now, is built from whatever the server holds by then.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var crossSigned = await TestIssuerService.MintAsync(
            http, IdpBHost, "cross-signed", ServerUnderTest.Resource, ["weather:read"],
            new Dictionary<string, object> { ["signedBy"] = IdpAHost });
        var status = await InitializeAsync(http, crossSigned);
        Claim.True(
            status == HttpStatusCode.Unauthorized,
            $"after /app/appsettings.Production.json gained {key}=https://{IdpAHost}/…, a token naming idp-b and signed with "
            + $"idp-a's key got {(int)status}, not 401. The running server applied a setting no startup check had read.");

        // Unchanged until it restarts: idp-b's scheme holds idp-b's own keys, so idp-b's own token is accepted
        // and the refusal above was the signature's.
        var idpB = await TestIssuerService.MintAsync(http, IdpBHost, "valid", ServerUnderTest.Resource, ["weather:read"]);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(http, idpB));

        // The restart reads the file as it now is, and refuses what it gained.
        var docker = environment.Docker;
        await docker.Containers.StopContainerAsync(server.Container.Id, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
        await docker.Containers.StartContainerAsync(server.Container.Id, new ContainerStartParameters());
        long? exitCode = null;
        var restartDeadline = DateTime.UtcNow.AddMinutes(1);
        while (exitCode is null && DateTime.UtcNow < restartDeadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            exitCode = await DockerEngine.ExitCodeIfStoppedAsync(docker, server.Container.Id, CancellationToken.None);
        }

        var (_, stderr) = await server.Container.GetLogsAsync();
        var refusal = StartupOutcome.RefusalIn(stderr);
        output.WriteLine($"On restart: exit {exitCode}; {refusal}");
        Claim.True(
            exitCode == 78 && refusal?.Contains($"'{key}'", StringComparison.Ordinal) == true,
            $"restarted with {key} in /app/appsettings.Production.json, the server exited with {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no code within a minute"}; "
            + $"{refusal ?? "its stderr names no refusal"}.");
    }

    private static async Task<HttpStatusCode> InitializeAsync(HttpClient http, string token)
    {
        using var response = await http.SendAsync(McpRequests.Initialize(ServerUnderTest.Endpoint, token));
        return response.StatusCode;
    }
}
