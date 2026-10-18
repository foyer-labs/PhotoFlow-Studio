// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Sources;

/// <summary>Import source (SD card or folder). Read-only by contract: invariant I1.</summary>
public interface ISource
{
    IAsyncEnumerable<SourceItem> EnumerateAsync(CancellationToken ct);
}

public sealed record SourceItem(string FullPath, string RelativePath, long Length, DateTimeOffset LastWriteTime);

/// <summary>Kind of a file, decided from its content (signature), not from its extension (spec §3).</summary>
public enum MediaKind
{
    Raw,
    Jpeg,
    Png,
    Tiff,
    Heif,
    WebP,
    Video,

    /// <summary>Not a photo or video PhotoFlow knows: copied, never analyzed.</summary>
    Other,
}

public sealed record ScannedFile(SourceItem Item, MediaKind Kind)
{
    /// <summary>Photos PhotoFlow decodes and analyzes (RAW, JPEG, PNG, TIFF, HEIF/HEIC/AVIF, WebP).</summary>
    public bool IsAnalyzable => Kind is MediaKind.Raw or MediaKind.Jpeg or MediaKind.Png or MediaKind.Tiff or MediaKind.Heif or MediaKind.WebP;

    /// <summary>Directory + file name without extension: RAW and JPEG with the same key form one photo.</summary>
    public string PairKey => Path.Combine(Path.GetDirectoryName(Item.RelativePath) ?? string.Empty, Path.GetFileNameWithoutExtension(Item.RelativePath));
}

/// <summary>A memory card: a removable volume with a DCIM folder.</summary>
public sealed record CardVolume(string RootPath, string Label, long TotalBytes);

/// <summary>Watches removable volumes and reports cards as they are inserted or removed.</summary>
public interface ICardDetector : IDisposable
{
    event EventHandler<CardVolume>? CardInserted;

    event EventHandler<CardVolume>? CardRemoved;

    IReadOnlyList<CardVolume> Current { get; }

    void Start();
}
