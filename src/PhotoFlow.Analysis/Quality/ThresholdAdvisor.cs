// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Analysis.Quality;

/// <summary>
/// A blur-discard threshold that the user's corrections would have preferred (board 5e), with what it changes.
/// </summary>
/// <param name="Corrections">Corrections of the profile with sharpness signals.</param>
/// <param name="AvoidedWrong">Discards the user kept that the new threshold would not have discarded.</param>
/// <param name="AddedCaught">Photos the user discarded that the new threshold would have discarded too.</param>
/// <param name="NewWrong">Kept photos the new threshold would discard (raising the threshold).</param>
/// <param name="LostCaught">Discards the user confirmed that the new threshold would miss (lowering it).</param>
public sealed record ThresholdSuggestion(double Current, double Value, int Corrections, int AvoidedWrong, int AddedCaught, int NewWrong, int LostCaught);

/// <summary>
/// Learning from the corrections (spec §6): after enough corrections on a profile, the application suggests moving the
/// blur-discard threshold. It never changes anything by itself and trains nothing. A photo counts as discarded for blur
/// at threshold T when both zone and whole-frame sharpness are below T (the rule of <see cref="DecisionEngine"/>); a
/// wrong discard weighs three times a missed one, as the targets do (wrong discards &lt; 2 %, intercepted &gt; 70 %).
/// </summary>
public static class ThresholdAdvisor
{
    public const int MinCorrections = 30;
    public const double WrongDiscardWeight = 3;
    private const double Step = 0.01;

    public static ThresholdSuggestion? SuggestBlurDiscard(SceneProfile profile, IReadOnlyList<Correction> corrections, int minCorrections = MinCorrections)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(corrections);
        if (!profile.AbsoluteSharpness)
        {
            return null;
        }

        // Kept: the user kept a photo the system discarded; Discarded: the user discarded a photo the system kept.
        var samples = new List<(double Sharpness, bool UserDiscards)>();
        foreach (var correction in corrections.Where(c => c.ProfileId == profile.Id))
        {
            CullingSignals signals;
            try
            {
                signals = CullingScores.FromScores(correction.Signals);
            }
            catch (KeyNotFoundException)
            {
                continue; // signals of an older analyzer version
            }

            var sharpness = Math.Max(signals.ZoneSharpness, signals.FrameSharpness);
            if (double.IsNaN(sharpness))
            {
                continue;
            }

            var userDiscards = correction.UserOutcome == CullingOutcome.Discard;
            var systemDiscarded = correction.AutomaticOutcome == CullingOutcome.Discard;
            if (userDiscards != systemDiscarded)
            {
                samples.Add((sharpness, userDiscards));
            }
        }

        if (samples.Count < minCorrections)
        {
            return null;
        }

        var current = profile.Thresholds.BlurDiscard;
        double Cost(double t) =>
            (WrongDiscardWeight * samples.Count(s => !s.UserDiscards && s.Sharpness < t)) - samples.Count(s => s.UserDiscards && s.Sharpness < t);

        var best = current;
        var bestCost = Cost(current);
        for (var t = Step; t <= profile.Thresholds.BlurReview + 1e-9; t += Step)
        {
            var value = Math.Round(t, 2);
            var cost = Cost(value);
            if (cost < bestCost - 1e-9 || (Math.Abs(cost - bestCost) < 1e-9 && Math.Abs(value - current) < Math.Abs(best - current)))
            {
                (best, bestCost) = (value, cost);
            }
        }

        if (Math.Abs(best - current) < 1e-9)
        {
            return null;
        }

        int Count(bool userDiscards, double t) => samples.Count(s => s.UserDiscards == userDiscards && s.Sharpness < t);
        var wrongBefore = Count(false, current);
        var wrongAfter = Count(false, best);
        var caughtBefore = Count(true, current);
        var caughtAfter = Count(true, best);
        return new ThresholdSuggestion(current, best, samples.Count,
            Math.Max(0, wrongBefore - wrongAfter), Math.Max(0, caughtAfter - caughtBefore),
            Math.Max(0, wrongAfter - wrongBefore), Math.Max(0, caughtBefore - caughtAfter));
    }
}
