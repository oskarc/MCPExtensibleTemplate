using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The images the environment builds from this checkout: the server, from the repository's own
/// Dockerfile, the test issuer, and the test host (contract-005 · G-10).
///
/// contract-005 · G-8 — both are built with the Testcontainers Dockerfile builder from the repository
/// root, and tagged with a hash of the whole build context as .dockerignore filters it. An image is
/// reused only while that hash is unchanged: edit any file that reaches the context and the next run
/// builds again; edit a file the context excludes and it does not. Each image also carries two
/// labels: the checkout's revision (the commit it was built at, which is GITHUB_SHA in CI, with
/// <see cref="DirtySuffix"/> when the context held changes that commit does not) and the context hash.
/// Both are checked against the checkout before any test runs, so a stale or foreign image under the
/// right tag is rebuilt, never run, and every run's images name the commit they were tested at. A new
/// commit that changes nothing in the context rebuilds from Docker's layer cache.
/// </summary>
internal static class Images
{
    /// <summary>What a revision label carries after the commit when the build context differs from it.</summary>
    public const string DirtySuffix = "-dirty";

    /// <summary>The server image's repository name; the tag is the context hash.</summary>
    public const string ServerRepository = "mcp-e2e-server";

    /// <summary>The test issuer image's repository name; test-only, never pushed.</summary>
    public const string IssuerRepository = "mcp-e2e-testissuer";

    /// <summary>contract-005 · G-10 — the test host image's repository name; test-only, never pushed.</summary>
    public const string TestHostRepository = "mcp-e2e-testhost";

    /// <summary>The label that carries the commit of the checkout an image was built from.</summary>
    public const string RevisionLabel = "org.opencontainers.image.revision";

    /// <summary>The label that carries the hash of the build context an image was built from.</summary>
    public const string ContextLabel = "org.mcp-server-template.e2e.context";

    // What the Testcontainers builder puts before and after the .dockerignore lines when it packs a
    // context (its DockerIgnoreFile), reproduced so the hash covers exactly what is sent.
    private static readonly string[] AlwaysIgnored = ["**/.idea", "**/.vs"];
    private static readonly string[] AlwaysKept = ["!.dockerignore", "!Dockerfile"];

