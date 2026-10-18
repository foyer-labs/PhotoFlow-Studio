// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.IO;
using PhotoFlow.Core.Sources;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Scanning;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Sources;

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class ClassificationTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }, ".NEF", MediaKind.Jpeg)] // content wins over the extension
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, ".NEF", MediaKind.Raw)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, ".tif", MediaKind.Tiff)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, ".dng", MediaKind.Raw)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png", MediaKind.Png)]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }, ".jpg", MediaKind.Heif)]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x63, 0x72, 0x78, 0x20 }, ".CR3", MediaKind.Raw)]
    [InlineData(new byte[] { 0, 0, 0, 0x14, 0x66, 0x74, 0x79, 0x70, 0x71, 0x74, 0x20, 0x20 }, ".MOV", MediaKind.Video)]
    [InlineData(new byte[] { 0x46, 0x55, 0x4A, 0x49, 0x46, 0x49, 0x4C, 0x4D }, ".RAF", MediaKind.Raw)]
    [InlineData(new byte[] { 0x23, 0x21, 0x2F, 0x62 }, ".txt", MediaKind.Other)]
    public void Kind_comes_from_the_content(byte[] header, string extension, MediaKind expected) =>
        Assert.Equal(expected, MediaClassifier.Classify(header, extension));
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class SourceScannerTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-scan");

    [Fact]
    public async Task Scans_a_card_reading_only_and_skipping_system_files()
    {
        SyntheticFiles.CreateCard(_temp.Path, nefCount: 2, jpegCount: 1);
        var folder = Path.Combine(_temp.Path, "DCIM", "100NIKON");
        File.WriteAllBytes(Path.Combine(folder, "._DSC_0001.NEF"), [0, 5, 22, 7]);
        File.WriteAllBytes(Path.Combine(folder, "renamed.NEF"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2]);
        SyntheticFiles.MakeReadOnly(_temp.Path);
        var fileSystem = new RecordingFileSystem(new PhysicalFileSystem(), _temp.Path);

        var files = new List<ScannedFile>();
        await foreach (var file in new SourceScanner(fileSystem, _temp.Path).ScanAsync(TestContext.Current.CancellationToken))
        {
            files.Add(file);
        }

        Assert.Empty(fileSystem.Violations);
        Assert.Equal(4, files.Count);
        Assert.Equal(MediaKind.Jpeg, files.Single(f => f.Item.RelativePath.EndsWith("renamed.NEF", StringComparison.Ordinal)).Kind);
        Assert.Equal(2, files.Count(f => f.Kind == MediaKind.Raw));
        Assert.All(files, f => Assert.False(Path.IsPathRooted(f.Item.RelativePath)));
    }

    [Fact]
    public void Raw_and_jpeg_with_the_same_name_share_the_pair_key()
    {
        var raw = new ScannedFile(new SourceItem("x", Path.Combine("DCIM", "DSC_0001.NEF"), 1, default), MediaKind.Raw);
        var jpeg = new ScannedFile(new SourceItem("y", Path.Combine("DCIM", "DSC_0001.JPG"), 1, default), MediaKind.Jpeg);
        Assert.Equal(raw.PairKey, jpeg.PairKey);
    }

    public void Dispose() => _temp.Dispose();
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class QuickHashTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-qh");

    [Theory]
    [InlineData(10)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public async Task Identical_files_match_and_a_changed_tail_does_not(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        var a = Path.Combine(_temp.Path, "a.bin");
        var b = Path.Combine(_temp.Path, "b.bin");
        File.WriteAllBytes(a, bytes);
        File.WriteAllBytes(b, bytes);
        var fs = new PhysicalFileSystem();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(await QuickHash.ComputeAsync(fs, a, ct), await QuickHash.ComputeAsync(fs, b, ct));

        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(b, bytes);
        Assert.NotEqual(await QuickHash.ComputeAsync(fs, a, ct), await QuickHash.ComputeAsync(fs, b, ct));
    }

    public void Dispose() => _temp.Dispose();
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class PathTemplateTests
{
    private static readonly PathTemplateContext Context =
        new("Matrimonio Sara e Luca", new DateOnly(2026, 10, 12), 1, "NIKON Z5_2", "DSC_4821", "NEF");

    [Fact]
    public void Default_template_matches_board_3d() =>
        Assert.Equal(
            Path.Combine("2026", "2026-10-12_Matrimonio-Sara-e-Luca", "20261012_Matrimonio-Sara-e-Luca_0001.NEF"),
            new PathTemplate(PathTemplate.Default).Render(Context));

    [Fact]
    public void Invalid_characters_become_dashes_and_reserved_names_are_avoided()
    {
        var template = new PathTemplate("{evento}/{nomeoriginale}.{ext}");
        Assert.Equal(Path.Combine("Gara-Monza-2-3", "DSC_4821.NEF"), template.Render(Context with { EventName = "Gara: Monza  2/3?" }));
        Assert.Equal(Path.Combine("CON_", "DSC_4821.NEF"), template.Render(Context with { EventName = "CON" }));
    }

    [Fact]
    public void All_p20_variables_are_supported()
    {
        var template = new PathTemplate("{yyyy}/{MM}/{camera}_{seq}_{nomeoriginale}.{ext}");
        Assert.Equal(Path.Combine("2026", "10", "NIKON-Z5_2_7_DSC_4821.NEF"), template.Render(Context with { Sequence = 7 }));
    }

    [Theory]
    [InlineData("{evento}/{foo}.{ext}")]
    [InlineData("{evento}/{seq}")]
    public void Unknown_variables_or_a_missing_extension_are_rejected(string template) =>
        Assert.Throws<ArgumentException>(() => new PathTemplate(template));
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class CardDetectorTests
{
    [Fact]
    public void Insert_and_removal_raise_events_once()
    {
        var volumes = new List<CardVolume>();
        using var detector = new PollingCardDetector(() => volumes.ToList());
        var inserted = new List<string>();
        var removed = new List<string>();
        detector.CardInserted += (_, c) => inserted.Add(c.Label);
        detector.CardRemoved += (_, c) => removed.Add(c.Label);

        detector.Poll();
        volumes.Add(new CardVolume("E:\\", "NIKON Z5_2", 64L << 30));
        detector.Poll();
        detector.Poll();
        volumes.Clear();
        detector.Poll();

        Assert.Equal(["NIKON Z5_2"], inserted);
        Assert.Equal(["NIKON Z5_2"], removed);
        Assert.Empty(detector.Current);
    }

    [Fact]
    public void A_failing_probe_is_retried_at_the_next_pass()
    {
        var fail = true;
        using var detector = new PollingCardDetector(() => fail ? throw new IOException("not ready") : [new CardVolume("F:\\", "CARD", 1)]);
        detector.Poll();
        fail = false;
        detector.Poll();
        Assert.Single(detector.Current);
    }
}
