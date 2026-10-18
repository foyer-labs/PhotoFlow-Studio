// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PhotoFlow.Analysis.Signals;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;

namespace PhotoFlow.Analysis.Culling;

/// <summary>Face settings used by the culling analysis (spec §7 <c>faces</c>: <c>detectionConfidence</c>, <c>minFaceSizePx</c>).</summary>
/// <param name="MinFaceSize">Minimum face size (longest side) at the reference scale.</param>
/// <param name="RelevantFaceFraction">A face is relevant when at least this fraction of the largest face (spec §6: 40%).</param>
public sealed record CullingAnalyzerOptions(float FaceConfidence = 0.6f, double MinFaceSize = 48, double RelevantFaceFraction = 0.4, float SubjectConfidence = 0.5f, double MinSubjectArea = 0.01);

/// <summary>A face with its normalized box, relevance and eye judgement.</summary>
public sealed record AnalyzedFace(DetectedFace Face, BoundingBox Box, double ReferenceSize, bool Relevant, EyeJudgement Eyes);

/// <summary>Full result: the flat signals plus the details kept in the catalog (faces) and shown in the review.</summary>
public sealed record CullingAnalysis(CullingSignals Signals, BoundingBox? ZoneBox, IReadOnlyList<AnalyzedFace> Faces, IReadOnlyList<Detection> Subjects, string ModelVersion);

/// <summary>
/// Per-photo culling signals (spec §6): sharpness in the profile zone (AF area → eyes → faces → subject → tiles),
/// exposure, subjects (object detector), faces and eyes (face detector). Signals are measured on the oriented preview
/// reduced to <see cref="CullingSignals.ReferenceLongSide"/>; detectors receive the full preview.
/// </summary>
public sealed class CullingAnalyzer : IPhotoAnalyzer
{
    public const string AnalyzerId = "culling-v1";
    private const int TileGrid = 4;
    private const double MinZoneSize = 32;
    public const int MinBlurScaleSize = 32;

    private readonly IImageOperations _imageOperations;
    private readonly IObjectDetector? _objects;
    private readonly IFaceDetector? _faces;
    private readonly CullingAnalyzerOptions _options;

    public CullingAnalyzer(IImageOperations imageOperations, IObjectDetector? objects, IFaceDetector? faces, CullingAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(imageOperations);
        _imageOperations = imageOperations;
        _objects = objects;
        _faces = faces;
        _options = options ?? new CullingAnalyzerOptions();
    }

    public string Id => AnalyzerId;

    public string ModelVersion =>
        string.Join('+', new[] { AnalyzerId, _objects?.ModelVersion, _faces?.ModelVersion }.Where(v => v is not null));

    public async Task<AnalyzerResult> AnalyzeAsync(PhotoContext photo, SceneProfile profile, CancellationToken ct)
    {
        var analysis = await AnalyzeDetailedAsync(photo, profile, ct).ConfigureAwait(false);
        return new AnalyzerResult(Id, analysis.ModelVersion, CullingScores.ToScores(analysis.Signals));
    }

