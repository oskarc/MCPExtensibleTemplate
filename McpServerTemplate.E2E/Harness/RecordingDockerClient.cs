using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Handler.Abstractions;
using Docker.DotNet.Models;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// The run's Docker client (<see cref="E2EEnvironment.Docker"/>), which writes down, in the run's directory, every image name
/// the run gives an image, before it gives it: the name, what it named before, and the image it names now.
///
/// contract-005 · G-17 round 1 (CON-R1-03) — the docs profile gives this run's server image the name docs/04's compose file
/// uses (G-13, mcp-server:latest) and takes it back only in its own teardown, so a run killed during it left the name on its
/// build, and the next docs-profile run took that leftover for the image the name had replaced and put it back, for good. A
/// run killed before its teardown now has every such name put back by the next run's sweep of dead runs
/// (<see cref="TakeBackAsync"/>), as it stood before that run, from this record. Whatever gives a name through this client is
/// recorded, the docs profile's own teardown included: a name is put back only while it still names what the dead run last
/// gave it, so a name the run put back itself, or one changed since, is left as it is.
///
/// It pushes nothing (contract-005 · Boundaries: the test images are never pushed anywhere): its push refuses. Everything else
/// is the engine's client, unchanged.
/// </summary>
internal sealed class RecordingDockerClient(IDockerClient inner, string runDirectory) : IDockerClient
{
    /// <summary>The record, in the run's directory: one line per name given, name, what it named before (or -), what it names now.</summary>
    public const string TagsFile = "tags.tsv";

    public ResolvedClientOptions Options => inner.Options;

    public ISystemOperations System => inner.System;

    public IContainerOperations Containers => inner.Containers;

    public IImageOperations Images { get; } = new RecordingImages(inner.Images, Path.Combine(runDirectory, TagsFile));

    public INetworkOperations Networks => inner.Networks;

    public IVolumeOperations Volumes => inner.Volumes;

    public ISecretsOperations Secrets => inner.Secrets;

    public IConfigOperations Configs => inner.Configs;

    public IDistributionOperations Distribution => inner.Distribution;

    public ISwarmOperations Swarm => inner.Swarm;

    public ITasksOperations Tasks => inner.Tasks;

    public IPluginOperations Plugin => inner.Plugin;

    public IExecOperations Exec => inner.Exec;

    public void Dispose() => inner.Dispose();

    /// <summary>
    /// contract-005 · G-17 round 1 (CON-R1-03) — puts back every image name the dead run whose directory is
    /// <paramref name="runDirectory"/> gave, as it stood before that run: pointed again at the image it named, or removed when
    /// it named none (or that image is gone). A name that no longer names what the run last gave it is left alone: the run put
    /// it back itself, or someone has named something else since. Returns what was done, a line each, for swept.txt.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TakeBackAsync(IDockerClient docker, string runDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);

        var record = Path.Combine(runDirectory, TagsFile);
        if (!File.Exists(record))
        {
            return [];
        }

        var run = Path.GetFileName(runDirectory);
        var done = new List<string>();
        var given = (await File.ReadAllLinesAsync(record, cancellationToken))
            .Select(l => l.Split('\t'))
            .Where(f => f.Length == 3)
            .GroupBy(f => f[0], StringComparer.Ordinal);
        foreach (var name in given)
        {
            var before = name.First()[1];
            var last = name.Last()[2];
            if (await IdOfAsync(docker.Images, name.Key, cancellationToken) != last || before == last)
            {
                continue;
            }

            var (repository, tag) = Split(name.Key);
            try
            {
                if (before != "-" && await IdOfAsync(docker.Images, before, cancellationToken) is not null)
                {
                    await docker.Images.TagImageAsync(before, new ImageTagParameters { RepositoryName = repository, Tag = tag }, cancellationToken);
                    done.Add($"image name {name.Key}, given to {last} by run {run}: put back on {before}, the image it named before that run");
                }
                else
                {
                    await docker.Images.DeleteImageAsync(name.Key, new ImageDeleteParameters { NoPrune = true }, cancellationToken);
                    done.Add($"image name {name.Key}, given to {last} by run {run}: removed, {(before == "-" ? "as it named nothing before that run" : $"as {before}, which it named before that run, is gone")}");
                }
            }
            catch (DockerApiException ex)
            {
                throw new EnvironmentFaultException(
                    "environment: sweep dead runs",
                    $"the image name {name.Key}, which the dead run {run} gave {last} (its record: {record}), could not be put back as it "
                    + $"was before that run ({(before == "-" ? "naming nothing" : $"naming {before}")}): {ex.Message}",
                    ex);
            }
        }

