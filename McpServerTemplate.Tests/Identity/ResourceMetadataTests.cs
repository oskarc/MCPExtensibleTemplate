using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-1 (G-1, G-2) — a caller with no credential is told where to get one.
///
/// This was the contract's red test. Before G-1 and G-2 the 401 carried no WWW-Authenticate at
/// all, so a client with no token had nowhere to go. RFC 9728 says the challenge must point at
/// the metadata document, and that document is how a client discovers where to authenticate.
/// </summary>
public class ResourceMetadataTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpServerTemplate.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root");
        return dir!.FullName;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// Starts the server over HTTP and drains its stderr, which a redirected pipe requires —
    /// an undrained one fills and the server blocks mid-request (drift-003).
    private static async Task<(Process Process, HttpClient Client, StringBuilder Stderr)> StartAsync()
    {
        var exe = Path.Combine(
            RepositoryRoot(), "McpServerTemplate", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0", OperatingSystem.IsWindows() ? "McpServerTemplate.exe" : "McpServerTemplate");

        Assert.True(File.Exists(exe), $"server executable not found at {exe}; build the solution first");

        var port = FreePort();
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        info.Environment.Remove("DOTNET_ENVIRONMENT");
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["Transport"] = "http";
        info.Environment["HttpTransport__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["HttpTransport__BindAddress"] = "127.0.0.1";

        // Identity as a deployment would configure it. The authority is never reached in this
        // test: no token is presented, so nothing triggers discovery.
        info.Environment["Authentication__Resource"] = "https://mcp.example.com/mcp";
        info.Environment["Authentication__IdentityProviders__corp__Authority"] = "https://login.example.com";
        info.Environment["Authentication__IdentityProviders__corp__Issuer"] = "https://login.example.com/";
        info.Environment["Authentication__IdentityProviders__corp__Algorithms__0"] = "RS256";
        info.Environment["Authentication__IdentityProviders__corp__ScopeCatalog__0"] = "weather:read";
        info.Environment["Authentication__IdentityProviders__corp__ScopeCatalog__1"] = "observations:read";
        info.Environment["Authentication__IdentityProviders__corp__ScopeCatalog__2"] = "demo:read";
        info.Environment["Providers__Smhi__IdentityProvider"] = "corp";
        info.Environment["Providers__SmhiObs__IdentityProvider"] = "corp";
        info.Environment["Providers__JsonPlaceholder__IdentityProvider"] = "corp";

        // contract-002 · G-12 — Production over loopback plaintext is refused unless a trusted
        // proxy is declared. Here the harness is that proxy.
        info.Environment["HttpTransport__KnownNetworks__0"] = "127.0.0.0/8";

        var process = Process.Start(info)!;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            lock (stderr)
                stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();

        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                string text;
                lock (stderr)
                    text = stderr.ToString();

                Assert.Fail($"the server exited during startup ({process.ExitCode}): {text}");
            }

            try
            {
                using var probe = await client.GetAsync("/healthz");
                return (process, client, stderr);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(300);
            }
        }

        process.Kill(entireProcessTree: true);
        throw new TimeoutException("the server did not start within 60 seconds");
    }

    [Fact]
    public async Task T1_a_caller_without_a_token_is_told_where_to_get_one()
    {
        var (process, client, _) = await StartAsync();

        try
        {
            // What a client actually does. A GET here is 405 before authorization is ever
            // consulted, because the endpoint is POST-only and routing rejects the method
            // first — so a GET would have tested method matching, not the challenge.
            using var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"acceptance-test","version":"1"}}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            // The whole point of the challenge: a client that has no token must be able to find
            // the authorization server without being told out of band.
            var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));

            Assert.False(
                string.IsNullOrWhiteSpace(challenge),
                "401 carried no WWW-Authenticate header at all");
            Assert.Contains("resource_metadata", challenge, StringComparison.Ordinal);
        }
        finally
        {
            client.Dispose();
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}
