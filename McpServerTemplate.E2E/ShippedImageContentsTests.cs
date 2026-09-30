using System.Formats.Tar;
using Docker.DotNet.Models;
using McpServerTemplate.E2E.Harness;
using Xunit.Abstractions;

namespace McpServerTemplate.E2E;

/// <summary>
/// contract-005 · T-13 (G-1 · UC-8, UC-12), G-17 round 1 (CON-R1-01) — the image that ships holds the product and nothing
/// else. Its /app is, file for file, what `dotnet publish` of McpServerTemplate gives — run as the Dockerfile's build stage
/// runs it, over the build context the image is built from — and the log directory the Dockerfile makes, empty. No file of
/// it is a test, harness, issuer, host or key file.
///
/// The fast suite's check (RepositoryInvariantsTests.T13_the_root_dockerfile_has_no_test_stage_and_copies_no_test_material),
/// kept, reads where the Dockerfile copies from. It passed a COPY --from another image — the test host's, say — and a JSON
/// file dropped into the product's folder, which the Web SDK publishes into /app with every other. This reads what reached
/// /app, from a container of the image that is created and never started.
///
/// contract-005 · G-17 round 2 (CON-R2-02) — and everything outside /app, which is the pinned runtime base the Dockerfile's final
/// stage names, file for file: nothing added, removed or changed — its kind, mode, owner, link, device, extended attributes and
/// content. Only /app was read, so a COPY --from of the upstream fake's image into /opt, or a build stage's RUN copied out
/// anywhere but /app, shipped with every check green (seen on a probe image: 239 of WireMock's files under /opt, its /app
/// identical to the image's).
///
/// contract-005 · G-17 round 3 — and three more things. (CON-R3-01) The image under test is the one BuildKit builds from this
/// checkout, the builder operators build with: the harness built it with the engine's classic builder, which made another image
/// of the same Dockerfile — its /app root's where BuildKit's was the server's own user's. (HAR-R3-03) /app is compared with the
/// product's publish output by content, not by names alone, and held to its owners and modes: root's, writable by root alone,
/// but for the log directory, the app user's. A build step that rewrote a published settings file under its own name shipped
/// with every check green. And every archive is read whole before any of it is (<see cref="ContainerArchive"/>): the
/// outside-/app test failed on every run with EndOfStreamException, reading the image's filesystem off the engine's response.
/// </summary>
public sealed class ShippedImageContentsTests(ITestOutputHelper output)
{
    private const string TestHostAssemblyWritten = "t13-test-host-assembly-written-into-app";
    private const string FakeWrittenOutsideApp = "t13-upstream-fake-assembly-written-outside-app";
    private const string ClassicBuild = "t13-image-built-by-the-classic-builder";

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-02) — what the engine puts in every container it makes, of any image, rather than the
    /// image: its marker and the files it mounts over at start. Compared by kind, mode and owner, not by content, which the
    /// engine may write per container.
    /// </summary>
    private static readonly string[] EngineFiles = [".dockerenv", "etc/hostname", "etc/hosts", "etc/resolv.conf"];

    /// <summary>The product's project, by the context's path to it.</summary>
    private const string ProductProject = "McpServerTemplate/McpServerTemplate.csproj";

    /// <summary>The product's assembly: its name, and the start of the SDK's manifests beside it.</summary>
    private const string ProductAssembly = "McpServerTemplate";

    /// <summary>The one directory the Dockerfile adds to the publish output: logs/, owned by the app user, empty in the image.</summary>
    private const string LogDirectory = "logs";

    /// <summary>The settings files the product ships: its folder's three appsettings files.</summary>
    private static readonly string[] SettingsFiles = ["appsettings.json", "appsettings.Development.json", "appsettings.Production.json"];

    /// <summary>What the SDK writes beside the assembly it publishes: its dependency, runtime and static-web-assets manifests.</summary>
    private static readonly string[] PublishManifests = [".deps.json", ".runtimeconfig.json", ".staticwebassets.endpoints.json"];

    /// <summary>A key's or a certificate's file kinds, as T-13's check of what git tracks reads them.</summary>
    private static readonly string[] KeyKinds = [".pem", ".crt", ".cer", ".der", ".key", ".pfx", ".p12", ".p7b", ".p7c", ".jks", ".keystore", ".csr"];

    /// <summary>The test frameworks and the harness's own libraries, by the start of their assemblies' names.</summary>
    private static readonly string[] TestLibraries =
        ["xunit.", "Microsoft.TestPlatform.", "Microsoft.VisualStudio.TestPlatform.", "testhost", "Microsoft.NET.Test.Sdk", "Testcontainers", "Docker.DotNet", "coverlet.", "WireMock"];

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-03) — the image's /app is the product's publish output by content: every file in it is one
    /// the publish makes, with the same bytes, and every file the publish makes is there — the publish run as the Dockerfile's build
    /// stage runs it, in the image that stage is built on, over the build context (<see cref="Images.PublishOutputAsync"/>). And it is
    /// held to what the Dockerfile says of its owners (CON-R3-01): /app and everything in it root's, writable by root alone, with no
    /// set-id bit, but for the log directory, the app user's. The server runs as the app user, and the log directory is the one
    /// place of /app it may write.
    /// </summary>
    [Fact]
    [Sabotage(TestHostAssemblyWritten, SabotageActs.ContainerEnvironment,
        "A file named McpServerTemplate.E2EHost.dll, the test host's assembly, is written into /app of the container the image's "
        + "/app is read from, through Docker's archive API, as a COPY --from of the test host's image would put it there.")]
    public async Task T13_the_images_app_directory_is_the_products_publish_output_and_nothing_else()
    {
        var environment = await E2EEnvironment.GetAsync();
        var context = Images.ContextFiles(environment.RepositoryRoot);
        var sdk = PublishImageOf(await File.ReadAllTextAsync(Path.Combine(environment.RepositoryRoot, "Dockerfile")));
        var published = await E2EEnvironment.Timings.MeasureAsync("t13: the product's publish output, by BuildKit, over the build context", () =>
            Images.PublishOutputAsync(environment.RepositoryRoot, sdk, CancellationToken.None));
        var (app, entries) = await AppOfAsync(environment);
        var files = entries.Where(e => e.Value.Kind == TarEntryType.RegularFile).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var directories = entries.Where(e => e.Value.Kind == TarEntryType.Directory).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var publishedFiles = published.Where(p => p.Value.Kind == TarEntryType.RegularFile).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        output.WriteLine($"Published in {sdk}: {publishedFiles.Count} files. The image's /app: {files.Count} files, directories [{string.Join(", ", directories.Order(StringComparer.Ordinal))}].");

        // Positive controls: the publish and the image both hold the product's own entry point, and the image the Dockerfile's
        // log directory — each read was of what it says — and each read the entry point's bytes.
        Assert.Contains($"{ProductAssembly}.dll", publishedFiles);
        Assert.Contains($"{ProductAssembly}.dll", files);
        Assert.Contains(LogDirectory, directories);
        Assert.NotNull(published[$"{ProductAssembly}.dll"].Content);
        Assert.NotNull(entries[$"{ProductAssembly}.dll"].Content);

        // The repository's other projects, by their assemblies' names: every one is test material here.
        var otherProjects = context
            .Where(f => f.EndsWith(".csproj", StringComparison.Ordinal) && f != ProductProject)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToArray();
        Assert.Contains("McpServerTemplate.E2EHost", otherProjects);

        var extra = files.Except(publishedFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missing = publishedFiles.Except(files, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changed = files.Intersect(publishedFiles, StringComparer.Ordinal)
            .Where(f => entries[f].Content != published[f].Content)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var material = files.Select(f => TestMaterial(f, otherProjects)).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        var strays = directories
            .Where(d => d != LogDirectory && !publishedFiles.Any(f => f.StartsWith(d + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var owners = entries.Select(e => NotAsTheDockerfileOwnsIt(e.Key, e.Value)).Prepend(NotAsTheDockerfileOwnsIt(string.Empty, app))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Claim.True(
            extra.Length == 0 && missing.Length == 0 && changed.Length == 0 && material.Length == 0 && strays.Length == 0 && owners.Length == 0,
            $"the image's /app ({files.Count} files) is not the product's publish output ({publishedFiles.Count} files) and its log directory, "
            + $"owned as the Dockerfile owns them: {extra.Length} not published [{string.Join(", ", extra)}]; {missing.Length} published and "
            + $"not there [{string.Join(", ", missing)}]; {changed.Length} whose content is not what the publish made [{string.Join(", ", changed)}]; "
            + $"test, harness, issuer, host or key files [{string.Join("; ", material)}]; directories the publish does not make "
            + $"[{string.Join(", ", strays)}]; owned or writable otherwise than root's alone, or the log directory otherwise than the "
            + $"app user's [{string.Join("; ", owners)}].");
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-01, HAR-R3-03) — why <paramref name="entry"/>, at <paramref name="path"/> under /app (empty
    /// for /app itself), is owned otherwise than the Dockerfile makes it, or null when it is not: the log directory is the app user's
    /// directory; everything else is root's (0:0), writable by its owner alone, with no set-user, set-group or sticky bit. The app user
    /// that could write to /app, as it could in the image BuildKit built before the Dockerfile set its owners, could replace the
    /// frame's settings files and its assemblies, read at the next start.
    /// </summary>
    private static string? NotAsTheDockerfileOwnsIt(string path, FileEntry entry)
    {
        var at = path.Length == 0 ? "/app" : $"/app/{path}";
        var how = $"{entry.Kind}, {entry.Uid}:{entry.Gid}, mode {Convert.ToString((int)entry.Mode, 8)}";
        if (path == LogDirectory)
        {
            return entry.Kind == TarEntryType.Directory && (entry.Uid, entry.Gid) == (ServerUnderTest.AppUser, ServerUnderTest.AppUser)
                ? null
                : $"{at} is {how}, where the Dockerfile makes it the app user's ({ServerUnderTest.AppUser}:{ServerUnderTest.AppUser}) directory";
        }

        const UnixFileMode writableByOthers = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        const UnixFileMode special = UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit;
        return (entry.Uid, entry.Gid) == (0, 0) && (entry.Mode & (writableByOthers | special)) == 0
            ? null
            : $"{at} is {how}";
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-01) — the image under test is the one BuildKit builds from this checkout: it carries the
    /// checkout's revision and build-context labels, and every step the Dockerfile added to its runtime base carries BuildKit's mark
    /// (<see cref="Images.BuildKitFrontend"/>), none of them written the classic builder's way. Operators build the image with BuildKit
    /// (`docker build`, `docker compose build`, and docs/04 deploys an image they built), and the harness built it with the engine's
    /// classic builder, through Testcontainers and the engine's build API: another image of the same Dockerfile, whose /app was root's
    /// where BuildKit's was the server's own user's. The harness builds with BuildKit now (<see cref="Images.BuildAsync"/>), and this
    /// holds the image the tests run to it.
    /// </summary>
    [Fact]
    [Sabotage(ClassicBuild, SabotageActs.Inputs,
        "The test reads, in place of the image under test, an image the engine's classic builder makes through its build API (the "
        + "builder Testcontainers built the harness's images with) of the Dockerfile's final stage in part - its runtime base and "
        + "WORKDIR /app - carrying this checkout's revision and build-context labels.")]
    public async Task T13_the_image_under_test_is_the_one_buildkit_builds_from_this_checkout()
    {
        var environment = await E2EEnvironment.GetAsync();
        var docker = environment.Docker;
        var runtime = RuntimeBaseOf(await File.ReadAllTextAsync(Path.Combine(environment.RepositoryRoot, "Dockerfile")));
        await PresentAsync(docker, runtime);

        var probe = Sabotage.Applies(ClassicBuild) ? await ClassicBuildAsync(environment, runtime) : null;
        try
        {
            var image = probe ?? environment.ServerImage;
            var labels = (await docker.Images.InspectImageAsync(image)).Config?.Labels ?? new Dictionary<string, string>();
            var history = await docker.Images.GetImageHistoryAsync(image);
            var baseHistory = await docker.Images.GetImageHistoryAsync(runtime);

            // Positive controls: the image read is this checkout's, by the labels every image of the run was checked to carry, and it is
            // built on the runtime base, whose own steps are its oldest — so the steps judged below are the ones the Dockerfile added.
            Assert.Equal(environment.Revision, labels.TryGetValue(Images.RevisionLabel, out var revision) ? revision : null);
            Assert.Equal(environment.ContextHash, labels.TryGetValue(Images.ContextLabel, out var context) ? context : null);
            Assert.True(
                history.Count > baseHistory.Count
                && history.TakeLast(baseHistory.Count).Select(h => h.CreatedBy).SequenceEqual(baseHistory.Select(h => h.CreatedBy), StringComparer.Ordinal),
                $"{image} is not built on {runtime}: its history ({history.Count} steps) does not end with the base's ({baseHistory.Count}).");

            var steps = history.Take(history.Count - baseHistory.Count).ToList();
            var notBuildKit = steps
                .Where(s => s.Comment != Images.BuildKitFrontend || Images.IsClassicStep(s.CreatedBy))
                .Select(s => $"'{s.CreatedBy}' (comment '{s.Comment}')")
                .ToList();
            output.WriteLine($"{image}: {steps.Count} steps on {runtime}: {string.Join("; ", steps.Select(s => $"'{s.CreatedBy}' ({s.Comment})"))}");

            Claim.True(
                notBuildKit.Count == 0,
                $"the image under test, {image}, is not the image BuildKit builds from this checkout: {notBuildKit.Count} of the {steps.Count} "
                + $"steps it adds to {runtime} were not made by BuildKit's Dockerfile frontend [{string.Join("; ", notBuildKit)}]. Operators "
                + "build the image with BuildKit, and the engine's classic builder makes another image of the same Dockerfile.");
        }
        finally
        {
            if (probe is not null)
            {
                await docker.Images.DeleteImageAsync(probe, new ImageDeleteParameters { Force = true });
            }
        }
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (CON-R3-01) — this test's sabotage: an image the engine's classic builder makes, through the engine's
    /// build API as Testcontainers built, of <paramref name="runtime"/> with WORKDIR /app, labelled as this checkout's. Named for the
    /// run, and removed by the test once it has read it.
    /// </summary>
    private static async Task<string> ClassicBuildAsync(E2EEnvironment environment, string runtime)
    {
        var tag = $"mcp-e2e-classic-probe:{environment.RunId}";
        using var context = new MemoryStream();
        await using (var writer = new TarWriter(context, TarEntryFormat.Pax, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"FROM {runtime}\nWORKDIR /app\n")),
            });
        }

        context.Position = 0;
        await environment.Docker.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters
            {
                Dockerfile = "Dockerfile",
                Tags = [tag],
                Labels = new Dictionary<string, string> { [Images.RevisionLabel] = environment.Revision, [Images.ContextLabel] = environment.ContextHash },
                Remove = true,
                ForceRemove = true,
            },
            context,
            authConfigs: null,
            headers: null,
            new Progress<JSONMessage>(),
            CancellationToken.None);
        return tag;
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-02) — outside /app, the image is its runtime base: the image the Dockerfile's final stage
    /// is built on, pinned by digest, file for file. Both are read whole, each from a container created and never started, and
    /// every path outside /app compared: added, removed, or changed in its kind, mode, owner, link, device, extended attributes
    /// or content. /app is the other test's, which holds it to the product's publish output and its log directory.
    /// </summary>
    [Fact]
    [Sabotage(FakeWrittenOutsideApp, SabotageActs.ContainerEnvironment,
        "A file named WireMock.Net.dll, standing for the upstream fake's assembly, is written at /opt/e2e-upstream-fake/ of the "
        + "container the image's filesystem is read from, through Docker's archive API, as a COPY --from of the fake's image would put it there.")]
    public async Task T13_outside_app_the_image_is_its_pinned_runtime_base_file_for_file()
    {
        var environment = await E2EEnvironment.GetAsync();
        var docker = environment.Docker;

        // The runtime base, as the Dockerfile names its final stage's: the file the image was built from, as the revision label
        // checked before any test ran.
        var runtime = RuntimeBaseOf(await File.ReadAllTextAsync(Path.Combine(environment.RepositoryRoot, "Dockerfile")));
        await PresentAsync(docker, runtime);

        // Positive control: the image is built on that base — its layers begin with the base's — so what is compared below is the
        // image and the base it was built from.
        var image = await docker.Images.InspectImageAsync(environment.ServerImage);
        var based = await docker.Images.InspectImageAsync(runtime);
        IList<string> imageLayers = image.RootFS?.Layers ?? [];
        IList<string> baseLayers = based.RootFS?.Layers ?? [];
        Assert.True(
            baseLayers.Count > 0 && imageLayers.Take(baseLayers.Count).SequenceEqual(baseLayers, StringComparer.Ordinal),
            $"the image {environment.ServerImage} is not built on {runtime}: its layers [{string.Join(", ", imageLayers)}] do not begin with the "
            + $"base's [{string.Join(", ", baseLayers)}].");

        var shipped = await E2EEnvironment.Timings.MeasureAsync("t13: the image's whole filesystem", () =>
            FileSystemOfAsync(environment, environment.ServerImage, Sabotage.Applies(FakeWrittenOutsideApp)));
        var theBase = await E2EEnvironment.Timings.MeasureAsync("t13: the runtime base's whole filesystem", () =>
            FileSystemOfAsync(environment, based.ID, plantOutsideApp: false));
        output.WriteLine($"Outside /app: the image holds {shipped.Outside.Count} entries, {runtime} {theBase.Outside.Count}; the image's /app {shipped.App.Count}.");

        // Positive controls: each read is of a whole filesystem — the image's holds its own /app, and both the runtime's core library.
        Assert.Contains($"app/{ProductAssembly}.dll", shipped.App);
        Assert.Contains(theBase.Outside.Keys, k => k.EndsWith("/System.Private.CoreLib.dll", StringComparison.Ordinal));
        Assert.Contains(shipped.Outside.Keys, k => k.EndsWith("/System.Private.CoreLib.dll", StringComparison.Ordinal));

        var differences = Differences(shipped.Outside, theBase.Outside);
        Claim.True(
            differences.Count == 0,
            $"outside /app, the image {environment.ServerImage} is not its runtime base {runtime}, file for file: {differences.Count} "
            + $"path(s) differ [{string.Join("; ", differences.Take(40))}{(differences.Count > 40 ? "; …" : string.Empty)}].");
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-02) — the image the Dockerfile's final stage is built on: its last FROM's image, or,
    /// where that names an earlier stage, the image that stage is built on, and so on. It must be pinned by digest, as T-13's
    /// check of every image reference requires; one that is not is no image this test can compare with.
    /// </summary>
    private static string RuntimeBaseOf(string dockerfile)
    {
        var stages = new List<(string From, string? Name)>();
        foreach (var line in dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()))
        {
            if (!line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Where(w => !w.StartsWith("--", StringComparison.Ordinal)).ToArray();
            stages.Add((words[0], words.Length >= 3 && words[1].Equals("AS", StringComparison.OrdinalIgnoreCase) ? words[2] : null));
        }

        if (stages.Count == 0)
        {
            throw new InvalidOperationException("The Dockerfile has no FROM line: no image is built on anything.");
        }

        var stage = stages.Count - 1;
        var reference = stages[stage].From;
        for (var earlier = stage - 1; earlier >= 0; earlier--)
        {
            if (string.Equals(stages[earlier].Name, reference, StringComparison.OrdinalIgnoreCase))
            {
                reference = stages[earlier].From;
            }
        }

        return reference.Contains("@sha256:", StringComparison.Ordinal)
            ? reference
            : throw new InvalidOperationException(
                $"The Dockerfile's final stage is built on '{reference}', which is not pinned by digest (T-13): nothing fixes what this test "
                + "would compare the image with.");
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-02) — <paramref name="reference"/>, an image pinned by digest, on the engine: the build
    /// pulled it, and it is pulled by its digest here only if it has gone since.
    /// </summary>
    private static async Task PresentAsync(Docker.DotNet.IDockerClient docker, string reference)
    {
        try
        {
            await docker.Images.InspectImageAsync(reference);
            return;
        }
        catch (Docker.DotNet.DockerImageNotFoundException)
        {
            // Pulled below, by its digest.
        }

        var at = reference.IndexOf('@', StringComparison.Ordinal);
        var name = reference[..at];
        var (slash, colon) = (name.LastIndexOf('/'), name.LastIndexOf(':'));
        await docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = colon > slash ? name[..colon] : name, Tag = reference[(at + 1)..] },
            authConfig: null,
            new Progress<JSONMessage>());
    }

    /// <summary>One entry of a filesystem, as its archive gives it, without its time: what two filesystems are compared by.</summary>
    /// <param name="Kind">A file, a directory, a link, a device.</param>
    /// <param name="Mode">Its permissions.</param>
    /// <param name="Uid">Its owner.</param>
    /// <param name="Gid">Its group.</param>
    /// <param name="Link">What a link names.</param>
    /// <param name="Device">A device's numbers, major and minor.</param>
    /// <param name="Attributes">Its extended attributes (a capability among them), as the archive carries them.</param>
    /// <param name="Content">A file's content, by its SHA-256; null for anything else, and for a file the engine writes per container.</param>
    private sealed record FileEntry(TarEntryType Kind, UnixFileMode Mode, int Uid, int Gid, string Link, string Device, string Attributes, string? Content);

    /// <summary>
    /// contract-005 · G-17 round 2 (CON-R2-02) — the whole filesystem of <paramref name="image"/>, from a container of it created and
    /// never started, with no network, then removed: every entry outside /app, and the names of those in it. In a run of this
    /// test's sabotage, the image's container has the fake's assembly written outside /app first.
    /// </summary>
    private static async Task<(Dictionary<string, FileEntry> Outside, HashSet<string> App)> FileSystemOfAsync(
        E2EEnvironment environment, string image, bool plantOutsideApp)
    {
        var docker = environment.Docker;
        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = image,
            // Never started: named only because an image may name no command, and the engine makes no container of one without.
            Entrypoint = ["/e2e-never-started"],
            Labels = new Dictionary<string, string>(E2ENetwork.RunLabels(environment.RunId)),
            HostConfig = new HostConfig { NetworkMode = "none" },
        });

        try
        {
            if (plantOutsideApp)
            {
                using var planted = new MemoryStream();
                await using (var writer = new TarWriter(planted, TarEntryFormat.Pax, leaveOpen: true))
                {
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "opt/"));
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "opt/e2e-upstream-fake/"));
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "opt/e2e-upstream-fake/WireMock.Net.dll")
                    {
                        DataStream = new MemoryStream("contract-005 · T-12 sabotage: stands for the upstream fake's assembly.\n"u8.ToArray()),
                    });
                }

                planted.Position = 0;
                await docker.Containers.ExtractArchiveToContainerAsync(created.ID, new CopyToContainerParameters { Path = "/" }, planted);
            }

            // contract-005 · G-17 round 3 — read whole first (ContainerArchive): hashed as it streamed off the engine's response, the
            // archive ended part way, at the first file of any size, with EndOfStreamException, on every run.
            var outside = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var app = new HashSet<string>(StringComparer.Ordinal);
            await ContainerArchive.ReadAsync(docker, created.ID, "/", async entry =>
            {
                var name = PathIn(entry.Name);
                if (name.Length == 0)
                {
                    return;
                }

                if (name == "app" || name.StartsWith("app/", StringComparison.Ordinal))
                {
                    app.Add(name);
                    return;
                }

                outside[name] = await FileEntryOfAsync(name, entry);
            });

            return (outside, app);
        }
        finally
        {
            await docker.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true });
        }
    }

    /// <summary>An archive entry's path from the filesystem's root, with no leading ./ or /, and no trailing /.</summary>
    private static string PathIn(string name)
    {
        var path = name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;
        path = path.TrimStart('/').TrimEnd('/');
        return path == "." ? string.Empty : path;
    }

    /// <summary>An archive entry as <see cref="FileEntry"/>: a file's content hashed as it streams past.</summary>
    private static async Task<FileEntry> FileEntryOfAsync(string name, TarEntry entry)
    {
        var kind = entry.EntryType is TarEntryType.V7RegularFile or TarEntryType.ContiguousFile ? TarEntryType.RegularFile : entry.EntryType;
        var content = kind == TarEntryType.RegularFile && !EngineFiles.Contains(name, StringComparer.Ordinal)
            ? Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(entry.DataStream ?? Stream.Null))
            : null;
        var device = entry is PosixTarEntry posix ? $"{posix.DeviceMajor}:{posix.DeviceMinor}" : "0:0";
        var attributes = entry is PaxTarEntry pax
            ? string.Join(";", pax.ExtendedAttributes
                .Where(a => a.Key.StartsWith("SCHILY.xattr.", StringComparison.Ordinal))
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => $"{a.Key}={a.Value}"))
            : string.Empty;
        var link = entry.EntryType == TarEntryType.HardLink ? PathIn(entry.LinkName) : entry.LinkName ?? string.Empty;
        return new FileEntry(kind, entry.Mode, entry.Uid, entry.Gid, link, device, attributes, content);
    }

    /// <summary>What differs between <paramref name="image"/>'s entries and <paramref name="runtime"/>'s: each path added, removed or changed, and how.</summary>
    private static List<string> Differences(Dictionary<string, FileEntry> image, Dictionary<string, FileEntry> runtime)
    {
        var differences = new List<string>();
        differences.AddRange(image.Keys.Where(p => !runtime.ContainsKey(p)).Order(StringComparer.Ordinal).Select(p => $"added /{p}"));
        differences.AddRange(runtime.Keys.Where(p => !image.ContainsKey(p)).Order(StringComparer.Ordinal).Select(p => $"removed /{p}"));
        foreach (var (path, entry) in image.Where(e => runtime.ContainsKey(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var was = runtime[path];
            if (entry != was)
            {
                string?[] how =
                [
                    entry.Kind != was.Kind ? $"kind {was.Kind} to {entry.Kind}" : null,
                    entry.Mode != was.Mode ? $"mode {Convert.ToString((int)was.Mode, 8)} to {Convert.ToString((int)entry.Mode, 8)}" : null,
                    (entry.Uid, entry.Gid) != (was.Uid, was.Gid) ? $"owner {was.Uid}:{was.Gid} to {entry.Uid}:{entry.Gid}" : null,
                    entry.Link != was.Link ? $"link '{was.Link}' to '{entry.Link}'" : null,
                    entry.Device != was.Device ? $"device {was.Device} to {entry.Device}" : null,
                    entry.Attributes != was.Attributes ? $"extended attributes '{was.Attributes}' to '{entry.Attributes}'" : null,
                    entry.Content != was.Content ? "content" : null,
                ];
                differences.Add($"changed /{path} ({string.Join(", ", how.OfType<string>())})");
            }
        }

        return differences;
    }

    /// <summary>
    /// Why <paramref name="file"/>, a path under /app, is a test, harness, issuer, host or key file, or null when it is none:
    /// an assembly of another project of this repository, a test framework's or the harness's library, a key or a certificate,
    /// or a JSON file that is neither one of the product's three settings files nor the SDK's manifest of its assembly — a
    /// fixture, or the settings of some other deployment (an appsettings.E2E.json naming .test identity providers).
    /// </summary>
    private static string? TestMaterial(string file, IReadOnlyList<string> otherProjects)
    {
        var name = Path.GetFileName(file);
        if (otherProjects.FirstOrDefault(p => name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)) is { } project)
        {
            return $"{file}, of {project}, a project of this repository that is not the product";
        }

        if (TestLibraries.FirstOrDefault(l => name.StartsWith(l, StringComparison.OrdinalIgnoreCase)) is { } library)
        {
            return $"{file}, a test framework's or the harness's library ({library}…)";
        }

        if (KeyKinds.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            return $"{file}, a key or a certificate";
        }

        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !SettingsFiles.Contains(file, StringComparer.Ordinal)
            && !PublishManifests.Any(m => file == ProductAssembly + m)
            ? $"{file}, a JSON file that is neither one of the product's settings files nor the SDK's manifest of its assembly"
            : null;
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (HAR-R3-03) — the image the Dockerfile publishes the product in: the stage its final stage copies
    /// /app from (COPY --from=STAGE /app, whatever other flags it carries), followed through stage names to the image that stage is
    /// built on. It must be pinned by digest, as T-13's check of every image reference requires; one that is not is no image the
    /// publish can be run in as the build stage runs it.
    /// </summary>
    private static string PublishImageOf(string dockerfile)
    {
        var stages = new List<(string From, string? Name, List<string> Instructions)>();
        foreach (var line in Instructions(dockerfile))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words[0].Equals("FROM", StringComparison.OrdinalIgnoreCase))
            {
                var named = words.Skip(1).Where(w => !w.StartsWith("--", StringComparison.Ordinal)).ToArray();
                stages.Add((named[0], named.Length >= 3 && named[1].Equals("AS", StringComparison.OrdinalIgnoreCase) ? named[2] : null, []));
            }
            else if (stages.Count > 0)
            {
                stages[^1].Instructions.Add(line);
            }
        }

        if (stages.Count == 0)
        {
            throw new InvalidOperationException("The Dockerfile has no FROM line: no image is built on anything.");
        }

        var stage = stages[^1].Instructions
            .Select(i => i.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w[0].Equals("COPY", StringComparison.OrdinalIgnoreCase)
                && w.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) is "/app" or "/app/")
            .Select(w => w.FirstOrDefault(a => a.StartsWith("--from=", StringComparison.OrdinalIgnoreCase))?["--from=".Length..])
            .FirstOrDefault(s => s is not null)
            ?? throw new InvalidOperationException("The Dockerfile's final stage copies /app from no stage (COPY --from=STAGE /app): nothing says where the product is published.");

        // Followed through stage names, a stage built on another stage, at most as many times as there are stages.
        var reference = stage;
        for (var guard = 0; guard <= stages.Count; guard++)
        {
            var named = stages.FindIndex(s => string.Equals(s.Name, reference, StringComparison.OrdinalIgnoreCase));
            if (named < 0)
            {
                break;
            }

            reference = stages[named].From;
        }

        return reference.Contains("@sha256:", StringComparison.Ordinal)
            ? reference
            : throw new InvalidOperationException(
                $"The Dockerfile publishes the product in '{reference}', which is not pinned by digest (T-13): nothing fixes what this test would "
                + "run the publish in.");
    }

    /// <summary>A Dockerfile's instructions, one a line: comments and blank lines left out, and a line ended by a backslash joined to the next.</summary>
    private static IEnumerable<string> Instructions(string dockerfile)
    {
        var joined = new System.Text.StringBuilder();
        foreach (var raw in dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (joined.Length == 0 && (line.Length == 0 || line.StartsWith('#')))
            {
                continue;
            }

            if (line.EndsWith('\\'))
            {
                joined.Append(line[..^1]).Append(' ');
                continue;
            }

            joined.Append(line);
            yield return joined.ToString().Trim();
            joined.Clear();
        }

        if (joined.Length > 0)
        {
            yield return joined.ToString().Trim();
        }
    }

    /// <summary>
    /// The image's /app: /app itself, and every entry under it, relative to it, with its kind, mode, owner and — for a file — its
    /// content, read from a container of the image that is created and never started — so it has no address on any network, and
    /// holds only what the image holds — through Docker's archive API, read whole first (<see cref="ContainerArchive"/>), then
    /// removed. It carries the run's label, so a run killed before it is removed has it removed by the next run's sweep. In a run
    /// of this test's sabotage, the test host's assembly is written into its /app first. contract-005 · G-17 round 3 (HAR-R3-03) —
    /// with each file's content and owner, where it was read with its names alone.
    /// </summary>
    private static async Task<(FileEntry App, Dictionary<string, FileEntry> Entries)> AppOfAsync(E2EEnvironment environment)
    {
        var docker = environment.Docker;
        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = environment.ServerImage,
            Labels = new Dictionary<string, string>(E2ENetwork.RunLabels(environment.RunId)),
            HostConfig = new HostConfig { NetworkMode = "none" },
        });

        try
        {
            if (Sabotage.Applies(TestHostAssemblyWritten))
            {
                using var archive = new MemoryStream();
                await using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
                {
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, "McpServerTemplate.E2EHost.dll")
                    {
                        DataStream = new MemoryStream("contract-005 · T-12 sabotage: stands for the test host's assembly.\n"u8.ToArray()),
                    };
                    await writer.WriteEntryAsync(entry);
                }

                archive.Position = 0;
                await docker.Containers.ExtractArchiveToContainerAsync(created.ID, new CopyToContainerParameters { Path = "/app" }, archive);
            }

            FileEntry? app = null;
            var entries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            await ContainerArchive.ReadAsync(docker, created.ID, "/app", async entry =>
            {
                var name = PathIn(entry.Name);
                if (name == "app")
                {
                    app = await FileEntryOfAsync(name, entry);
                    return;
                }

                var relative = name.StartsWith("app/", StringComparison.Ordinal) ? name["app/".Length..] : name;
                entries[relative] = await FileEntryOfAsync(relative, entry);
            });

            return (app ?? throw new IOException("the engine's archive of /app holds no entry for /app itself."), entries);
        }
        finally
        {
            await docker.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true });
        }
    }
}
