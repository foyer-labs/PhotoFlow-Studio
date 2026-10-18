// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Analysis.Culling;
using PhotoFlow.Analysis.Profiles;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>Analyzer + decision without models (no detector available: tiles zone, no faces).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class CullingAnalyzerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_detailed_image_is_kept_and_its_heavily_blurred_copy_is_discarded()
    {
        var analyzer = new CullingAnalyzer(new SkiaImageOperations(), objects: null, faces: null);
        var profile = BuiltInProfiles.Get("events");
        var engine = new DecisionEngine();
        var sharp = Texture(1200, 800);
        var blurred = PillowResampler.Resize(PillowResampler.Resize(sharp, 60, 40, ResampleMode.Bilinear), 1200, 800, ResampleMode.Bilinear);

        var sharpSignals = (await analyzer.AnalyzeDetailedAsync(Photo(sharp), profile, Ct)).Signals;
        var blurredSignals = (await analyzer.AnalyzeDetailedAsync(Photo(blurred), profile, Ct)).Signals;

        Assert.Equal(SharpnessZoneKind.Tiles, sharpSignals.Zone);
        Assert.Equal(CullingOutcome.Kept, engine.Decide(sharpSignals, profile).Outcome);
        var decision = engine.Decide(blurredSignals, profile);
        Assert.Equal(CullingOutcome.Discard, decision.Outcome);
        Assert.Equal([DecisionEngine.Reasons.BlurSevere], decision.Reasons);
        Assert.True(blurredSignals.ZoneDetail < sharpSignals.ZoneDetail);
    }

    [Fact]
    public async Task The_af_area_is_the_first_zone_when_the_camera_wrote_it()
    {
        var analyzer = new CullingAnalyzer(new SkiaImageOperations(), objects: null, faces: null);
        var exif = new ExifSummary("NIKON Z5_2", null, null, null, null, null, null, null, new BoundingBox(0.4f, 0.4f, 0.6f, 0.6f));
        var analysis = await analyzer.AnalyzeDetailedAsync(Photo(Texture(600, 400)) with { Exif = exif }, BuiltInProfiles.Get("events"), Ct);
        Assert.Equal(SharpnessZoneKind.AfArea, analysis.Signals.Zone);
        Assert.Equal(new BoundingBox(0.4f, 0.4f, 0.6f, 0.6f), analysis.ZoneBox);
    }

    private static PhotoContext Photo(RgbImage image) => new(null, "synthetic.jpg", new PreviewImage(image, image.Width, image.Height, false), null);

    /// <summary>Deterministic texture with detail at several scales (blocks of 2, 8, 32 and 128 px), like natural images.</summary>
    private static RgbImage Texture(int width, int height)
    {
        var random = new Random(7);
        var values = new int[width * height];
        foreach (var block in new[] { 2, 8, 32, 128 })
        {
            var cols = (width / block) + 1;
            var levels = Enumerable.Range(0, cols * ((height / block) + 1)).Select(_ => random.Next(-26, 27)).ToArray();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    values[(y * width) + x] += levels[((y / block) * cols) + (x / block)];
                }
            }
        }

        var pixels = new byte[width * height * 3];
        for (var i = 0; i < values.Length; i++)
        {
            pixels[i * 3] = pixels[(i * 3) + 1] = pixels[(i * 3) + 2] = (byte)Math.Clamp(128 + values[i], 0, 255);
        }

        return new RgbImage(width, height, pixels);
    }
}

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class CullingPhotoProcessorTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-processor");

    [Fact]
    public async Task A_jpeg_is_previewed_thumbnailed_analyzed_and_decided()
    {
        var fs = new PhysicalFileSystem();
        var path = SyntheticImages.WriteJpeg(Path.Combine(_temp.Path, "photo.jpg"), 640, 480);
        var thumbnails = new ThumbnailCache(fs, _temp.Sub("thumbs"));
        var processor = new CullingPhotoProcessor(
            new PreviewProvider(fs, new ImageHandlerRegistry([new JpegImageHandler(fs)])),
            new CullingAnalyzer(new SkiaImageOperations(), objects: null, faces: null),
            new DecisionEngine(),
            BuiltInProfiles.Get,
            thumbnails);

        var result = await processor.ProcessAsync(new PhotoInput(path, "0123456789abcdef", null), new ProcessingOptions("events", FaceMode.Off), TestContext.Current.CancellationToken);

        Assert.True(thumbnails.Exists("0123456789abcdef"));
        Assert.Equal(CullingAnalyzer.AnalyzerId, result.Analysis.AnalyzerId);
        Assert.Equal(SharpnessZoneKind.Tiles, CullingScores.FromScores(result.Analysis.Scores).Zone);
        Assert.Empty(result.Faces);
        Assert.StartsWith($"{DecisionEngine.EngineVersion}+{CullingAnalyzer.AnalyzerId}+events@{BuiltInProfiles.Get("events").Version}", result.DecisionVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_without_a_handler_is_reported_as_undecodable()
    {
        var fs = new PhysicalFileSystem();
        var path = Path.Combine(_temp.Path, "notes.txt");
        await File.WriteAllTextAsync(path, "not an image", TestContext.Current.CancellationToken);
        var previews = new PreviewProvider(fs, new ImageHandlerRegistry([new JpegImageHandler(fs)]));
        await Assert.ThrowsAsync<ImageDecodeException>(() => previews.GetPreviewAsync(path, 2048, TestContext.Current.CancellationToken));
    }

    public void Dispose() => _temp.Dispose();
}
