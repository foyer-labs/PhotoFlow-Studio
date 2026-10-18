// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Tagging;

namespace PhotoFlow.Tagging.Ocr;

/// <summary>A detected text box in input pixels, with its DB score.</summary>
public sealed record TextBox(int X0, int Y0, int X1, int Y1, double Score);

/// <summary>
/// OCR with the PaddleOCR models converted by RapidOCR (decision F4-1): DB text detection (ch_PP-OCRv4_det) and CTC
/// recognition (en_PP-OCRv3_rec, dictionary in the model metadata). Inputs are prepared with antialiased resampling
/// (Pillow semantics, as the other adapters) instead of cv2.resize. Detection post-processing is deliberately simple
/// (numbers on vehicles are upright): probability &gt; 0.3, 8-connected components, score = mean probability of the
/// component (kept from 0.5, sides ≥ 3 px), box expanded by area × 1.6 / perimeter. tools/lab/ocr_refs.py implements the
/// same steps for the parity test.
/// </summary>
public sealed class PaddleOcr : ITextReader
{
    public const string AdapterId = "ppocr-v1";
    public const string DetectorFile = "ch_PP-OCRv4_det_infer.onnx";
    public const string RecognizerFile = "en_PP-OCRv3_rec_infer.onnx";
    public const int DetectorMinSide = 736;
    public const double ProbabilityThreshold = 0.3;
    public const double BoxThreshold = 0.5;
    public const double UnclipRatio = 1.6;
    public const int RecognizerHeight = 48;
    public const int RecognizerMaxWidth = 320;

    /// <summary>
    /// The recognizer runs on the CPU: its small inputs of always different widths are about 9 times slower on the
    /// GPU (3 ms against 27 ms per text, phase 4 measures); the detector stays on the best backend.
    /// </summary>
    public const ExecutionTarget RecognizerTarget = ExecutionTarget.Cpu;

    private readonly IInferenceSession _detector;
    private readonly IInferenceSession _recognizer;
    private readonly IImageOperations _imageOperations;
    private readonly string[] _characters;

    public PaddleOcr(IInferenceSession detector, IInferenceSession recognizer, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(imageOperations);
        _detector = detector;
        _recognizer = recognizer;
        _imageOperations = imageOperations;
        _characters = recognizer.Metadata.TryGetValue("character", out var dictionary)
            ? [.. dictionary.Split('\n'), " "]
            : throw new InvalidDataException("The OCR recognizer has no 'character' metadata.");
    }

    public string ModelVersion => $"{_detector.Model.Id}@{_detector.Model.Version}";

    public async Task<IReadOnlyList<TextReading>> ReadAsync(RgbImage image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (input, width, height, resized) = DetectorInput(image, _imageOperations);
        var probabilities = (await _detector.RunAsync([input], ct).ConfigureAwait(false))[0].AsFloat32().ToArray();
        var readings = new List<TextReading>();
        foreach (var box in Boxes(probabilities, width, height))
        {
            var (text, confidence) = await RecognizeAsync(Crop(resized, box), ct).ConfigureAwait(false);
            if (text.Length > 0)
            {
                readings.Add(new TextReading(new BoundingBox((float)box.X0 / width, (float)box.Y0 / height, (float)box.X1 / width, (float)box.Y1 / height), text, confidence));
            }
        }

        return readings;
    }

