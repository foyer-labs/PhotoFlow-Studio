// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.Tagging.Ocr;

/// <summary>
/// Race numbers of a job (spec §8, batch stage after the groupings): the readings of each vehicle are matched with the
/// entry list of the job (<see cref="NumberMatcher"/>); in a burst the main vehicle of the doubtful photos takes the
/// number most photos agree on; the numbers and their tags are saved. The user's numbers are kept (I5). Run again when
/// the entry list of the job changes.
/// </summary>
public sealed class NumbersStage(ICatalog catalog) : IBatchStage
{
    public PipelineStage Stage => PipelineStage.TaggingAndOcr;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var assets = (await catalog.Jobs.GetItemsAsync(job.JobId, ct).ConfigureAwait(false))
            .Where(i => i.State == JobItemState.Done && i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct().ToList();
        var listId = await catalog.Motorsport.GetJobListAsync(job.JobId, ct).ConfigureAwait(false);
        var entries = listId is { } id ? await catalog.Motorsport.GetEntriesAsync(id, ct).ConfigureAwait(false) : null;
        var list = entries?.Select(e => e.Number).ToHashSet(StringComparer.Ordinal);

        // Readings → numbers per vehicle.
        var results = new Dictionary<long, List<(int Vehicle, Core.Analysis.BoundingBox Box, NumberMatcher.Result Result)>>();
        foreach (var assetId in assets)
        {
            var readings = await catalog.Motorsport.GetReadingsAsync(assetId, ct).ConfigureAwait(false);
            if (readings.Count == 0)
            {
                continue;
            }

            results[assetId] = [.. readings.GroupBy(r => r.Vehicle).Select(g => (g.Key, g.First().VehicleBox,
                NumberMatcher.Choose(g.Select(r => (r.Text, r.Confidence)).ToList(), list)))];
        }

        // Bursts: the main vehicle (the largest, index 0) of the doubtful photos takes the majority.
        var propagated = new HashSet<long>();
        foreach (var (group, members) in await catalog.Groups.ListAsync(job.JobId, ct).ConfigureAwait(false))
        {
            if (group.Type != GroupType.Burst)
            {
                continue;
            }

            var main = members.Select(m => results.TryGetValue(m.AssetId, out var r) ? r.FirstOrDefault(v => v.Vehicle == 0) : default)
                .Where(v => v.Result is not null).Select(v => v.Result).ToList();
            if (NumberMatcher.Majority(main) is not { } number)
            {
                continue;
            }

            foreach (var member in members.Where(m => results.ContainsKey(m.AssetId)))
            {
                var vehicles = results[member.AssetId];
                var index = vehicles.FindIndex(v => v.Vehicle == 0);
                if (index >= 0 && NumberMatcher.CanTake(vehicles[index].Result, number))
                {
                    vehicles[index] = (0, vehicles[index].Box, new NumberMatcher.Result(NumberState.Certain, number, vehicles[index].Result.Candidates));
                    propagated.Add(member.AssetId);
                }
            }
        }

        var done = 0;
        foreach (var (assetId, vehicles) in results)
        {
            var numbers = vehicles.Select(v => new VehicleNumber(assetId, v.Vehicle, v.Box, v.Result.Number, v.Result.State,
                propagated.Contains(assetId) && v.Vehicle == 0 ? NumberOrigin.Propagated : NumberOrigin.Automatic,
                v.Result.Candidates)).ToList();
            await catalog.Motorsport.ReplaceAutomaticNumbersAsync(assetId, numbers, CancellationToken.None).ConfigureAwait(false);
            await MotorsportTags.ApplyAsync(catalog, assetId, entries, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new StageProgress(Stage, ++done, results.Count, null));
        }
    }
}
