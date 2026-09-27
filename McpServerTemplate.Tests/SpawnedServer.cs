using System.Collections;
using System.Diagnostics;

namespace McpServerTemplate.Tests;

/// <summary>
/// What every test that spawns the real server gives it, so no spawned server reads anyone's user secrets.
///
/// contract-005 review round 4 — a server in Development reads the user secrets its assembly names
/// (UserSecretsId), from a store the framework finds through the APPDATA variable first, on every operating
/// system, and through HOME only when APPDATA is unset (Microsoft.Extensions.Configuration.UserSecrets
/// 10.0.1, PathHelper). Spawned with this process's environment, it read the developer's own store, so a
/// machine with secrets set could see different results. Each spawned server gets an APPDATA of its own
/// that no test creates, so its store is always empty. Nothing else the server reads comes from APPDATA:
/// data protection's keys come from LOCALAPPDATA and HOME, which stay as they are, and the product reads
/// neither.
/// </summary>
internal static class SpawnedServer
{
    /// <summary>Points <paramref name="info"/>'s user secrets at a location of its own that nobody keeps.</summary>
    public static void ReadNoUserSecrets(ProcessStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.Environment["APPDATA"] = Path.Combine(Path.GetTempPath(), $"mcp-tests-no-user-secrets-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// The user secrets file a process with <paramref name="environment"/> resolves for
    /// <paramref name="userSecretsId"/>: the framework's own order, APPDATA, then HOME, then the user's
    /// folders, then DOTNET_USER_SECRETS_FALLBACK_DIR. A path only; nothing is read or listed.
    /// </summary>
    public static string UserSecretsPath(IDictionary<string, string?> environment, string userSecretsId)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var appData = environment.TryGetValue("APPDATA", out var a) ? a : null;
        var root = appData
            ?? (environment.TryGetValue("HOME", out var home) ? home : null)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            ?? (environment.TryGetValue("DOTNET_USER_SECRETS_FALLBACK_DIR", out var fallback) ? fallback : null)
            ?? string.Empty;

        return string.IsNullOrEmpty(appData)
            ? Path.Combine(root, ".microsoft", "usersecrets", userSecretsId, "secrets.json")
            : Path.Combine(root, "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
    }

    /// <summary>This process's own environment, as a spawned process starts from it.</summary>
    public static IDictionary<string, string?> CurrentEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
