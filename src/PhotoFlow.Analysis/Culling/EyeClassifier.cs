// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Signals;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Analysis.Culling;

/// <summary>
/// Eye state of a face from its eye contours (spec §6): closed or open only when the face is large and frontal enough
/// and the eye region is not covered; otherwise unknown with the reason. Thresholds come from the profile.
/// </summary>
public static class EyeClassifier
{
    /// <summary>
    /// Nose offset from the eye midpoint, along the eye axis, relative to the eye distance: above it the head is in
    /// profile and the far eye contour is unreliable. A turned head widens nothing vertically, so moderate yaw only makes
    /// eyes look more open (the safe direction): the limit is loose (lab, 12 Oct 2026).
    /// </summary>
    public const double MaxNoseOffset = 0.45;

    /// <summary>Width ratio of the narrower to the wider eye: below it one eye is foreshortened (profile).</summary>
    public const double MinEyeWidthRatio = 0.45;

    /// <summary>
    /// Eye-to-nose over nose-to-mouth distance (perpendicular to the eye axis): above it the head is tilted down and
    /// open eyes look closed (lab: the faces looking down measured 3.0 and 4.2, frontal faces 0.5-2.2).
    /// </summary>
    public const double MaxLookingDownRatio = 2.5;

    /// <summary>Eye region darker than this fraction of the face center, and flat: covered (sunglasses).</summary>
    public const double CoveredBrightnessRatio = 0.5;

    public const double CoveredMaxStdDev = 14;

    /// <param name="face">Face in pixels of the image whose gray version is <paramref name="gray"/> scaled by <paramref name="grayScale"/>.</param>
    /// <param name="referenceSize">Longest side of the face at the reference scale.</param>
    public static EyeJudgement Judge(DetectedFace face, double referenceSize, GrayImage gray, double grayScale, CullingThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(gray);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (face.LeftEye is null || face.RightEye is null)
        {
            return Unknown(EyeUnknownReason.NoContours);
        }

        if (referenceSize < thresholds.EyesMinFaceSize)
        {
            return Unknown(EyeUnknownReason.SmallFace);
        }

        if (IsTurned(face))
        {
            return Unknown(EyeUnknownReason.Turned);
        }

        if (IsLookingDown(face))
        {
            return Unknown(EyeUnknownReason.LookingDown);
        }

        if (IsCovered(face, gray, grayScale))
        {
            return Unknown(EyeUnknownReason.Covered);
        }

        var left = face.LeftEye.AspectRatio;
        var right = face.RightEye.AspectRatio;
        var leftState = State(left, thresholds);
        var rightState = State(right, thresholds);
        var reason = leftState == EyeState.Unknown || rightState == EyeState.Unknown ? EyeUnknownReason.Uncertain : (EyeUnknownReason?)null;
        return new EyeJudgement(leftState, rightState, left, right, reason);
    }

    private static EyeState State(double ratio, CullingThresholds thresholds) =>
        ratio < thresholds.EyesClosed ? EyeState.Closed : ratio > thresholds.EyesOpen ? EyeState.Open : EyeState.Unknown;

    private static EyeJudgement Unknown(EyeUnknownReason reason) => new(EyeState.Unknown, EyeState.Unknown, null, null, reason);

    private static bool IsTurned(DetectedFace face)
    {
        if (face.Keypoints.Count < 3)
        {
            return true;
        }

        var (le, re, nose) = (face.Keypoints[0], face.Keypoints[1], face.Keypoints[2]);
        double ax = re.X - le.X, ay = re.Y - le.Y;
        var distance = Math.Sqrt((ax * ax) + (ay * ay));
        if (distance <= 0)
        {
            return true;
        }

        var offset = Math.Abs(((nose.X - ((le.X + re.X) / 2)) * ax) + ((nose.Y - ((le.Y + re.Y) / 2)) * ay)) / (distance * distance);
        var widths = (face.LeftEye!.Width, face.RightEye!.Width);
        var widthRatio = Math.Min(widths.Item1, widths.Item2) / Math.Max(Math.Max(widths.Item1, widths.Item2), 1e-6);
        return offset > MaxNoseOffset || widthRatio < MinEyeWidthRatio;
    }

