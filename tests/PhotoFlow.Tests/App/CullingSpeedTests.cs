// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Culling of thousands of photos (M9) on the demo catalog: the next large photos are decoded in advance, Z shows the eyes
/// at 100%, the candidate stays in the comparison while the burst passes beside it.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class CullingSpeedTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-culling");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Large_photos_are_prefetched_and_the_loupe_finds_the_eyes()
    {
        var (host, byFile, _) = await DocsScreenshots.PrepareAsync(_temp.Path, Ct);
        await using var _ = host;
        var job = (await host.Catalog.Jobs.ListRecentAsync(1, Ct))[0];
        var review = new ReviewViewModel(host);
        await review.OpenJobAsync(job.Id);
        var photos = review.Rows.OfType<ReviewRowViewModel>().SelectMany(r => r.Items).ToList();

        // The group photo of the beach: three faces.
        var group = photos.First(p => p.AnalyzedAssetId == byFile[DemoCatalog.GroupPhoto]);
        review.SelectCommand.Execute(group);
        review.ToggleLargePhotoCommand.Execute(null);
        await Wait(() => review.LargeImage is { PixelSize.Width: > 1000 }, 30);
        await review.ToggleLoupeCommand.ExecuteAsync(null);
        Assert.True(review.IsLoupe);
        Assert.Equal(PhotoFlow.App.Localization.Strings.Get("Review.Loupe.Eyes"), review.LoupeText);
        await review.ToggleLoupeCommand.ExecuteAsync(null);
        Assert.False(review.IsLoupe);

        // The next photo was decoded while this one was shown: it appears at once, in full.
        await Task.Delay(1500, Ct);
        Dispatcher.UIThread.RunJobs();
        review.MoveCommand.Execute("1,0");
        Assert.True(review.LargeImage is { PixelSize.Width: > 1000 });
    }

    [AvaloniaFact]
    public async Task The_candidate_stays_while_the_burst_passes()
    {
        var (host, _, _) = await DocsScreenshots.PrepareAsync(_temp.Path, Ct);
        await using var _ = host;
        var job = (await host.Catalog.Jobs.ListRecentAsync(1, Ct))[0];
        var review = new ReviewViewModel(host);
        await review.OpenJobAsync(job.Id);
        var burst = review.Rows.OfType<ReviewRowViewModel>().SelectMany(r => r.Items).Where(p => p.Group is not null).GroupBy(p => p.Group).First().ToList();
        Assert.True(burst.Count >= 3);

        review.SelectCommand.Execute(burst[0]);
        review.ToggleCompared(burst[0]);
        review.ToggleCompared(burst[1]);
        review.CompareCommand.Execute(null);
        Assert.Equal(2, review.CompareItems.Count);
        review.PinCandidateCommand.Execute(null);
        Assert.True(review.CompareItems[0].IsPinned);

        review.MoveCommand.Execute("1,0"); // focus on the second photo
        review.MoveCommand.Execute("1,0"); // past the last: the third comes in place of the second
        Assert.Equal([burst[0], burst[2]], review.CompareItems.Select(c => c.Photo));
        Assert.True(review.CompareItems[0].IsPinned);
        Assert.Equal(burst[2], review.Selected);
    }

    private static async Task Wait(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }

    public void Dispose() => _temp.Dispose();
}
