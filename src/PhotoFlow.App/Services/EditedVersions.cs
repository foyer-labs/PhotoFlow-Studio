// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.App.Services;

/// <summary>A version of a photo saved by an external editor next to the original (M11).</summary>
public sealed record EditedVersion(string Path, DateTime ModifiedAt, long Length)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The return from the external editor (M11): what Photoshop and the others save next to the original, with the same name
/// and a suffix (DSC_0001.psd, DSC_0001-Modifica.tif, DSC_0001_edit.jpg), belongs to the photo. Found by looking at the
/// folder when the photo is shown: nothing to set up, and a file moved or renamed by the user simply stops being listed.
/// The exports of PhotoFlow Studio are not versions: they are recorded apart.
/// </summary>
public static class EditedVersions
{
    private static readonly HashSet<string> Editable = new(StringComparer.OrdinalIgnoreCase) { ".psd", ".psb", ".tif", ".tiff", ".jpg", ".jpeg", ".png", ".heic", ".webp", ".dng" };

    public static IReadOnlyList<EditedVersion> Find(string originalPath, IReadOnlyCollection<string>? exports = null)
    {
        ArgumentNullException.ThrowIfNull(originalPath);
        var folder = Path.GetDirectoryName(originalPath);
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        if (folder is null || stem.Length == 0 || !Directory.Exists(folder))
        {
            return [];
        }

        var excluded = new HashSet<string>(exports ?? [], StringComparer.OrdinalIgnoreCase) { originalPath };
        try
        {
            return [.. Directory.EnumerateFiles(folder, stem + "*")
                .Where(f => Editable.Contains(Path.GetExtension(f)) && !excluded.Contains(f))
                .Where(f => IsVersionName(Path.GetFileNameWithoutExtension(f), stem))
                .Select(f => new FileInfo(f))
                .Select(f => new EditedVersion(f.FullName, f.LastWriteTime, f.Length))
                .OrderByDescending(v => v.ModifiedAt)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The same name, or the name and a suffix after a separator (DSC_0001-Modifica, DSC_0001 copia, DSC_0001_edit): not
    /// another shot whose name only starts the same (DSC_00010).
    /// </summary>
    public static bool IsVersionName(string candidate, string stem) =>
        candidate.Equals(stem, StringComparison.OrdinalIgnoreCase)
        || (candidate.Length > stem.Length && candidate.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && candidate[stem.Length] is '-' or '_' or ' ' or '.' or '(');
}