        return done;
    }

    /// <summary>The id of the image <paramref name="name"/> names now, or null when it names none.</summary>
    private static async Task<string?> IdOfAsync(IImageOperations images, string name, CancellationToken cancellationToken)
    {
        try
        {
            return (await images.InspectImageAsync(name, cancellationToken)).ID;
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A name as repository and tag, the tag latest when it has none.</summary>
    private static (string Repository, string Tag) Split(string name)
    {
        var slash = name.LastIndexOf('/');
        var colon = name.LastIndexOf(':');
        return colon > slash ? (name[..colon], name[(colon + 1)..]) : (name, "latest");
    }

    /// <summary>The engine's image operations, with every name given written down first, and no push.</summary>
    private sealed class RecordingImages(IImageOperations inner, string record) : IImageOperations
    {
        private static readonly SemaphoreSlim Writing = new(1, 1);

        public async Task TagImageAsync(string name, ImageTagParameters parameters, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            var given = $"{parameters.RepositoryName}:{(string.IsNullOrEmpty(parameters.Tag) ? "latest" : parameters.Tag)}";
            var before = await IdOfAsync(inner, given, cancellationToken) ?? "-";
            var now = (await inner.InspectImageAsync(name, cancellationToken)).ID;

            // Written before the name is given, so a run killed in between leaves a line whose image the name does not name,
            // which the sweep leaves alone.
            await Writing.WaitAsync(cancellationToken);
            try
            {
                await File.AppendAllTextAsync(record, string.Create(CultureInfo.InvariantCulture, $"{given}\t{before}\t{now}\n"), cancellationToken);
            }
            finally
            {
                Writing.Release();
            }

            await inner.TagImageAsync(name, parameters, cancellationToken);
        }

        public Task PushImageAsync(string name, ImagePushParameters parameters, AuthConfig? authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The end-to-end environment pushes no image anywhere (contract-005 · Boundaries).");

        public Task<IList<ImagesListResponse>> ListImagesAsync(ImagesListParameters parameters, CancellationToken cancellationToken = default) =>
            inner.ListImagesAsync(parameters, cancellationToken);

        public Task BuildImageFromDockerfileAsync(ImageBuildParameters parameters, Stream contents, IEnumerable<AuthConfig>? authConfigs, IDictionary<string, string>? headers, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) =>
            inner.BuildImageFromDockerfileAsync(parameters, contents, authConfigs, headers, progress, cancellationToken);

        // The engine's client marks this overload obsolete (it does not wait for the build to end), and the harness builds its
        // images through Testcontainers: nothing asks for it.
        public Task<Stream> BuildImageFromDockerfileAsync(Stream contents, ImageBuildParameters parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The run's client does not build through this overload, which does not wait for the build to end.");

        public Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig? authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) =>
            inner.CreateImageAsync(parameters, authConfig, progress, cancellationToken);

        public Task CreateImageAsync(ImagesCreateParameters parameters, AuthConfig? authConfig, IDictionary<string, string>? headers, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) =>
            inner.CreateImageAsync(parameters, authConfig, headers, progress, cancellationToken);

        public Task CreateImageAsync(ImagesCreateParameters parameters, Stream? imageStream, AuthConfig? authConfig, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) =>
            inner.CreateImageAsync(parameters, imageStream, authConfig, progress, cancellationToken);

        public Task CreateImageAsync(ImagesCreateParameters parameters, Stream? imageStream, AuthConfig? authConfig, IDictionary<string, string>? headers, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) =>
            inner.CreateImageAsync(parameters, imageStream, authConfig, headers, progress, cancellationToken);

        public Task<ImageInspectResponse> InspectImageAsync(string name, CancellationToken cancellationToken = default) =>
            inner.InspectImageAsync(name, cancellationToken);

        public Task<IList<ImageHistoryResponse>> GetImageHistoryAsync(string name, CancellationToken cancellationToken = default) =>
            inner.GetImageHistoryAsync(name, cancellationToken);

        public Task<IList<IDictionary<string, string>>> DeleteImageAsync(string name, ImageDeleteParameters parameters, CancellationToken cancellationToken = default) =>
            inner.DeleteImageAsync(name, parameters, cancellationToken);

        public Task<IList<ImageSearchResponse>> SearchImagesAsync(ImagesSearchParameters parameters, CancellationToken cancellationToken = default) =>
            inner.SearchImagesAsync(parameters, cancellationToken);

        public Task<ImagesPruneResponse> PruneImagesAsync(ImagesPruneParameters? parameters = null, CancellationToken cancellationToken = default) =>
            inner.PruneImagesAsync(parameters, cancellationToken);

        public Task<CommitContainerChangesResponse> CommitContainerChangesAsync(CommitContainerChangesParameters parameters, CancellationToken cancellationToken = default) =>
            inner.CommitContainerChangesAsync(parameters, cancellationToken);

        public Task<Stream> SaveImageAsync(string name, CancellationToken cancellationToken = default) =>
            inner.SaveImageAsync(name, cancellationToken);

        public Task<Stream> SaveImagesAsync(string[]? names, CancellationToken cancellationToken = default) =>
            inner.SaveImagesAsync(names, cancellationToken);

        public Task LoadImageAsync(ImageLoadParameters parameters, Stream imageStream, IProgress<JSONMessage> progress, CancellationToken cancellationToken = default) =>
            inner.LoadImageAsync(parameters, imageStream, progress, cancellationToken);
    }
}
