using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-17 round 3 (HAR-R3-02) — the source files a type was compiled from, as the compiler recorded them in the
/// assembly's portable PDB: the document of every sequence point of every method of the type and of every type nested in it.
///
/// A recorded red is fresh while its test's file is as it was when the red was taken (<see cref="Sabotage.Fingerprint"/>), and
/// that is one file, McpServerTemplate.E2E/{Class}.cs. A test class is partial (UpstreamTests was), and a part of it in another
/// file — a test, a helper its claim rests on — could change with every red still counted fresh. The compiler's record says
/// which files a class's code came from, whatever they are called and however the class is spelled, so the registry refuses a
/// test class compiled from any file but its own (<see cref="Sabotage.Problems"/>). Sources a generator wrote (under obj/, as
/// the regular-expression generator's are) are the compiler's, and not counted.
/// </summary>
internal static class SourceDocuments
{
    private static readonly Lazy<MetadataReaderProvider?> Pdb = new(() =>
    {
        var path = Path.ChangeExtension(typeof(SourceDocuments).Assembly.Location, ".pdb");
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.PrefetchMetadata);
    });

    /// <summary>The PDB the suite's assembly was built with, or null when there is none beside it.</summary>
    public static string? Missing =>
        Pdb.Value is null ? $"{Path.ChangeExtension(typeof(SourceDocuments).Assembly.Location, ".pdb")} is not there" : null;

    /// <summary>
    /// Every source file <paramref name="type"/> (of this assembly) was compiled from, with / between names, as the PDB records
    /// it; generated sources left out. Empty when there is no PDB (<see cref="Missing"/> says so).
    /// </summary>
    public static IReadOnlySet<string> Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var documents = new HashSet<string>(StringComparer.Ordinal);
        if (Pdb.Value?.GetMetadataReader() is not { } pdb)
        {
            return documents;
        }

        foreach (var method in CompiledCode.MethodsOf(type).Where(m => m.Module == typeof(SourceDocuments).Module))
        {
            var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00FFFFFF);
            foreach (var point in pdb.GetMethodDebugInformation(handle).GetSequencePoints())
            {
                if (point.Document.IsNil)
                {
                    continue;
                }

                var name = pdb.GetString(pdb.GetDocument(point.Document).Name).Replace('\\', '/');
                if (!name.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                {
                    documents.Add(name);
                }
            }
        }

        return documents;
    }
}
