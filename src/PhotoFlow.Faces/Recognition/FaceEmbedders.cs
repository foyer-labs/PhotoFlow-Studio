// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Faces.Recognition;

/// <summary>
/// ArcFace w600k_r50 of buffalo_l (non-commercial license, accepted by the user): aligned 112×112 crop, RGB,
/// (x − 127.5) / 127.5, CHW → 512 values, L2-normalized (insightface <c>ArcFaceONNX</c>).
/// </summary>
public sealed class ArcFaceEmbedder(IInferenceSession session) : IFaceEmbedder
{
    public const string File = "w600k_r50.onnx";
    public const string InputName = "input.1";

    public string ModelVersion => $"{session.Model.Id}@{session.Model.Version}/arcface";

    public int Dimensions => 512;

    public async Task<float[]> EmbedAsync(RgbImage image, DetectedFace face, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(face);
        var crop = FaceAlignment.AlignedCrop(image, face.Keypoints);
        var outputs = await session.RunAsync([PrepareInput(crop)], ct).ConfigureAwait(false);
        return Vectors.Normalize(outputs[0].AsFloat32().Span);
    }

    public static NamedTensor PrepareInput(RgbImage crop) => PrepareInput(crop, InputName);

    /// <summary>The ArcFace input under another tensor name (AdaFace takes the same crop and normalization).</summary>
    public static NamedTensor PrepareInput(RgbImage crop, string inputName)
    {
        ArgumentNullException.ThrowIfNull(crop);
        const int plane = FaceAlignment.CropSize * FaceAlignment.CropSize;
        var data = new float[plane * 3];
        var pixels = crop.Pixels.Span;
        for (var i = 0; i < plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                data[(c * plane) + i] = (pixels[(i * 3) + c] - 127.5f) * (float)(1 / 127.5);
            }
        }

        return NamedTensor.Float32(inputName, data, 1, 3, FaceAlignment.CropSize, FaceAlignment.CropSize);
    }
}

/// <summary>
/// AdaFace IR-101 WebFace12M (P22, optional, non-commercial, accepted by the user; ONNX exported on this PC by
/// tools/vlm/recipes/adaface_ir101): the ArcFace crop and normalization → 512 values, L2-normalized. Better than ArcFace
/// on small and blurred faces; its own thresholds (<see cref="FaceMatching.AdaFaceThreshold"/>).
/// </summary>
public sealed class AdaFaceEmbedder(IInferenceSession session) : IFaceEmbedder
{
    public const string File = "adaface_ir101.onnx";
    public const string InputName = "input";

    public string ModelVersion => $"{session.Model.Id}@{session.Model.Version}/adaface";

    public int Dimensions => 512;

    public async Task<float[]> EmbedAsync(RgbImage image, DetectedFace face, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(face);
        var crop = FaceAlignment.AlignedCrop(image, face.Keypoints);
        var outputs = await session.RunAsync([ArcFaceEmbedder.PrepareInput(crop, InputName)], ct).ConfigureAwait(false);
        return Vectors.Normalize(outputs[0].AsFloat32().Span);
    }
}

/// <summary>
/// SFace (OpenCV Zoo, Apache 2.0), the permissive alternative: aligned 112×112 crop, RGB values 0..255, CHW → 128
/// values, L2-normalized (OpenCV <c>FaceRecognizerSF</c>). The model lists its weights as inputs: only <c>data</c> is fed.
/// </summary>
public sealed class SFaceEmbedder(IInferenceSession session) : IFaceEmbedder
{
    public const string File = "face_recognition_sface_2021dec.onnx";
    public const string InputName = "data";

    public string ModelVersion => $"{session.Model.Id}@{session.Model.Version}/sface";

    public int Dimensions => 128;

    public async Task<float[]> EmbedAsync(RgbImage image, DetectedFace face, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(face);
        var crop = FaceAlignment.AlignedCrop(image, face.Keypoints);
        var outputs = await session.RunAsync([PrepareInput(crop)], ct).ConfigureAwait(false);
        return Vectors.Normalize(outputs[0].AsFloat32().Span);
    }

    public static NamedTensor PrepareInput(RgbImage crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        const int plane = FaceAlignment.CropSize * FaceAlignment.CropSize;
        var data = new float[plane * 3];
        var pixels = crop.Pixels.Span;
        for (var i = 0; i < plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                data[(c * plane) + i] = pixels[(i * 3) + c];
            }
        }

        return NamedTensor.Float32(InputName, data, 1, 3, FaceAlignment.CropSize, FaceAlignment.CropSize);
    }
}
