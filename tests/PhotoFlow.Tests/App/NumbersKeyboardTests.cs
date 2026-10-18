// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Numbers view (2c) with the keyboard: N from the review opens it on the doubts; 9 + Enter resolves "6 or 9",
/// Ctrl Z undoes it, ↓ + Enter takes the first candidate; a number typed on a burst goes to the rest of the burst;
/// Del says there is no number; the user's numbers survive a new match with an edited list (I5); Esc goes back.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class NumbersKeyboardTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-numbers-keys");

    [AvaloniaFact]
    public async Task Doubts_are_resolved_undone_propagated_and_kept()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        var ct = CancellationToken.None;
        var catalog = host.Catalog;

        // Photos 0-1: car 9 then a doubt "6 or 9" alone; 2-3: a burst with nothing readable; 4: a sponsor's number.
        var box = new BoundingBox(0.2f, 0.2f, 0.7f, 0.8f);
        var assets = new List<long>();
        string[][] texts = [["9"], ["6"], ["---"], ["---"], ["348"]];
        double[] confidences = [0.95, 0.5, 0.4, 0.4, 0.95];
        for (var i = 0; i < texts.Length; i++)
        {
            var asset = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", 10, 10, null, DateSource.Unknown, null, AssetState.Active), ct);
            assets.Add(asset);
            var thumbnail = host.Thumbnails.PathFor($"q{i}");
            Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
            SyntheticImages.WriteScene(thumbnail, 480, 320, (char)('a' + i));
            await catalog.Motorsport.ReplaceReadingsAsync(asset, texts[i].Select(t => new OcrReading(asset, 0, box, box, t, confidences[i], "ocr@1")).ToList(), ct);
        }

        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.Import, "src", "Monza", null, "motorsport", false, null, JobState.Completed, JobPriority.Normal, "{}", DateTimeOffset.UtcNow), ct);
        await catalog.Jobs.AddItemsAsync(jobId, assets.Select((a, i) => new JobItem(0, jobId, a, $"p{i}.jpg", null, null, PipelineStage.Decision, JobItemState.Done, null)).ToList(), ct);
        await catalog.Groups.ReplaceAutomaticAsync(jobId, [(GroupType.Burst, (IReadOnlyList<long>)[assets[2], assets[3]])], ct);
        var list = await catalog.Motorsport.CreateListAsync("Monza", null, ct);
        await catalog.Motorsport.ReplaceEntriesAsync(list, [new Entry(0, list, "9", ["Paolo Galli"], "Galli Racing", null, null), new Entry(0, list, "6", ["Andrea Neri"], "Neri Motorsport", null, null)], ct);
        await catalog.Motorsport.SetJobListAsync(jobId, list, ct);
        await new NumbersStage(catalog).RunAsync(new JobContext(jobId, JobType.Import, "motorsport"), null, ct);

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.OpenReview(jobId);
        var review = Assert.IsType<ReviewViewModel>(shell.CurrentPage);
        await WaitAsync(() => review.JobId == jobId);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<ReviewView>()?.Focus();
        Press(window, PhysicalKey.N);
        var numbers = Assert.IsType<NumbersViewModel>(shell.CurrentPage);
        await WaitAsync(() => numbers.Current is not null);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<NumbersView>()?.Focus();

        await WaitAsync(() => numbers.Photos.All(p => p.Crop is not null));
        Save(window, "2c-numbers");

        // The doubt comes first.
        Assert.Equal(NumberListKind.ToResolve, numbers.Group?.Kind);
        Assert.Equal($"6{Strings.Get("Numbers.Or")}9", numbers.Group?.Title);
        Assert.Equal(assets[1], numbers.Current?.AssetId);
        Assert.Equal(["6", "9"], numbers.Candidates.Select(c => c.Number).Order(StringComparer.Ordinal));

        // 9 + Enter: the doubt is resolved, the photo is car 9, by the user.
        Press(window, PhysicalKey.Digit9);
        Assert.Equal("9", numbers.Typed);
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => numbers.ToResolve.Count == 0);
        Assert.Equal(("9", NumberOrigin.Manual), await MainNumber(assets[1]));
        Assert.Contains("number:Numero 9", await Tags(assets[1]));

        // Ctrl Z: the doubt is back and selected.
        Press(window, PhysicalKey.Z, RawInputModifiers.Control);
        await WaitAsync(() => numbers.ToResolve.Count == 1 && numbers.Current?.AssetId == assets[1]);
        Assert.Equal(NumberState.ToReview, (await catalog.Motorsport.GetNumbersAsync(assets[1], ct)).Single().State);
        Assert.DoesNotContain("number:Numero 9", await Tags(assets[1]));

        // ↓ + Enter: the first candidate.
        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal("6", numbers.Typed);
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => numbers.ToResolve.Count == 0);
        Assert.Equal(("6", NumberOrigin.Manual), await MainNumber(assets[1]));

        // The burst with no readable number: 4 4 Enter on its first photo goes to the second one too.
        await numbers.SelectGroupCommand.ExecuteAsync(numbers.Other.Single(g => g.Key == "none"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(assets[2], numbers.Current?.AssetId);
        Assert.True(numbers.Propagate);
        Press(window, PhysicalKey.Digit4);
        Press(window, PhysicalKey.Digit4);
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => numbers.Other.Single(g => g.Key == "none").Vehicles.Count == 0);
        Assert.Equal(("44", NumberOrigin.Manual), await MainNumber(assets[2]));
        Assert.Equal(("44", NumberOrigin.Manual), await MainNumber(assets[3]));

        // Del on the sponsor's photo: no number.
        await numbers.SelectGroupCommand.ExecuteAsync(numbers.Other.Single(g => g.Key == "out"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(assets[4], numbers.Current?.AssetId);
        Press(window, PhysicalKey.Delete);
        await WaitAsync(() => numbers.Other.Single(g => g.Key == "none").Vehicles.Count == 1);
        Assert.Equal((null, NumberOrigin.Manual), await MainNumber(assets[4]));

        // Car 44 added to the list: numbers matched again, the user's ones kept, 44 now has its driver.
        numbers.EditCommand.Execute(null);
        Assert.True(numbers.IsEditing);
        numbers.EditText += "44;Luca Sala;Autodromo Racing\n";
        Save(window, "2c-numbers-list-edit");
        await numbers.SaveEditCommand.ExecuteAsync(null);
        await WaitAsync(() => !numbers.IsEditing && numbers.InList.Any(g => g.Title == "44" && g.Vehicles.Count == 2));
        Assert.Equal(("6", NumberOrigin.Manual), await MainNumber(assets[1]));
        Assert.Contains("driver:Luca Sala", await Tags(assets[3]));

        // Esc: back to the review of the job.
        Press(window, PhysicalKey.Escape);
        Assert.IsType<ReviewViewModel>(shell.CurrentPage);

        async Task<(string?, NumberOrigin)> MainNumber(long assetId)
        {
            var n = (await catalog.Motorsport.GetNumbersAsync(assetId, ct)).Single(v => v.Vehicle == 0);
            return (n.Number, n.Origin);
        }

        async Task<List<string>> Tags(long assetId) => [.. (await catalog.Tags.GetForAssetAsync(assetId, ct)).Select(t => t.Tag.Key)];
    }

    public void Dispose() => _temp.Dispose();

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("PHOTOFLOW_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
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

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
