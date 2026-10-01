using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Docker.DotNet;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · UC-6 — the pioneer's decision of 2026-09-30, "no internet access mock everything": the run's network is
/// internal (<see cref="E2ENetwork.CreateAsync"/>), so no container on it has a route off it, and none can publish a port. The
/// door is the one way in from the test process: one container of the pinned nginx image, on the run's network at a static
/// address (<see cref="E2ENetwork.DoorAddress"/>) and on an ordinary network of its own, publishing one port on 127.0.0.1. A
/// connection to it names its target in a PROXY protocol header, and the door carries it to that address of the run's network
/// and to no other address.
///
/// The test process reaches a container's port as it used to reach its published port: <see cref="Published"/> gives a port on
/// 127.0.0.1 of its own, where each connection is carried to the door with that header. So the host-side endpoints the tests
/// use — the name map's entries, a server's direct endpoint — are unchanged in form. A request reaches a container of the run
/// from the door's address, where one to a published port arrived from the network's gateway.
///
/// The door itself reaches the internet, through its ordinary network: it is the positive control of the run's live witness
/// that nothing on the run's network does (<see cref="WitnessAsync"/>).
/// </summary>
public sealed class Door : IAsyncDisposable
{
    /// <summary>The port the door listens on, inside its container.</summary>
    public const int Port = 8000;

    /// <summary>The public address the witness tries to connect to: Cloudflare's resolver, on https's port.</summary>
    public const string PublicAddress = "1.1.1.1";

    /// <summary>The port of <see cref="PublicAddress"/> the witness tries.</summary>
    public const int PublicPort = 443;

    private static Door? _open;

    private readonly IDockerClient _docker;
    private readonly IContainer _container;
    private readonly INetwork _outside;
    private readonly string _runNetwork;
    private readonly DnsEndPoint _published;
    private readonly ConcurrentDictionary<(string Container, int Port), Lazy<Relay>> _relays = new();

    internal Door(IDockerClient docker, IContainer container, INetwork outside, string runNetwork)
    {
        _docker = docker;
        _container = container;
        _outside = outside;
        _runNetwork = runNetwork;
        _published = new DnsEndPoint(IPAddress.Loopback.ToString(), container.GetMappedPublicPort(Port));
        _open = this;
    }

    /// <summary>The run's door, once the environment has opened it.</summary>
    public static Door Open => _open ?? throw new InvalidOperationException("The run's door is not open: the environment has not started.");

    public IContainer Container => _container;

    /// <summary>
    /// Where the test process reaches <paramref name="port"/> of <paramref name="container"/>: a port on 127.0.0.1 whose every
    /// connection is carried through the door to the container's address on the run's network, read from the engine as the
    /// connection is made.
    /// </summary>
    public DnsEndPoint Published(IContainer container, int port)
    {
        ArgumentNullException.ThrowIfNull(container);
        return _relays.GetOrAdd((container.Id, port), key => new Lazy<Relay>(() => new Relay(this, key.Container, key.Port))).Value.EndPoint;
    }

    /// <summary>Whether the test process reaches the container <paramref name="containerId"/> through the door.</summary>
    public bool Reaches(string containerId) => _relays.Keys.Any(k => k.Container == containerId);

