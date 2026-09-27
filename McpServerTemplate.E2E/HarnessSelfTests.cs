using McpServerTemplate.E2E.Harness;

namespace McpServerTemplate.E2E;

/// <summary>
/// The harness's own checks that need no environment: nothing here starts a container.
/// </summary>
public sealed class HarnessSelfTests
{
    /// <summary>
    /// contract-005 · G-8 — a key outside the sections the server's own startup check governs is one
    /// the server would ignore silently, so the delta refuses it before any container starts, naming
    /// the key and the nearest key the server is declared to read. Serilog:WriteTo:1:Args:pth is one
    /// that left a server running as if it were not there; the others are misspellings of the rest of
    /// what the server reads outside the governed sections.
    /// </summary>
    [Theory]
    [InlineData("Serilog:WriteTo:1:Args:pth", "Serilog:WriteTo:{n}:Args:path")]
    [InlineData("Serilog__MinimumLevel__Defualt", "Serilog:MinimumLevel:Default")]
    [InlineData("Transprot", "Transport")]
    [InlineData("SSL_CERT_FIEL", "SSL_CERT_FILE")]
    public void A_misspelt_key_outside_the_governed_sections_is_refused_naming_the_nearest_declared_key(string key, string nearest)
    {
        // Positive controls: a declared key and a governed key both pass, so the refusal below is
        // this key's and not a delta that refuses everything.
        SettingsDelta.None.Set("Serilog:WriteTo:1:Args:path", "logs/e2e-.log");
        SettingsDelta.None.Set("Authentication:Resourse", "left for the server's own check");

        var set = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Set(key, "x"));
        Assert.Contains($"'{key}'", set.Message, StringComparison.Ordinal);
        Assert.Contains($"Did you mean '{nearest}'?", set.Message, StringComparison.Ordinal);

        var removed = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Remove(key));
        Assert.Contains($"'{key}'", removed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — HttpTransport is governed by the server's own check now, so a
    /// misspelt transport key is passed through untouched: refusing it is the server's job, and a test
    /// of that refusal needs the key to arrive. These two were refused here before.
    /// </summary>
    [Theory]
    [InlineData("HttpTransport:AlowedHosts:0")]
    [InlineData("HttpTransport__KnownProxys__0")]
    public void A_misspelt_transport_key_reaches_the_server(string key)
    {
        var delta = SettingsDelta.None.Set(key, "x");
        Assert.Equal("x", delta.Changes[key.Replace("__", ":", StringComparison.Ordinal)]);
    }

    /// <summary>contract-005 · G-8 — a key under no section at all is refused the same way.</summary>
    [Fact]
    public void An_invented_top_level_key_is_refused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Set("Bogus:Key", "1"));
        Assert.Contains("'Bogus:Key'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-8 — an image's revision label names the commit it was built from, and an image
    /// built from a working tree whose build context differs from that commit is not the commit's: its
    /// label says so with -dirty. A change the context does not carry (docs/, which .dockerignore
    /// excludes) leaves the label as the commit. Run on a repository made for the test, so the
    /// checkout the suite builds from is never touched.
    /// </summary>
    [Fact]
    public async Task The_revision_label_marks_a_build_context_that_differs_from_its_commit()
    {
        var repository = Directory.CreateTempSubdirectory("e2e-revision-").FullName;
        try
        {
            await GitAsync(repository, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, ".dockerignore"), "docs/\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// one\n");
            Directory.CreateDirectory(Path.Combine(repository, "docs"));
            await File.WriteAllTextAsync(Path.Combine(repository, "docs", "guide.md"), "one\n");
            await GitAsync(repository, "add", "--all");
            await GitAsync(repository, "-c", "user.name=e2e", "-c", "user.email=e2e@example.invalid", "commit", "--quiet", "--message", "base");
            var commit = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();

            // Positive control: the tree as committed is labelled with the commit alone.
            Assert.Equal(commit, await Images.CheckoutRevisionAsync(repository, CancellationToken.None));

            // Changes the build context does not carry leave it so.
            await File.WriteAllTextAsync(Path.Combine(repository, "docs", "guide.md"), "two\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "docs", "new.md"), "new\n");
            Assert.Equal(commit, await Images.CheckoutRevisionAsync(repository, CancellationToken.None));

            // A change it carries — an edit, a new file, a deletion — and the image is not the commit's.
            var changes = new (string What, Func<Task> Make, Func<Task> Undo)[]
            {
                ("an edited file", () => File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// two\n"),
                    () => File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// one\n")),
                ("an untracked file", () => File.WriteAllTextAsync(Path.Combine(repository, "Added.cs"), "// new\n"),
                    () => Task.Run(() => File.Delete(Path.Combine(repository, "Added.cs")))),
                ("a deleted file", () => Task.Run(() => File.Delete(Path.Combine(repository, "Program.cs"))),
                    () => File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "// one\n")),
            };

            foreach (var (what, make, undo) in changes)
            {
                await make();
                var revision = await Images.CheckoutRevisionAsync(repository, CancellationToken.None);
                Assert.True(
                    revision == $"{commit}-dirty",
                    $"a build context with {what} not in its commit was labelled '{revision}', not '{commit}-dirty'.");
                await undo();
                Assert.Equal(commit, await Images.CheckoutRevisionAsync(repository, CancellationToken.None));
            }
        }
        finally
        {
            // Git writes its objects read-only, and Windows will not delete them so.
            foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(repository, recursive: true);
        }
    }

    private static async Task<string> GitAsync(string repository, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git", ["-C", repository, .. arguments])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var git = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var output = git.StandardOutput.ReadToEndAsync();
        var errors = git.StandardError.ReadToEndAsync();
        await git.WaitForExitAsync();
        return git.ExitCode == 0
            ? await output
            : throw new InvalidOperationException($"git {string.Join(" ", arguments)} failed in {repository}: {await errors}");
    }
}