    public async Task<CullingAnalysis> AnalyzeDetailedAsync(PhotoContext photo, SceneProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(profile);
        var image = photo.Preview.Image;
        var longSide = Math.Max(image.Width, image.Height);
        var scale = Math.Min(1.0, (double)CullingSignals.ReferenceLongSide / longSide);
        var reference = scale < 1.0
            ? _imageOperations.Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)), ResampleMode.Bilinear)
            : image;
        var referenceFactor = (double)CullingSignals.ReferenceLongSide / longSide;

        // CPU signals overlap with the detectors.
        var signalsTask = Task.Run(() =>
        {
            var gray = ImageSignals.ToGray(reference);
            return (Gray: gray, Blurred: ImageSignals.GaussianBlur3(gray));
        }, ct);

        var subjects = new List<Detection>();
        if (_objects is not null && profile.SubjectClasses.Count > 0)
        {
            var detections = await _objects.DetectAsync(image, ct).ConfigureAwait(false);
            subjects.AddRange(detections
                .Where(d => d.Score >= _options.SubjectConfidence && d.Box.Area >= _options.MinSubjectArea && profile.SubjectClasses.Contains(d.Label))
                .OrderByDescending(d => d.Box.Area));
        }

        var detected = _faces is not null && profile.Faces != FaceMode.Off
            ? (await _faces.DetectAsync(image, ct).ConfigureAwait(false)).Where(f => f.Score >= _options.FaceConfidence).ToList()
            : [];

        var (gray, blurred) = await signalsTask.ConfigureAwait(false);
        var faces = AnalyzeFaces(detected, image, referenceFactor, gray, scale, profile.Thresholds);

        // Sharpness zone: first available in the profile order, the whole frame otherwise.
        var frameSharpness = 1 - MultiScaleBlur(gray, PixelRect.Full(gray.Width, gray.Height));
        var zone = SharpnessZoneKind.Tiles;
        var zoneSharpness = frameSharpness;
        var zoneDetail = TilesDetail(blurred);
        BoundingBox? zoneBox = null;
        foreach (var kind in profile.SharpnessZones)
        {
            var measured = MeasureZone(kind, photo, faces, subjects, gray, blurred, MultiScaleBlur);
            if (measured is { } m)
            {
                (zone, zoneSharpness, zoneDetail, zoneBox) = (kind, m.Sharpness, m.Detail, m.Box);
                break;
            }
        }

        var frame = ImageSignals.Exposure(reference, gray, PixelRect.Full(reference.Width, reference.Height));
        var zoneExposure = zoneBox is { } zb ? ImageSignals.Exposure(reference, gray, PixelRect.FromBox(zb, reference.Width, reference.Height)) : frame;

        var relevant = faces.Where(f => f.Relevant).ToList();
        var main = relevant.FirstOrDefault();
        var others = relevant.Skip(1).ToList();
        var signals = new CullingSignals(
            zone,
            zoneSharpness,
            frameSharpness,
            zoneDetail,
            zoneExposure.HighlightClip,
            zoneExposure.ShadowClip,
            frame.HighlightClip,
            frame.ShadowClip,
            frame.MeanBrightness,
            subjects.Count,
            subjects.Count > 0 ? subjects[0].Box.Area : null,
            faces.Count,
            relevant.Count,
            main?.ReferenceSize,
            main?.Eyes.Left ?? EyeState.Unknown,
            main?.Eyes.Right ?? EyeState.Unknown,
            main?.Eyes.LeftRatio,
            main?.Eyes.RightRatio,
            others.Count(f => f.Eyes.AnyClosed),
            others.Count(f => f.Eyes.Reason == EyeUnknownReason.Uncertain));

        return new CullingAnalysis(signals, zoneBox, faces, subjects, ModelVersion);
    }

    /// <summary>Faces sorted by size; relevant = large enough and at least the relevant fraction of the largest (spec §6).</summary>
    private List<AnalyzedFace> AnalyzeFaces(List<DetectedFace> detected, RgbImage image, double referenceFactor, GrayImage gray, double grayScale, CullingThresholds thresholds)
    {
        var sized = detected
            .Select(f => (Face: f, Size: Math.Max(f.Box.Width, f.Box.Height) * referenceFactor))
            .OrderByDescending(f => f.Size)
            .ToList();
        var largest = sized.Count > 0 ? sized[0].Size : 0;
        return sized.Select(f =>
        {
            var relevant = f.Size >= _options.MinFaceSize && f.Size >= largest * _options.RelevantFaceFraction;
            var eyes = relevant
                ? EyeClassifier.Judge(f.Face, f.Size, gray, grayScale, thresholds)
                : new EyeJudgement(EyeState.Unknown, EyeState.Unknown, null, null, EyeUnknownReason.SmallFace);
            return new AnalyzedFace(f.Face, f.Face.Box.ToNormalized(image.Width, image.Height), f.Size, relevant, eyes);
        }).ToList();
    }

    private static (double Sharpness, double Detail, BoundingBox Box)? MeasureZone(
        SharpnessZoneKind kind, PhotoContext photo, List<AnalyzedFace> faces, List<Detection> subjects, GrayImage gray, GrayImage blurred,
        Func<GrayImage, PixelRect, double> blur)
    {
        (double, double, BoundingBox) Measure(BoundingBox box)
        {
            var rect = PixelRect.FromBox(box, gray.Width, gray.Height);
            return (1 - blur(gray, rect), ImageSignals.LaplacianVariance(blurred, rect), box);
        }

        switch (kind)
        {
            case SharpnessZoneKind.AfArea when photo.Exif?.AfArea is { } af:
                return Measure(Expand(af, MinZoneSize / CullingSignals.ReferenceLongSide));

            case SharpnessZoneKind.Eyes when faces.FirstOrDefault(f => f.Relevant) is { Face.LeftEye: { } left, Face.RightEye: { } right } main:
                var points = left.Points.Concat(right.Points).ToList();
                var pad = (float)(Math.Max(left.Width, right.Width) * 0.5);
                var image = photo.Preview.Image;
                var eyes = new PixelBox(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad, points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad)
                    .ToNormalized(image.Width, image.Height);
                if (Math.Max(eyes.Width * blurred.Width, eyes.Height * blurred.Height) < MinZoneSize)
                {
                    return null;
                }

                return Measure(Clamp(eyes));

            case SharpnessZoneKind.Faces when faces.Any(f => f.Relevant):
                // The sharpest relevant face: focus on any of them is a valid photo. Inner part, without background.
                return faces.Where(f => f.Relevant)
                    .Select(f => Measure(Inner(f.Box, 0.15f)))
                    .MaxBy(z => z.Item1);

            case SharpnessZoneKind.Subject when subjects.Count > 0:
                return Measure(subjects[0].Box);

            default:
                return null;
        }
    }

    /// <summary>
    /// Worst blur effect of the zone at full, half and quarter size (Pillow bilinear), on the scales where the crop keeps
    /// at least <see cref="MinBlurScaleSize"/> pixels per side. A blur much wider than the 11-pixel window of the measure
    /// saturates it at full size; at reduced size the same blur falls back into its range (lab blur_metrics.py).
    /// </summary>
    public double MultiScaleBlur(GrayImage gray, PixelRect zone)
    {
        ArgumentNullException.ThrowIfNull(gray);
        var crop = ImageSignals.Crop(gray, zone);
        var worst = ImageSignals.BlurEffect(crop, PixelRect.Full(crop.Width, crop.Height));
        foreach (var factor in new[] { 2, 4 })
        {
            int w = (int)Math.Round((double)crop.Width / factor), h = (int)Math.Round((double)crop.Height / factor);
            if (Math.Min(w, h) < MinBlurScaleSize)
            {
                break;
            }

            var rgb = new byte[crop.Pixels.Length * 3];
            for (var i = 0; i < crop.Pixels.Length; i++)
            {
                rgb[i * 3] = rgb[(i * 3) + 1] = rgb[(i * 3) + 2] = crop.Pixels[i];
            }

            var small = _imageOperations.Resize(new RgbImage(crop.Width, crop.Height, rgb), w, h, ResampleMode.Bilinear).Pixels.Span;
            var reduced = new byte[w * h];
            for (var i = 0; i < reduced.Length; i++)
            {
                reduced[i] = small[i * 3];
            }

            worst = Math.Max(worst, ImageSignals.BlurEffect(new GrayImage(w, h, reduced), PixelRect.Full(w, h)));
        }

        return worst;
    }

    /// <summary>Detail (Laplacian variance) of the sharpest tile of a 4×4 grid.</summary>
    private static double TilesDetail(GrayImage blurred)
    {
        var best = 0.0;
        for (var ty = 0; ty < TileGrid; ty++)
        {
            for (var tx = 0; tx < TileGrid; tx++)
            {
                var x1 = blurred.Width * tx / TileGrid;
                var y1 = blurred.Height * ty / TileGrid;
                var x2 = blurred.Width * (tx + 1) / TileGrid;
                var y2 = blurred.Height * (ty + 1) / TileGrid;
                best = Math.Max(best, ImageSignals.LaplacianVariance(blurred, new PixelRect(x1, y1, x2 - x1, y2 - y1)));
            }
        }

        return best;
    }

    private static BoundingBox Inner(BoundingBox box, float margin) => Clamp(new BoundingBox(
        box.X1 + (box.Width * margin), box.Y1 + (box.Height * margin), box.X2 - (box.Width * margin), box.Y2 - (box.Height * margin)));

    /// <summary>Grows a box around its center to at least <paramref name="minSize"/> (normalized) per side.</summary>
    private static BoundingBox Expand(BoundingBox box, double minSize)
    {
        var half = (float)(minSize / 2);
        var cx = (box.X1 + box.X2) / 2;
        var cy = (box.Y1 + box.Y2) / 2;
        return Clamp(new BoundingBox(
            Math.Min(box.X1, cx - half), Math.Min(box.Y1, cy - half), Math.Max(box.X2, cx + half), Math.Max(box.Y2, cy + half)));
    }

    private static BoundingBox Clamp(BoundingBox b) =>
        new(Math.Clamp(b.X1, 0, 1), Math.Clamp(b.Y1, 0, 1), Math.Clamp(b.X2, 0, 1), Math.Clamp(b.Y2, 0, 1));
}

