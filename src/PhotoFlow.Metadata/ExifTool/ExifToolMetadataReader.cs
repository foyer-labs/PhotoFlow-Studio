// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Hashing;
using System.Text.Json;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;

namespace PhotoFlow.Metadata.ExifTool;

/// <summary>
/// Reads with exiftool the XMP fields PhotoFlow writes (rating, label, keywords, hierarchy, description, MWG regions)
/// from the file that holds them: the <c>name.xmp</c> sidecar for RAW files and sidecar-only libraries, the image itself
/// otherwise (spec §4). The stamp of that file (size, date, xxHash3 of the content) tells whether another program
/// changed it since PhotoFlow last looked. Only reads (I1, I2).
/// </summary>
public sealed class ExifToolMetadataReader(ExifToolSession exifTool, IFileSystem fileSystem) : IMetadataReader
{
    public static string HolderPath(MetadataTarget target, WritePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.IsRaw || policy == WritePolicy.SidecarOnly ? ExifToolMetadataWriter.SidecarPath(target.FilePath) : target.FilePath;
    }

    public async Task<XmpFileStamp?> StampAsync(MetadataTarget target, WritePolicy policy, CancellationToken ct)
    {
        var path = HolderPath(target, policy);
        if (!fileSystem.FileExists(path))
        {
            return null;
        }

        var metadata = fileSystem.GetFileMetadata(path);
        var hash = new XxHash3();
        await using (var stream = fileSystem.OpenRead(path))
        {
            await hash.AppendAsync(stream, ct).ConfigureAwait(false);
        }

        return new XmpFileStamp(path, metadata.Length, metadata.LastWriteTimeUtc, Convert.ToHexString(hash.GetCurrentHash()));
    }

    public async Task<XmpSnapshot> ReadAsync(MetadataTarget target, WritePolicy policy, CancellationToken ct)
    {
        var path = HolderPath(target, policy);
        if (!fileSystem.FileExists(path))
        {
            return XmpSnapshot.Empty;
        }

        var response = await exifTool.ExecuteAsync(
            ["-use", "MWG", "-j", "-struct", "-n", "-XMP-xmp:Rating", "-XMP-xmp:Label", "-XMP-dc:Subject", "-XMP-lr:HierarchicalSubject", "-XMP-dc:Description",
                "-XMP-mwg-rs:RegionInfo", path], ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(response.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return XmpSnapshot.Empty;
            }

            var root = document.RootElement[0];
            int? rating = root.TryGetProperty("Rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32()
                : root.TryGetProperty("Rating", out r) && int.TryParse(r.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            return new XmpSnapshot(
                rating,
                Text(root, "Label"),
                List(root, "Subject"),
                List(root, "HierarchicalSubject"),
                Text(root, "Description"),
                Regions(root));
        }
        catch (JsonException)
        {
            return XmpSnapshot.Empty;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.ToString() is { Length: > 0 } text ? text : null;

    private static IReadOnlyList<string> List(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.ToString()).Where(v => v.Length > 0)]
            : value.ToString() is { Length: > 0 } single ? [single] : [];
    }

    private static IReadOnlyList<XmpRegion> Regions(JsonElement root)
    {
        if (!(root.TryGetProperty("RegionInfo", out var info) || root.TryGetProperty("Regions", out info))
            || info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("RegionList", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var regions = new List<XmpRegion>();
        foreach (var region in list.EnumerateArray())
        {
            if (region.TryGetProperty("Area", out var area))
            {
                regions.Add(new XmpRegion(Text(region, "Name"), Text(region, "Type"), Number(area, "X"), Number(area, "Y"), Number(area, "W"), Number(area, "H")));
            }
        }

        return regions;

        static double Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : element.TryGetProperty(name, out var text) && double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}
