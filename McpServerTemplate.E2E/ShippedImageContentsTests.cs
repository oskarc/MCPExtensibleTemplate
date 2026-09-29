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
/// </summary>
public sealed class ShippedImageContentsTests(ITestOutputHelper output)
{
    private const string TestHostAssemblyWritten = "t13-test-host-assembly-written-into-app";
    private const string FakeWrittenOutsideApp = "t13-upstream-fake-assembly-written-outside-app";

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

    [Fact]
    [Sabotage(TestHostAssemblyWritten, SabotageActs.ContainerEnvironment,
        "A file named McpServerTemplate.E2EHost.dll, the test host's assembly, is written into /app of the container the image's "
        + "/app is read from, through Docker's archive API, as a COPY --from of the test host's image would put it there.")]
    public async Task T13_the_images_app_directory_is_the_products_publish_output_and_nothing_else()
    {
        var environment = await E2EEnvironment.GetAsync();
        var context = Images.ContextFiles(environment.RepositoryRoot);
        var published = await E2EEnvironment.Timings.MeasureAsync("t13: dotnet publish of the product, over the build context", () => PublishAsync(environment.RepositoryRoot, context));
        var (files, directories) = await AppOfAsync(environment);
        output.WriteLine($"Published: {published.Count} files. The image's /app: {files.Count} files, directories [{string.Join(", ", directories.Order(StringComparer.Ordinal))}].");

        // Positive controls: the publish and the image both hold the product's own entry point, and the image the Dockerfile's
        // log directory — each read was of what it says.
        Assert.Contains($"{ProductAssembly}.dll", published);
        Assert.Contains($"{ProductAssembly}.dll", files);
        Assert.Contains(LogDirectory, directories);

        // The repository's other projects, by their assemblies' names: every one is test material here.
        var otherProjects = context
            .Where(f => f.EndsWith(".csproj", StringComparison.Ordinal) && f != ProductProject)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToArray();
        Assert.Contains("McpServerTemplate.E2EHost", otherProjects);

        var extra = files.Except(published, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missing = published.Except(files, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var material = files.Select(f => TestMaterial(f, otherProjects)).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        var strays = directories
            .Where(d => d != LogDirectory && !published.Any(f => f.StartsWith(d + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Claim.True(
            extra.Length == 0 && missing.Length == 0 && material.Length == 0 && strays.Length == 0,
            $"the image's /app ({files.Count} files) is not the product's publish output ({published.Count} files) and its log directory: "
            + $"{extra.Length} not published [{string.Join(", ", extra)}]; {missing.Length} published and not there [{string.Join(", ", missing)}]; "
            + $"test, harness, issuer, host or key files [{string.Join("; ", material)}]; directories the publish does not make "
            + $"[{string.Join(", ", strays)}].");
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

            var outside = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var app = new HashSet<string>(StringComparer.Ordinal);
            var archive = await docker.Containers.GetArchiveFromContainerAsync(created.ID, new ContainerPathStatParameters { Path = "/" }, statOnly: false);
            using (var stream = archive.Stream ?? throw new IOException("the engine returned no archive of /."))
            using (var reader = new TarReader(stream))
            {
                while (await reader.GetNextEntryAsync(copyData: false) is { } entry)
                {
                    var name = PathIn(entry.Name);
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (name == "app" || name.StartsWith("app/", StringComparison.Ordinal))
                    {
                        app.Add(name);
                        continue;
                    }

                    outside[name] = await FileEntryOfAsync(name, entry);
                }
            }

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
    /// The files `dotnet publish` of the product gives, relative to its output, with / between names: run as the Dockerfile's
    /// build stage runs it (a locked restore, then publish in Release with no app host), over a copy of exactly the files the
    /// build context sends (<see cref="Images.ContextFiles"/>) — so a file the context leaves out, as .dockerignore leaves out
    /// McpServerTemplate/init.json, is left out here too. Build servers are not kept, so nothing holds the copy once it is done.
    /// </summary>
    private static async Task<HashSet<string>> PublishAsync(string repositoryRoot, IReadOnlyList<string> context)
    {
        var work = Directory.CreateTempSubdirectory("e2e-publish-").FullName;
        try
        {
            var source = Path.Combine(work, "context");
            foreach (var file in context)
            {
                var target = Path.Combine(source, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(repositoryRoot, file), target);
            }

            var app = Path.Combine(work, "app");
            await DotnetAsync(source, "restore", ProductProject, "--locked-mode", "--disable-build-servers");
            await DotnetAsync(source, "publish", ProductProject, "-c", "Release", "--no-restore", "-o", app, "-p:UseAppHost=false", "--disable-build-servers");
            return [.. Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(app, f).Replace('\\', '/'))];
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A copy of the source and its build, no key material; the machine's temporary directory keeps it.
            }
        }
    }

    /// <summary>Runs dotnet with <paramref name="arguments"/> in <paramref name="directory"/>, in English, or fails saying what it said.</summary>
    private static async Task DotnetAsync(string directory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";

        using var dotnet = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var said = dotnet.StandardOutput.ReadToEndAsync();
        var errors = dotnet.StandardError.ReadToEndAsync();
        await dotnet.WaitForExitAsync();
        if (dotnet.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(" ", arguments)} failed ({dotnet.ExitCode}) in {directory}: {await errors}{await said}");
        }
    }

    /// <summary>
    /// The image's /app: its files and its directories, relative to it, read from a container of the image that is created and
    /// never started — so it has no address on any network, and holds only what the image holds — through Docker's archive API,
    /// then removed. It carries the run's label, so a run killed before it is removed has it removed by the next run's sweep.
    /// In a run of this test's sabotage, the test host's assembly is written into its /app first.
    /// </summary>
    private static async Task<(HashSet<string> Files, HashSet<string> Directories)> AppOfAsync(E2EEnvironment environment)
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

            var files = new HashSet<string>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            var app = await docker.Containers.GetArchiveFromContainerAsync(created.ID, new ContainerPathStatParameters { Path = "/app" }, statOnly: false);
            using (var stream = app.Stream ?? throw new IOException("the engine returned no archive of /app."))
            using (var reader = new TarReader(stream))
            {
                while (await reader.GetNextEntryAsync(copyData: false) is { } entry)
                {
                    var name = entry.Name.TrimEnd('/');
                    if (name == "app")
                    {
                        continue;
                    }

                    var relative = name.StartsWith("app/", StringComparison.Ordinal) ? name["app/".Length..] : name;
                    (entry.EntryType is TarEntryType.Directory ? directories : files).Add(relative);
                }
            }

            return (files, directories);
        }
        finally
        {
            await docker.Containers.RemoveContainerAsync(created.ID, new ContainerRemoveParameters { Force = true });
        }
    }
}
