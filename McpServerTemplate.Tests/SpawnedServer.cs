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
/// that no test creates, so its store is always empty. Nothing else the server reads comes from APPDATA.
///
/// Review round 5, addendum 2 pointed LOCALAPPDATA there too, on Linux, where data protection found its key ring
/// through that variable, and left it alone on Windows, where no variable moves it: the framework asks the
/// known-folder API, which ignores the environment. Since contract-005 · G-18 the server keeps no key ring on any
/// operating system — an in-memory, ephemeral provider, and no key ring loaded at startup (T17_a_spawned_server_loads_
/// no_key_ring) — so the Windows limitation no longer matters, and the Linux isolation is gone: nothing else the server
/// reads comes from LOCALAPPDATA.
/// </summary>
internal static class SpawnedServer
{
    /// <summary>
    /// Points <paramref name="info"/>'s user secrets at a location of its own that nobody keeps, and returns it. A test
    /// that starts the server removes it when it ends (<see cref="Cleanup"/>).
    /// </summary>
    public static string IsolateFromTheDeveloper(ProcessStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var root = Path.Combine(Path.GetTempPath(), $"mcp-tests-no-user-secrets-{Guid.NewGuid():N}");
        info.Environment["APPDATA"] = root;
        return root;
    }

    /// <summary>
    /// contract-005 review round 5 — nothing left behind: the locations a test's spawned servers were given, and the
    /// servers, ended when the test ends — its class's Dispose, which xUnit calls after every test, passed or failed.
    /// A server still running is stopped, and every server has exited before any location is removed: a server can
    /// write there (a File log sink under %APPDATA%, as the test that proves this gives one), and on Windows a file it
    /// holds open cannot be removed.
    /// </summary>
    internal sealed class Cleanup : IDisposable
    {
        private readonly List<string> _locations = [];
        private readonly List<(Process Process, int Id, DateTime? Started)> _servers = [];

        /// <summary>Isolates <paramref name="info"/> (<see cref="IsolateFromTheDeveloper(ProcessStartInfo)"/>), and removes its location when the test ends.</summary>
        public void Isolate(ProcessStartInfo info) => _locations.Add(IsolateFromTheDeveloper(info));

        /// <summary>
        /// A server the test started, returned as it is; ended, if it still runs, when the test ends. Its id and start
        /// time are kept with it, so it is found again even when the test has disposed its handle.
        /// </summary>
        public Process Started(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);
            DateTime? started;
            try
            {
                started = process.StartTime;
            }
            catch (InvalidOperationException)
            {
                started = null; // It has exited already.
            }

            _servers.Add((process, process.Id, started));
            return process;
        }

        public void Dispose()
        {
            foreach (var (server, id, started) in _servers)
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
                    // Review round 8 — the test disposed its handle, which says nothing of whether the server has exited:
                    // a helper that gave up waiting for an exit disposed it too, and the server ran on. It is found again
                    // by its id and start time, so no process that has taken the id since is touched.
                    EndIfStillRunning(id, started);
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

        private static void EndIfStillRunning(int id, DateTime? started)
        {
            if (started is null)
            {
                return;
            }

            try
            {
                using var again = Process.GetProcessById(id);
                if (again.StartTime == started)
                {
                    again.Kill(entireProcessTree: true);
                    again.WaitForExit();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // No process has that id any more, it exited while it was looked at, or the id now belongs to a process
                // this one may not inspect: not the server.
            }
        }
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
