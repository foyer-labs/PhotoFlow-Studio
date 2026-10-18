// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// Culling quality on the user's labelled set (spec §6 targets, §14; decision F1-7): PHOTOFLOW_TESTDATA/quality with
/// the folders <c>buone</c> (good), <c>mosse</c> (motion blur / out of focus), <c>occhi_chiusi</c> (closed eyes). The
/// report (markdown + CSV per photo) is written next to the set; figures carry a 95% Wilson interval.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Quality)]
public sealed class CullingQualityTests
{
    private static readonly string[] Extensions = [".nef", ".nrw", ".jpg", ".jpeg", ".cr2", ".cr3", ".arw", ".raf", ".dng", ".orf", ".rw2"];

    [Fact]
    public async Task Interception_of_blur_and_closed_eyes_is_measured_on_the_labelled_set()
    {
        var root = TestData.RequireDirectory("quality");
        var classes = new[] { "buone", "mosse", "occhi_chiusi" }
            .Select(c => (Name: c, Files: Files(Path.Combine(root, c))))
            .ToList();
        if (classes.Any(c => c.Files.Count == 0))
        {
            Assert.Skip($"Labelled set incomplete in {root}: folders buone, mosse, occhi_chiusi with photos are required.");
        }

        var ct = TestContext.Current.CancellationToken;
        await using var analysis = await LocalAnalysis.CreateAsync(new PhysicalFileSystem(), null, ct);
        var rows = new List<(string Class, string File, CullingOutcome Outcome, string Reasons)>();
        foreach (var (name, files) in classes)
        {
            foreach (var file in files)
            {
                var result = await analysis.Processor.ProcessAsync(new PhotoInput(file, null, null), new ProcessingOptions("events", PhotoFlow.Core.Analysis.FaceMode.DetectOnly), ct);
                rows.Add((name, Path.GetFileName(file), result.Decision.Outcome, string.Join(" ", result.Decision.Reasons)));
            }
        }

        var good = rows.Where(r => r.Class == "buone").ToList();
        var blurred = rows.Where(r => r.Class == "mosse").ToList();
        var eyes = rows.Where(r => r.Class == "occhi_chiusi").ToList();
        var discards = rows.Where(r => r.Outcome == CullingOutcome.Discard).ToList();
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# Culling quality — {DateTime.Now:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Profile Eventi, prudent decisions of phase 1, backend {analysis.Backend}. Photos: {good.Count} good, {blurred.Count} blurred, {eyes.Count} closed eyes.")
            .AppendLine()
            .AppendLine("| Metric | Value | 95% interval | Target (spec §6) |")
            .AppendLine("|---|---|---|---|")
            .AppendLine(Line("Blurred intercepted (discard or to review for blur)", blurred.Count(r => r.Reasons.Contains("blur", StringComparison.Ordinal)), blurred.Count, "> 70%"))
            .AppendLine(Line("Blurred discarded", blurred.Count(r => r.Outcome == CullingOutcome.Discard), blurred.Count, "—"))
            .AppendLine(Line("Closed eyes intercepted (discard or to review for eyes)", eyes.Count(r => r.Reasons.Contains("eyes", StringComparison.Ordinal)), eyes.Count, "> 70%"))
            .AppendLine(Line("Closed eyes discarded", eyes.Count(r => r.Outcome == CullingOutcome.Discard), eyes.Count, "—"))
            .AppendLine(Line("Good photos discarded", good.Count(r => r.Outcome == CullingOutcome.Discard), good.Count, "—"))
            .AppendLine(Line("Wrong discards (good among all discards)", discards.Count(r => r.Class == "buone"), discards.Count, "< 2%"))
            .AppendLine(Line("Good photos to review", good.Count(r => r.Outcome == CullingOutcome.ToReview), good.Count, "< 20% (all photos)"))
            .AppendLine();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(Path.Combine(root, $"report-{stamp}.md"), report.ToString(), ct);
        await File.WriteAllLinesAsync(Path.Combine(root, $"report-{stamp}.csv"),
            ["class;file;outcome;reasons", .. rows.Select(r => $"{r.Class};{r.File};{r.Outcome};{r.Reasons}")], ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        Assert.Equal(classes.Sum(c => c.Files.Count), rows.Count);
    }

    /// <summary>A metric with its 95% Wilson score interval.</summary>
    public static (double Value, double Low, double High) Wilson(int hits, int total)
    {
        if (total == 0)
        {
            return (double.NaN, double.NaN, double.NaN);
        }

        const double z = 1.959964;
        double p = (double)hits / total, n = total;
        var centre = (p + (z * z / (2 * n))) / (1 + (z * z / n));
        var half = z * Math.Sqrt((p * (1 - p) / n) + (z * z / (4 * n * n))) / (1 + (z * z / n));
        return (p, Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    private static string Line(string label, int hits, int total, string target)
    {
        var (value, low, high) = Wilson(hits, total);
        return string.Create(CultureInfo.InvariantCulture, $"| {label} | {hits}/{total} = {value:P1} | {low:P1}–{high:P1} | {target} |");
    }

    private static List<string> Files(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.Ordinal).ToList()
            : [];
}

[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class WilsonIntervalTests
{
    [Fact]
    public void Wilson_interval_matches_the_reference_values()
    {
        // 70/100: Wilson 95% interval 0.604-0.781 (reference: standard formula).
        var (value, low, high) = CullingQualityTests.Wilson(70, 100);
        Assert.Equal(0.70, value, 6);
        Assert.Equal(0.6041, low, 3);
        Assert.Equal(0.7810, high, 3);
        Assert.Equal(0, CullingQualityTests.Wilson(0, 30).Low);
    }
}
