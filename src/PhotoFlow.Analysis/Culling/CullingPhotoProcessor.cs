// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;

namespace PhotoFlow.Analysis.Culling;

/// <summary>
/// Stages Preview → Analyzers → Decision for one verified photo: upright preview at the reference size, grid thumbnail,
/// culling signals, faces (normalized coordinates) and the proposed outcome of the profile.
/// </summary>
public sealed class CullingPhotoProcessor : IPhotoProcessor
{
    private readonly IPreviewProvider _previews;
    private readonly CullingAnalyzer _analyzer;
    private readonly IDecisionEngine _engine;
    private readonly Func<string, SceneProfile> _profiles;
    private readonly IThumbnailStore? _thumbnails;
    private readonly IImageEmbedder? _embedder;
    private readonly IFaceEmbedder? _faceEmbedder;
    private readonly Func<IFingerprintProtector>? _protector;
    private readonly IImageTagger? _tagger;
    private readonly ITextReader? _textReader;
    private readonly IImageCaptioner? _captioner;

    /// <summary>Vehicles read by the OCR at most, the largest first.</summary>
    public const int MaxVehicles = 6;

    /// <summary>Faces smaller than this (long side, reference scale) get no fingerprint (spec §7 <c>minFaceSizePx</c>).</summary>
    public const double MinFingerprintFaceSize = 48;

    /// <param name="embedder">CLIP image embedding for bursts and similar photos (phase 2); null without the model.</param>
    /// <param name="faceEmbedder">Fingerprints for the Recognize mode; null without the model.</param>
    /// <param name="protector">Current fingerprint encryption; without an available key no fingerprint is computed (P13).</param>
    /// <param name="tagger">Image tagger (level B, phase 3), run when the options ask for it; null without the model.</param>
    /// <param name="textReader">OCR of race numbers (phase 4), run on each vehicle when the profile asks for it.</param>
    /// <param name="captioner">Captions (level C, phase 5), run when the options ask for them.</param>
    public CullingPhotoProcessor(IPreviewProvider previews, CullingAnalyzer analyzer, IDecisionEngine engine, Func<string, SceneProfile> profiles, IThumbnailStore? thumbnails,
        IImageEmbedder? embedder = null, IFaceEmbedder? faceEmbedder = null, Func<IFingerprintProtector>? protector = null, IImageTagger? tagger = null,
        ITextReader? textReader = null, IImageCaptioner? captioner = null)
    {
        ArgumentNullException.ThrowIfNull(previews);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(profiles);
        _previews = previews;
        _analyzer = analyzer;
        _engine = engine;
        _profiles = profiles;
        _thumbnails = thumbnails;
        _embedder = embedder;
        _faceEmbedder = faceEmbedder;
        _protector = protector;
        _tagger = tagger;
        _textReader = textReader;
        _captioner = captioner;
    }

    public async Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(options);
        var profile = _profiles(options.ProfileId);
        if (options.Faces is { } faceMode)
        {
            profile = profile with { Faces = faceMode };
        }

        var preview = await _previews.GetPreviewAsync(photo.Path, CullingSignals.ReferenceLongSide, ct).ConfigureAwait(false);
        if (_thumbnails is not null && photo.ContentHash is { } hash && !_thumbnails.Exists(hash))
        {
            await _thumbnails.StoreAsync(hash, preview.Image, ct).ConfigureAwait(false);
        }

