// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless.XUnit;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Search;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Multiple selection (M3) and colour labels (M4) on the demo catalog: P, X, stars and labels on all the selected photos
/// with one undo, the selection sent to Develop, the edits in bulk of Browse, the label in the XMP and in the search.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class MultipleSelectionTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-multi");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Review_acts_on_all_the_selected_photos()
    {
        var (host, _, _) = await DocsScreenshots.PrepareAsync(_temp.Path, Ct);
        await using var _ = host;
        var job = (await host.Catalog.Jobs.ListRecentAsync(1, Ct))[0];
        var review = new ReviewViewModel(host);
        await review.OpenJobAsync(job.Id);
        var photos = review.Rows.OfType<ReviewRowViewModel>().SelectMany(r => r.Items).Take(4).ToList();
        Assert.Equal(4, photos.Count);

        // Ctrl+click: the photo already selected joins the selection with the clicked one.
        review.SelectCommand.Execute(photos[0]);
        review.ToggleMark(photos[1]);
        review.ToggleMark(photos[2]);
        Assert.Equal(3, review.MarkedCount);
        Assert.True(photos[0].IsMarked);

        await review.RateCommand.ExecuteAsync("4");
        await review.SetLabelCommand.ExecuteAsync("Green");
        Assert.All(photos.Take(3), p => Assert.Equal(4, p.Stars));
        Assert.All(photos.Take(3), p => Assert.Equal("Green", p.ColorLabel));
        Assert.Null(photos[3].ColorLabel);
        Assert.Equal(3, review.Filters.Single(f => f.ColorLabel == "Green").Count);

        // The same key again takes the label off; Ctrl+Z puts it back, on all of them.
        await review.SetLabelCommand.ExecuteAsync("Green");
        Assert.All(photos.Take(3), p => Assert.Null(p.ColorLabel));
        await review.UndoCommand.ExecuteAsync(null);
        Assert.All(photos.Take(3), p => Assert.Equal("Green", p.ColorLabel));

        // The selection goes to Develop instead of the best photos.
        Assert.Equal(Strings3(3), review.DevelopBestText);
        Assert.True(await review.SendToWorkingSetAsync(forExport: false));
        Assert.Equal(3, host.WorkingSet.AssetIds.Count);

        // Esc clears the selection; P then acts on the selected photo only.
        review.EscapeCommand.Execute(null);
        Assert.Equal(0, review.MarkedCount);
        var selected = review.Selected!;
        var before = photos.Where(p => p != selected).ToDictionary(p => p, p => p.Outcome);
        await review.DiscardCommand.ExecuteAsync(null);
        Assert.Equal(CullingOutcome.Discard, selected.Outcome);
        Assert.All(before, b => Assert.Equal(b.Value, b.Key.Outcome));
    }

    [AvaloniaFact]
    public async Task Browse_edits_in_bulk_and_finds_by_label()
    {
        var (host, byFile, _) = await DocsScreenshots.PrepareAsync(_temp.Path, Ct);
        await using var _ = host;
        var ids = new[] { byFile["DSC_0401.jpg"], byFile["DSC_0402.jpg"], byFile["DSC_0420.jpg"] };

        Assert.True(await BulkEdits.SetStarsAsync(host, ids, 3, Ct));
        Assert.True(await BulkEdits.SetLabelAsync(host, ids[..2], "Blue", Ct));
        Assert.True(await BulkEdits.AddTagAsync(host, ids, "consegna Rossi", Ct));
        foreach (var id in ids)
        {
            Assert.Equal(3, (await host.Catalog.Decisions.GetAsync(id, Ct))!.Rating);
            Assert.Contains(await host.Catalog.Tags.GetForAssetAsync(id, Ct), t => t.Tag.Italian == "consegna Rossi" || t.Tag.Key == "consegna Rossi");
        }

        var hits = await host.Catalog.Search.SearchAsync(SearchQuery.Empty with { Label = "Blue", Limit = 100 }, Ct);
        Assert.Equal(ids[..2].Order(), hits.Hits.Select(h => h.AssetId).Order());
    }

    [Fact]
    [Trait(TestCategory.Name, TestCategory.Unit)]
    public void The_users_label_wins_over_the_automatic_red()
    {
        var decision = new Decision(1, 1, CullingOutcome.ToReview, [], null, null, null, "Blue");
        Assert.Equal("Blue", XmpPublisher.ChangesFor(decision, prudentMode: true, writtenBefore: false)!.Label);
        Assert.Equal(XmpPublisher.RedLabel, XmpPublisher.ChangesFor(decision with { Label = null }, prudentMode: true, writtenBefore: false)!.Label);
    }

    private static string Strings3(int n) => PhotoFlow.App.Localization.Strings.Format("Review.DevelopMarked", n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

    public void Dispose() => _temp.Dispose();
}