    /// <summary>RapidOCR DetPreProcess (limit "min" 736, mean/std 0.5), sizes rounded to multiples of 32.</summary>
    public static (NamedTensor Tensor, int Width, int Height, RgbImage Resized) DetectorInput(RgbImage image, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imageOperations);
        var ratio = Math.Min(image.Width, image.Height) < DetectorMinSide ? (double)DetectorMinSide / Math.Min(image.Width, image.Height) : 1.0;
        var width = Math.Max(32, (int)Math.Round((int)(image.Width * ratio) / 32.0, MidpointRounding.ToEven) * 32);
        var height = Math.Max(32, (int)Math.Round((int)(image.Height * ratio) / 32.0, MidpointRounding.ToEven) * 32);
        var resized = imageOperations.Resize(image, width, height, ResampleMode.Bilinear);
        return (Normalize(resized, width, height, width), width, height, resized);
    }

    /// <summary>The recognizer input [1,3,48,W]: height 48, width in proportion (at most 320), padded with zeros.</summary>
    public static NamedTensor RecognizerInput(RgbImage crop, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentNullException.ThrowIfNull(imageOperations);
        var width = Math.Min(RecognizerMaxWidth, Math.Max(1, (int)Math.Ceiling(RecognizerHeight * (double)crop.Width / crop.Height)));
        var resized = imageOperations.Resize(crop, width, RecognizerHeight, ResampleMode.Bilinear);
        return Normalize(resized, width, RecognizerHeight, Math.Max(width, RecognizerHeight));
    }

    /// <summary>The boxes of the probability map (see the class summary).</summary>
    public static IReadOnlyList<TextBox> Boxes(float[] probabilities, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        var labels = new int[probabilities.Length];
        var boxes = new List<TextBox>();
        var stack = new Stack<int>();
        var next = 0;
        for (var start = 0; start < probabilities.Length; start++)
        {
            if (labels[start] != 0 || probabilities[start] <= ProbabilityThreshold)
            {
                continue;
            }

            next++;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
            double sum = 0;
            var count = 0;
            labels[start] = next;
            stack.Push(start);
            while (stack.TryPop(out var p))
            {
                var (x, y) = (p % width, p / width);
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x + 1), Math.Max(y1, y + 1));
                sum += probabilities[p];
                count++;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        var q = (ny * width) + nx;
                        if (labels[q] == 0 && probabilities[q] > ProbabilityThreshold)
                        {
                            labels[q] = next;
                            stack.Push(q);
                        }
                    }
                }
            }

            var score = sum / count;
            var (bw, bh) = (x1 - x0, y1 - y0);
            if (score < BoxThreshold || Math.Min(bw, bh) < 3)
            {
                continue;
            }

            var distance = bw * bh * UnclipRatio / (2.0 * (bw + bh));
            boxes.Add(new TextBox(
                Math.Max(0, (int)Math.Floor(x0 - distance)), Math.Max(0, (int)Math.Floor(y0 - distance)),
                Math.Min(width, (int)Math.Ceiling(x1 + distance)), Math.Min(height, (int)Math.Ceiling(y1 + distance)), score));
        }

        return [.. boxes.OrderBy(b => b.Y0).ThenBy(b => b.X0)];
    }

    /// <summary>Greedy CTC decoding: best class per step, repeats and blanks (class 0) removed; confidence = mean of the kept steps.</summary>
    public async Task<(string Text, double Confidence)> RecognizeAsync(RgbImage crop, CancellationToken ct)
    {
        var output = (await _recognizer.RunAsync([RecognizerInput(crop, _imageOperations)], ct).ConfigureAwait(false))[0];
        var data = output.AsFloat32().Span;
        var classes = (int)output.Shape[^1];
        var steps = data.Length / classes;
        var text = new System.Text.StringBuilder();
        double sum = 0;
        var kept = 0;
        var previous = -1;
        for (var t = 0; t < steps; t++)
        {
            var row = data.Slice(t * classes, classes);
            var best = 0;
            for (var c = 1; c < classes; c++)
            {
                if (row[c] > row[best])
                {
                    best = c;
                }
            }

            if (best != previous && best != 0 && best - 1 < _characters.Length)
            {
                text.Append(_characters[best - 1]);
                sum += row[best];
                kept++;
            }

            previous = best;
        }

        return (text.ToString(), kept == 0 ? 0 : sum / kept);
    }

    private static RgbImage Crop(RgbImage image, TextBox box)
    {
        var (w, h) = (box.X1 - box.X0, box.Y1 - box.Y0);
        var pixels = new byte[w * h * 3];
        var source = image.Pixels.Span;
        for (var y = 0; y < h; y++)
        {
            source.Slice((((box.Y0 + y) * image.Width) + box.X0) * 3, w * 3).CopyTo(pixels.AsSpan(y * w * 3, w * 3));
        }

        return new RgbImage(w, h, pixels);
    }

    /// <summary>CHW float32, (x/255 − 0.5)/0.5, in a tensor <paramref name="tensorWidth"/> wide (zero padding on the right).</summary>
    private static NamedTensor Normalize(RgbImage image, int width, int height, int tensorWidth)
    {
        var plane = height * tensorWidth;
        var data = new float[plane * 3];
        var pixels = image.Pixels.Span;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = ((y * width) + x) * 3;
                var d = (y * tensorWidth) + x;
                for (var c = 0; c < 3; c++)
                {
                    data[(c * plane) + d] = ((pixels[s + c] * (1f / 255f)) - 0.5f) / 0.5f;
                }
            }
        }

        return NamedTensor.Float32("x", data, 1, 3, height, tensorWidth);
    }
}
