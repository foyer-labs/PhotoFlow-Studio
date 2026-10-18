// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Detectors;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Faces.Detection;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Imaging;
using PhotoFlow.Inference;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tagging.Captions;
using PhotoFlow.Tagging.Clip;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tagging.RamPlus;
using PhotoFlow.Tests.Parity;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// The real analysis of the application (previews, D-FINE M, buffalo_l, decision engine) built from the models in
/// PHOTOFLOW_TESTDATA/models (lab layout), for the local Quality and Performance tests. CUDA when available.
/// </summary>
public sealed class LocalAnalysis : IAsyncDisposable
{
    private readonly List<IInferenceSession> _sessions = [];

    private LocalAnalysis(ExifToolSession exifTool, CullingPhotoProcessor processor, ExecutionBackend backend)
    {
        ExifTool = exifTool;
        Processor = processor;
        Backend = backend;
    }

    /// <summary>CLIP scene classifier (phase 2, <c>recognition</c> only).</summary>
    public ISceneClassifier? SceneClassifier { get; private init; }

    /// <summary>Model version of the stored CLIP embeddings (phase 2, <c>recognition</c> only).</summary>
    public string? EmbeddingVersion { get; private init; }

    /// <summary>Fingerprint encryption with a key of the test (phase 2, <c>recognition</c> only).</summary>
    public IFingerprintProtector Protector { get; private init; } = UnavailableProtector.Instance;

    /// <summary>The versions these models record (for the regeneration).</summary>
    public PhotoFlow.Core.Pipeline.ModelVersions? Versions { get; private init; }

    public ExifToolSession ExifTool { get; }

    public CullingPhotoProcessor Processor { get; }

    public ExecutionBackend Backend { get; }

