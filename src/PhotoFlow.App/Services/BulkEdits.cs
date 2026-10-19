// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.App.Services;

/// <summary>
/// Changes made on many photos at once (M3: the multiple selection of Browse): stars, colour label, a tag. The XMP of the
/// photos already confirmed is written again in one pass; the others get it at the confirmation of their review.
/// </summary>
public static class BulkEdits
{
    /// <summary>Stars (0 takes them off) on <paramref name="assetIds"/>; true when every XMP was written.</summary>
    public static Task<bool> SetStarsAsync(PhotoFlowHost host, IReadOnlyList<long> assetIds, int stars, CancellationToken ct) =>
        ChangeDecisionsAsync(host, assetIds, d => (d?.UserOutcome, stars is >= 1 and <= 5 ? stars : null, d?.Label), ct);

    /// <summary>The colour label (null takes it off) on <paramref name="assetIds"/>.</summary>
    public static Task<bool> SetLabelAsync(PhotoFlowHost host, IReadOnlyList<long> assetIds, string? label, CancellationToken ct) =>
        ChangeDecisionsAsync(host, assetIds, d => (d?.UserOutcome, d?.Rating, label), ct);

    /// <summary>
    /// The tag <paramref name="text"/> (Italian or English label of an existing tag, otherwise a new tag of the user) on
    /// <paramref name="assetIds"/>, as manual: no analysis takes it away (I5).
    /// </summary>
    public static async Task<bool> AddTagAsync(PhotoFlowHost host, IReadOnlyList<long> assetIds, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(assetIds);
        text = text.Trim();
        if (text.Length == 0 || assetIds.Count == 0)
        {
            return true;
        }

        var catalog = host.Catalog;
        var existing = (await catalog.Tags.ListAsync(ct))
            .FirstOrDefault(t => string.Equals(t.Italian, text, StringComparison.CurrentCultureIgnoreCase) || string.Equals(t.English, text, StringComparison.CurrentCultureIgnoreCase));
        foreach (var id in assetIds)
        {
            await catalog.Tags.AddManualAsync(id, existing?.Key ?? text, ct);
        }

        if (existing is null && await catalog.Tags.FindAsync(text, ct) is { } created)
        {
            await catalog.Tags.UpdateAsync(created.Id, text, reviewed: true, null, blacklisted: false, ct);
        }

        return await RewriteXmpAsync(host, assetIds, ct);
    }

    private static async Task<bool> ChangeDecisionsAsync(PhotoFlowHost host, IReadOnlyList<long> assetIds,
        Func<Decision?, (CullingOutcome? Outcome, int? Rating, string? Label)> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(assetIds);
        foreach (var id in assetIds)
        {
            var (outcome, rating, label) = change(await host.Catalog.Decisions.GetAsync(id, ct));
            await host.Catalog.Decisions.SetUserAsync(id, outcome, rating, label, ct);
        }

        return await RewriteXmpAsync(host, assetIds, ct);
    }

    /// <summary>The XMP of the photos already confirmed, written again; false when some writing failed.</summary>
    public static async Task<bool> RewriteXmpAsync(PhotoFlowHost host, IReadOnlyList<long> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        var confirmed = new List<long>();
        foreach (var id in assetIds.Distinct())
        {
            if ((await host.Catalog.XmpSync.GetAsync(id, ct))?.LastWrittenAt is not null)
            {
                await host.Catalog.XmpSync.MarkPendingAsync(id, null, ct);
                confirmed.Add(id);
            }
        }

        if (confirmed.Count == 0)
        {
            return true;
        }

        var summary = await Task.Run(() => host.CreatePublisher().WriteAsync(confirmed, host.Settings.PrudentMode, null, ct), ct);
        return summary.Failed.Count == 0;
    }
}
