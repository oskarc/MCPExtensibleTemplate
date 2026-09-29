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
/// </summary>
public sealed class ShippedImageContentsTests(ITestOutputHelper output)
{
    private const string TestHostAssemblyWritten = "t13-test-host-assembly-written-into-app";

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
            Labels = new Dictionary<string, string> { [E2ENetwork.RunLabel] = environment.RunId },
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