    /// <param name="recognition">Phase 2: CLIP image embeddings and scene classifier, ArcFace fingerprints with a random key.</param>
    /// <param name="tagging">Phase 3: RAM++ fp32 tags on every photo.</param>
    /// <param name="ocr">Phase 4: PaddleOCR on the vehicles of the photos whose profile asks for it.</param>
    /// <param name="captions">Phase 5: Florence-2 captions (our vision encoder export).</param>
    /// <param name="yunet">Phase 5: YuNet + SFace instead of buffalo_l (change of face model).</param>
    /// <param name="protector">Fingerprint key to use (a new one otherwise).</param>
    public static async Task<LocalAnalysis> CreateAsync(IFileSystem fileSystem, IThumbnailStore? thumbnails, CancellationToken ct, bool recognition = false, bool tagging = false,
        bool ocr = false, bool captions = false, bool yunet = false, IFingerprintProtector? protector = null)
    {
        var models = TestData.RequireDirectory("models");
        var provider = new OnnxRuntimeInferenceProvider(new InferenceOptions(GpuRuntime.Directory));
        var target = provider.Detected.AvailableBackends.Contains(ExecutionBackend.Cuda) ? ExecutionTarget.Gpu : ExecutionTarget.Cpu;
        var exifTool = ExifToolFixture.Start();
        var operations = new SkiaImageOperations();
        var sessions = new List<IInferenceSession>();

        async Task<IInferenceSession> Open(string folder, string file, string adapter)
        {
            var path = Path.Combine(models, folder, file);
            if (!File.Exists(path))
            {
                Assert.Skip($"Model missing for the local test: {path}");
            }

            var session = await provider.CreateSessionAsync(new ModelDescriptor(folder, ModelFunction.Faces, folder, "lab", path, string.Empty, adapter), target, ct);
            sessions.Add(session);
            return session;
        }

        var detector = new DFineDetector(await Open("dfine-m-obj2coco", "model.onnx", DFineDetector.AdapterId), operations);
        IFaceDetector faces = yunet
            ? new YuNetFaceDetector(await Open("opencv-yunet-2023mar", "face_detection_yunet_2023mar.onnx", YuNetFaceDetector.AdapterId), operations)
            : new InsightFaceDetector(
                await Open("insightface-buffalo-l", "det_10g.onnx", InsightFaceDetector.AdapterId),
                await Open("insightface-buffalo-l", "2d106det.onnx", InsightFaceDetector.AdapterId),
                operations);
        var previews = new PreviewProvider(fileSystem, ImageHandlerRegistry.CreateDefault(fileSystem, new ExifToolPreviewSource(exifTool), new MagickRawDecoder()));
        ClipImageEmbedder? clip = null;
        IFaceEmbedder? arcFace = null;
        ClipSceneClassifier? scenes = null;
        var keys = protector ?? UnavailableProtector.Instance;
        if (recognition)
        {
            clip = new ClipImageEmbedder(await Open("clip-vit-b32", "vision_model.onnx", ClipImageEmbedder.AdapterId), operations);
            scenes = new ClipSceneClassifier(new ClipTextEmbedder(await Open("clip-vit-b32", "text_model.onnx", ClipImageEmbedder.AdapterId),
                ClipTokenizer.Load(Path.Combine(models, "clip-vit-b32", "tokenizer.json"))));
            arcFace = yunet
                ? new SFaceEmbedder(await Open("opencv-sface-2021dec", SFaceEmbedder.File, "sface-v1"))
                : new ArcFaceEmbedder(await Open("insightface-buffalo-l", ArcFaceEmbedder.File, "arcface-v1"));
            keys = protector ?? new FingerprintCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }

        RamPlusTagger? tagger = null;
        if (tagging)
        {
            var folder = Path.Combine(models, "ramplus-fp32");
            tagger = new RamPlusTagger(await Open("ramplus-fp32", RamPlusTagger.ModelFile, RamPlusTagger.AdapterId), operations,
                RamPlusClasses.Load(Path.Combine(folder, RamPlusTagger.TagListFile), Path.Combine(folder, RamPlusTagger.ThresholdsFile)));
        }

        PaddleOcr? reader = ocr
            ? new PaddleOcr(await Open("ppocr", PaddleOcr.DetectorFile, PaddleOcr.AdapterId), await Open("ppocr", PaddleOcr.RecognizerFile, PaddleOcr.AdapterId), operations)
            : null;
        Florence2Captioner? captioner = null;
        if (captions)
        {
            captioner = new Florence2Captioner(
                await Open("florence2-base-ft", Florence2Captioner.VisionFile, Florence2Captioner.AdapterId),
                await Open("florence2-base-ft", Florence2Captioner.EmbedFile, Florence2Captioner.AdapterId),
                await Open("florence2-base-ft", Florence2Captioner.EncoderFile, Florence2Captioner.AdapterId),
                await Open("florence2-base-ft", Florence2Captioner.DecoderFile, Florence2Captioner.AdapterId),
                ByteLevelDecoder.Load(Path.Combine(models, "florence2-base-ft", Florence2Captioner.TokenizerFile)), operations);
        }

        var analyzer = new CullingAnalyzer(operations, detector, faces);
        var processor = new CullingPhotoProcessor(previews, analyzer, new DecisionEngine(), BuiltInProfiles.Get, thumbnails,
            clip, arcFace, () => keys, tagger, reader, captioner);
        var analysis = new LocalAnalysis(exifTool, processor, target == ExecutionTarget.Gpu ? ExecutionBackend.Cuda : ExecutionBackend.Cpu)
        {
            SceneClassifier = scenes,
            EmbeddingVersion = clip?.ModelVersion,
            Protector = keys,
            Versions = new PhotoFlow.Core.Pipeline.ModelVersions(analyzer.Id, analyzer.ModelVersion, clip?.ModelVersion, arcFace?.ModelVersion, tagger?.ModelVersion,
                captioner?.ModelVersion, reader?.ModelVersion),
        };
        analysis._sessions.AddRange(sessions);
        return analysis;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        await ExifTool.DisposeAsync();
    }
}
