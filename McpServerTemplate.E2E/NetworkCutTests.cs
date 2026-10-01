using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-6 (UC-6) — nothing reaches the internet: the pioneer's ruling, "no internet access mock everything". The run's
/// network is internal (<see cref="E2ENetwork.CreateAsync"/>), so no container on it has a route off it, by name or by an address
/// written as one; UpstreamNamesTests proves the names half.
///
/// The witness is live, taken at the environment's start (<see cref="Door.WitnessAsync"/>): a TCP connect to a public address from
/// a container of the run's network, which must fail, and the same connect from the run's door, which sits on an ordinary network
/// too and must succeed — the positive control. A machine with no internet at all fails the control, and that is an environment
/// fault, never a pass.
///
/// No server of its own: the witness is the environment's.
/// </summary>
public sealed class NetworkCutTests(ITestOutputHelper output)
{
    [Fact]
    [Sabotage(E2ENetwork.NotInternal, SabotageActs.Network,
        "The run's network is created as an ordinary bridge network, not internal: its containers have a route off it, and only "
        + "their names are cut.")]
    public async Task T6_no_container_of_the_run_reaches_the_internet()
    {
        var environment = await E2EEnvironment.GetAsync();
        var witness = environment.Internet;
        output.WriteLine(witness.Describe());

        if (!witness.FromTheDoorReached)
        {
            throw new EnvironmentFaultException(
                "no internet",
                $"the positive control failed: {witness.Describe()}. This machine reaches no internet, so the cut cannot be witnessed.");
        }

        Claim.True(!witness.FromTheRunReached, $"a container of the run reached the internet: {witness.Describe()}.");
    }
}
