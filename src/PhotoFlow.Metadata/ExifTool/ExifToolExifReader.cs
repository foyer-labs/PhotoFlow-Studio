// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;

namespace PhotoFlow.Metadata.ExifTool;

/// <summary>EXIF reading through exiftool, in batches, as JSON (numeric tags in exact form with the <c>#</c> suffix).</summary>
public sealed class ExifToolExifReader : IExifReader
{
    private const int BatchSize = 64;

    private static readonly string[] Tags =
    [
        "-FileType", "-Make", "-Model", "-LensModel", "-Lens", "-LensID", "-ISO#", "-ExposureTime#", "-FNumber#", "-FocalLength#",
        "-ExposureCompensation#", "-SubSecDateTimeOriginal", "-DateTimeOriginal", "-OffsetTimeOriginal", "-FileModifyDate", "-Orientation#",
        "-AFPoint", "-PrimaryAFPoint", "-AFPointsUsed", "-ImageWidth#", "-ImageHeight#", "-NEFCompression", "-JpgFromRawLength#", "-PreviewImageLength#",
        "-AFAreaXPosition#", "-AFAreaYPosition#", "-AFAreaWidth#", "-AFAreaHeight#", "-AFImageWidth#", "-AFImageHeight#",
    ];

    private readonly ExifToolSession _exifTool;
    private readonly TimeZoneInfo _localZone;

    /// <param name="localZone">Zone assumed for capture times without an offset (camera clock): the PC zone by default.</param>
    public ExifToolExifReader(ExifToolSession exifTool, TimeZoneInfo? localZone = null)
    {
        ArgumentNullException.ThrowIfNull(exifTool);
        _exifTool = exifTool;
        _localZone = localZone ?? TimeZoneInfo.Local;
    }

    public async Task<IReadOnlyList<ExifInfo>> ReadAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var results = new List<ExifInfo>(paths.Count);
        foreach (var batch in paths.Chunk(BatchSize))
        {
            var response = await _exifTool.ExecuteAsync(["-json", "-api", "LargeFileSupport=1", .. Tags, .. batch], ct).ConfigureAwait(false);
            var byPath = Parse(response.Output).ToDictionary(e => Normalize(e.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in batch)
            {
                results.Add(byPath.TryGetValue(Normalize(path), out var info)
                    ? info with { Path = path }
                    : new ExifInfo(path, null, null, null, null, null, null, null, null, null, null, DateSource.Unknown, null, null, null, null, null, null, null));
            }
        }

        return results;
    }

    private IEnumerable<ExifInfo> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var e in document.RootElement.EnumerateArray())
        {
            var (capturedAt, source) = CaptureTime(e);
            yield return new ExifInfo(
                Str(e, "SourceFile")!,
                Str(e, "FileType"),
                Str(e, "Make"),
                Str(e, "Model"),
                Str(e, "LensModel") ?? Str(e, "LensID") ?? Str(e, "Lens"),
                (int?)Num(e, "ISO"),
                Num(e, "ExposureTime"),
                Num(e, "FNumber"),
                Num(e, "FocalLength"),
                Num(e, "ExposureCompensation"),
                capturedAt,
                source,
                (int?)Num(e, "Orientation"),
                Str(e, "PrimaryAFPoint") ?? Str(e, "AFPointsUsed") ?? Str(e, "AFPoint"),
                (int?)Num(e, "ImageWidth"),
                (int?)Num(e, "ImageHeight"),
                Str(e, "NEFCompression"),
                (long?)Num(e, "JpgFromRawLength"),
                (long?)Num(e, "PreviewImageLength"),
                AfArea(e));
        }
    }

    /// <summary>Nikon mirrorless and live view write the AF area in pixels; named viewfinder points (DSLR) carry no position.</summary>
    private static BoundingBox? AfArea(JsonElement e) =>
        AfAreas.FromCenter(Num(e, "AFAreaXPosition"), Num(e, "AFAreaYPosition"), Num(e, "AFAreaWidth"), Num(e, "AFAreaHeight"),
            Num(e, "AFImageWidth"), Num(e, "AFImageHeight"), (int?)Num(e, "Orientation"));

    /// <summary>SubSecDateTimeOriginal → DateTimeOriginal (+ OffsetTimeOriginal or local zone) → file date.</summary>
    private (DateTimeOffset? Value, DateSource Source) CaptureTime(JsonElement e)
    {
        foreach (var tag in new[] { "SubSecDateTimeOriginal", "DateTimeOriginal" })
        {
            if (Str(e, tag) is { } text && TryParseExifDate(text, Str(e, "OffsetTimeOriginal"), out var value))
            {
                return (value, DateSource.Exif);
            }
        }

        return Str(e, "FileModifyDate") is { } file && TryParseExifDate(file, null, out var fileDate)
            ? (fileDate, DateSource.File)
            : (null, DateSource.Unknown);
    }

    private bool TryParseExifDate(string text, string? offsetText, out DateTimeOffset value)
    {
        value = default;
        text = text.Trim();
        if (text.Length < 19 || text.StartsWith("0000", StringComparison.Ordinal))
        {
            return false;
        }

        // "yyyy:MM:dd HH:mm:ss[.fff][+hh:mm|Z]"
        if (!DateTime.TryParseExact(text[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        var rest = text[19..];
        var fraction = 0.0;
        if (rest.StartsWith('.'))
        {
            var digits = new string(rest[1..].TakeWhile(char.IsAsciiDigit).ToArray());
            fraction = double.Parse("0." + digits, CultureInfo.InvariantCulture);
            rest = rest[(1 + digits.Length)..];
        }

        local = local.AddTicks((long)Math.Round(fraction * TimeSpan.TicksPerSecond));
        var offset = ParseOffset(rest) ?? (offsetText is null ? null : ParseOffset(offsetText)) ?? _localZone.GetUtcOffset(local);
        value = new DateTimeOffset(local, offset);
        return true;
    }

    private static TimeSpan? ParseOffset(string text)
    {
        text = text.Trim();
        if (text == "Z")
        {
            return TimeSpan.Zero;
        }

        return text.Length == 6 && (text[0] == '+' || text[0] == '-')
               && TimeSpan.TryParseExact(text[1..], @"hh\:mm", CultureInfo.InvariantCulture, out var span)
            ? (text[0] == '-' ? -span : span)
            : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
            : null;

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
}
