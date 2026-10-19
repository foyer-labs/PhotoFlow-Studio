// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.App.Services.Develop;

/// <summary>
/// Groups of photos taken in the same light (M7): in shooting order, a new group starts after a pause longer than
/// <see cref="Pause"/> or when the exposure changes by more than <see cref="EvStep"/> stops (a different room, outside,
/// the flash). Developing one photo per group and synchronizing the others is how a whole event gets developed.
/// </summary>
public static class DevelopGroups
{
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(15);

    public const double EvStep = 1.0;

    /// <summary>Exposure value at ISO 100 (EV100): log2(N²/t) − log2(ISO/100); null when a datum is missing.</summary>
    public static double? Ev100(ExifSummary? exif) =>
        exif is { Aperture: > 0 and var n, ExposureTimeSeconds: > 0 and var t, Iso: > 0 and var iso }
            ? Math.Log2(n * n / t) - Math.Log2(iso / 100.0)
            : null;

    /// <summary>The group of each photo (0, 1, 2…), given in shooting order.</summary>
    public static IReadOnlyList<int> Assign(IReadOnlyList<(DateTimeOffset? At, double? Ev)> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        var groups = new int[photos.Count];
        var group = 0;
        for (var i = 1; i < photos.Count; i++)
        {
            var (at, ev) = photos[i];
            var (previousAt, previousEv) = photos[i - 1];
            var pause = at is { } a && previousAt is { } p && (a - p).Duration() > Pause;
            var light = ev is { } e && previousEv is { } pe && Math.Abs(e - pe) > EvStep;
            if (pause || light)
            {
                group++;
            }

            groups[i] = group;
        }

        return groups;
    }
}
