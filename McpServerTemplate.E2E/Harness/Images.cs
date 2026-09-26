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
/// Dockerfile, and the test issuer.
///
/// contract-005 · G-8 — both are built with the Testcontainers Dockerfile builder from the repository
/// root, and tagged with a hash of the whole build context as .dockerignore filters it. An image is
/// reused only while that hash is unchanged: edit any file that reaches the context and the next run
/// builds again; edit a file the context excludes and it does not. Each image also carries two
/// labels: the checkout's revision (the commit it was built at, which is GITHUB_SHA in CI) and the
/// context hash. Both are checked against the checkout before any test runs, so a stale or foreign
/// image under the right tag is rebuilt, never run, and every run's images name the commit they were
/// tested at. A new commit that changes nothing in the context rebuilds from Docker's layer cache.
/// </summary>
internal static class Images
{
    /// <summary>The server image's repository name; the tag is the context hash.</summary>
    public const string ServerRepository = "mcp-e2e-server";

    /// <summary>The test issuer image's repository name; test-only, never pushed.</summary>
    public const string IssuerRepository = "mcp-e2e-testissuer";

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
        var root = Path.GetFullPath(contextDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var ignoreFile = Path.Combine(root, ".dockerignore");
        if (!File.Exists(ignoreFile))
        {
            throw new EnvironmentFaultException("image", $"There is no .dockerignore at {root}; the context would carry bin, obj and .git.");
        }

        var patterns = AlwaysIgnored
            .Concat(File.ReadLines(ignoreFile))
            .Concat(AlwaysKept);
        var ignore = new IgnoreFile(patterns, NullLogger.Instance);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFullPath(f).Replace('\\', '/'))
            .Select(f => f[(root.Length + 1)..])
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
    /// </summary>
    public static async Task<string> CheckoutRevisionAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git", ["-C", repositoryRoot, "rev-parse", "--verify", "HEAD"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        string revision;
        string error;
        try
        {
            using var git = Process.Start(start)
                ?? throw new InvalidOperationException("git did not start.");
            var output = git.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = git.StandardError.ReadToEndAsync(cancellationToken);
            await git.WaitForExitAsync(cancellationToken);
            revision = (await output).Trim();
            error = (await errors).Trim();
            if (git.ExitCode != 0)
            {
                revision = string.Empty;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EnvironmentFaultException("image", $"the checkout's revision could not be read: git is not available ({ex.Message}).", ex);
        }

        if (revision.Length is not (40 or 64) || !revision.All(char.IsAsciiHexDigitLower))
        {
            throw new EnvironmentFaultException("image", $"the checkout's revision could not be read from {repositoryRoot}: git said '{error}'.");
        }

        return revision;
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
