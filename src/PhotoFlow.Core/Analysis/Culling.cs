// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Core.Analysis;

/// <summary>Scene profile (spec §6): where sharpness is measured, which signals count, thresholds and weights.</summary>
/// <param name="Id">Stable key; built-in profiles take their display name from the UI resources (<c>Profile_{Id}</c>).</param>
/// <param name="Name">Name of a custom profile, English fallback for built-ins.</param>
/// <param name="Version">Incremented at every change of a built-in profile (results record it).</param>
/// <param name="CullingEnabled">False for the Archive profile: signals are measured, no outcome is proposed.</param>
/// <param name="SharpnessZones">Preference order; the first zone available in the photo is used, <see cref="SharpnessZoneKind.Tiles"/> otherwise.</param>
/// <param name="SubjectClasses">COCO labels that count as subject (D-FINE).</param>
/// <param name="AbsoluteSharpness">False for night/aurora: no absolute blur threshold, only relative within a burst (phase 2).</param>
public sealed record SceneProfile(
    string Id,
    string Name,
    int Version,
    bool IsBuiltIn,
    bool CullingEnabled,
    IReadOnlyList<SharpnessZoneKind> SharpnessZones,
    ExposureMode Exposure,
    FaceMode Faces,
    IReadOnlyList<string> SubjectClasses,
    bool Ocr,
    bool AbsoluteSharpness,
    ProfileWeights Weights,
    CullingThresholds Thresholds)
{
    /// <summary>Burst and ranking parameters (P17); the defaults when the profile file has none.</summary>
    public GroupingParameters Grouping { get; init; } = GroupingParameters.Default;

    /// <summary>The profile a custom profile was duplicated from (board 5e: its values are the grey marks).</summary>
    public string? BasedOn { get; init; }
}

/// <summary>
/// Burst parameters exposed per profile (P17): maximum gap between two shots of a burst, minimum embedding similarity
/// between consecutive shots, and the score margin within which the first two of a burst are a tie (both to review).
/// </summary>
public sealed record GroupingParameters(double MaxBurstGapSeconds, double MinSimilarity, double TieMargin)
{
    public static GroupingParameters Default { get; } = new(2.0, 0.90, 0.02);
}

/// <summary>Composite score weights (spec §6), percentages summing to 100.</summary>
public sealed record ProfileWeights(int Sharpness, int Eyes, int Exposure, int Subject, int Aesthetic);

public enum FaceMode
{
    Off,
    DetectOnly,
    Recognize,
}

public enum SharpnessZoneKind
{
    /// <summary>AF area written by the camera.</summary>
    AfArea,

    /// <summary>Both eyes of the main face.</summary>
    Eyes,

    /// <summary>Relevant faces (the sharpest counts).</summary>
    Faces,

    /// <summary>Largest subject of the profile classes.</summary>
    Subject,

    /// <summary>No specific zone: the whole frame (detail: sharpest tile of a 4×4 grid).</summary>
    Tiles,
}

public enum ExposureMode
{
    Normal,
    Tolerant,
    Off,
}