    /// <summary>
    /// The door's nginx configuration: a connection names its target in a PROXY header, and the door carries it there when the
    /// target is an address of the run's network other than the door's own; anything else goes to a port nothing listens on.
    /// </summary>
    internal static string Configuration(IEnumerable<IPAddress> runAddresses)
    {
        var targets = string.Concat(runAddresses.Select(a => string.Create(
            CultureInfo.InvariantCulture, $"        {a} {a}:$proxy_protocol_server_port;\n")));
        return string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            # contract-005 · UC-6 — the run's door: the test process's one way onto the internal run network.
            worker_processes 1;
            error_log /dev/stderr notice;
            pid /tmp/nginx.pid;

            events { worker_connections 4096; }

            stream {
                map $proxy_protocol_server_addr $e2e_target {
            {{targets}}        default 127.0.0.1:9;
                }

                server {
                    listen {{Port}} proxy_protocol;
                    proxy_pass $e2e_target;
                    proxy_timeout 1h;
                }
            }
            """);
    }

    /// <summary>
    /// contract-005 · UC-6 — the live witness: the same TCP connect to <see cref="PublicAddress"/>:<see cref="PublicPort"/>, from
    /// <paramref name="onTheRun"/>, a container of the run's network, and from the door, which sits on an ordinary network too.
    /// </summary>
    internal async Task<InternetWitness> WitnessAsync(IContainer onTheRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onTheRun);

        string[] probe = ["nc", "-z", "-w", "5", PublicAddress, PublicPort.ToString(CultureInfo.InvariantCulture)];
        var fromTheRun = await onTheRun.ExecAsync(probe, cancellationToken);
        var fromTheDoor = await _container.ExecAsync(probe, cancellationToken);
        return new InternetWitness(
            $"{onTheRun.Name.TrimStart('/')} ({onTheRun.Image.Repository})", fromTheRun.ExitCode == 0, (fromTheRun.Stdout + fromTheRun.Stderr).Trim(),
            fromTheDoor.ExitCode == 0, (fromTheDoor.Stdout + fromTheDoor.Stderr).Trim());
    }

    public async ValueTask DisposeAsync()
    {
        _open = null;
        foreach (var relay in _relays.Values.Where(r => r.IsValueCreated).Select(r => r.Value))
        {
            relay.Dispose();
        }

        await _container.DisposeAsync();
        await _outside.DisposeAsync();
    }

    /// <summary>
    /// One connection of the test process, carried through the door to <paramref name="port"/> of the container
    /// <paramref name="containerId"/>. A container that is not running has no address, and the connection is closed, as a
    /// published port of a stopped container would close it.
    /// </summary>
    private async Task CarryAsync(Socket client, string containerId, int port)
    {
        using var inside = client;
        using var door = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            client.NoDelay = true;
            var target = await AddressAsync(containerId);
            if (target is null)
            {
                return;
            }

            await door.ConnectAsync(_published);
            var source = (IPEndPoint)client.RemoteEndPoint!;
            await door.SendAsync(Encoding.ASCII.GetBytes(string.Create(
                CultureInfo.InvariantCulture, $"PROXY TCP4 {source.Address} {target} {source.Port} {port}\r\n")));
            await Task.WhenAll(PumpAsync(client, door), PumpAsync(door, client));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or DockerApiException)
        {
            // Either side went: the connection ends, as a published port's would.
        }
    }

    /// <summary>The container's address on the run's network, or null when it is not running there.</summary>
    private async Task<string?> AddressAsync(string containerId)
    {
        try
        {
            var inspected = await _docker.Containers.InspectContainerAsync(containerId);
            return inspected.State is { Running: true }
                && inspected.NetworkSettings?.Networks is { } networks
                && networks.TryGetValue(_runNetwork, out var endpoint)
                && !string.IsNullOrEmpty(endpoint.IPAddress)
                ? endpoint.IPAddress
                : null;
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Copies one direction until it ends, then ends it at the other side; a failure on either side closes both.</summary>
    private static async Task PumpAsync(Socket from, Socket to)
    {
        try
        {
            await using (var reading = new NetworkStream(from, ownsSocket: false))
            await using (var writing = new NetworkStream(to, ownsSocket: false))
            {
                await reading.CopyToAsync(writing);
            }

            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            from.Close();
            to.Close();
        }
    }

    /// <summary>A port on 127.0.0.1 standing for one container's port: every connection to it is carried through the door.</summary>
    private sealed class Relay : IDisposable
    {
        private readonly Door _door;
        private readonly string _container;
        private readonly int _port;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public Relay(Door door, string container, int port)
        {
            _door = door;
            _container = container;
            _port = port;
            _listener.Start();
            EndPoint = new DnsEndPoint(IPAddress.Loopback.ToString(), ((IPEndPoint)_listener.LocalEndpoint).Port);
            _ = AcceptAsync();
        }

        public DnsEndPoint EndPoint { get; }

        public void Dispose() => _listener.Stop();

        private async Task AcceptAsync()
        {
            while (true)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptSocketAsync();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = _door.CarryAsync(client, _container, _port);
            }
        }
    }
}

/// <summary>
/// contract-005 · UC-6 — what the live witness saw (<see cref="Door.WitnessAsync"/>): whether a TCP connect to a public address
/// succeeded from a container of the run's network, and from the door, on an ordinary network, as its positive control.
/// </summary>
public sealed record InternetWitness(string RunContainer, bool FromTheRunReached, string FromTheRunSaid, bool FromTheDoorReached, string FromTheDoorSaid)
{
    public string Describe() =>
        $"a TCP connect to {Door.PublicAddress}:{Door.PublicPort} from {RunContainer}, on the run's network, "
        + $"{(FromTheRunReached ? "succeeded" : "failed")}{Said(FromTheRunSaid)}; from the door, on an ordinary network, "
        + $"{(FromTheDoorReached ? "succeeded" : "failed")}{Said(FromTheDoorSaid)}";

    private static string Said(string text) => text.Length == 0 ? string.Empty : $" ({text})";
}