/// <summary>Flat score keys of <see cref="CullingSignals"/> for <c>AnalyzerResults</c>.</summary>
public static class CullingScores
{
    /// <summary>
    /// The parameters of a profile that change the measured signals (zones, subjects, faces on/off, eye thresholds):
    /// two profiles with the same key give the same signals, so a photo moving between them needs no new analysis,
    /// only a new decision.
    /// </summary>
    public static string MeasurementKey(SceneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var t = profile.Thresholds;
        return string.Create(CultureInfo.InvariantCulture,
            $"{string.Join(',', profile.SharpnessZones)}|{string.Join(',', profile.SubjectClasses)}|{profile.Faces != FaceMode.Off}|{t.EyesClosed}|{t.EyesOpen}|{t.EyesMinFaceSize}");
    }

    public static IReadOnlyDictionary<string, double> ToScores(CullingSignals s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var scores = new Dictionary<string, double>
        {
            ["zone"] = (int)s.Zone,
            ["sharpness.zone"] = s.ZoneSharpness,
            ["sharpness.frame"] = s.FrameSharpness,
            ["detail.zone"] = s.ZoneDetail,
            ["exposure.zone.highlights"] = s.ZoneHighlights,
            ["exposure.zone.shadows"] = s.ZoneShadows,
            ["exposure.frame.highlights"] = s.FrameHighlights,
            ["exposure.frame.shadows"] = s.FrameShadows,
            ["exposure.frame.brightness"] = s.FrameBrightness,
            ["subjects"] = s.SubjectCount,
            ["faces"] = s.FaceCount,
            ["faces.relevant"] = s.RelevantFaceCount,
            ["eyes.main.left"] = (int)s.MainFaceLeftEye,
            ["eyes.main.right"] = (int)s.MainFaceRightEye,
            ["eyes.others.closed"] = s.OtherFacesWithClosedEyes,
            ["eyes.others.uncertain"] = s.OtherFacesWithUncertainEyes,
        };
        Optional(scores, "subject.area", s.SubjectArea);
        Optional(scores, "faces.main.size", s.MainFaceSize);
        Optional(scores, "eyes.main.left.ratio", s.MainFaceLeftEyeRatio);
        Optional(scores, "eyes.main.right.ratio", s.MainFaceRightEyeRatio);
        return scores;
    }

