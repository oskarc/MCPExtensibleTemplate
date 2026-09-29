namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-11 — a server's "Frame installed:" line, read into its parts. The expected sets a test uses — the
/// tools, resources and prompts an image serves, whose they are, and what each requires — come from here: the image's
/// own account of what it installed, at startup. The fast suite's rule matrices remain the independent check on the
/// line itself.
///
/// Its shape is GovernedServer.ValidateAtStartup's and FrameManifest.Describe's:
///   Frame installed: limits={store} providers={name,name} requests={method,method} :: {list}=[frame,sdk] {list}=[…] | {entry};{entry};…
/// where an entry is tool:{provider}/{name}:{scope}:{risk}, resource:{provider}/{uri}:{scope} or
/// prompt:{provider}/{name}:{scope}. A scope and a resource URI may both hold colons, so an entry is split with the
/// scopes the environment's identity providers can issue (<see cref="Entries"/>).
/// </summary>
public sealed record FrameLine(string Text, string Limits, IReadOnlyList<string> Providers, string Filters, IReadOnlyList<string> Manifest)
{
    private const string Marker = "Frame installed: ";

    /// <summary>
    /// contract-005 · G-11 — the request kinds the frame governs, as the line's requests= names them: every method it
    /// lets past its request-kind gate. Empty when the line names none.
    /// </summary>
    public IReadOnlyList<string> RequestKinds { get; init; } = [];

    /// <summary>One governed primitive on the line.</summary>
    /// <param name="Kind">tool, resource or prompt.</param>
    /// <param name="Provider">The provider module that declares it.</param>
    /// <param name="Key">Its name, or a resource's URI.</param>
    /// <param name="Scope">The scope a caller must hold to use it.</param>
    /// <param name="Risk">A tool's risk class; null for a resource or a prompt.</param>
    public sealed record Entry(string Kind, string Provider, string Key, string Scope, string? Risk);

    /// <summary>Reads the line from where "Frame installed: " begins; whatever a log put before it is not part of it.</summary>
    /// <exception cref="FormatException">The line is not a frame line of that shape.</exception>
    public static FrameLine Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var start = line.IndexOf(Marker, StringComparison.Ordinal);
        var text = start < 0 ? throw new FormatException($"'{line}' holds no '{Marker.Trim()}'.") : line[start..].Trim();
        var body = text[Marker.Length..];

        var headEnd = body.IndexOf(" :: ", StringComparison.Ordinal);
        var manifestStart = body.IndexOf(" | ", StringComparison.Ordinal);
        if (headEnd < 0 || manifestStart < headEnd)
        {
            throw new FormatException($"'{text}' is not 'limits=… providers=… :: {{filters}} | {{manifest}}'.");
        }

        var head = body[..headEnd].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? pair[1] : string.Empty, StringComparer.Ordinal);
        if (!head.TryGetValue("limits", out var limits) || !head.TryGetValue("providers", out var providers))
        {
            throw new FormatException($"'{text}' names no limits= or no providers= before its filters.");
        }

        return new FrameLine(
            text,
            limits,
            providers.Split(',', StringSplitOptions.RemoveEmptyEntries),
            body[(headEnd + 4)..manifestStart],
            body[(manifestStart + 3)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            RequestKinds = head.TryGetValue("requests", out var requests) ? requests.Split(',', StringSplitOptions.RemoveEmptyEntries) : [],
        };
    }

    /// <summary>
    /// The manifest's entries, each split with the longest of <paramref name="scopes"/> that ends it (after a tool's
    /// risk class). An entry that no scope ends is a fault of the line or of the scopes given, and is named.
    /// </summary>
    public IReadOnlyList<Entry> Entries(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var known = scopes.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();

        return [.. Manifest.Select(raw =>
        {
            var kindEnd = raw.IndexOf(':', StringComparison.Ordinal);
            var providerEnd = raw.IndexOf('/', kindEnd + 1);
            if (kindEnd < 0 || providerEnd < 0)
            {
                throw new FormatException($"The manifest entry '{raw}' is not {{kind}}:{{provider}}/{{key}}:{{scope}}.");
            }

            var kind = raw[..kindEnd];
            var rest = raw[(providerEnd + 1)..];
            string? risk = null;
            if (kind == "tool")
            {
                var riskStart = rest.LastIndexOf(':');
                risk = rest[(riskStart + 1)..];
                rest = rest[..riskStart];
            }

            var scope = known.FirstOrDefault(s => rest.EndsWith(":" + s, StringComparison.Ordinal))
                ?? throw new FormatException($"The manifest entry '{raw}' ends with no scope of [{string.Join(", ", known)}].");
            return new Entry(kind, raw[(kindEnd + 1)..providerEnd], rest[..^(scope.Length + 1)], scope, risk);
        })];
    }
}
