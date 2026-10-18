// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Tagging.Clip;

/// <summary>
/// Adapter for the CLIP ViT-B/32 vision model (Xenova ONNX conversion of openai/clip-vit-base-patch32, fp32: F2-1).
/// Preprocessing as the official <c>CLIPImageProcessor</c>: Pillow bicubic resize of the shortest side to 224 (the long
/// side truncated), center crop 224×224 (offsets rounded down), values /255 then normalized with the CLIP mean and
/// standard deviation, CHW float32. The output <c>image_embeds</c> (512) is L2-normalized.
/// </summary>
public sealed class ClipImageEmbedder : IImageEmbedder
{
    public const string AdapterId = "clip-hf-v1";
    public const int InputSize = 224;
    public const string InputName = "pixel_values";
    public const string OutputName = "image_embeds";

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly IInferenceSession _session;
    private readonly IImageOperations _imageOperations;

    public ClipImageEmbedder(IInferenceSession session, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(imageOperations);
        _session = session;
        _imageOperations = imageOperations;
    }

    public string ModelVersion => $"{_session.Model.Id}@{_session.Model.Version}";

    public int Dimensions => 512;

    public async Task<float[]> EmbedAsync(RgbImage image, CancellationToken ct)
    {
        var outputs = await _session.RunAsync([PrepareInput(image, _imageOperations)], ct).ConfigureAwait(false);
        var embeds = outputs.First(o => o.Name == OutputName).AsFloat32().Span;
        return Vectors.Normalize(embeds[..Dimensions]);
    }

    /// <summary>The <c>pixel_values</c> tensor [1,3,224,224].</summary>
    public static NamedTensor PrepareInput(RgbImage image, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imageOperations);

        // transformers get_resize_output_image_size(default_to_square=False): short side → 224, long side int(224·long/short).
        var (width, height) = image.Width <= image.Height
            ? (InputSize, (int)((double)InputSize * image.Height / image.Width))
            : ((int)((double)InputSize * image.Width / image.Height), InputSize);
        var resized = imageOperations.Resize(image, width, height, ResampleMode.Bicubic);
        var top = (height - InputSize) / 2;
        var left = (width - InputSize) / 2;

        const int plane = InputSize * InputSize;
        var data = new float[plane * 3];
        var pixels = resized.Pixels.Span;
        for (var y = 0; y < InputSize; y++)
        {
            for (var x = 0; x < InputSize; x++)
            {
                var s = (((y + top) * width) + x + left) * 3;
                var d = (y * InputSize) + x;
                for (var c = 0; c < 3; c++)
                {
                    // rescale in double then float32, normalize in float32 (as transformers' numpy pipeline).
                    var rescaled = (float)(pixels[s + c] * (1 / 255.0));
                    data[(c * plane) + d] = (rescaled - Mean[c]) / Std[c];
                }
            }
        }

        return NamedTensor.Float32(InputName, data, 1, 3, InputSize, InputSize);
    }
}
