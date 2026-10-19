// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless.XUnit;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Tests.Docs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>Where an event is in the work, from the Home (M10), and the versions saved by the external editor (M11).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class EventProgressUnitTests
{
    private static JobRowViewModel Row(int toReview, int chosen, int developed, int exported) =>
        new(new Job(1, JobType.Import, null, "Matrimonio", null, null, false, null, JobState.Completed, JobPriority.Normal, "{}", DateTimeOffset.UtcNow))
        {
            ToReview = toReview, Photos = 100, Chosen = chosen, Developed = developed, Exported = exported,
        };

    [Theory]
    [InlineData(4, 15, 0, 0, EventStep.Review)]
    [InlineData(0, 15, 3, 0, EventStep.Develop)]
    [InlineData(0, 15, 15, 2, EventStep.Export)]
    [InlineData(0, 15, 15, 15, EventStep.Done)]
    [InlineData(0, 0, 0, 0, EventStep.Review)]
    public void The_next_step_follows_the_work(int toReview, int chosen, int developed, int exported, EventStep step) =>
        Assert.Equal(step, Row(toReview, chosen, developed, exported).NextStep);

    [Theory]
    [InlineData("DSC_0001", true)]
    [InlineData("DSC_0001-Modifica", true)]
    [InlineData("DSC_0001_edit", true)]
    [InlineData("DSC_0001 copia", true)]
    [InlineData("DSC_00010", false)]
    [InlineData("DSC_0002", false)]
    public void Versions_have_the_name_of_the_photo(string candidate, bool version) =>
        Assert.Equal(version, EditedVersions.IsVersionName(candidate, "DSC_0001"));

    [Fact]
    public void The_versions_next_to_the_original_are_found()
    {
        using var temp = new TempFolder("pf-versions");
        string Touch(string name)
        {
            var path = Path.Combine(temp.Path, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }

        var original = Touch("DSC_0001.NEF");
        Touch("DSC_0001.xmp");
        Touch("DSC_00010.NEF");
        var psd = Touch("DSC_0001.psd");
        var tif = Touch("DSC_0001-Modifica.tif");
        var export = Touch("DSC_0001-PF-NR.dng");
        var found = EditedVersions.Find(original, [export]).Select(v => v.Path).Order().ToList();
        Assert.Equal(new[] { psd, tif }.Order(), found);
    }
}

[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class EventProgressTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-event-progress");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task The_home_says_where_each_event_is()
    {
        var (host, _, _) = await DocsScreenshots.PrepareAsync(_temp.Path, Ct);
        await using var _ = host;
        var shell = new MainWindowViewModel(host);
        using var __ = shell;
        shell.ShowSection(SectionCatalog.Home);
        var home = Assert.IsType<HomeViewModel>(shell.CurrentPage);
        await home.ActivateAsync();

        var row = Assert.Single(home.Events);
        Assert.Equal(21, row.Photos); // the backup copy is the same photo
        Assert.True(row.Chosen > 0);
        Assert.Equal(EventStep.Review, row.NextStep); // 4 photos still to review

        await home.NextStepCommand.ExecuteAsync(row);
        Assert.IsType<ReviewViewModel>(shell.CurrentPage);
    }

    public void Dispose() => _temp.Dispose();
}
