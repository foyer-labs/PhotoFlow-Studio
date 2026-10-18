// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Metadata;

namespace PhotoFlow.Metadata.ExifTool;

/// <summary>Extracts the largest JPEG embedded in a RAW file, with the RAW orientation (spec §4).</summary>
public sealed class ExifToolPreviewSource : IEmbeddedPreviewSource
{
    private static readonly string[] PreviewTags = ["JpgFromRaw", "PreviewImage", "OtherImage"];

    private readonly ExifToolSession _exifTool;

    public ExifToolPreviewSource(ExifToolSession exifTool)
    {
        ArgumentNullException.ThrowIfNull(exifTool);
        _exifTool = exifTool;
    }

    public async Task<EmbeddedPreviews> ExtractLargestAsync(string rawPath, CancellationToken ct)
    {
        var info = await _exifTool.ExecuteAsync(
            ["-json", "-Orientation#", "-NEFCompression", .. PreviewTags.Select(t => $"-{t}Length#"), .. PreviewTags.Select(t => $"-{t}Start#"), rawPath], ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(info.Output))
        {
            return new EmbeddedPreviews(null);
        }

        using var document = JsonDocument.Parse(info.Output);
        var e = document.RootElement[0];
        var orientation = e.TryGetProperty("Orientation", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 1;
        var decodable = !HighEfficiencyRaw.Matches(e.TryGetProperty("NEFCompression", out var c) ? c.ToString() : null);
        var best = PreviewTags
            .Select(tag => (Tag: tag, Length: e.TryGetProperty(tag + "Length", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt64() : 0))
            .Where(p => p.Length > 0)
            .OrderByDescending(p => p.Length)
            .ToList();

        foreach (var (tag, _) in best)
        {
            var (bytes, _) = await _exifTool.ExecuteBinaryAsync(["-b", "-" + tag, rawPath], ct).ConfigureAwait(false);
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                return new EmbeddedPreviews(new EmbeddedPreview(bytes, tag, orientation is >= 1 and <= 8 ? orientation : 1), decodable);
            }
        }

        return new EmbeddedPreviews(null, decodable);
    }
}
