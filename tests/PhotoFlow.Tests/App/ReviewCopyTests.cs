// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless.XUnit;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// A job with an exact copy of a photo in another folder (the demo catalog keeps one in «Copia di sicurezza»): two
/// items of the same asset. The review opens with its title and its groups; the copy once broke the grouping and the
/// review stayed without title, bursts and «Sviluppa le migliori».
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ReviewCopyTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-review-copy");

    [AvaloniaFact]
    public async Task A_job_with_an_exact_copy_opens_in_review()
    {
        var (host, _, _) = await DocsScreenshots.PrepareAsync(_temp.Path, CancellationToken.None);
        await using var _ = host;
        var job = (await host.Catalog.Jobs.ListRecentAsync(1, CancellationToken.None))[0];
        var items = await host.Catalog.Jobs.GetItemsAsync(job.Id, CancellationToken.None);
        Assert.Contains(items.GroupBy(i => i.AssetId), g => g.Count() > 1);

        var review = new ReviewViewModel(host);
        await review.OpenJobAsync(job.Id);

        Assert.NotEmpty(review.JobTitle);
        Assert.NotEmpty(review.JobMeta);
        Assert.True(review.HasBest);
    }

    public void Dispose() => _temp.Dispose();
}
