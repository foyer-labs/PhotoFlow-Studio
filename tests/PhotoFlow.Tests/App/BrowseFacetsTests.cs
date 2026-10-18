// SPDX-License-Identifier: Apache-2.0

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>The selection of the facets: AND opens a group, OR joins the last one, a group left empty disappears.</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class TagSelectionTests
{
    [Fact]
    public void Groups_follow_AND_and_OR()
    {
        var selection = new TagSelection();
        selection.Add(1, orWithLast: true); // nothing to join: a group of its own
        selection.Add(2, orWithLast: true);
        selection.Add(3, orWithLast: false);
        selection.Add(2, orWithLast: false); // already chosen
        Assert.Equal([[1L, 2L], [3L]], selection.Groups.Select(g => g.ToArray()));
        Assert.Equal([[1L, 2L]], selection.WithoutLast.Select(g => g.ToArray()));

        selection.Remove(3);
        selection.Remove(1);
        Assert.Equal([[2L]], selection.Groups.Select(g => g.ToArray()));
        Assert.True(selection.Contains(2));
        selection.Clear();
        Assert.Empty(selection.Groups);
    }
}

/// <summary>
/// The facets panel of Consultazione (spec §12bis, 13 Oct 2026) on a real catalog: tags with the number of photos,
/// recomputed at each choice; AND keeps the photos with every group, OR adds the photos of another tag ("+N");
/// Ctrl+click does the other; events are alternatives, people must all be there; F hides the panel; "Azzera" clears.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class BrowseFacetsTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-browse-facets");

    [AvaloniaFact]
    public async Task Tags_events_and_people_filter_with_counts()
    {
        var ct = CancellationToken.None;
        await using var host = await PhotoFlowHost.StartAsync(new DefaultAppPaths(_temp.Sub("profile")), ct);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, ct);
        var catalog = host.Catalog;
        var library = await catalog.Libraries.AddAsync(new Library(0, "L", _temp.Sub("lib"), WritePolicy.SidecarOnly, false, LibraryOrigin.Import, null), ct);
        string[][] tags = [["car", "track"], ["car"], ["moto", "track"], ["moto"], ["boat"], ["car", "moto"]];
        var ids = new List<long>();
        for (var i = 0; i < tags.Length; i++)
        {
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q{i}", null, "JPG", "jpeg", 10, 10, new DateTimeOffset(2025, 5, 1 + i, 9, 0, 0, TimeSpan.Zero), DateSource.Exif, null, AssetState.Active), ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, library, $"p{i}.jpg", DateTimeOffset.UtcNow), ct);
            await catalog.Tags.ReplaceAutomaticAsync(id, TagSource.RamPlus, "r", tags[i].Select(k => (k, (double?)0.9)).ToList(), ct);
            ids.Add(id);
        }

        // Two events (Italian labels) and a person on photos 0 and 5.
        var monza = await catalog.Tags.EnsureAsync("event:Monza", TagSource.Job, "Eventi", "Monza", reviewed: true, ct);
        var mugello = await catalog.Tags.EnsureAsync("event:Mugello", TagSource.Job, "Eventi", "Mugello", reviewed: true, ct);
        for (var i = 0; i < ids.Count; i++)
        {
            await catalog.Tags.ReplaceAutomaticAsync(ids[i], TagSource.Job, null, [(i < 3 ? "event:Monza" : "event:Mugello", null)], ct);
        }

        // Luca on photos 0 (car, track) and 5 (car, moto).
        var luca = await catalog.People.CreatePersonAsync("Luca", ct);
        foreach (var i in new[] { 0, 5 })
        {
            await catalog.Analysis.ReplaceAutomaticFacesAsync(ids[i], [new Face(0, ids[i], new PhotoFlow.Core.Analysis.BoundingBox(0.3f, 0.3f, 0.5f, 0.6f), null, 0.9,
                EyeState.Open, EyeState.Open, null, null, null, null, "t")], ct);
            await catalog.People.AssignManualAsync([(await catalog.Analysis.GetFacesAsync(ids[i], ct))[0].Id], luca, ct);
        }

        var carTag = (await catalog.Tags.FindAsync("car", ct))!;
        await catalog.Tags.UpdateAsync(carTag.Id, "auto", reviewed: true, "Veicoli", blacklisted: false, ct);

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.ShowSection(SectionCatalog.Browse);
        var browse = Assert.IsType<BrowseViewModel>(shell.CurrentPage);
        var facets = browse.Facets;
        await WaitAsync(() => facets.Tags.Count > 0 && browse.Rows.Sum(r => r.Items.Count) == 6);

        // Counts over everything; the Italian label when there is one; events listed apart.
        var tagIds = new Dictionary<string, long>();
        foreach (var key in new[] { "car", "moto", "track", "boat" })
        {
            tagIds[key] = (await catalog.Tags.FindAsync(key, ct))!.Id;
        }

        FacetItemViewModel Item(string key) => facets.Tags.Single(t => t.Id == tagIds[key]);
        Assert.Equal(("auto", "3", "Veicoli"), (Item("car").Label, Item("car").CountText, Item("car").Detail));
        Assert.Equal("3", Item("moto").CountText);
        Assert.DoesNotContain(facets.Tags, t => t.Label is "Monza" or "Mugello");
        Assert.Equal(["Monza", "Mugello"], facets.Events.Select(e => e.Label).Order());
        Save(window, "facets-1-start");

        // AND: auto → 0, 1, 5; the others count within it.
        await facets.ClickTagAsync(Item("car"), other: false);
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 3 && facets.Tags.All(t => t.Id != tagIds["car"]));
        Assert.Equal(("1", "1"), (Item("moto").CountText, Item("track").CountText));
        Assert.DoesNotContain(facets.Tags, t => t.Id == tagIds["boat"]);

        // OR mode: the numbers become the photos a tag adds to (auto).
        facets.OrMode = true;
        await WaitAsync(() => facets.Tags.Any(t => t.CountText.StartsWith('+')));
        Assert.Equal(("+2", "+1", "+1"), (Item("moto").CountText, Item("track").CountText, Item("boat").CountText));
        await facets.ClickTagAsync(Item("moto"), other: false); // (auto OR moto) → 0, 1, 2, 3, 5
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 5);
        Assert.Equal([tagIds["car"], tagIds["moto"]], facets.Groups.Single().Chips.Select(c => c.Id));

        // Ctrl+click in OR mode: a new group in AND → (auto OR moto) AND track → 0 was given track, 2.
        await facets.ClickTagAsync(Item("track"), other: true);
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 2);
        Assert.Equal(2, facets.Groups.Count);
        Save(window, "facets-2-groups");

        // Events in OR: Mugello alone empties (0 and 2 are at Monza); both → back to 2.
        await facets.ToggleEventCommand.ExecuteAsync(facets.Events.Single(e => e.Label == "Monza"));
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 2 && facets.Events.Single(e => e.Label == "Monza").IsSelected);

        // Removing a chip; "Azzera" clears everything.
        await facets.RemoveTagCommand.ExecuteAsync(facets.Groups[1].Chips.Single());
        await WaitAsync(() => facets.Groups.Count == 1 && browse.Rows.Sum(r => r.Items.Count) == 3); // (auto OR moto) at Monza: 0, 1, 2
        await facets.ClearCommand.ExecuteAsync(null);
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 6 && !facets.HasSelection);

        // The large preview (double click or Space) says how to go back: ✕ Chiudi, Esc, double click.
        browse.TogglePreviewCommand.Execute(null);
        Assert.True(browse.IsLargePreview);
        Save(window, "facets-3-preview");
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<BrowseView>()?.Focus();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(browse.IsLargePreview);

        // People: Luca alone, then Luca AND moto (the person combines with the tags).
        await WaitAsync(() => facets.Persons.Any(p => p.Id == luca));
        Assert.Equal("2", facets.Persons.Single(p => p.Id == luca).CountText);
        await facets.TogglePersonCommand.ExecuteAsync(facets.Persons.Single(p => p.Id == luca));
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 2);
        facets.OrMode = false;
        await WaitAsync(() => facets.Tags.Any(t => t.Id == tagIds["moto"] && t.CountText == "1"));
        await facets.ClickTagAsync(Item("moto"), other: false);
        await WaitAsync(() => browse.Rows.Sum(r => r.Items.Count) == 1);
        Save(window, "facets-4-people");
        await facets.ClearCommand.ExecuteAsync(null);

        // F hides and shows the panel.
        Assert.True(facets.IsOpen);
        facets.ToggleCommand.Execute(null);
        Assert.False(facets.IsOpen);
        _ = (monza, mugello);
    }

    public void Dispose() => _temp.Dispose();

    private static void Save(Avalonia.Controls.Window window, string name)
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

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