        var analysis = await _analyzer.AnalyzeDetailedAsync(new PhotoContext(null, photo.Path, preview, photo.Exif), profile, ct).ConfigureAwait(false);
        var embedding = _embedder is null
            ? null
            : new ImageEmbedding(_embedder.ModelVersion, await _embedder.EmbedAsync(preview.Image, ct).ConfigureAwait(false));
        var tags = options.Tagger && _tagger is not null ? await _tagger.TagAsync(preview.Image, ct).ConfigureAwait(false) : null;
        var readings = profile.Ocr && _textReader is not null ? await ReadNumbersAsync(preview.Image, analysis.Subjects, ct).ConfigureAwait(false) : null;
        var caption = options.Captions && _captioner is not null
            ? new GeneratedCaption(_captioner.Language, await _captioner.CaptionAsync(preview.Image, ct).ConfigureAwait(false), _captioner.ModelVersion)
            : null;
        var decision = _engine.Decide(analysis.Signals, profile);
        var (width, height) = (preview.Image.Width, preview.Image.Height);
        var protector = profile.Faces == FaceMode.Recognize && _faceEmbedder is not null ? _protector?.Invoke() : null;
        var faces = new List<Face>(analysis.Faces.Count);
        foreach (var f in analysis.Faces)
        {
            byte[]? fingerprint = null;
            if (protector is { IsAvailable: true } && f.ReferenceSize >= MinFingerprintFaceSize)
            {
                fingerprint = protector.Protect(await _faceEmbedder!.EmbedAsync(preview.Image, f.Face, ct).ConfigureAwait(false));
            }

            faces.Add(new Face(
                0,
                0,
                f.Box,
                f.Face.Landmarks?.Select(p => new Point2(p.X / width, p.Y / height)).ToArray(),
                f.Face.Score,
                f.Eyes.Left,
                f.Eyes.Right,
                fingerprint,
                null,
                null,
                null,
                analysis.ModelVersion,
                f.Face.Keypoints.Select(p => new Point2(p.X / width, p.Y / height)).ToArray(),
                fingerprint is null ? null : _faceEmbedder!.ModelVersion));
        }

        return new PhotoProcessingResult(
            new AnalyzerResult(_analyzer.Id, analysis.ModelVersion, CullingScores.ToScores(analysis.Signals)),
            faces,
            decision,
            $"{_engine.Version}+{analysis.ModelVersion}+{profile.Id}@{profile.Version}",
            embedding,
            tags,
            readings,
            caption);
    }

    /// <summary>
    /// OCR on the crop of each vehicle found by the object detector (spec §8: numbers are read on the vehicle only),
    /// the whole frame when none is found; the boxes of the readings are given in the photo, normalized.
    /// </summary>
    private async Task<IReadOnlyList<OcrReading>> ReadNumbersAsync(RgbImage image, IReadOnlyList<Detection> subjects, CancellationToken ct)
    {
        var vehicles = subjects.Count > 0
            ? subjects.OrderByDescending(s => s.Box.Area).Take(MaxVehicles).Select(s => s.Box).ToList()
            : [new BoundingBox(0, 0, 1, 1)];
        var readings = new List<OcrReading>();
        for (var v = 0; v < vehicles.Count; v++)
        {
            var box = vehicles[v];
            var (x0, y0) = ((int)Math.Floor(Math.Max(0, box.X1 - 0.02f) * image.Width), (int)Math.Floor(Math.Max(0, box.Y1 - 0.02f) * image.Height));
            var (x1, y1) = ((int)Math.Ceiling(Math.Min(1, box.X2 + 0.02f) * image.Width), (int)Math.Ceiling(Math.Min(1, box.Y2 + 0.02f) * image.Height));
            if (x1 - x0 < 32 || y1 - y0 < 32)
            {
                continue;
            }

            var crop = Crop(image, x0, y0, x1 - x0, y1 - y0);
            foreach (var reading in await _textReader!.ReadAsync(crop, ct).ConfigureAwait(false))
            {
                float X(float u) => (x0 + (u * (x1 - x0))) / image.Width;
                float Y(float u) => (y0 + (u * (y1 - y0))) / image.Height;
                readings.Add(new OcrReading(0, v, box, new BoundingBox(X(reading.Box.X1), Y(reading.Box.Y1), X(reading.Box.X2), Y(reading.Box.Y2)),
                    reading.Text, reading.Confidence, _textReader.ModelVersion));
            }
        }

        return readings;
    }

    private static RgbImage Crop(RgbImage image, int x, int y, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        var source = image.Pixels.Span;
        for (var row = 0; row < height; row++)
        {
            source.Slice((((y + row) * image.Width) + x) * 3, width * 3).CopyTo(pixels.AsSpan(row * width * 3, width * 3));
        }

        return new RgbImage(width, height, pixels);
    }
}
