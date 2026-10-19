// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.Sources.Ingest;

namespace PhotoFlow.App.ViewModels;

/// <summary>
/// A camera of the card in the import (M5) with the correction of its clock: «+0:05» when it is five minutes behind.
/// Two bodies of one event, corrected, give one order of the shots in Review and Develop.
/// </summary>
public sealed partial class CameraClockViewModel(CameraOnCard camera) : ObservableObject
{
    public CameraOnCard Camera { get; } = camera;

    public string Model => Camera.Model;

    public string Detail => Camera.First is { } first && Camera.Last is { } last
        ? Strings.Format("Import.Camera.Detail", Camera.Photos.ToString("N0", CultureInfo.CurrentCulture), Formats.CaptureRange(first, last))
        : Strings.Format("Import.Camera.Photos", Camera.Photos.ToString("N0", CultureInfo.CurrentCulture));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Offset), nameof(HasError), nameof(CorrectedText))]
    private string _offsetText = string.Empty;

    /// <summary>The correction, or null when the text is not one.</summary>
    public TimeSpan? Offset => ClockOffset.TryParse(OffsetText, out var offset) ? offset : null;

    public bool HasError => Offset is null;

    /// <summary>«primo scatto 14:07:30 corretto» while a correction is typed.</summary>
    public string CorrectedText => Offset is { } offset && offset != TimeSpan.Zero && Camera.First is { } first
        ? Strings.Format("Import.Camera.Corrected", (first + offset).ToString("HH:mm:ss", CultureInfo.CurrentCulture))
        : string.Empty;
}

/// <summary>Clock corrections as typed by the user: «+5» (minutes), «-1:30» (hours and minutes), «+0:00:45», empty = none.</summary>
public static partial class ClockOffset
{
    public static bool TryParse(string? text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        var value = (text ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (value.Length == 0 || value is "0" or "+0" or "-0")
        {
            return true;
        }

        var match = Pattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        var sign = match.Groups["sign"].Value == "-" ? -1 : 1;
        var parts = match.Groups["value"].Value.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var span = parts.Length switch
        {
            1 => TimeSpan.FromMinutes(parts[0]),
            2 => new TimeSpan(parts[0], parts[1], 0),
            _ => new TimeSpan(parts[0], parts[1], parts[2]),
        };
        if (parts.Skip(1).Any(p => p >= 60) || span > TimeSpan.FromDays(2))
        {
            return false;
        }

        offset = sign * span;
        return true;
    }

    [GeneratedRegex(@"^(?<sign>[+-]?)(?<value>\d{1,4}(:\d{1,2}){0,2})$")]
    private static partial Regex Pattern();
}
