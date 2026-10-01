using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-6 (G-7, G-8, G-11 · UC-6) — with one fake's alias removed, that tool's call fails at the name lookup, and
/// no container of the run resolves a real name.
///
/// The pioneer's decision of 2026-09-29 (contract-005's revision of that date, "2. yes, cut it and mock any calls"), as
/// implemented, cut the run's network off from real names: every container is started with its own loopback address as its
/// only name server and no search domain (<see cref="E2ENetwork.NameServer"/>), so Docker's embedded DNS answers the network's
/// own names and fails every other at once. T-6's "that tool's call fails TLS" reads "fails at the name lookup".
///
/// contract-005 · UC-6 — the pioneer has since ruled on names versus a real cut: "no internet access mock everything". The network
/// itself is cut as well: it is internal (<see cref="E2ENetwork.CreateAsync"/>), so no container of the run reaches the internet
/// by an address written as one either, and the test process reaches the run through its one door (<see cref="Door"/>). That
/// half of T-6's "nothing reaches the internet" is witnessed live at the environment's start, a public address tried from a
/// container of the run and, as the positive control, from the door, and claimed by NetworkCutTests; this test proves the
/// names half.
///
/// The test takes a provider host of this server's whose owner the test process does not reach — the stand-in, in an ordinary run — and
/// its witness first: with the name on the network, the owner records this server's call under it (G-11: an absence is
/// asserted only on a witness seen recording in the same test). Then the name is taken off the network
/// (<see cref="E2EEnvironment.WithoutNameAsync"/>) and the same tool is called again; the stand-in closed its connection
/// after its answer, so the call looks the name up afresh. The claim: the call fails, and not by the frame; the server's
/// log names a failed name lookup for that host; no owner records a request under it from this server after the removal;
/// and every container on the run's network, as the engine records how it started, resolves no real name. The name is given
/// back whatever happens.
///
/// Every server of the run resolves the provider hosts through the one network, so this class runs in a collection of its
/// own, after every other (<see cref="UpstreamNamesAlone"/>).
/// </summary>
[Collection(Alone)]
public sealed class UpstreamNamesTests(UpstreamNamesTests.Server fixture, ITestOutputHelper output)
    : IClassFixture<UpstreamNamesTests.Server>
{
    /// <summary>The collection this class runs in, alone (<see cref="UpstreamNamesAlone"/>).</summary>
    public const string Alone = "contract-005 T-6: one fake's alias removed";

    /// <summary>Both SMHI providers bound to idp-a, which mints this class's token.</summary>
    public sealed class Server() : ServerFixture(SettingsDelta.None
        .Set("Providers:Smhi:IdentityProvider", E2EEnvironment.IdpA)
        .Set("Providers:SmhiObs:IdentityProvider", E2EEnvironment.IdpA));

    private const string IssuerA = "idp-a.e2e.test";

    private const string AliasLeftInPlace = "t6-the-alias-left-in-place";

    /// <summary>
    /// What .NET says when a name does not resolve, before the "(host:port)" an HttpRequestException adds: on Linux, as
    /// getaddrinfo words each failure or as .NET words the socket error it maps one to.
    /// </summary>
    private static readonly string[] NameLookupFailures =
    [
        "Name or service not known",            // EAI_NONAME
        "Temporary failure in name resolution", // EAI_AGAIN, getaddrinfo's words; the embedded DNS's SERVFAIL comes back so
        "Resource temporarily unavailable",     // EAI_AGAIN, as .NET words SocketError.TryAgain on Linux
        "No address associated with hostname",  // EAI_NODATA
        "No such host is known",                // HostNotFound, as .NET words it on Windows
    ];

    [Fact]
    [Sabotage(AliasLeftInPlace, SabotageActs.Network,
        "The host's name is not taken off the run's network: its owner keeps answering it, so the second call reaches the owner "
        + "and no name lookup fails.")]
    public async Task T6_with_one_fakes_alias_removed_that_tools_call_fails_at_the_name_lookup_and_no_container_resolves_a_real_name()
    {
        var environment = fixture.Environment;
        using var http = fixture.CreateClient();

        // contract-005 · G-17 round 2 (HAR-R2-02) — each owner's record read for this server: from its address since it started.
        var server = await fixture.Server.RequestSourceAsync();

        // What the image serves, from its own startup line (G-11), and the first of its tools whose provider's host is answered by
        // an owner the test process does not reach through the run's door: the one kind of owner a name can be taken from and given back.
        var line = FrameLine.Parse(await fixture.Server.StartupLineAsync());
        var tools = line.Entries(E2EEnvironment.Issuers.Entries.SelectMany(e => e.ScopeCatalog))
            .Where(e => e.Kind == "tool" && ProviderCalls.Hosts.ContainsKey(e.Provider))
            .ToList();
        var removable = await environment.RemovableNamesAsync();
        var tool = tools.FirstOrDefault(t => removable.Contains(ProviderCalls.Hosts[t.Provider], StringComparer.OrdinalIgnoreCase))
            ?? throw new EnvironmentFaultException(
                "upstream names",
                $"no tool this server serves calls a host whose owner the test process does not reach through the door (such owners answer [{string.Join(", ", removable)}]; "
                + $"the upstream registry: [{string.Join(", ", E2EEnvironment.Upstreams.Owners.Select(o => $"{o.Key} -> {o.Value.Name}"))}]).");
        var host = ProviderCalls.Hosts[tool.Provider];
        var owner = environment.UpstreamFor(host);
        Assert.True(ProviderCalls.Arguments.TryGetValue(tool.Key, out var arguments), $"{tool.Key} has no arguments in {nameof(ProviderCalls)}.");

        var token = await TestIssuerService.MintAsync(
            http, IssuerA, "valid", ServerUnderTest.Resource, E2EEnvironment.Issuers[E2EEnvironment.IdpA].ScopeCatalog);

        // The witness (positive control): with the name on the network, its owner records this server's call under it.
        var before = (await ProviderCalls.FromServerAsync(owner, http, server, host)).Count;
        var first = McpRequests.TextOf(await ProviderCalls.CallAsync(http, token, tool.Key, arguments));
        var witnessed = await ProviderCalls.SeenSinceAsync(owner, http, server, host, before);
        output.WriteLine($"{tool.Key}, with {host} on the network: {witnessed} request(s) at {owner.Container.Name.TrimStart('/')}; it answered '{first}'");
        Assert.True(witnessed > 0, $"the witness recorded nothing: {owner.Container.Name.TrimStart('/')} recorded no request from this server, {server}, under {host} during {tool.Key}, which answered '{first}'.");

        // The name taken off the network — unless in a run of this test's sabotage — and given back whatever happens.
        await using var removal = Sabotage.Applies(AliasLeftInPlace) ? null : await environment.WithoutNameAsync(host);

        var owners = environment.UpstreamServices.Values.ToList();
        var recorded = new Dictionary<IUpstreamService, int>();
        foreach (var each in owners)
        {
            recorded[each] = (await ProviderCalls.FromServerAsync(each, http, server, host)).Count;
        }

        var marker = $"({host}:";
        var logged = (await fixture.Server.StderrLinesAsync(marker)).Count;

        var answer = await ProviderCalls.CallAsync(http, token, tool.Key, arguments);
        var text = McpRequests.TextOf(answer);
        output.WriteLine($"{tool.Key}, with {host} taken off the network: it answered '{text}'");

        var failed = ProviderCalls.IsError(answer) && McpRequests.RuleOf(text) is null;
        var lookups = (await fixture.Server.StderrLinesAfterAsync(marker, logged))
            .Where(l => NameLookupFailures.Any(p => l.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var reached = new List<string>();
        foreach (var each in owners)
        {
            var since = await ProviderCalls.SeenSinceAsync(each, http, server, host, recorded[each]);
            if (since > 0)
            {
                reached.Add($"{each.Container.Name.TrimStart('/')} recorded {since}");
            }
        }

        var resolving = await E2ENetwork.ResolvingRealNamesAsync(environment.Docker, environment.Network.Name, CancellationToken.None);
        foreach (var lookup in lookups)
        {
            output.WriteLine($"  the server logged: {lookup}");
        }

        Claim.True(
            failed && lookups.Count > 0 && reached.Count == 0 && resolving.Count == 0,
            $"with {host} taken off the run's network, {tool.Key} answered '{text}' "
            + (failed ? "(a failure, not the frame's refusal)" : "(not a failure of its upstream call)")
            + $"; the server logged {(lookups.Count > 0 ? "a failed name lookup" : "no failed name lookup")} for {host}"
            + (reached.Count == 0 ? "; no upstream owner recorded the call" : $"; the call reached [{string.Join(", ", reached)}]")
            + (resolving.Count == 0
                ? "; every container on the run's network resolves no real name."
                : $"; and these containers on the run's network could resolve real names: {string.Join("; ", resolving)}."));
    }
}

/// <summary>
/// contract-005 · T-6 — the collection <see cref="UpstreamNamesTests"/> runs in, alone: xUnit runs a collection that disables
/// parallelization only after every parallel collection has finished, so no other class's server is calling the provider
/// host whose name it takes off the run's network.
/// </summary>
[CollectionDefinition(UpstreamNamesTests.Alone, DisableParallelization = true)]
public sealed class UpstreamNamesAlone;
