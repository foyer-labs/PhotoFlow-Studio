// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;

namespace PhotoFlow.App.Services;

/// <summary>
/// Moves files of a library into a folder of the same library on the user's explicit request (invariant I4): the
/// discards into <c>_scarti</c> (P6), the copies not kept into <c>_duplicati</c> (P5). The relative path is kept; the
/// sidecar and the <c>_original</c> copy follow the file, unless another file of the same name (the RAW or JPEG twin)
/// stays behind and still needs the shared sidecar. Only renames inside the library root (same volume): nothing is
/// copied, nothing is deleted, nothing is overwritten (a name already taken gets a suffix). Read-only libraries are left
/// alone. The catalog follows the move.
/// </summary>
public sealed class LibraryMover(ICatalog catalog, IFileSystem fileSystem, string folderName)
{
    public string FolderName { get; } = folderName;

    public async Task<(int Moved, int Skipped)> MoveAsync(IReadOnlyList<AssetLocation> locations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var libraries = (await catalog.Libraries.ListAsync(ct).ConfigureAwait(false)).ToDictionary(l => l.Id);
        var moving = locations.Select(l => (l.LibraryId, l.RelativePath)).ToHashSet();
        int moved = 0, skipped = 0;
        foreach (var location in locations)
        {
            if (!libraries.TryGetValue(location.LibraryId, out var library) || library.IsReadOnly
                || location.RelativePath.StartsWith(FolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || location.RelativePath.StartsWith(FolderName + "/", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var source = Path.Combine(library.RootPath, location.RelativePath);
            if (!fileSystem.FileExists(source))
            {
                skipped++;
                continue;
            }

            var relative = FreeName(library.RootPath, Path.Combine(FolderName, location.RelativePath));
            var target = Path.Combine(library.RootPath, relative);
            fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);
            fileSystem.Move(source, target);
            var sidecar = Path.ChangeExtension(source, ".xmp");
            var twinStays = SiblingsOf(source).Any(s => !moving.Contains((location.LibraryId, Path.GetRelativePath(library.RootPath, s))));
            foreach (var companion in new[] { twinStays ? default : (sidecar, Path.ChangeExtension(target, ".xmp")), (source + "_original", target + "_original") })
            {
                if (companion.Item1 is not null && fileSystem.FileExists(companion.Item1) && !fileSystem.FileExists(companion.Item2))
                {
                    fileSystem.Move(companion.Item1, companion.Item2);
                }
            }

            await catalog.Assets.MoveLocationAsync(location.LibraryId, location.RelativePath, relative, CancellationToken.None).ConfigureAwait(false);
            moved++;
        }

        return (moved, skipped);
    }

    /// <summary>Other photos with the same name in the folder (a RAW+JPEG twin), sidecars and backups excluded.</summary>
    private IEnumerable<string> SiblingsOf(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        return fileSystem.EnumerateFiles(folder, stem + ".*", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)
                        && !f.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("_original", StringComparison.OrdinalIgnoreCase));
    }

    private string FreeName(string root, string relative)
    {
        var candidate = relative;
        for (var n = 2; fileSystem.FileExists(Path.Combine(root, candidate)); n++)
        {
            candidate = Path.Combine(Path.GetDirectoryName(relative) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(relative)}-{n}{Path.GetExtension(relative)}");
        }

        return candidate;
    }
}

/// <summary>"Move the discards…" (P6): every location of the discarded photos into <c>_scarti</c>.</summary>
public sealed class DiscardMover(ICatalog catalog, IFileSystem fileSystem)
{
    public const string FolderName = "_scarti";

    public async Task<(int Moved, int Skipped)> MoveAsync(IReadOnlyList<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var locations = new List<AssetLocation>();
        foreach (var assetId in assetIds)
        {
            locations.AddRange(await catalog.Assets.GetLocationsAsync(assetId, ct).ConfigureAwait(false));
        }

        return await new LibraryMover(catalog, fileSystem, FolderName).MoveAsync(locations, ct).ConfigureAwait(false);
    }
}