/// <summary>
/// Decision thresholds (spec §6, P17), initial values to be calibrated on the labelled set (spec §14). Sharpness is
/// 1 − blur effect measured at <see cref="CullingSignals.ReferenceLongSide"/>; eye ratios are eye aspect ratios.
/// </summary>
/// <param name="BlurDiscard">Zone and whole-frame sharpness both below this: certain blur (discard).</param>
/// <param name="BlurReview">Zone sharpness below this: to review.</param>
/// <param name="EyesClosed">Eye ratio below this: closed.</param>
/// <param name="EyesOpen">Eye ratio above this: open; in between: uncertain.</param>
/// <param name="EyesMinFaceSize">Minimum face size (longest side, reference pixels) to judge the eyes.</param>
/// <param name="EyesDiscardFaceSize">Minimum main-face size for a closed-eyes discard (smaller faces: to review).</param>
/// <param name="HighlightsReview">Fraction of clipped highlights in the zone above which the photo is to review.</param>
/// <param name="ShadowsReview">Fraction of crushed shadows in the zone above which the photo is to review.</param>
/// <param name="BlankFrame">Fraction of the whole frame clipped (or crushed) above which the frame is blank: discard.</param>
/// <param name="FrameHighlightsReview">Fraction of the whole frame clipped above which the photo is to review, whatever the zone.</param>
/// <param name="FrameBrightnessReview">Mean brightness of the whole frame (0-1) below which the photo is to review (underexposed).</param>
public sealed record CullingThresholds(
    double BlurDiscard,
    double BlurReview,
    double EyesClosed,
    double EyesOpen,
    double EyesMinFaceSize,
    double EyesDiscardFaceSize,
    double HighlightsReview,
    double ShadowsReview,
    double BlankFrame,
    double FrameHighlightsReview = CullingThresholds.DefaultFrameHighlightsReview,
    double FrameBrightnessReview = CullingThresholds.DefaultFrameBrightnessReview)
{
    /// <summary>Initial values (spec §6, 15 Oct 2026), also for profiles saved before they existed.</summary>
    public const double DefaultFrameHighlightsReview = 0.15;

    public const double DefaultFrameBrightnessReview = 0.20;
}

/// <summary>
/// Measurements of one photo for culling, flat so that they can be stored (<c>AnalyzerResults</c>) and decided again
/// without a new analysis. Null when the signal is not available.
/// <para>Sharpness is 1 − blur effect (Crété-Roffet 2007): 1 sharp, 0 blurred, little dependent on contrast and content;
/// <see cref="ZoneSharpness"/> on the profile zone (the whole frame for <see cref="SharpnessZoneKind.Tiles"/>),
/// <see cref="FrameSharpness"/> on the whole frame. <see cref="ZoneDetail"/> is the variance of the Laplacian (spec §6),
/// content dependent, kept for the relative ranking inside a burst (phase 2).</para>
/// </summary>
public sealed record CullingSignals(
    SharpnessZoneKind Zone,
    double ZoneSharpness,
    double FrameSharpness,
    double ZoneDetail,
    double ZoneHighlights,
    double ZoneShadows,
    double FrameHighlights,
    double FrameShadows,
    double FrameBrightness,
    int SubjectCount,
    double? SubjectArea,
    int FaceCount,
    int RelevantFaceCount,
    double? MainFaceSize,
    EyeState MainFaceLeftEye,
    EyeState MainFaceRightEye,
    double? MainFaceLeftEyeRatio,
    double? MainFaceRightEyeRatio,
    int OtherFacesWithClosedEyes,
    int OtherFacesWithUncertainEyes)
{
    /// <summary>Long side of the image the signals are measured on (thresholds depend on it).</summary>
    public const int ReferenceLongSide = 2048;
}

/// <summary>Eye judgement of a face, with the reason when it cannot be judged.</summary>
public sealed record EyeJudgement(EyeState Left, EyeState Right, double? LeftRatio, double? RightRatio, EyeUnknownReason? Reason)
{
    public bool BothClosed => Left == EyeState.Closed && Right == EyeState.Closed;

    public bool AnyClosed => Left == EyeState.Closed || Right == EyeState.Closed;
}

public enum EyeUnknownReason
{
    /// <summary>The face model gives no eye contours (YuNet, A5) or the face was too small for landmarks.</summary>
    NoContours,
    SmallFace,

    /// <summary>Head in profile: one eye foreshortened or hidden.</summary>
    Turned,

    /// <summary>Head tilted down: open eyes look closed.</summary>
    LookingDown,

    /// <summary>Dark, flat eye region (sunglasses).</summary>
    Covered,

    /// <summary>Ratio between the closed and open thresholds.</summary>
    Uncertain,
}

/// <summary>Proposed outcome from the signals and the profile (phase 1: discard, to review, kept).</summary>
public interface IDecisionEngine
{
    string Version { get; }

    DecisionProposal Decide(CullingSignals signals, SceneProfile profile);
}

/// <summary><paramref name="Reasons"/> are stable keys (localized in the UI), e.g. <c>blur.severe</c>.</summary>
public sealed record DecisionProposal(CullingOutcome Outcome, IReadOnlyList<string> Reasons);
