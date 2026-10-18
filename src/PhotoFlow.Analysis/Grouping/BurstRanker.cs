// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Analysis.Grouping;

/// <summary>A burst photo: its signals and its decision as a single shot (technical discards, doubts).</summary>
public sealed record BurstMember(long AssetId, CullingSignals Signals, DecisionProposal Single);

/// <summary>
/// Ranking inside a burst (spec §6, Decision 2-3): composite score with the weights of the profile, each component
/// normalized within the burst, then the first valid photo becomes "best", the other valid ones "similar"; when the
/// first two are within the tie margin (P17, 0.02) both go "to review". Discards and doubts of the single-shot decision
/// stay as they are. Components: sharpness = Laplacian variance on the zone relative to the sharpest of the burst
/// (§6: relative sharpness in the burst); eyes of the main face (open 1, unknown ½, closed 0); exposure = 1 − clipped
/// fraction of the zone; subject = area relative to the largest. Components no member has (no faces, no subject,
/// aesthetic before its model) are left out and the weights renormalized.
/// </summary>
public static class BurstRanker
{
    public const string Version = "burst-v1";

    public static class Reasons
    {
        public const string Best = "burst.best";
        public const string Similar = "burst.similar";
        public const string Tie = "burst.tie";
    }

    public static IReadOnlyDictionary<long, double> Scores(IReadOnlyList<BurstMember> members, ProfileWeights weights)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(weights);
        var maxDetail = members.Max(m => m.Signals.ZoneDetail);
        var maxSubject = members.Max(m => m.Signals.SubjectArea ?? 0);
        var anyFace = members.Any(m => m.Signals.MainFaceSize is not null);
        var components = new List<(double Weight, Func<CullingSignals, double> Value)>
        {
            (weights.Sharpness, s => maxDetail > 0 ? s.ZoneDetail / maxDetail : 1),
            (weights.Exposure, s => 1 - Math.Min(1, s.ZoneHighlights + s.ZoneShadows)),
        };
        if (anyFace)
        {
            components.Add((weights.Eyes, s => s.MainFaceSize is null ? 0.5 : (Eye(s.MainFaceLeftEye) + Eye(s.MainFaceRightEye)) / 2));
        }

        if (maxSubject > 0)
        {
            components.Add((weights.Subject, s => (s.SubjectArea ?? 0) / maxSubject));
        }

        var total = components.Sum(c => c.Weight);
        return members.ToDictionary(
            m => m.AssetId,
            m => total > 0 ? components.Sum(c => c.Weight * c.Value(m.Signals)) / total : 0);
    }

    /// <summary>Outcome of every member after the ranking.</summary>
    public static IReadOnlyDictionary<long, DecisionProposal> Decide(IReadOnlyList<BurstMember> members, IReadOnlyDictionary<long, double> scores, double tieMargin)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(scores);
        var result = members.ToDictionary(m => m.AssetId, m => m.Single);
        var valid = members.Where(m => m.Single.Outcome == CullingOutcome.Kept)
            .OrderByDescending(m => scores[m.AssetId])
            .ThenBy(m => m.AssetId)
            .ToList();
        if (valid.Count == 0)
        {
            return result;
        }

        var tie = valid.Count >= 2 && scores[valid[0].AssetId] - scores[valid[1].AssetId] <= tieMargin;
        for (var i = 0; i < valid.Count; i++)
        {
            result[valid[i].AssetId] = i switch
            {
                0 when tie => new DecisionProposal(CullingOutcome.ToReview, [Reasons.Tie]),
                0 => new DecisionProposal(CullingOutcome.Best, [Reasons.Best]),
                1 when tie => new DecisionProposal(CullingOutcome.ToReview, [Reasons.Tie]),
                _ => new DecisionProposal(CullingOutcome.Similar, [Reasons.Similar]),
            };
        }

        return result;
    }

    private static double Eye(EyeState state) => state switch
    {
        EyeState.Open => 1,
        EyeState.Closed => 0,
        _ => 0.5,
    };
}
