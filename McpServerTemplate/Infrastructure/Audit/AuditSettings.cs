using System.Text.RegularExpressions;

namespace McpServerTemplate.Infrastructure.Audit;

/// <summary>
/// The audit trail's settings, read once and checked at startup (contract-006 · G-12). Every one has a default, so a
/// deployment that sets none still has a trail: the roadmap's defaults for the two decisions nobody made (D6: 400 days;
/// D7: a request that is not a change is refused too while the trail is down).
/// </summary>
/// <param name="Directory">Where the audit files are written, resolved against the working directory.</param>
/// <param name="ReadWhenUnavailable">
/// Whether a request that changes nothing is served while the trail cannot be written. A Write or Irreversible tool never is.
/// </param>
/// <param name="RetentionDays">How many days of audit files are kept.</param>
/// <param name="AppId">The application id each record carries (roadmap LOG-02).</param>
/// <param name="Otlp">The collector a copy of each record is sent to; null when none is configured.</param>
public sealed partial record AuditSettings(string Directory, bool ReadWhenUnavailable, int RetentionDays, string AppId, AuditOtlpSettings? Otlp)
{
    /// <summary>
    /// Where the files go when <c>Audit:Path</c> is not set: beside the log files, the one directory the image's user may
    /// write (Dockerfile), so the shipped image has a trail with nothing configured.
    /// </summary>
    public const string DefaultPath = "logs/audit";

    /// <summary>Roadmap D6's default.</summary>
    public const int DefaultRetentionDays = 400;

    /// <summary>The application id when <c>Audit:AppId</c> is not set (roadmap §3.5).</summary>
    public const string DefaultAppId = "mcp-server";

    /// <summary>Reads and checks the <c>Audit</c> section.</summary>
    /// <param name="configuration">The server's settings.</param>
    /// <param name="environment">The environment: a plain-http collector is allowed in Development alone, on loopback.</param>
    /// <param name="workingDirectory">The directory a relative <c>Audit:Path</c> resolves against.</param>
    /// <exception cref="ConfigurationException">A value the server will not honour, or a directory it cannot write.</exception>
    public static AuditSettings Read(IConfiguration configuration, IHostEnvironment environment, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        var configured = configuration["Audit:Path"];
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured;

        // As for a log file (LogSinkGuard): a path that can climb out of its directory is a write primitive.
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new ConfigurationException(
                $"Audit:Path is '{path}', which contains path traversal characters (..). Use an absolute path.");
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(path, workingDirectory);
            ProbeWritable(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new ConfigurationException(
                $"Audit:Path is '{path}', resolved against the working directory {workingDirectory}, and this process cannot "
                + $"write the audit trail there: {ex.Message} A server that cannot record a decision refuses it, so it does not "
                + "start without a trail it can write.");
        }

        var mode = configuration["Audit:ReadWhenUnavailable"];
        var readWhenUnavailable = mode?.Trim().ToUpperInvariant() switch
        {
            null or "" or "DENY" => false,
            "ALLOW" => true,
            _ => throw new ConfigurationException(
                $"Audit:ReadWhenUnavailable is '{LogSafe.Text(mode!)}'. It is 'deny' (the default: while the audit trail cannot be "
                + "written, nothing is served) or 'allow' (requests that change nothing are served unrecorded; a Write or "
                + "Irreversible tool never is)."),
        };

        var retention = ConfigurationGuard.IntegerInRange(
            configuration, "Audit:RetentionDays", minimum: 1, maximum: 36_500, fallback: DefaultRetentionDays,
            because: "days of audit files kept");

        var appId = configuration["Audit:AppId"];
        if (string.IsNullOrWhiteSpace(appId))
        {
            appId = DefaultAppId;
        }
        else if (!AppIdShape().IsMatch(appId))
        {
            throw new ConfigurationException(
                $"Audit:AppId is '{LogSafe.Text(appId)}'. It names this application in every audit record: 1 to 64 letters, "
                + "digits, dots, hyphens or underscores.");
        }

        return new AuditSettings(directory, readWhenUnavailable, retention, appId, AuditOtlpSettings.Read(configuration, environment));
    }

    private static void ProbeWritable(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".probe-{Guid.NewGuid():N}");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose))
        {
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdShape();
}

/// <summary>The collector a copy of each record is sent to (contract-006 · G-11).</summary>
/// <param name="Endpoint">The OTLP/HTTP logs endpoint, whole: for a collector, <c>https://collector.example.com:4318/v1/logs</c>.</param>
/// <param name="Headers">Headers sent with every export, an API key among them. Never logged.</param>
public sealed record AuditOtlpSettings(Uri Endpoint, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Reads <c>Audit:Otlp</c>; null when no endpoint is set.</summary>
    /// <exception cref="ConfigurationException">An endpoint the server will not send audit records to, or headers with no endpoint.</exception>
    public static AuditOtlpSettings? Read(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var headers = configuration.GetSection("Audit:Otlp:Headers").GetChildren()
            .ToDictionary(h => h.Key, h => h.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var configured = configuration["Audit:Otlp:Endpoint"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return headers.Count == 0
                ? null
                : throw new ConfigurationException(
                    "Audit:Otlp:Headers is set and Audit:Otlp:Endpoint is not, so the headers would be sent nowhere. Set the "
                    + "endpoint, or remove the headers.");
        }

        // The endpoint is echoed through LogSafe.Url in every refusal: user information, a query and a fragment can each
        // carry a credential, and a refusal must not print one (contract-005 · T-10).
        var shown = LogSafe.Text(LogSafe.Url(configured));
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http"))
        {
            throw new ConfigurationException(
                $"Audit:Otlp:Endpoint is '{shown}', which is not an absolute http(s) URL. Give the collector's OTLP/HTTP logs "
                + "endpoint whole, for example https://collector.example.com:4318/v1/logs.");
        }

        if (endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
        {
            throw new ConfigurationException(
                $"Audit:Otlp:Endpoint is '{shown}', which carries user information, a query or a fragment. A credential in a URL "
                + "ends up in logs and proxies; send it as a header instead, under Audit:Otlp:Headers.");
        }

        if (endpoint.Scheme == "http" && !(environment.IsDevelopment() && endpoint.IsLoopback))
        {
            throw new ConfigurationException(
                $"Audit:Otlp:Endpoint is '{shown}', which is plain http. Audit records name callers and what they did; they leave "
                + "this machine over https only. Plain http is accepted in Development alone, to a collector on loopback.");
        }

        foreach (var name in headers.Keys)
        {
            if (name.Length == 0 || name.Any(c => c is <= ' ' or >= (char)0x7F or ':'))
            {
                throw new ConfigurationException(
                    $"Audit:Otlp:Headers names a header '{LogSafe.Text(name)}', which is not a header name.");
            }
        }

        return new AuditOtlpSettings(endpoint, headers);
    }
}