    private static bool IsLookingDown(DetectedFace face)
    {
        if (face.Keypoints.Count < 5)
        {
            return true;
        }

        var k = face.Keypoints;
        double ax = k[1].X - k[0].X, ay = k[1].Y - k[0].Y;
        var length = Math.Sqrt((ax * ax) + (ay * ay));
        if (length <= 0)
        {
            return true;
        }

        // Unit vector perpendicular to the eye axis, pointing down the face.
        double px = -ay / length, py = ax / length;
        double eyeX = (k[0].X + k[1].X) / 2, eyeY = (k[0].Y + k[1].Y) / 2;
        double mouthX = (k[3].X + k[4].X) / 2, mouthY = (k[3].Y + k[4].Y) / 2;
        var eyeToNose = ((k[2].X - eyeX) * px) + ((k[2].Y - eyeY) * py);
        var noseToMouth = ((mouthX - k[2].X) * px) + ((mouthY - k[2].Y) * py);
        return noseToMouth <= 0 || eyeToNose / noseToMouth > MaxLookingDownRatio;
    }

    /// <summary>Sunglasses: both eye regions much darker than the face center and nearly uniform.</summary>
    private static bool IsCovered(DetectedFace face, GrayImage gray, double scale)
    {
        var center = Stats(gray, Scale(Region(face.Box.X1 + (face.Box.Width * 0.3f), face.Box.Y1 + (face.Box.Height * 0.55f),
            face.Box.X1 + (face.Box.Width * 0.7f), face.Box.Y1 + (face.Box.Height * 0.8f)), scale));
        foreach (var eye in new[] { face.LeftEye!, face.RightEye! })
        {
            var xs = eye.Points.Select(p => p.X).ToArray();
            var ys = eye.Points.Select(p => p.Y).ToArray();
            var pad = (float)(eye.Width * 0.15);
            var (mean, std) = Stats(gray, Scale(Region(xs.Min() - pad, ys.Min() - pad, xs.Max() + pad, ys.Max() + pad), scale));
            if (!(mean < center.Mean * CoveredBrightnessRatio && std < CoveredMaxStdDev))
            {
                return false;
            }
        }

        return true;
    }

    private static (float X1, float Y1, float X2, float Y2) Region(float x1, float y1, float x2, float y2) => (x1, y1, x2, y2);

    private static PixelRect Scale((float X1, float Y1, float X2, float Y2) r, double scale) =>
        new((int)Math.Floor(r.X1 * scale), (int)Math.Floor(r.Y1 * scale),
            Math.Max(1, (int)Math.Ceiling((r.X2 - r.X1) * scale)), Math.Max(1, (int)Math.Ceiling((r.Y2 - r.Y1) * scale)));

    private static (double Mean, double Std) Stats(GrayImage gray, PixelRect rect)
    {
        var x1 = Math.Clamp(rect.X, 0, gray.Width - 1);
        var y1 = Math.Clamp(rect.Y, 0, gray.Height - 1);
        var x2 = Math.Clamp(rect.X + rect.Width, x1 + 1, gray.Width);
        var y2 = Math.Clamp(rect.Y + rect.Height, y1 + 1, gray.Height);
        double sum = 0, squares = 0;
        for (var y = y1; y < y2; y++)
        {
            for (var x = x1; x < x2; x++)
            {
                double v = gray.Pixels[(y * gray.Width) + x];
                sum += v;
                squares += v * v;
            }
        }

        var n = (double)(x2 - x1) * (y2 - y1);
        var mean = sum / n;
        return (mean, Math.Sqrt(Math.Max(0, (squares / n) - (mean * mean))));
    }
}