    /// <summary>
    /// A hash of every file the build context sends, by the same rules the Testcontainers builder
    /// applies when it packs the context: its ignore-file reader, with the two entries it always adds
    /// in front (**/.idea, **/.vs) and the two it always keeps (.dockerignore, Dockerfile). Hashing by
    /// Docker's own rules instead would drift from what is actually sent.
    /// </summary>
    public static string ContextHash(string contextDirectory)
    {
        var root = ContextRoot(contextDirectory);
        var ignore = ContextRules(root);

        var files = FilesBeneath(root, root)
            .Where(ignore.Accepts)
            .Order(StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\0"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file))));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The commit the repository at <paramref name="repositoryRoot"/> has checked out, as git names
    /// it. In CI this is GITHUB_SHA: actions/checkout checks out exactly that commit.
    ///
    /// contract-005 · G-8 — with <see cref="DirtySuffix"/> when the build context differs from that
    /// commit: a file the context carries (by the rules <see cref="ContextHash"/> reads) is modified,
    /// added, deleted, renamed or untracked, or is one git ignores and the context still carries. An
    /// image built from uncommitted changes used to be labelled with the commit alone, naming content it
    /// did not hold. A change the context does not carry (docs/, .claude/) leaves the label as the
    /// commit. Reuse still keys on the context hash: the same dirty tree labels and hashes the same.
    /// </summary>
    public static async Task<string> CheckoutRevisionAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var root = ContextRoot(repositoryRoot);

        var (found, head, error) = await GitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        var revision = found ? head.Trim() : string.Empty;
        if (revision.Length is not (40 or 64) || !revision.All(char.IsAsciiHexDigitLower))
        {
            throw new EnvironmentFaultException("image", $"the checkout's revision could not be read from {repositoryRoot}: git said '{error.Trim()}'.");
        }

        // NUL-separated and unquoted (-z); untracked files one by one; and what git ignores, since a file
        // git ignores can be one the context carries (a *.user file, say).
        var (compared, status, statusError) = await GitAsync(
            root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching"], cancellationToken);
        if (!compared)
        {
            throw new EnvironmentFaultException("image", $"the checkout at {repositoryRoot} could not be compared with its commit: git said '{statusError.Trim()}'.");
        }

        return ContextCarriesAny(root, ChangedPaths(status)) ? revision + DirtySuffix : revision;
    }

    /// <summary>
    /// The paths git's porcelain status (v1, -z) names: each entry's path, and a rename's or a copy's
    /// source, which follows it in a field of its own. A directory git reports whole ends with /.
    /// </summary>
    private static IEnumerable<string> ChangedPaths(string status)
    {
        using var fields = status.Split('\0', StringSplitOptions.RemoveEmptyEntries).AsEnumerable().GetEnumerator();
        while (fields.MoveNext())
        {
            var entry = fields.Current;
            if (entry.Length < 4)
            {
                continue;
            }

            yield return entry[3..];
            if ((entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') && fields.MoveNext())
            {
                yield return fields.Current;
            }
        }
    }

    /// <summary>Whether the build context carries any of <paramref name="paths"/>, or any file beneath a directory among them.</summary>
    private static bool ContextCarriesAny(string root, IEnumerable<string> paths)
    {
        var ignore = ContextRules(root);
        return paths.Any(path => path.EndsWith('/')
            ? Directory.Exists(Path.Combine(root, path)) && FilesBeneath(root, Path.Combine(root, path)).Any(ignore.Accepts)
            : ignore.Accepts(path));
    }

    /// <summary>The context's root: full, and without a trailing separator, as every relative path is cut from it.</summary>
    private static string ContextRoot(string contextDirectory) =>
        Path.GetFullPath(contextDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>What the build context leaves out: its .dockerignore, read by the Testcontainers builder's own rules.</summary>
    private static IgnoreFile ContextRules(string root)
    {
        var ignoreFile = Path.Combine(root, ".dockerignore");
        if (!File.Exists(ignoreFile))
        {
            throw new EnvironmentFaultException("image", $"There is no .dockerignore at {root}; the context would carry bin, obj and .git.");
        }

        var patterns = AlwaysIgnored
            .Concat(File.ReadLines(ignoreFile))
            .Concat(AlwaysKept);
        return new IgnoreFile(patterns, NullLogger.Instance);
    }

    /// <summary>Every file beneath <paramref name="directory"/>, relative to <paramref name="root"/>, with / between names.</summary>
    private static IEnumerable<string> FilesBeneath(string root, string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFullPath(f).Replace('\\', '/'))
            .Select(f => f[(root.Length + 1)..]);

    private static async Task<(bool Succeeded, string Output, string Errors)> GitAsync(
        string repositoryRoot, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git", ["-C", repositoryRoot, .. arguments])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };

        try
        {
            using var git = Process.Start(start)
                ?? throw new InvalidOperationException("git did not start.");
            var output = git.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = git.StandardError.ReadToEndAsync(cancellationToken);
            await git.WaitForExitAsync(cancellationToken);
            return (git.ExitCode == 0, await output, await errors);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EnvironmentFaultException("image", $"the checkout's revision could not be read: git is not available ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// Builds <paramref name="repository"/>:<c>hash</c> from <paramref name="dockerfile"/>, unless an
    /// image with that tag already carries both this context hash and this revision.
    /// </summary>
    public static async Task<string> BuildAsync(
        string repository, string dockerfile, string contextDirectory, string contextHash, string revision, CancellationToken cancellationToken)
    {
        var tag = $"{repository}:{contextHash}";

        var image = new ImageFromDockerfileBuilder()
            .WithName(tag)
            .WithDockerfileDirectory(contextDirectory)
            .WithDockerfile(dockerfile)
            .WithContextDirectory(contextDirectory)
            .WithLabel(ContextLabel, contextHash)
            .WithLabel(RevisionLabel, revision)
            // Rebuild when the tag is missing or names an image built from something else.
            .WithImageBuildPolicy(existing => Mismatch(existing?.Config?.Labels, contextHash, revision) is not null)
            // Kept after the run, so the next run with the same context reuses it. It carries no
            // run label for the same reason: it is not a run's leftover.
            .WithCleanUp(false)
            .Build();

        try
        {
            await image.CreateAsync(cancellationToken);
        }
        catch (ImageBuildFailedException ex)
        {
            // A Dockerfile that does not build is the product's failure, and says so in its own words.
            throw new InvalidOperationException($"{dockerfile} did not build: {ex.Message}", ex);
        }

        return tag;
    }

    /// <summary>
    /// Checks the image about to run was built from this checkout: its revision label must be the
    /// commit checked out now, and its context label the hash just computed from the working tree.
    /// </summary>
    public static async Task VerifyRevisionAsync(IDockerClient docker, string tag, string contextHash, string revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var inspected = await docker.Images.InspectImageAsync(tag, cancellationToken);
        if (Mismatch(inspected.Config?.Labels, contextHash, revision) is { } mismatch)
        {
            throw new EnvironmentFaultException(
                "image",
                $"{tag} {mismatch}. The image under that tag was not built from this checkout; remove it and run again.");
        }
    }

    /// <summary>What about an image's labels does not match this checkout, or null when both do.</summary>
    private static string? Mismatch(IDictionary<string, string>? labels, string contextHash, string revision)
    {
        var context = labels is not null && labels.TryGetValue(ContextLabel, out var c) ? c : "(none)";
        var built = labels is not null && labels.TryGetValue(RevisionLabel, out var r) ? r : "(none)";

        if (built != revision)
        {
            return $"carries revision {built}, but the checkout is at {revision}";
        }

        return context != contextHash
            ? $"carries context {context}, but the checkout's build context hashes to {contextHash}"
            : null;
    }
}
