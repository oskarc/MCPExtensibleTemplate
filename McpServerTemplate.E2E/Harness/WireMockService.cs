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
/// (opendata-download-metfcst.smhi.se and jsonplaceholder.typicode.com; opendata-download-metobs.smhi.se
/// is T-15's stand-in's) and the neutral alias wiremock.e2e.test. Inside the network those names resolve to this container,
/// so the server under test calls its upstreams by their real names and reaches the fake. contract-005 · the pioneer's
/// decision of 2026-09-29 — and a name with no fake on the network resolves to nothing (<see cref="E2ENetwork.NameServer"/>):
/// the call fails at the name lookup, where it used to resolve to the real host and fail TLS there.
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
        var container = E2ENetwork.OnTheRunNetwork(Image, docker, network, runId)
            .WithNetworkAliases([.. hosts])
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
            // contract-005 · UC-6 — reached through the run's door.
            var published = Door.Open.Published(container, Port);
            names = names.With(Alias, Port, published.Host, published.Port);

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

    /// <summary>
    /// Every request the fake has recorded, as WireMock's admin API returns them. contract-005 · G-17 round 3 (HAR-R3-04) — private:
    /// a test reads one server's entries (<see cref="EntriesAsync"/>, through its <see cref="RequestSource"/>), and the diagnostics
    /// bundle's whole journal is written, never returned (<see cref="WriteJournalAsync"/>).
    /// </summary>
    private static async Task<JsonElement> JournalAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        return await http.GetFromJsonAsync<JsonElement>(new Uri($"https://{Alias}/__admin/requests"), cancellationToken);
    }

    /// <summary>contract-005 · G-17 round 3 (HAR-R3-04) — the whole journal, every server's, written to <paramref name="path"/> for the diagnostics bundle.</summary>
    internal static async Task WriteJournalAsync(HttpClient http, string path, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, (await JournalAsync(http, cancellationToken)).ToString(), cancellationToken);

    /// <summary>One request the fake recorded: the address it came from, when, and what it asked for.</summary>
    /// <param name="Id">WireMock's own id for the entry.</param>
    /// <param name="Client">The address the request came from: on the run's network, the server that made it.</param>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Url">The URL as the fake received it, host included.</param>
    /// <param name="Path">The path.</param>
    /// <param name="At">
    /// contract-005 · G-17 round 2 (HAR-R2-02) — when the fake received it (its Request.DateTime, UTC), by the Docker engine's
    /// clock, which the fake's is: what a read bounds by a server's start (<see cref="RequestSource"/>).
    /// </param>
    public sealed record JournalEntry(string Id, System.Net.IPAddress? Client, string Method, string Url, string Path, DateTimeOffset At);

    /// <summary>
    /// contract-005 · T-2, T-8 — every request the fake has recorded, read from its admin API. A request is attributed
    /// to the server that made it by its client address, which WireMock records for each: every server in a run shares
    /// the fake, and a test reads what its own server sent. contract-005 · G-17 round 2 (HAR-R2-02) — and by when it came,
    /// since Docker hands a freed address to the next container at once: a test reads its server's entries through
    /// <see cref="RequestSource.Made(JournalEntry)"/>, never by address alone. An entry with no time the fake wrote is a fault
    /// of the fake, named, and never a request silently left out. contract-005 · G-17 round 3 (HAR-R3-04) — the entries
    /// <paramref name="from"/> made, and no others: a test read every server's entries and filtered them itself, by
    /// <see cref="RequestSource"/> or by an address alone.
    /// </summary>
    public static async Task<IReadOnlyList<JournalEntry>> EntriesAsync(HttpClient http, RequestSource from, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        return [.. (await EveryEntryAsync(http, cancellationToken)).Where(from.Made)];
    }

    /// <summary>Every entry of the journal, every server's: for this type's own reads, each of which a <see cref="RequestSource"/> bounds.</summary>
    private static async Task<IReadOnlyList<JournalEntry>> EveryEntryAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var journal = await JournalAsync(http, cancellationToken);
        if (journal.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"WireMock's journal is not a list of requests: {journal}");
        }

        static string Read(JsonElement request, string name) =>
            request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

        return [.. journal.EnumerateArray().Select(entry =>
        {
            var request = entry.GetProperty("Request");
            var client = System.Net.IPAddress.TryParse(Read(request, "ClientIP"), out var address)
                ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
                : null;
            var at = DateTimeOffset.TryParse(
                Read(request, "DateTime"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var received)
                ? received
                : throw new InvalidOperationException(
                    $"WireMock's journal entry {Read(entry, "Guid")} carries no time it was received ('{Read(request, "DateTime")}'), so it "
                    + "cannot be told from an earlier server's at the same address.");
            return new JournalEntry(Read(entry, "Guid"), client, Read(request, "Method"), Read(request, "Url"), Read(request, "Path"), at);
        })];
    }

    /// <summary>
    /// contract-005 · T-10 — an upstream that holds its answer back: a GET whose path matches <paramref name="pathPattern"/>
    /// (a wildcard, * standing for any run of characters) is answered 200 with an empty JSON object, but only after
    /// <paramref name="delay"/>. Set through the admin API, as the journal is read; every server in the run shares the
    /// fake, so the pattern names a path no other test asks for. Returns the stub's id, for <see cref="RemoveStubAsync"/>.
    /// </summary>
    public static async Task<string> StubAsync(HttpClient http, string pathPattern, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(pathPattern);

        var mapping = new
        {
            Request = new
            {
                Path = new { Matchers = new[] { new { Name = "WildcardMatcher", Pattern = pathPattern } } },
                Methods = new[] { "GET" },
            },
            Response = new
            {
                StatusCode = 200,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Body = "{}",
                Delay = (int)delay.TotalMilliseconds,
            },
        };

        using var response = await http.PostAsJsonAsync(new Uri($"https://{Alias}/__admin/mappings"), mapping, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? guid = null;
        if (response.IsSuccessStatusCode)
        {
            using var added = JsonDocument.Parse(body);
            guid = added.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("Guid", StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } id
                ? id.GetString()
                : null;
        }

        return guid ?? throw new InvalidOperationException($"WireMock refused the stub for {pathPattern}: {(int)response.StatusCode} {body}");
    }

    /// <summary>contract-005 · T-10 — removes a stub <see cref="StubAsync"/> set; one already gone is gone either way.</summary>
    public static async Task RemoveStubAsync(HttpClient http, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var response = await http.DeleteAsync(new Uri($"https://{Alias}/__admin/mappings/{Uri.EscapeDataString(id)}"), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UpstreamRequest>> RecordedAsync(HttpClient http, RequestSource from, CancellationToken cancellationToken = default) =>
        [.. (await EntriesAsync(http, from, cancellationToken)).Select(e => new UpstreamRequest(
            e.Client,
            e.Method,
            Uri.TryCreate(e.Url, UriKind.Absolute, out var url) ? url.Host : string.Empty,
            e.Path,
            e.At))];

    /// <summary>
    /// contract-005 · G-17 round 2 (HAR-R2-06) — through the test process's name map, as a server would send it: a POST to
    /// <see cref="UpstreamWitness.Path"/> on the fake's own name, answered by no mapping, recorded as every request is.
    /// </summary>
    public async Task SendWitnessAsync(HttpClient http, string header, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{Alias}{UpstreamWitness.Path}"))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add(UpstreamWitness.Header, header);
        using var response = await http.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// contract-005 · T-6 — every entry of the journal, whole, as WireMock's admin API returns it: the request's URL, every
    /// header, its cookies and its body, and the fake's answer. A body WireMock keeps as bytes (Request.BodyAsBytes, base64) is
    /// decoded and added, so a token in it reads as it was sent.
    /// </summary>
    public async Task<IReadOnlyList<string>> WholeRecordsAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        var journal = await JournalAsync(http, cancellationToken);
        if (journal.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"WireMock's journal is not a list of requests: {journal}");
        }

        return [.. journal.EnumerateArray().Select(entry =>
        {
            var whole = entry.GetRawText();
            if (entry.TryGetProperty("Request", out var request)
                && request.TryGetProperty("BodyAsBytes", out var bytes)
                && bytes.ValueKind == JsonValueKind.String
                && bytes.GetString() is { Length: > 0 } encoded)
            {
                var buffer = new byte[encoded.Length];
                if (Convert.TryFromBase64String(encoded, buffer, out var written))
                {
                    whole += "\n" + System.Text.Encoding.UTF8.GetString(buffer, 0, written);
                }
            }

            return whole;
        })];
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
