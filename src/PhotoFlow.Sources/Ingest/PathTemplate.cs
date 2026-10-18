// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoFlow.Sources.Ingest;

/// <summary>Values available to the path template for one file.</summary>
/// <param name="EventDate">Event date (P10, proposed from the shots): all date tokens use it, so an event that crosses midnight stays in one folder.</param>
/// <param name="Sequence">Position of the photo in the import (RAW and JPEG of a pair share it).</param>
/// <param name="OriginalName">Original file name without extension.</param>
/// <param name="Extension">Original extension without the dot, case preserved.</param>
public sealed record PathTemplateContext(string EventName, DateOnly EventDate, int Sequence, string? Camera, string OriginalName, string Extension);

/// <summary>
/// Destination path template (spec §4, P20). Variables: <c>{yyyy}</c> <c>{MM}</c> and any date pattern made of y/M/d and
/// separators (e.g. <c>{yyyy-MM-dd}</c>), <c>{evento}</c>, <c>{seq}</c> / <c>{seq:0000}</c>, <c>{camera}</c>, <c>{nomeoriginale}</c>,
/// <c>{ext}</c>. Characters not valid in file names become "-"; "/" separates folders.
/// </summary>
public sealed partial class PathTemplate
{
    public const string Default = "{yyyy}/{yyyy-MM-dd}_{evento}/{yyyyMMdd}_{evento}_{seq:0000}.{ext}";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _template;

    public PathTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        foreach (Match match in Token().Matches(template))
        {
            if (!IsKnown(match.Groups[1].Value))
            {
                throw new ArgumentException($"Unknown template variable '{match.Value}'.", nameof(template));
            }
        }

        if (!template.Contains("{ext}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The template must keep the original extension ({ext}).", nameof(template));
        }

        _template = template;
    }

    /// <summary>Relative path with the platform directory separator.</summary>
    public string Render(PathTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var segments = _template.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Sanitize(Token().Replace(segment, m => Value(m.Groups[1].Value, context))))
            .ToArray();
        return Path.Combine(segments);
    }

    private static bool IsKnown(string token) =>
        token is "evento" or "seq" or "camera" or "nomeoriginale" or "ext" || token.StartsWith("seq:", StringComparison.Ordinal) || DatePattern().IsMatch(token);

    private static string Value(string token, PathTemplateContext c) => token switch
    {
        "evento" => c.EventName,
        "seq" => c.Sequence.ToString(CultureInfo.InvariantCulture),
        "camera" => string.IsNullOrWhiteSpace(c.Camera) ? "camera" : c.Camera,
        "nomeoriginale" => c.OriginalName,
        "ext" => c.Extension,
        _ when token.StartsWith("seq:", StringComparison.Ordinal) => c.Sequence.ToString(token[4..], CultureInfo.InvariantCulture),
        _ => c.EventDate.ToString(token, CultureInfo.InvariantCulture),
    };

    /// <summary>Invalid characters and spaces become "-", repeated dashes collapse, reserved names get a suffix.</summary>
    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var builder = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            builder.Append(invalid.Contains(ch) || char.IsWhiteSpace(ch) || char.IsControl(ch) ? '-' : ch);
        }

        var cleaned = MultipleDashes().Replace(builder.ToString(), "-").Trim('-', '.', ' ');
        if (cleaned.Length == 0)
        {
            cleaned = "-";
        }

        var stem = cleaned.Split('.')[0];
        return ReservedNames.Contains(stem) ? stem + "_" + cleaned[stem.Length..] : cleaned;
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"^[yMd]+([-_.]?[yMd]+)*$")]
    private static partial Regex DatePattern();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultipleDashes();
}
