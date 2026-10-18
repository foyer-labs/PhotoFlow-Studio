// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Phase 2 acceptance criterion "Revisione interamente utilizzabile da tastiera": a real in-place analysis of photos
/// shot in bursts (EXIF written by exiftool), then the whole review only with keys — navigation, decisions, stars,
/// undo, comparison with zoom and synchronization, groups, filters, sizes, panel, confirmation and XMP writing.
/// Set PHOTOFLOW_SCREENSHOTS to a folder outside the repository to save the boards.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ReviewKeyboardTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 12, 15, 0, 0);
    private readonly TempFolder _temp = new("pf-review-keys");

    [AvaloniaFact]
    public async Task A_review_is_completed_with_the_keyboard_only()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        var archive = _temp.Sub("archive");
        await WriteBurstsAsync(host, archive);
        var libraryId = await host.Catalog.Libraries.AddAsync(new Library(0, "Archivio", archive, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        var jobId = await host.StartArchiveAnalysisAsync(libraryId, "events", ArchiveOrder.ByFolder, CancellationToken.None);
        await WaitAsync(() => !host.Jobs.IsRunning(jobId));
        Assert.Equal(JobState.Completed, (await host.Catalog.Jobs.GetAsync(jobId, CancellationToken.None))!.State);
        Assert.Equal(2, (await host.Catalog.Groups.ListAsync(jobId, CancellationToken.None)).Count(g => g.Group.Type == GroupType.Burst));

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.OpenReview(jobId);
        var review = Assert.IsType<ReviewViewModel>(shell.CurrentPage);
        await Settle(review.OpenJobAsync(jobId));
        Assert.Equal(9, review.Filters[0].Count);
        Assert.Equal(2, review.Rows.OfType<ReviewHeaderRow>().Count());
        Focus(window);
        Save(window, "2a-review-bursts");

        // Navigation: ← → in shooting order, ↓ to the row below, PageDown to the next burst.
        var first = review.Selected!;
        Press(window, PhysicalKey.ArrowRight);
        Assert.NotEqual(first, review.Selected);
        Press(window, PhysicalKey.ArrowLeft);
        Assert.Equal(first, review.Selected);
        Press(window, PhysicalKey.PageDown);
        Assert.NotNull(review.Selected!.Group);
        var burst = review.Selected.Group!;

        // Decisions: X, P, stars, undo.
        Press(window, PhysicalKey.X);
        Assert.Equal(CullingOutcome.Discard, review.Selected!.Outcome);
        Press(window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.False(review.Selected!.IsManual);
        Press(window, PhysicalKey.Digit3);
        Assert.Equal(3, review.Selected!.Stars);

        // Comparison (2b): C opens the burst, → moves the focus, P makes it the best and the others similar.
        Press(window, PhysicalKey.C);
        Assert.True(review.IsCompare);
        Assert.Equal(Math.Min(4, burst.Size), review.CompareItems.Count);
        await WaitAsync(() => review.CompareItems.All(c => c.Preview is not null));
        Press(window, PhysicalKey.ArrowRight);
        var chosen = review.Selected!;
        Press(window, PhysicalKey.P);
        Assert.Equal(CullingOutcome.Best, chosen.Outcome);
        Assert.All(review.CompareItems.Select(c => c.Photo).Where(p => p != chosen && p.Outcome != CullingOutcome.Discard), p => Assert.Equal(CullingOutcome.Similar, p.Outcome));
        Press(window, PhysicalKey.Z);
        Assert.Equal(1.0, review.Zoom);
        Press(window, PhysicalKey.S);
        Assert.False(review.IsZoomSynced);
        Press(window, PhysicalKey.S);
        Press(window, PhysicalKey.F);
        Assert.Null(review.Zoom);
        Save(window, "2b-compare");
        Press(window, PhysicalKey.Escape);
        Assert.True(review.IsGrid);

        // Filters (Alt+digit), sizes (Ctrl +/−), panel (Tab).
        Press(window, PhysicalKey.Digit4, RawInputModifiers.Alt);
        Assert.True(review.Filters[4].IsActive);
        Assert.All(review.Rows.OfType<ReviewRowViewModel>().SelectMany(r => r.Items), i => Assert.Equal(CullingOutcome.Similar, i.Outcome));
        Press(window, PhysicalKey.Digit0, RawInputModifiers.Alt);
        Assert.True(review.Filters[0].IsActive);
        Press(window, PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(ReviewViewModel.CellSizes[2], review.CellSize);
        Press(window, PhysicalKey.Minus, RawInputModifiers.Control);
        Press(window, PhysicalKey.Tab);
        Assert.False(review.IsDetailsOpen);
        Press(window, PhysicalKey.Tab);
        Assert.True(review.IsDetailsOpen);

        // P21: Space opens the photo large; there T and D fix tags and caption, a click on a face says who it is,
        // Maiusc N starts a face by hand, ← → go on with the photo still large, Esc goes back to the grid.
        var photo = first;
        review.SelectCommand.Execute(photo);
        await WaitAsync(() => review.SelectedSignals.Any(s => s.Label == Strings.Get("Signal.Brightness"))); // exposure of the whole photo too
        await host.Catalog.Analysis.ReplaceAutomaticFacesAsync(photo.AnalyzedAssetId,
            [new Face(0, photo.AnalyzedAssetId, new PhotoFlow.Core.Analysis.BoundingBox(0.3f, 0.2f, 0.6f, 0.7f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "t")],
            CancellationToken.None);
        await review.Editor.LoadAsync(photo.AnalyzedAssetId); // the face arrived after the photo was selected
        Press(window, PhysicalKey.Space);
        Assert.True(review.IsLargePhoto);
        await WaitAsync(() => review.LargeImage is not null);
        await WaitAsync(() => review.Editor.FaceBoxes.Count == 1);
        Press(window, PhysicalKey.T);
        Assert.True(review.Editor.IsTagging);
        review.Editor.TagText = "Monza 2026";
        await review.Editor.ApplyTagCommand.ExecuteAsync(null);
        Assert.Contains(review.Editor.Tags, t => t.Label == "Monza 2026" && t.IsManual);
        Press(window, PhysicalKey.D);
        Assert.True(review.Editor.IsEditingCaption);
        review.Editor.CaptionText = "La partenza";
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => review.Editor.CaptionLine == "La partenza");
        Assert.Equal(CaptionOrigin.Manual, (await host.Catalog.Captions.GetAsync(photo.AnalyzedAssetId, CancellationToken.None))!.Origin);

        Dispatcher.UIThread.RunJobs();
        var large = window.GetVisualDescendants().OfType<FacesPhotoView>().Single(v => v.Name == "Photo");
        var surface = large.GetVisualDescendants().OfType<Canvas>().First(c => c.Name == "Surface");
        var face = review.Editor.FaceBoxes.Single();
        var center = surface.TranslatePoint(new Avalonia.Point(face.X + (face.Width / 2), face.Y + (face.Height / 2)), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await WaitAsync(() => review.Editor.IdentifyingFace is not null && review.Editor.FacePeople.Count > 0);
        review.Editor.FacePerson = review.Editor.FacePeople[0]; // + Nuova persona…
        review.Editor.NewFacePersonName = "Giulia";
        Save(window, "2i-large-photo");
        await review.Editor.SaveFaceCommand.ExecuteAsync(null);
        await WaitAsync(() => review.SelectedPeople.Any(p => p.Name == "Giulia") && review.Editor.FaceBoxes.Single().Name == "Giulia");
        Focus(window);

        Press(window, PhysicalKey.N, RawInputModifiers.Shift);
        await WaitAsync(() => review.Editor.IsDrawingFace);
        Press(window, PhysicalKey.Escape);
        Assert.False(review.Editor.IsDrawingFace);
        Assert.True(review.IsLargePhoto);
        Press(window, PhysicalKey.ArrowRight);
        Assert.NotEqual(photo, review.Selected);
        Assert.True(review.IsLargePhoto);
        Press(window, PhysicalKey.Escape);
        Assert.False(review.IsLargePhoto);
        Assert.True(review.IsGrid);

        // Confirmation and writing (2g, 2h): Ctrl+Enter twice.
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.True(review.IsConfirming);
        Save(window, "2g-confirm-bursts");
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitAsync(() => !review.IsWriting && review.ResultMessage is not null);
        Assert.Null(review.ResultError);
        Assert.NotEmpty(Directory.EnumerateFiles(archive, "*.xmp", SearchOption.AllDirectories)); // SidecarOnly library
        Save(window, "2h-written-bursts");

        // Every command of the review has a key in the single command table (the status bar shows the same table).
        foreach (var command in review.Commands!.Commands)
        {
            Assert.NotNull(review.ResolveCommand(command.Id));
        }

        window.Close();
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Two bursts of 3 and 4 shots (0.3 s apart) and two single shots, with EXIF times written by exiftool.</summary>
    private static async Task WriteBurstsAsync(PhotoFlowHost host, string folder)
    {
        var shots = new List<(string Name, double Seconds)>
        {
            ("A1", 0), ("A2", 0.3), ("A3", 0.6),
            ("S1", 20),
            ("B1", 40), ("B2", 40.3), ("B3", 40.6), ("B4", 40.9),
            ("S2", 70),
        };
        foreach (var (name, seconds) in shots)
        {
            var path = SyntheticImages.WriteJpeg(Path.Combine(folder, name + ".JPG"), 400, 300, name.GetHashCode(StringComparison.Ordinal));
            var time = T0.AddSeconds(seconds);
            await host.ExifTool.ExecuteAsync(
            [
                "-overwrite_original", "-Model=Z 5_2",
                "-DateTimeOriginal=" + time.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture),
                "-SubSecTimeOriginal=" + time.ToString("fff", CultureInfo.InvariantCulture),
                path,
            ], CancellationToken.None);
        }
    }

    private static void Focus(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<ReviewView>()?.Focus();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static async Task Settle(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        await task;
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("PHOTOFLOW_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, name + ".png"), new PngBitmapEncoderOptions());
        }
    }
}

internal static class VisualHelpers
{
    public static T? GetVisualDescendantsOf<T>(this Avalonia.Visual root)
        where T : Avalonia.Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().FirstOrDefault();
}
