// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;

namespace PhotoFlow.Core.Tagging;

/// <summary>A tagger that looks at the image (level B, RAM++): the tags above their thresholds with their confidence.</summary>
public interface IImageTagger
{
    /// <summary>Recorded with every tag it produces (CLAUDE.md §4).</summary>
    string ModelVersion { get; }

    Task<IReadOnlyList<TagResult>> TagAsync(RgbImage image, CancellationToken ct);
}

/// <summary>Tagging levels of spec §5.</summary>
public enum TaggingLevel
{
    /// <summary>A: CLIP zero-shot on the personal vocabulary.</summary>
    ZeroShot,

    /// <summary>B: RAM++.</summary>
    Tagger,

    /// <summary>C: Florence-2 captions.</summary>
    Caption,

    /// <summary>D: VLM captions (GPU only).</summary>
    VlmCaption,
}

/// <summary>Tagging levels chosen for a job (spec §5): A runs on the stored embeddings, B looks at every photo.</summary>
[Flags]
public enum TagLevels
{
    None = 0,

    /// <summary>A: personal CLIP tags.</summary>
    ZeroShot = 1,

    /// <summary>B: RAM++.</summary>
    Tagger = 2,

    /// <summary>C: Florence-2 captions (optional, spec §5).</summary>
    Caption = 4,

    /// <summary>D: Italian captions of the VLM (optional, GPU only; phase 5b).</summary>
    VlmCaption = 8,

    Default = ZeroShot | Tagger,
}

/// <param name="TagKey">Stable English key (spec §8).</param>
public sealed record TagResult(string TagKey, TagSource Source, double Confidence, string ModelVersion);

/// <summary>OCR (spec §8): the text lines of an image.</summary>
public interface ITextReader
{
    string ModelVersion { get; }

    Task<IReadOnlyList<TextReading>> ReadAsync(RgbImage image, CancellationToken ct);
}

/// <param name="Box">Normalized to the image read.</param>
public sealed record TextReading(BoundingBox Box, string Text, double Confidence);

/// <summary>Captions of level C/D (spec §8): one sentence or paragraph describing the image, in English for Florence-2.</summary>
public interface IImageCaptioner
{
    /// <summary>Recorded with every caption it produces (CLAUDE.md §4).</summary>
    string ModelVersion { get; }

    /// <summary>Language of the captions (BCP 47, "en" for Florence-2).</summary>
    string Language { get; }

    Task<string> CaptionAsync(RgbImage image, CancellationToken ct);
}

/// <summary>
/// Level D (phase 5b) needs the GPU for itself: the VLM alone peaks at ~10 GB (P16: GPU ≥ 12 GB). A pass waits for
/// the analyses in progress, frees the GPU of the other models, runs <paramref name="work"/> with the captioner and
/// frees it again; the analyses waiting meanwhile go on after.
/// </summary>
public interface IExclusiveCaptioning
{
    /// <summary>Version of the captioner, null when level D cannot run here (model or GPU).</summary>
    string? ModelVersion { get; }

    /// <summary>Language of the captions ("it").</summary>
    string Language { get; }

    Task RunAsync(Func<IImageCaptioner, Task> work, CancellationToken ct);
}
