using System.Collections;
using System.Diagnostics;

namespace McpServerTemplate.Tests;

/// <summary>
/// What every test that spawns the real server gives it, so no spawned server reads anyone's user secrets, and on
/// Linux none uses anyone's data-protection keys.
///
/// contract-005 review round 4 — a server in Development reads the user secrets its assembly names
/// (UserSecretsId), from a store the framework finds through the APPDATA variable first, on every operating
/// system, and through HOME only when APPDATA is unset (Microsoft.Extensions.Configuration.UserSecrets
/// 10.0.1, PathHelper). Spawned with this process's environment, it read the developer's own store, so a
/// machine with secrets set could see different results. Each spawned server gets an APPDATA of its own
/// that no test creates, so its store is always empty. Nothing else the server reads comes from APPDATA.
///
/// Review round 5, addendum 2 — data protection keeps its keys where the framework finds them
/// (Microsoft.AspNetCore.DataProtection 10.0.1, XmlKeyManager and DefaultKeyStorageDirectories). On Linux, where
/// the CI job runs these tests, that is through the LOCALAPPDATA variable first, then HOME, so a spawned server
/// there used the keys in its account's home. It gets the same location as its user secrets, and writes a key of
/// its own there, under the temp directory; nothing else it reads comes from LOCALAPPDATA (the MCP SDK names it
/// only for processes its stdio client launches, which this server never does). On Windows LOCALAPPDATA is left
/// as it is, because no variable moves the keys there: the framework asks the known-folder API first, and that
/// ignores the environment. Nothing in the server uses data protection — authentication registers it, and the
/// framework loads the key ring at startup — and a full test run left the developer's key folder unchanged,
/// checked by its files' names and modified times.
/// </summary>
internal static class SpawnedServer
{
    /// <summary>
    /// Points <paramref name="info"/>'s user secrets, and on Linux its data-protection keys, at a location of its own
    /// that nobody keeps, and returns it. A test that starts the server removes it when it ends (<see cref="Cleanup"/>).
    /// </summary>
    public static string IsolateFromTheDeveloper(ProcessStartInfo info) => IsolateFromTheDeveloper(info, OperatingSystem.IsWindows());

    /// <summary>The same, as it is done on Windows or on Linux, so a test on either can check both.</summary>
    public static string IsolateFromTheDeveloper(ProcessStartInfo info, bool windows)
    {
        ArgumentNullException.ThrowIfNull(info);
        var root = Path.Combine(Path.GetTempPath(), $"mcp-tests-no-user-secrets-{Guid.NewGuid():N}");
        info.Environment["APPDATA"] = root;
        if (!windows)
        {
            info.Environment["LOCALAPPDATA"] = root;
        }

        return root;
    }

    /// <summary>
    /// contract-005 review round 5 — nothing left behind: the locations a test's spawned servers were given, and the
    /// servers, ended when the test ends — its class's Dispose, which xUnit calls after every test, passed or failed.
    /// A server still running is stopped, and every server has exited before any location is removed: on Linux a
    /// server writes its data-protection key there, and on Windows a file it holds open cannot be removed.
    /// </summary>
    internal sealed class Cleanup : IDisposable
    {
        private readonly List<string> _locations = [];
        private readonly List<Process> _servers = [];

        /// <summary>Isolates <paramref name="info"/> (<see cref="IsolateFromTheDeveloper(ProcessStartInfo)"/>), and removes its location when the test ends.</summary>
        public void Isolate(ProcessStartInfo info) => _locations.Add(IsolateFromTheDeveloper(info));

        /// <summary>A server the test started, returned as it is; ended, if it still runs, when the test ends.</summary>
        public Process Started(Process process)
        {
            _servers.Add(process);
            return process;
        }

        public void Dispose()
        {
            foreach (var server in _servers)
            {
                try
                {
                    if (!server.HasExited)
                    {
                        server.Kill(entireProcessTree: true);
                    }

                    server.WaitForExit();
                }
                catch (InvalidOperationException)
                {
                    // The test disposed it, which every test here does only once the server has exited.
                }
            }

            foreach (var location in _locations)
            {
                if (Directory.Exists(location))
                {
                    Directory.Delete(location, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// The directory ASP.NET Core's data protection keeps its keys in, on Linux, for a process with
    /// <paramref name="environment"/>: the framework's own order (Microsoft.AspNetCore.DataProtection 10.0.1,
    /// XmlKeyManager and DefaultKeyStorageDirectories) — Azure App Service's, where WEBSITE_INSTANCE_ID and HOME are
    /// set, then LOCALAPPDATA, then HOME. Null where it would come from the account's entry, HOME being unset. A path
    /// only; nothing is read or listed.
    /// </summary>
    public static string? KeyDirectoryOnLinux(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var home = environment.TryGetValue("HOME", out var h) ? h : null;
        var website = environment.TryGetValue("WEBSITE_INSTANCE_ID", out var w) ? w : null;
        if (!string.IsNullOrEmpty(website) && !string.IsNullOrEmpty(home))
        {
            return Path.Combine(home, "ASP.NET", "DataProtection-Keys");
        }

        if (environment.TryGetValue("LOCALAPPDATA", out var localAppData) && localAppData is not null)
        {
            return Path.Combine(localAppData, "ASP.NET", "DataProtection-Keys");
        }

        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".aspnet", "DataProtection-Keys");
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
