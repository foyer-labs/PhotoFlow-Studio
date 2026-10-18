// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Metadata;

/// <summary>Writes XMP by merging, never overwriting; RAW files only get sidecars (invariant I2).</summary>
public interface IMetadataWriter
{
    Task<MetadataWriteResult> WriteAsync(MetadataTarget target, MetadataChanges changes, WritePolicy policy, CancellationToken ct);
}

/// <summary>The file whose metadata is written. RAW files are never modified: only their sidecar.</summary>
public sealed record MetadataTarget(string FilePath, bool IsRaw);

public enum MetadataWriteOutcome
{
    Embedded,
    Sidecar,
    CatalogOnly,
    Failed,
}

public sealed record MetadataWriteResult(MetadataWriteOutcome Outcome, string? WrittenPath, string? Error);

/// <summary>XMP write policy of a library (spec §4, D21).</summary>
public enum WritePolicy
{
    SidecarOnly,
    EmbeddedForNonRaw,
    CatalogOnly,
}

/// <param name="Regions">Named face regions (MWG), or null to leave the regions untouched.</param>
/// <param name="RemovedFlatKeywords">Keywords PhotoFlow may have written before that no longer apply (tags removed by the user or blacklisted); other keywords are never touched.</param>
/// <param name="DescriptionAlternatives">Other languages of <c>dc:description</c> (language → text) besides x-default (phase 5b, D5b-6).</param>
public sealed record MetadataChanges(
    int? Rating,
    string? Label,
    IReadOnlyList<string> FlatKeywords,
    IReadOnlyList<string> HierarchicalKeywords,
    string? Description,
    FaceRegions? Regions = null,
    IReadOnlyList<string>? RemovedFlatKeywords = null,
    IReadOnlyList<string>? RemovedHierarchicalKeywords = null,
    IReadOnlyDictionary<string, string>? DescriptionAlternatives = null);

/// <summary>
/// Face regions to write (spec §4, §7: MWG regions with the name). Regions already in the file with a name that is
/// not managed by PhotoFlow (<paramref name="ManagedNames"/>: the names of the catalog's people) are kept.
/// </summary>
public sealed record FaceRegions(int ImageWidth, int ImageHeight, IReadOnlyList<FaceRegion> Regions, IReadOnlyCollection<string> ManagedNames);

/// <summary>A named face, MWG convention: centre and size normalized to the image (0..1).</summary>
public sealed record FaceRegion(string Name, double CenterX, double CenterY, double Width, double Height);

/// <summary>The XMP fields PhotoFlow manages, as found in a file (spec §4: read to import changes made by other programs).</summary>
public sealed record XmpSnapshot(
    int? Rating,
    string? Label,
    IReadOnlyList<string> FlatKeywords,
    IReadOnlyList<string> HierarchicalKeywords,
    string? Description,
    IReadOnlyList<XmpRegion> Regions)
{
    public static XmpSnapshot Empty { get; } = new(null, null, [], [], null, []);
}

/// <summary>An MWG region of the file (normalized centre and size), with the name and type given by its program.</summary>
public sealed record XmpRegion(string? Name, string? Type, double CenterX, double CenterY, double Width, double Height);

/// <summary>Identity of the file that holds the XMP (sidecar or image): changed by any program that writes it.</summary>
public sealed record XmpFileStamp(string Path, long Size, DateTimeOffset Modified, string Hash)
{
    public bool SameFileState(XmpFileStamp? other) => other is not null && other.Path == Path && other.Size == Size && other.Modified == Modified;
}

/// <summary>Reads the XMP of a photo where PhotoFlow writes it (RAW and sidecar-only libraries: <c>name.xmp</c>).</summary>
public interface IMetadataReader
{
    /// <summary>The file that holds the XMP and its state (size, date, content hash), or null when there is none.</summary>
    Task<XmpFileStamp?> StampAsync(MetadataTarget target, WritePolicy policy, CancellationToken ct);

    Task<XmpSnapshot> ReadAsync(MetadataTarget target, WritePolicy policy, CancellationToken ct);
}
