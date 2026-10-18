// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Sources;

namespace PhotoFlow.Sources.Ingest;

/// <summary>"Only new" (default) or "all, including those already imported" (P9).</summary>
public enum ImportSelection
{
    OnlyNew,
    All,
}

public sealed record PlannedFile(ScannedFile File, ExifInfo? Exif, string QuickHash, bool AlreadyImported, int Sequence, string TargetRelativePath);

public sealed record DestinationSpace(IngestDestination Destination, long RequiredBytes, long AvailableBytes)
{
    public bool IsSufficient => AvailableBytes >= RequiredBytes;
}

/// <summary>What an import will do: drives the "card detected" banner (3a) and the three import steps (3d-3f).</summary>
public sealed record ImportPlan(
    IReadOnlyList<PlannedFile> Files,
    int NewPhotos,
    int AlreadyImportedPhotos,
    int RawJpegPairs,
    int Videos,
    long TotalBytes,
    DateTimeOffset? FirstCapture,
    DateTimeOffset? LastCapture,
    DateOnly ProposedEventDate)
{
    /// <summary>Files to copy for the chosen selection.</summary>
    public IReadOnlyList<PlannedFile> Selected(ImportSelection selection) =>
        selection == ImportSelection.All ? Files : Files.Where(f => !f.AlreadyImported).ToList();
}

/// <summary>Builds the import plan: identity, sequence, destination paths, space check. Reads the source only.</summary>
public sealed class ImportPlanner
{
    private readonly IFileSystem _fileSystem;
    private readonly IExifReader _exifReader;
    private readonly IAssetStore _assets;

    public ImportPlanner(IFileSystem fileSystem, IExifReader exifReader, IAssetStore assets)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(exifReader);
        ArgumentNullException.ThrowIfNull(assets);
        _fileSystem = fileSystem;
        _exifReader = exifReader;
        _assets = assets;
    }

    public async Task<ImportPlan> PlanAsync(
        IReadOnlyList<ScannedFile> scanned, string eventName, DateOnly? eventDate, PathTemplate template, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scanned);
        ArgumentNullException.ThrowIfNull(template);
        var exifByPath = (await _exifReader.ReadAsync(scanned.Where(f => f.Kind != MediaKind.Other).Select(f => f.Item.FullPath).ToList(), ct).ConfigureAwait(false))
            .ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

        var identified = new List<(ScannedFile File, ExifInfo? Exif, string QuickHash, bool Already)>();
        foreach (var file in scanned)
        {
            var exif = exifByPath.GetValueOrDefault(file.Item.FullPath);
            var quick = await QuickHash.ComputeAsync(_fileSystem, file.Item.FullPath, ct).ConfigureAwait(false);
            var captured = exif?.CaptureDateSource == DateSource.Exif ? exif.CapturedAt : null;
            var already = await _assets.FindByIdentityAsync(quick, captured, ct).ConfigureAwait(false) is not null;
            identified.Add((file, exif, quick, already));
        }

        var photoCaptures = identified
            .Where(i => i.Exif?.CaptureDateSource == DateSource.Exif && i.File.Kind != MediaKind.Video)
            .Select(i => i.Exif!.CapturedAt!.Value)
            .ToList();
        var first = photoCaptures.Count > 0 ? photoCaptures.Min() : (DateTimeOffset?)null;
        var last = photoCaptures.Count > 0 ? photoCaptures.Max() : (DateTimeOffset?)null;
        var proposed = first is { } f ? DateOnly.FromDateTime(f.DateTime) : DateOnly.FromDateTime(DateTime.Today);
        var date = eventDate ?? proposed;

        // Sequence in shooting order; RAW and JPEG of the same photo share it.
        var groups = identified
            .GroupBy(i => i.File.PairKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Members: g.ToList(), Order: g.Min(i => i.Exif?.CapturedAt ?? DateTimeOffset.MaxValue), Name: g.Key))
            .OrderBy(g => g.Order)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var planned = new List<PlannedFile>();
        var sequence = 0;
        foreach (var group in groups)
        {
            sequence++;
            foreach (var (file, exif, quick, already) in group.Members)
            {
                var context = new PathTemplateContext(
                    eventName, date, sequence, exif?.Model, Path.GetFileNameWithoutExtension(file.Item.RelativePath), Path.GetExtension(file.Item.RelativePath).TrimStart('.'));
                planned.Add(new PlannedFile(file, exif, quick, already, sequence, template.Render(context)));
            }
        }

        var photos = groups.Where(g => g.Members.Any(m => m.File.Kind != MediaKind.Video && m.File.Kind != MediaKind.Other)).ToList();
        return new ImportPlan(
            planned,
            NewPhotos: photos.Count(g => g.Members.Any(m => !m.Already)),
            AlreadyImportedPhotos: photos.Count(g => g.Members.All(m => m.Already)),
            RawJpegPairs: groups.Count(g => g.Members.Any(m => m.File.Kind == MediaKind.Raw) && g.Members.Any(m => m.File.Kind == MediaKind.Jpeg)),
            Videos: identified.Count(i => i.File.Kind == MediaKind.Video),
            TotalBytes: identified.Sum(i => i.File.Item.Length),
            first,
            last,
            proposed);
    }

    /// <summary>Free space check per local destination (board 3f).</summary>
    public IReadOnlyList<DestinationSpace> CheckSpace(ImportPlan plan, ImportSelection selection, IReadOnlyList<IngestDestination> destinations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var required = plan.Selected(selection).Sum(f => f.File.Item.Length);
        return destinations
            .Where(d => d.Kind != DestinationKind.Deferred)
            .Select(d => new DestinationSpace(d, required, SafeFreeSpace(d.RootPath)))
            .ToList();
    }

    private long SafeFreeSpace(string path)
    {
        try
        {
            return _fileSystem.GetAvailableFreeSpace(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }
}
