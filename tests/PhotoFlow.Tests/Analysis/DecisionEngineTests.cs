// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Signals;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class DecisionEngineTests
{
    private static readonly CullingThresholds Thresholds = new(
        BlurDiscard: 0.2, BlurReview: 0.35, EyesClosed: 0.12, EyesOpen: 0.18, EyesMinFaceSize: 80, EyesDiscardFaceSize: 150,
        HighlightsReview: 0.25, ShadowsReview: 0.4, BlankFrame: 0.95);

    private static readonly DecisionEngine Engine = new();

    private static SceneProfile Profile(bool culling = true, bool absolute = true, ExposureMode exposure = ExposureMode.Normal) =>
        new("events", "Events", 1, true, culling, [SharpnessZoneKind.AfArea, SharpnessZoneKind.Faces], exposure, FaceMode.Recognize,
            ["person"], false, absolute, new ProfileWeights(35, 30, 15, 10, 10), Thresholds);

    private static CullingSignals Sharp() => new(
        SharpnessZoneKind.Faces, 0.6, 0.7, 120, 0.01, 0.01, 0.01, 0.01, 0.45, 1, 0.2, 1, 1, 200,
        EyeState.Open, EyeState.Open, 0.25, 0.26, 0, 0);

    [Fact]
    public void A_sharp_well_exposed_photo_with_open_eyes_is_kept()
    {
        Assert.Equal(new DecisionProposal(CullingOutcome.Kept, []), Engine.Decide(Sharp(), Profile()) with { Reasons = [] });
        Assert.Empty(Engine.Decide(Sharp(), Profile()).Reasons);
    }

    [Fact]
    public void Blur_over_the_whole_image_is_a_discard_blur_only_in_the_zone_is_to_review()
    {
        var everywhere = Engine.Decide(Sharp() with { ZoneSharpness = 0.1, FrameSharpness = 0.15 }, Profile());
        Assert.Equal(CullingOutcome.Discard, everywhere.Outcome);
        Assert.Contains(DecisionEngine.Reasons.BlurSevere, everywhere.Reasons);

        var zoneOnly = Engine.Decide(Sharp() with { ZoneSharpness = 0.1, FrameSharpness = 0.6 }, Profile());
        Assert.Equal(CullingOutcome.ToReview, zoneOnly.Outcome);
        Assert.Equal([DecisionEngine.Reasons.BlurZone], zoneOnly.Reasons);

        var nearThreshold = Engine.Decide(Sharp() with { ZoneSharpness = 0.3, FrameSharpness = 0.3 }, Profile());
        Assert.Equal(CullingOutcome.ToReview, nearThreshold.Outcome);
    }

    [Fact]
    public void Without_absolute_sharpness_blur_never_decides()
    {
        Assert.Equal(CullingOutcome.Kept, Engine.Decide(Sharp() with { ZoneSharpness = 0.05, FrameSharpness = 0.05 }, Profile(absolute: false)).Outcome);
    }

    [Fact]
    public void Closed_eyes_discard_only_on_a_large_main_face()
    {
        var closed = Sharp() with { MainFaceLeftEye = EyeState.Closed, MainFaceRightEye = EyeState.Closed };
        Assert.Equal(CullingOutcome.Discard, Engine.Decide(closed, Profile()).Outcome);
        Assert.Equal(CullingOutcome.ToReview, Engine.Decide(closed with { MainFaceSize = 120 }, Profile()).Outcome);
        Assert.Equal(CullingOutcome.ToReview, Engine.Decide(Sharp() with { MainFaceLeftEye = EyeState.Closed, MainFaceRightEye = EyeState.Unknown }, Profile()).Outcome);
        Assert.Equal(CullingOutcome.ToReview, Engine.Decide(Sharp() with { OtherFacesWithClosedEyes = 1 }, Profile()).Outcome);
        Assert.Equal(CullingOutcome.Kept, Engine.Decide(Sharp() with { MainFaceLeftEye = EyeState.Unknown, MainFaceRightEye = EyeState.Unknown }, Profile()).Outcome);
    }

    [Fact]
    public void Exposure_is_never_a_discard_except_a_blank_frame()
    {
        var clipped = Sharp() with { ZoneHighlights = 0.3 };
        Assert.Equal(CullingOutcome.ToReview, Engine.Decide(clipped, Profile()).Outcome);
        Assert.Equal(CullingOutcome.Kept, Engine.Decide(clipped, Profile(exposure: ExposureMode.Tolerant)).Outcome);
        Assert.Equal(CullingOutcome.Kept, Engine.Decide(clipped with { ZoneHighlights = 0.9 }, Profile(exposure: ExposureMode.Off)).Outcome);
        Assert.Equal(CullingOutcome.Kept, Engine.Decide(Sharp() with { Zone = SharpnessZoneKind.Tiles, ZoneShadows = 0.6, FrameShadows = 0.6 }, Profile()).Outcome);

        var black = Engine.Decide(Sharp() with { FrameShadows = 0.99, ZoneShadows = 0.99 }, Profile());
        Assert.Equal(CullingOutcome.Discard, black.Outcome);
        Assert.Equal([DecisionEngine.Reasons.BlankBlack], black.Reasons);
    }

    [Fact]
    public void Archive_profile_proposes_nothing()
    {
        Assert.Equal(CullingOutcome.Kept, Engine.Decide(Sharp() with { ZoneSharpness = 0, FrameSharpness = 0 }, Profile(culling: false)).Outcome);
    }

    [Fact]
    public void Signals_round_trip_through_the_stored_scores()
    {
        var signals = Sharp() with { SubjectArea = null, MainFaceLeftEyeRatio = null };
        Assert.Equal(signals, CullingScores.FromScores(CullingScores.ToScores(signals)));
    }
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class EyeClassifierTests
{
    private static readonly CullingThresholds Thresholds = new(0.2, 0.35, 0.12, 0.18, 80, 150, 0.25, 0.4, 0.95);
    private static readonly GrayImage Gray = new(400, 400, Enumerable.Repeat((byte)150, 400 * 400).ToArray());

    /// <summary>Frontal face 200 px wide: eyes at y 150, nose at (200, 200), mouth at y 250.</summary>
    private static DetectedFace Face(float eyeHeight, float noseX = 200, float noseY = 200, float rightEyeWidth = 40)
    {
        static EyeContour Eye(float cx, float width, float height) => new([
            new(cx - (width / 2), 150), new(cx - (width / 4), 150 + (height / 2)), new(cx, 150 + (height / 2)), new(cx + (width / 4), 150 + (height / 2)),
            new(cx + (width / 2), 150), new(cx + (width / 4), 150 - (height / 2)), new(cx, 150 - (height / 2)), new(cx - (width / 4), 150 - (height / 2)),
        ]);
        return new DetectedFace(new PixelBox(100, 80, 300, 320), 0.9f,
            [new(160, 150), new(240, 150), new(noseX, noseY), new(170, 250), new(230, 250)],
            null, Eye(160, 40, eyeHeight), Eye(240, rightEyeWidth, eyeHeight * rightEyeWidth / 40));
    }

    [Theory]
    [InlineData(12f, EyeState.Open)]
    [InlineData(2f, EyeState.Closed)]
    [InlineData(6f, EyeState.Unknown)]
    public void Frontal_eyes_are_judged_from_the_ratio(float height, EyeState expected)
    {
        var judgement = EyeClassifier.Judge(Face(height), 240, Gray, 1, Thresholds);
        Assert.Equal(expected, judgement.Left);
        Assert.Equal(expected, judgement.Right);
    }

    [Fact]
    public void Small_turned_or_downward_faces_are_unknown()
    {
        Assert.Equal(EyeUnknownReason.SmallFace, EyeClassifier.Judge(Face(2), 60, Gray, 1, Thresholds).Reason);
        Assert.Equal(EyeUnknownReason.Turned, EyeClassifier.Judge(Face(2, noseX: 245), 240, Gray, 1, Thresholds).Reason);
        Assert.Equal(EyeUnknownReason.Turned, EyeClassifier.Judge(Face(2, rightEyeWidth: 12), 240, Gray, 1, Thresholds).Reason);
        Assert.Equal(EyeUnknownReason.LookingDown, EyeClassifier.Judge(Face(2, noseY: 235), 240, Gray, 1, Thresholds).Reason);
        Assert.Equal(EyeUnknownReason.NoContours, EyeClassifier.Judge(Face(2) with { LeftEye = null }, 240, Gray, 1, Thresholds).Reason);
    }

    [Fact]
    public void Dark_flat_eye_regions_are_covered()
    {
        var pixels = Enumerable.Repeat((byte)170, 400 * 400).ToArray();
        for (var y = 120; y < 180; y++)
        {
            for (var x = 120; x < 280; x++)
            {
                pixels[(y * 400) + x] = 30;
            }
        }

        Assert.Equal(EyeUnknownReason.Covered, EyeClassifier.Judge(Face(2), 240, new GrayImage(400, 400, pixels), 1, Thresholds).Reason);
    }
}
