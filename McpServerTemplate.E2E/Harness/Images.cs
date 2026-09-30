using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using DotNet.Testcontainers.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The images the environment builds from this checkout: the server, from the repository's own
/// Dockerfile, the test issuer, and the test host (contract-005 · G-10).
///
/// contract-005 · G-8 — each is built from the repository root, and tagged with a hash of the whole build context as
/// .dockerignore filters it. An image is reused only while that hash is unchanged: edit any file that reaches the context and
/// the next run builds again; edit a file the context excludes and it does not. Each image also carries two
/// labels: the checkout's revision (the commit it was built at, which is GITHUB_SHA in CI, with
/// <see cref="DirtySuffix"/> when the context held changes that commit does not) and the context hash.
/// Both are checked against the checkout before any test runs, so a stale or foreign image under the
/// right tag is rebuilt, never run, and every run's images name the commit they were tested at. A new
/// commit that changes nothing in the context rebuilds from Docker's layer cache.
///
/// contract-005 · G-17 round 3 (CON-R3-01) — built by BuildKit, the builder operators build with (`docker build`, `docker compose
/// build`), through the command line (<see cref="DockerCli"/>). They were built with the Testcontainers Dockerfile builder, which
/// goes through the engine's build API and so its classic builder, and the classic builder's image of the root Dockerfile was not
/// BuildKit's: its /app was root's where BuildKit's was the app user's, so the server's own user could rewrite its settings and
/// its assemblies in the image operators run, and in the image under test it could not. An image under the right tag and labels
/// that BuildKit did not build is rebuilt, never reused (<see cref="NotBuiltByBuildKitAsync"/>), and ShippedImageContentsTests
/// holds the image under test to having been built by BuildKit from this checkout.
/// </summary>
internal static class Images
{
    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-01) — the mark BuildKit's Dockerfile frontend leaves on every step of an image it builds:
    /// each history entry's comment. The classic builder leaves none, and writes a metadata step as "/bin/sh -c #(nop) …".
    /// </summary>
    public const string BuildKitFrontend = "buildkit.dockerfile.v0";

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

    /// <summary>
    /// A hash of every file the build context sends: the files .dockerignore leaves in, read by an ignore-file reader.
    /// contract-005 · G-17 round 3 (CON-R3-01) — the .dockerignore's own lines and nothing more, as BuildKit sends a context. The
    /// Testcontainers builder, which packed the context before, added two exclusions of its own in front (**/.idea, **/.vs) and
    /// kept .dockerignore and the Dockerfile, and the hash followed it; BuildKit adds neither, so neither does the hash.
    /// </summary>
    public static string ContextHash(string contextDirectory)
    {
        var root = ContextRoot(contextDirectory);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in ContextFiles(root))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\0"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file))));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Every file the build context sends, relative to its root with / between names, in ordinal order: the files
    /// <see cref="ContextHash"/> hashes. contract-005 · G-17 round 1 (CON-R1-01) — what an image's build is given, so the
    /// product can be published from exactly that, as the image's build publishes it.
    /// </summary>
    public static IReadOnlyList<string> ContextFiles(string contextDirectory)
    {
        var root = ContextRoot(contextDirectory);
        var ignore = ContextRules(root);
        return [.. FilesBeneath(root, root).Where(ignore.Accepts).Order(StringComparer.Ordinal)];
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

    /// <summary>
    /// What the build context leaves out: its .dockerignore, its comments and blank lines aside, read by the Testcontainers library's
    /// ignore-file reader, which follows Docker's own rules. contract-005 · G-17 round 3 (CON-R3-01) — with nothing added: BuildKit
    /// sends the context by the file alone.
    /// </summary>
    private static IgnoreFile ContextRules(string root)
    {
        var ignoreFile = Path.Combine(root, ".dockerignore");
        if (!File.Exists(ignoreFile))
        {
            throw new EnvironmentFaultException("image", $"There is no .dockerignore at {root}; the context would carry bin, obj and .git.");
        }

        var patterns = File.ReadLines(ignoreFile)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));
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
    ///
    /// contract-005 · G-17 round 3 (CON-R3-01) — with BuildKit, by `docker buildx build` from the repository root, as an operator
    /// builds it, the labels given on its command line; and reused only when BuildKit built the image under the tag
    /// (<see cref="NotBuiltByBuildKitAsync"/>), so an image the classic builder made under the same tag and labels, by the harness
    /// before this or by anything else, is built again rather than run. The image is kept after the run, so the next run with the
    /// same context reuses it, and it carries no run label for the same reason: it is not a run's leftover.
    /// </summary>
    public static async Task<string> BuildAsync(
        IDockerClient docker, string repository, string dockerfile, string contextDirectory, string contextHash, string revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var tag = $"{repository}:{contextHash}";
        if (await ReusableAsync(docker, tag, contextHash, revision, cancellationToken))
        {
            return tag;
        }

        // BuildKit on the command line is the machine's to have: its absence is the environment's fault, named, before any build, so
        // that a build that fails is the Dockerfile's.
        var root = ContextRoot(contextDirectory);
        var buildx = await DockerCli.RunAsync(["buildx", "version"], root, standardInput: null, cancellationToken);
        if (buildx.ExitCode != 0)
        {
            throw new EnvironmentFaultException(
                "image",
                $"docker buildx is not available ({buildx.Tail(3)}): the harness builds its images with BuildKit, as `docker build` does. "
                + "Install Docker's buildx plugin, and run again.");
        }

        var built = await DockerCli.RunAsync(
            [
                "buildx", "build", "--progress=plain", "--load",
                "--file", Path.Combine(root, dockerfile),
                "--tag", tag,
                "--label", $"{ContextLabel}={contextHash}",
                "--label", $"{RevisionLabel}={revision}",
                root,
            ],
            root,
            standardInput: null,
            cancellationToken);
        if (built.ExitCode != 0)
        {
            // A Dockerfile that does not build is the product's failure, and says so in its own words.
            throw new DockerfileBuildException(dockerfile, built.Tail());
        }

        // Built by the command line's engine; run by the harness's. They are one engine where the command line's context and
        // Testcontainers' endpoint agree, which is so on a developer's machine and on the runner.
        try
        {
            await docker.Images.InspectImageAsync(tag, cancellationToken);
        }
        catch (DockerImageNotFoundException ex)
        {
            throw new EnvironmentFaultException(
                "image",
                $"docker buildx built {tag}, and the engine the harness reaches has no image of that name: the docker command line's "
                + "context and the engine Testcontainers resolved are not the same engine. Point both at one (DOCKER_HOST, or `docker "
                + "context use`), and run again.",
                ex);
        }

        if (await NotBuiltByBuildKitAsync(docker, tag, cancellationToken) is { } why)
        {
            throw new EnvironmentFaultException("image", $"docker buildx built {tag}, and the image under that name {why}.");
        }

        return tag;
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-01) — whether the image under <paramref name="tag"/> is one to reuse: there is one, it
    /// carries this checkout's revision and context hash, and BuildKit built it.
    /// </summary>
    private static async Task<bool> ReusableAsync(IDockerClient docker, string tag, string contextHash, string revision, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await docker.Images.InspectImageAsync(tag, cancellationToken);
            return Mismatch(existing.Config?.Labels, contextHash, revision) is null
                && await NotBuiltByBuildKitAsync(docker, tag, cancellationToken) is null;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-01) — why <paramref name="image"/> is not an image BuildKit built from a Dockerfile, or null
    /// when it is: its newest history entry, the Dockerfile's last step, carries BuildKit's mark (<see cref="BuildKitFrontend"/>) and
    /// is not written the classic builder's way. ShippedImageContentsTests holds every step the Dockerfile added to it.
    /// </summary>
    internal static async Task<string?> NotBuiltByBuildKitAsync(IDockerClient docker, string image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var history = await docker.Images.GetImageHistoryAsync(image, cancellationToken);
        var last = history.FirstOrDefault();
        return last is not null && last.Comment == BuildKitFrontend && !IsClassicStep(last.CreatedBy)
            ? null
            : $"was not built by BuildKit: its last step, '{last?.CreatedBy}', carries the comment '{last?.Comment}', not '{BuildKitFrontend}'";
    }

    /// <summary>contract-005 · G-17 round 3 (CON-R3-01) — whether a history entry's command is written as the classic builder writes a step.</summary>
    internal static bool IsClassicStep(string? createdBy) => createdBy?.Contains("#(nop)", StringComparison.Ordinal) == true;

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-03) — the product's publish output, as the Dockerfile's build stage makes it and nothing
    /// after: `dotnet publish` of McpServerTemplate in Release with no app host, after a locked restore, run by BuildKit in
    /// <paramref name="sdkImage"/> (the image that stage is built on) at /src, over the whole build context as .dockerignore sends
    /// it — so a file the context leaves out is left out here too — and exported, file for file, from a stage that holds nothing
    /// else. Returns each file and directory, relative to the output, with its kind and, for a file, its content's SHA-256.
    ///
    /// It was published by the machine's own SDK, which compiles the product's assembly at another path, on another filesystem, in
    /// another order, so only the names of what it published could be compared with the image's /app: a build step that rewrote a
    /// published file after the publish shipped with every check green. In the SDK image at /src the compiler is given what the
    /// image's build stage gave it, at the same paths, and the SDK builds deterministically by default, so the same sources give
    /// the same bytes; ShippedImageContentsTests names any file whose bytes differ. Its restore steps are the Dockerfile's as they
    /// stand, which lets BuildKit's cache serve them from the image's own build; the publish is the harness's own, and never the
    /// Dockerfile's text: a Dockerfile that published something else would be compared with this, not with itself.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, (TarEntryType Kind, string? Content)>> PublishOutputAsync(
        string repositoryRoot, string sdkImage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkImage);

        var root = ContextRoot(repositoryRoot);
        var dockerfile = string.Join(
            "\n",
            $"FROM {sdkImage} AS publish",
            "WORKDIR /src",
            "COPY global.json Directory.Build.props .editorconfig ./",
            "COPY McpServerTemplate/McpServerTemplate.csproj McpServerTemplate/packages.lock.json McpServerTemplate/",
            "RUN dotnet restore McpServerTemplate/McpServerTemplate.csproj --locked-mode",
            "COPY . ./",
            "RUN dotnet publish McpServerTemplate/McpServerTemplate.csproj -c Release --no-restore -o /app -p:UseAppHost=false",
            "FROM scratch",
            "COPY --from=publish /app /",
            string.Empty);

        var published = await DockerCli.RunAsync(
            ["buildx", "build", "--progress=plain", "--file", "-", "--output", "type=tar,dest=-", root],
            root,
            dockerfile,
            cancellationToken);
        if (published.ExitCode != 0)
        {
            throw new InvalidOperationException($"BuildKit did not publish the product from the build context: {published.Tail()}");
        }

        var files = new Dictionary<string, (TarEntryType Kind, string? Content)>(StringComparer.Ordinal);
        using var archive = new MemoryStream(published.Output, writable: false);
        await using var reader = new TarReader(archive);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            name = (name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name).Trim('/');
            if (name.Length == 0 || name == ".")
            {
                continue;
            }

            var kind = entry.EntryType is TarEntryType.V7RegularFile or TarEntryType.ContiguousFile ? TarEntryType.RegularFile : entry.EntryType;
            files[name] = (kind, kind == TarEntryType.RegularFile
                ? Convert.ToHexStringLower(await SHA256.HashDataAsync(entry.DataStream ?? Stream.Null, cancellationToken))
                : null);
        }

        return files;
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

/// <summary>
/// contract-005 · G-17 round 3 (CON-R3-01) — a Dockerfile BuildKit could not build: the product's failure when it is the root
/// Dockerfile's, in the builder's own words, and never read as the environment's (E2EEnvironment lets the server image's pass
/// through as it is). Testcontainers' ImageBuildFailedException was that, while the harness built through it.
/// </summary>
public sealed class DockerfileBuildException : Exception
{
    public DockerfileBuildException()
        : base("A Dockerfile did not build.")
    {
    }

    public DockerfileBuildException(string message)
        : base(message)
    {
    }

    public DockerfileBuildException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The Dockerfile <paramref name="dockerfile"/> did not build, and BuildKit said <paramref name="said"/>.</summary>
    public DockerfileBuildException(string dockerfile, string said)
        : base($"{dockerfile} did not build. BuildKit said:\n{said}")
    {
    }
}
