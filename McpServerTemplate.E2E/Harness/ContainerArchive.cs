using System.Formats.Tar;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-17 round 3 — a path of a container, through Docker's archive API, read whole before any entry of it is.
///
/// ShippedImageContentsTests.T13_outside_app… failed on every run with EndOfStreamException: it hashed each entry as the archive
/// streamed in, and the engine's client (Docker.DotNet.Enhanced 4.3.3's ChunkedReadStream) ended the chunked response part way,
/// at the first file of any size. The same archive, copied whole first, read cleanly, every entry. So the archive is copied,
/// as it comes, to a temporary file of its own — deleted when it is closed, since a filesystem read this way holds the whole
/// image — and read from there; no entry is read off the response.
/// </summary>
internal static class ContainerArchive
{
    /// <summary>
    /// Every entry of <paramref name="path"/>'s archive in the container <paramref name="containerId"/>, in order, each handed to
    /// <paramref name="each"/> with its data readable (a file's <see cref="TarEntry.DataStream"/>) until the next is read.
    /// </summary>
    public static async Task ReadAsync(
        IDockerClient docker, string containerId, string path, Func<TarEntry, Task> each, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(each);

        var response = await docker.Containers.GetArchiveFromContainerAsync(
            containerId, new ContainerPathStatParameters { Path = path }, statOnly: false, cancellationToken);

        // Not named mcp-e2e-*: that is a run directory's name, which the sweep, the sabotage script and CI's check look for.
        var buffer = Path.Combine(Path.GetTempPath(), $"e2e-archive-{Guid.NewGuid():N}.tar");
        await using var copy = new FileStream(
            buffer, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using (var stream = response.Stream ?? throw new IOException($"the engine returned no archive of {path}."))
        {
            await stream.CopyToAsync(copy, cancellationToken);
        }

        copy.Position = 0;
        await using var reader = new TarReader(copy, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            await each(entry);
        }
    }

    /// <summary>
    /// The file at <paramref name="path"/> in the container <paramref name="containerId"/>, as text (UTF-8), read whole; null
    /// when the archive holds no regular file there.
    /// </summary>
    public static async Task<string?> ReadTextAsync(IDockerClient docker, string containerId, string path, CancellationToken cancellationToken = default)
    {
        string? text = null;
        await ReadAsync(docker, containerId, path, async entry =>
        {
            if (text is null && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data)
            {
                using var reader = new StreamReader(data, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                text = await reader.ReadToEndAsync(cancellationToken);
            }
        }, cancellationToken);
        return text;
    }
}
