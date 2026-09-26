using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The network constant, and the one network a run creates from it.
///
/// contract-005 · G-4, G-16 — every container of a run sits on one user-defined network whose IPv4
/// subnet is <see cref="Subnet"/>. Docker's default networks fall in 172.16/12, which roadmap P2.7's
/// egress guard will refuse, so a fake upstream on one would be unreachable by design once that guard
/// lands. TEST-NET-2 lies outside P2.7's list as the roadmap states it today. Which ranges the guard
/// blocks is the egress contract's decision, not this constant's: if that decision moves, this is the
/// one line that moves with it, and no test-only relaxation of address checking is allowed instead.
///
/// Every address the harness uses is derived from the constant: the gateway is its first address,
/// the lower half holds the static addresses the front and the proxies are given (they must be
/// known before the server starts, because KnownProxies names them), and Docker hands out the
/// upper half to everything else, so a static address can never be taken by a dynamic one.
/// </summary>
public static class E2ENetwork
{
    /// <summary>The run's IPv4 subnet: a slice of 198.51.100.0/24 (RFC 5737 TEST-NET-2).</summary>
    public const string Subnet = "198.51.100.0/26";

    /// <summary>A label on everything a run creates, so a run's leftovers can be found by hand.</summary>
    public const string RunLabel = "org.mcp-server-template.e2e.run";

    private static readonly IPAddress Base = IPAddress.Parse(Subnet.Split('/')[0]);
    private static readonly int PrefixLength = int.Parse(Subnet.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture);
    private static readonly int Size = 1 << (32 - PrefixLength);

    // The first static address handed out; below it the gateway and a few spare addresses.
    private static int _nextStatic = 9;

    /// <summary>The network's gateway: the subnet's first address.</summary>
    public static IPAddress Gateway => Offset(1);

    /// <summary>The upper half of the subnet, from which Docker assigns addresses on its own.</summary>
    public static string DynamicRange => $"{Offset(Size / 2)}/{PrefixLength + 1}";

    /// <summary>
    /// A static address in the lower half, unique for this run. The front is given one before the
    /// server starts, so the server's KnownProxies can name it.
    /// </summary>
    public static IPAddress AllocateStatic()
    {
        var offset = Interlocked.Increment(ref _nextStatic);
        if (offset >= Size / 2)
        {
            throw new InvalidOperationException(
                $"The static half of {Subnet} is used up ({offset - 10} addresses handed out this run).");
        }

        return Offset(offset);
    }

    /// <summary>
    /// Creates the run's network. A subnet another network already holds is an environment fault,
    /// named with the network that holds it. A killed run's network is removed before this, by the
    /// start's sweep of dead runs, so the holder is a run still alive on this machine or a network that
    /// is not a run's at all.
    /// </summary>
    internal static async Task<INetwork> CreateAsync(string runId, IDockerClient docker, CancellationToken cancellationToken)
    {
        var network = new NetworkBuilder()
            .WithName($"mcp-e2e-{runId}")
            .WithLabel(RunLabel, runId)
            .WithCreateParameterModifier(parameters =>
            {
                parameters.EnableIPv6 = false;
                parameters.IPAM = new IPAM
                {
                    Config =
                    [
                        new IPAMConfig
                        {
                            Subnet = Subnet,
                            IPRange = DynamicRange,
                            Gateway = Gateway.ToString(),
                        },
                    ],
                };
            })
            .Build();

        try
        {
            await network.CreateAsync(cancellationToken);
            return network;
        }
        catch (DockerApiException ex)
        {
            var holder = await HolderOfSubnetAsync(docker, cancellationToken);
            throw new EnvironmentFaultException(
                "network",
                holder switch
                {
                    null => $"the run's network on {Subnet} could not be created: {ex.Message}",
                    { Labels: { } labels } when labels.TryGetValue(RunLabel, out var run) =>
                        $"subnet taken: {Subnet} is held by network '{holder.Name}' of run {run}, which is still alive on this "
                        + "machine (a dead run's would have been swept). Wait for it to finish; two runs cannot share the subnet.",
                    _ => $"subnet taken: {Subnet} overlaps network '{holder.Name}', which is not a run's. Remove it or move it "
                        + "off the subnet (docker network rm " + holder.Name + ").",
                },
                ex);
        }
    }

    private static async Task<NetworkResponse?> HolderOfSubnetAsync(IDockerClient docker, CancellationToken cancellationToken)
    {
        var networks = await docker.Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken);
        return networks.FirstOrDefault(n => n.IPAM?.Config?.Any(c => Overlaps(c.Subnet)) == true);
    }

    private static bool Overlaps(string? cidr)
    {
        if (cidr is null || !cidr.Contains('/', StringComparison.Ordinal) ||
            !IPNetwork.TryParse(cidr, out var other) || other.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var ours = IPNetwork.Parse(Subnet);
        return ours.Contains(other.BaseAddress) || other.Contains(ours.BaseAddress);
    }

    private static IPAddress Offset(int offset)
    {
        var bytes = Base.GetAddressBytes();
        var value = ((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]) + (uint)offset;
        return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }
}
