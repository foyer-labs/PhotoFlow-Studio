// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.IO;

namespace PhotoFlow.Core.Catalog;

/// <summary>Where the file of an asset can be read now: the first known location whose library and file exist.</summary>
public static class AssetFiles
{
    public static async Task<string?> LocateAsync(ICatalog catalog, IFileSystem fileSystem, long assetId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileSystem);
        foreach (var location in await catalog.Assets.GetLocationsAsync(assetId, ct).ConfigureAwait(false))
        {
            if (await catalog.Libraries.GetAsync(location.LibraryId, ct).ConfigureAwait(false) is { } library)
            {
                var path = Path.Combine(library.RootPath, location.RelativePath);
                if (fileSystem.FileExists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
