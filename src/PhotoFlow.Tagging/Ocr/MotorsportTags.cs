// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Tagging.Ocr;

/// <summary>
/// Tags of the race numbers (spec §8, D18): for every vehicle with a certain number (automatic, propagated or the
/// user's), <c>Motorsport|Numero N</c>; with the entry list also <c>Motorsport|Team|Name</c> and, for a single
/// driver, <c>Motorsport|Pilota|Name</c>. A crew gets only the number and the team: the names stay in the catalog.
/// Nothing is written for numbers to review or out of the list. Names and numbers are not translated.
/// </summary>
public static class MotorsportTags
{
    public const string NumberPrefix = "number:";
    public const string DriverPrefix = "driver:";
    public const string TeamPrefix = "team:";
    public const string Category = "Motorsport";
    public const string ModelVersion = "numbers-v1";

    public static async Task ApplyAsync(ICatalog catalog, long assetId, IReadOnlyList<Entry>? entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var byNumber = (entries ?? []).ToDictionary(e => e.Number, StringComparer.Ordinal);
        var keys = new List<(string Key, double? Confidence)>();
        foreach (var vehicle in await catalog.Motorsport.GetNumbersAsync(assetId, ct).ConfigureAwait(false))
        {
            if (vehicle.State != NumberState.Certain || vehicle.Number is not { } number)
            {
                continue;
            }

            keys.Add((await EnsureAsync(catalog, NumberPrefix + "Numero " + number, Category, ct).ConfigureAwait(false), null));
            if (byNumber.TryGetValue(number, out var entry))
            {
                if (entry.Team is { Length: > 0 } team)
                {
                    keys.Add((await EnsureAsync(catalog, TeamPrefix + team, Category + "|Team", ct).ConfigureAwait(false), null));
                }

                if (!entry.IsCrew && entry.Drivers.Count == 1)
                {
                    keys.Add((await EnsureAsync(catalog, DriverPrefix + entry.Drivers[0], Category + "|Pilota", ct).ConfigureAwait(false), null));
                }
            }
        }

        await catalog.Tags.ReplaceAutomaticAsync(assetId, TagSource.Ocr, ModelVersion, keys.DistinctBy(k => k.Key).ToList(), ct).ConfigureAwait(false);
    }

    private static async Task<string> EnsureAsync(ICatalog catalog, string key, string category, CancellationToken ct)
    {
        var label = key[(key.IndexOf(':', StringComparison.Ordinal) + 1)..];
        await catalog.Tags.EnsureAsync(key, TagSource.Ocr, category, label, reviewed: true, ct).ConfigureAwait(false);
        return key;
    }
}
