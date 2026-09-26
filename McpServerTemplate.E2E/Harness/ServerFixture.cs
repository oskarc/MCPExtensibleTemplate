using System.Text.RegularExpressions;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// A test class's own server, started into the shared environment.
///
/// contract-005 · G-8 — one server container per test class, so no count in Redis or at a fake leaks
/// from one class into another. A test class declares its server by nesting a subclass of this and
/// taking it as an IClassFixture; the subclass passes the class's <see cref="SettingsDelta"/>, which is
/// the only way the class configures its server.
///
/// contract-005 · G-5 — each subclass is also a Keycloak client of its own: the realm is imported with
/// one client-credentials client per subclass in this assembly, so two classes never share a
/// subject, and a per-caller limit spent by one is never seen by another.
/// </summary>
public abstract partial class ServerFixture : IAsyncLifetime
{
    protected ServerFixture(SettingsDelta delta)
    {
        Delta = delta;
        ClientAddress = ClientAddresses.Next();
    }

    /// <summary>What this class changes about the server's configuration.</summary>
    public SettingsDelta Delta { get; }

    /// <summary>
    /// The client address the front forwards for this class's requests (G-9), unique to the class, so
    /// the per-address limiter never sees two classes as one client.
    /// </summary>
    public string ClientAddress { get; }

    /// <summary>This class's Keycloak client.</summary>
    public string KeycloakClientId => ClientIdFor(GetType());

    public E2EEnvironment Environment { get; private set; } = null!;

    public ServerUnderTest Server { get; private set; } = null!;

    /// <summary>The class's name for its server and its diagnostics: the test class, in kebab case.</summary>
    public string Name => Kebab(GetType().DeclaringType?.Name ?? GetType().Name);

    public async Task InitializeAsync()
    {
        Environment = await E2EEnvironment.GetAsync();
        Server = await Environment.StartServerAsync(Name, Delta);
    }

    public async Task DisposeAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }
    }

    /// <summary>An HTTP client through the name map; requests to the front carry this class's client address.</summary>
    public HttpClient CreateClient(NameMapLog? log = null, string? clientAddress = null) =>
        Server.CreateClient(clientAddress ?? ClientAddress, log);

    /// <summary>A fresh client-credentials token from Keycloak for this class's client.</summary>
    public async Task<string> KeycloakTokenAsync(string? scope = null)
    {
        using var http = CreateClient();
        return await Environment.Keycloak.ClientCredentialsTokenAsync(http, KeycloakClientId, scope);
    }

    /// <summary>The Keycloak client id for a fixture type.</summary>
    internal static string ClientIdFor(Type fixture) => $"e2e-{Kebab(fixture.DeclaringType?.Name ?? fixture.Name)}";

    /// <summary>Every fixture type's client id: the clients the realm is imported with.</summary>
    internal static IReadOnlyList<string> AllClientIds() =>
        [.. typeof(ServerFixture).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(ServerFixture)))
            .Select(ClientIdFor)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static string Kebab(string name) => UpperCaseBoundary().Replace(name, "$1-$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex UpperCaseBoundary();
}

/// <summary>
/// Client addresses for the front's test-only header: RFC 5737 TEST-NET-3, which no real client has.
/// </summary>
public static class ClientAddresses
{
    private static int _next;

    /// <summary>The address the harness's own self-checks are forwarded as, apart from every test's.</summary>
    public const string SelfCheck = "203.0.113.254";

    /// <summary>An address no other caller in this run has been given.</summary>
    public static string Next()
    {
        var n = Interlocked.Increment(ref _next);
        if (n >= 254)
        {
            throw new InvalidOperationException("TEST-NET-3 has no client address left for this run.");
        }

        return $"203.0.113.{n}";
    }
}
