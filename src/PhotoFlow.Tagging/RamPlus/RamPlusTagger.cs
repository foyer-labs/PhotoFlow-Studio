// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Tagging;

namespace PhotoFlow.Tagging.RamPlus;

/// <summary>The RAM++ classes with their official thresholds (files of the model, in the model's order).</summary>
public sealed class RamPlusClasses
{
    public RamPlusClasses(IReadOnlyList<string> tags, IReadOnlyList<double> thresholds)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (tags.Count != thresholds.Count || tags.Count == 0)
        {
            throw new InvalidDataException($"RAM++ class files disagree: {tags.Count} tags, {thresholds.Count} thresholds.");
        }

        Tags = tags;
        Thresholds = thresholds;
    }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<double> Thresholds { get; }

    /// <summary>Reads <c>ram_tag_list.txt</c> and <c>ram_tag_list_threshold.txt</c>.</summary>
    public static RamPlusClasses Load(string tagListPath, string thresholdsPath)
    {
        var tags = File.ReadAllText(tagListPath).Split('\n').Select(t => t.TrimEnd('\r')).Where(t => t.Length > 0).ToList();
        var thresholds = File.ReadAllText(thresholdsPath).Split((char[])['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
        return new RamPlusClasses(tags, thresholds);
    }
}

/// <summary>
/// Adapter for RAM++ (Recognize Anything Plus, fp32 ONNX export, decision F3-1). Preprocessing as the official
/// <c>ram.get_transform(384)</c>: Pillow bilinear resize to 384×384 (aspect ratio not kept), values /255 in float32,
/// ImageNet normalization, CHW. The output are logits of the 4,585 classes; a tag is kept when its probability (sigmoid)
/// is above the official threshold of the class plus a global adjustment (spec §8, "regolatore globale").
/// </summary>
public sealed class RamPlusTagger : IImageTagger
{
    public const string AdapterId = "ramplus-v1";
    public const int InputSize = 384;
    public const string InputName = "input";
    public const string OutputName = "output";
    public const string ModelFile = "ram_plus.onnx";
    public const string TagListFile = "ram_tag_list.txt";
    public const string ThresholdsFile = "ram_tag_list_threshold.txt";

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly IInferenceSession _session;
    private readonly IImageOperations _imageOperations;
    private readonly RamPlusClasses _classes;
    private readonly double _adjustment;

    /// <param name="adjustment">Added to every class threshold (positive: fewer tags).</param>
    public RamPlusTagger(IInferenceSession session, IImageOperations imageOperations, RamPlusClasses classes, double adjustment = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(imageOperations);
        ArgumentNullException.ThrowIfNull(classes);
        _session = session;
        _imageOperations = imageOperations;
        _classes = classes;
        _adjustment = adjustment;
    }

    public string ModelVersion => $"{_session.Model.Id}@{_session.Model.Version}";

    public IReadOnlyList<string> Tags => _classes.Tags;

    public async Task<IReadOnlyList<TagResult>> TagAsync(RgbImage image, CancellationToken ct)
    {
        var probabilities = await ProbabilitiesAsync(image, ct).ConfigureAwait(false);
        var results = new List<TagResult>();
        for (var i = 0; i < probabilities.Length; i++)
        {
            if (probabilities[i] > _classes.Thresholds[i] + _adjustment)
            {
                results.Add(new TagResult(_classes.Tags[i], TagSource.RamPlus, probabilities[i], ModelVersion));
            }
        }

        return [.. results.OrderByDescending(r => r.Confidence)];
    }

    /// <summary>The probability of every class, in the model's order.</summary>
    public async Task<float[]> ProbabilitiesAsync(RgbImage image, CancellationToken ct)
    {
        var outputs = await _session.RunAsync([PrepareInput(image, _imageOperations)], ct).ConfigureAwait(false);
        var logits = outputs.First(o => o.Name == OutputName).AsFloat32().Span;
        if (logits.Length != _classes.Tags.Count)
        {
            throw new InvalidDataException($"RAM++ returned {logits.Length} classes, expected {_classes.Tags.Count}.");
        }

        var probabilities = new float[logits.Length];
        for (var i = 0; i < logits.Length; i++)
        {
            probabilities[i] = (float)(1 / (1 + Math.Exp(-logits[i])));
        }

        return probabilities;
    }

    /// <summary>The <c>input</c> tensor [1,3,384,384].</summary>
    public static NamedTensor PrepareInput(RgbImage image, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imageOperations);
        var resized = imageOperations.Resize(image, InputSize, InputSize, ResampleMode.Bilinear);
        const int plane = InputSize * InputSize;
        var data = new float[plane * 3];
        var pixels = resized.Pixels.Span;
        for (var i = 0; i < plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                // float32 throughout, as numpy: uint8 → float32 / 255, then (x − mean) / std.
                var value = pixels[(i * 3) + c] / 255f;
                data[(c * plane) + i] = (value - Mean[c]) / Std[c];
            }
        }

        return NamedTensor.Float32(InputName, data, 1, 3, InputSize, InputSize);
    }
}