    public static CullingSignals FromScores(IReadOnlyDictionary<string, double> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        double Get(string key) => scores.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(string.Create(CultureInfo.InvariantCulture, $"Score '{key}' missing."));
        double? Opt(string key) => scores.TryGetValue(key, out var v) ? v : null;
        return new CullingSignals(
            (SharpnessZoneKind)(int)Get("zone"),
            Get("sharpness.zone"),
            Get("sharpness.frame"),
            Get("detail.zone"),
            Get("exposure.zone.highlights"),
            Get("exposure.zone.shadows"),
            Get("exposure.frame.highlights"),
            Get("exposure.frame.shadows"),
            Get("exposure.frame.brightness"),
            (int)Get("subjects"),
            Opt("subject.area"),
            (int)Get("faces"),
            (int)Get("faces.relevant"),
            Opt("faces.main.size"),
            (EyeState)(int)Get("eyes.main.left"),
            (EyeState)(int)Get("eyes.main.right"),
            Opt("eyes.main.left.ratio"),
            Opt("eyes.main.right.ratio"),
            (int)Get("eyes.others.closed"),
            (int)Get("eyes.others.uncertain"));
    }

    private static void Optional(Dictionary<string, double> scores, string key, double? value)
    {
        if (value is { } v)
        {
            scores[key] = v;
        }
    }
}
