// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Analysis.Culling;

/// <summary>
/// Phase 1 decision (spec §6, single photos, no bursts yet): a discard only on a high-reliability signal clearly past
/// its threshold (blank frame, blur over the whole image, both eyes of a large main face closed); signals near a
/// threshold or of medium reliability (exposure, blur only in the zone, eyes of other faces) give "to review";
/// otherwise kept. Prudent mode (D20) is applied when writing XMP, not here.
/// </summary>
public sealed class DecisionEngine : IDecisionEngine
{
    /// <summary>v2 (15 Oct 2026): exposure of the whole photo (many blown highlights, very dark) also gives "to review".</summary>
    public const string EngineVersion = "decision-v2";

    /// <summary>Tolerant exposure (motorsport) accepts this many times the clipping of the normal mode.</summary>
    public const double TolerantExposureFactor = 1.5;

    public string Version => EngineVersion;

    public DecisionProposal Decide(CullingSignals signals, SceneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.CullingEnabled)
        {
            return new DecisionProposal(CullingOutcome.Kept, []);
        }

        var t = profile.Thresholds;
        var discard = new List<string>();
        var review = new List<string>();

        if (signals.FrameHighlights >= t.BlankFrame)
        {
            discard.Add(Reasons.BlankWhite);
        }
        else if (signals.FrameShadows >= t.BlankFrame)
        {
            discard.Add(Reasons.BlankBlack);
        }

        if (profile.AbsoluteSharpness)
        {
            if (signals.ZoneSharpness < t.BlurDiscard && signals.FrameSharpness < t.BlurDiscard)
            {
                discard.Add(Reasons.BlurSevere);
            }
            else if (signals.ZoneSharpness < t.BlurReview)
            {
                review.Add(signals.Zone == SharpnessZoneKind.Tiles ? Reasons.BlurSoft : Reasons.BlurZone);
            }
        }

        var (left, right) = (signals.MainFaceLeftEye, signals.MainFaceRightEye);
        if (left == EyeState.Closed && right == EyeState.Closed)
        {
            (signals.MainFaceSize >= t.EyesDiscardFaceSize ? discard : review).Add(Reasons.EyesClosed);
        }
        else if (left == EyeState.Closed || right == EyeState.Closed)
        {
            review.Add(Reasons.EyeClosed);
        }

        if (signals.OtherFacesWithClosedEyes > 0)
        {
            review.Add(Reasons.EyesClosedOthers);
        }

        if (profile.Exposure != ExposureMode.Off && discard.Count == 0)
        {
            var factor = profile.Exposure == ExposureMode.Tolerant ? TolerantExposureFactor : 1.0;

            // In the subject zone (spec §6): without a subject, dark or bright areas are often intended.
            var zoneHighlights = signals.Zone != SharpnessZoneKind.Tiles && signals.ZoneHighlights > t.HighlightsReview * factor;
            if (zoneHighlights)
            {
                review.Add(Reasons.Highlights);
            }

            if (signals.Zone != SharpnessZoneKind.Tiles && signals.ZoneShadows > t.ShadowsReview * factor)
            {
                review.Add(Reasons.Shadows);
            }

            // On the whole photo too (15 Oct 2026): many blown highlights or a very dark photo are to review even when
            // the subject zone — a small AF area — is well exposed.
            if (!zoneHighlights && signals.FrameHighlights > t.FrameHighlightsReview * factor)
            {
                review.Add(Reasons.FrameHighlights);
            }

            if (signals.FrameBrightness < t.FrameBrightnessReview / factor)
            {
                review.Add(Reasons.FrameDark);
            }
        }

        var outcome = discard.Count > 0 ? CullingOutcome.Discard : review.Count > 0 ? CullingOutcome.ToReview : CullingOutcome.Kept;
        return new DecisionProposal(outcome, [.. discard, .. review]);
    }

    /// <summary>Stable reason keys (localized in the UI resources as <c>Reason_{key}</c> with dots removed).</summary>
    public static class Reasons
    {
        public const string BlankWhite = "exposure.blank.white";
        public const string BlankBlack = "exposure.blank.black";
        public const string BlurSevere = "blur.severe";
        public const string BlurSoft = "blur.soft";
        public const string BlurZone = "blur.zone";
        public const string EyesClosed = "eyes.closed";
        public const string EyeClosed = "eyes.closed.one";
        public const string EyesClosedOthers = "eyes.closed.others";
        public const string Highlights = "exposure.highlights";
        public const string Shadows = "exposure.shadows";
        public const string FrameHighlights = "exposure.frame.highlights";
        public const string FrameDark = "exposure.frame.dark";
    }
}
