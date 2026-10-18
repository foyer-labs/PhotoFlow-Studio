// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// People (boards 4a, 4b) with the keyboard: select and merge groups, rename with a name typed in the field (the
/// letters go to the field, not to the commands), open the person page, separate a face, "not this person", back.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class PeopleKeyboardTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-people-keys");

    [AvaloniaFact]
    public async Task People_are_merged_named_and_separated_with_the_keyboard()
    {
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        await using var host = await PhotoFlowHost.StartAsync(paths, CancellationToken.None);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, CancellationToken.None);
        var ct = CancellationToken.None;

        // Three automatic groups: 4, 3 and 2 faces (one face per photo), each photo a real file of a library.
        var root = _temp.Sub("photos");
        var library = await host.Catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);
        var groups = new List<long>();
        foreach (var size in new[] { 4, 3, 2 })
        {
            var person = await host.Catalog.People.CreatePersonAsync(null, ct);
            groups.Add(person);
            var assignments = new List<(long, long, double?)>();
            for (var i = 0; i < size; i++)
            {
                var asset = await host.Catalog.Assets.AddAsync(new Asset(0, $"q{person}-{i}", $"f{person}-{i}", "JPG", "jpeg", 400, 300, new DateTimeOffset(2020 + i, 6, 1, 12, 0, 0, TimeSpan.Zero), DateSource.Exif, null, AssetState.Active), ct);
                await host.Catalog.Analysis.ReplaceAutomaticFacesAsync(asset, [new Face(0, asset, new BoundingBox(0.3f, 0.3f, 0.5f, 0.6f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "t")], ct);
                SyntheticImages.WriteJpeg(Path.Combine(root, $"p{person}-{i}.jpg"), 400, 300);
                await host.Catalog.Assets.AddLocationAsync(new AssetLocation(0, asset, library, $"p{person}-{i}.jpg", DateTimeOffset.UtcNow), ct);
                await host.Catalog.Tags.ReplaceAutomaticAsync(asset, TagSource.RamPlus, "r", [("beach", 0.9)], ct);
                var face = (await host.Catalog.Analysis.GetFacesAsync(asset, ct))[0];
                assignments.Add((face.Id, person, 0.8 - (i * 0.1)));
            }

            await host.Catalog.People.AssignAutomaticAsync(assignments, ct);
        }

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.SelectedSection = SectionCatalog.All.First(s => s.Id == SectionCatalog.People);
        var people = Assert.IsType<PeopleViewModel>(shell.CurrentPage);
        await Settle(people.ActivateAsync());
        Focus<PeopleView>(window);
        Assert.False(people.IsEmpty);
        Assert.Equal(PeopleFilter.Unnamed, people.Filter); // no import job: "new" is empty
        Assert.Equal([4, 3, 2], people.Persons.Select(p => p.Summary.FaceCount));

        // Space selects, → moves, Space selects again, M merges into the largest.
        Press(window, PhysicalKey.Space);
        Press(window, PhysicalKey.ArrowRight);
        Press(window, PhysicalKey.Space);
        Assert.True(people.HasSelection);
        Press(window, PhysicalKey.M);
        await WaitAsync(() => people.Persons.Count == 2);
        Assert.Equal([7, 2], people.Persons.Select(p => p.Summary.FaceCount));

        // F2 opens the name field: typed letters (M, I, S...) go to the field, Enter applies.
        Press(window, PhysicalKey.F2);
        Assert.True(people.IsRenaming);
        Press(window, PhysicalKey.I); // would ignore the group if it reached the page
        window.KeyTextInput("Mia Simi");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, people.Persons.Count);
        Assert.DoesNotContain((await host.Catalog.People.ListPersonsAsync(ct)), p => p.Person.Ignored);
        people.RenameText = "Mia Simi";
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => !people.IsRenaming && people.Persons.All(p => p.Id != groups[0]));
        var mia = (await host.Catalog.People.ListPersonsAsync(ct)).Single(p => p.Person.Name == "Mia Simi");
        Assert.Equal(7, mia.FaceCount);
        Assert.All(await host.Catalog.People.ListFacesOfPersonAsync(mia.Person.Id, ct), f => Assert.Equal(AssignmentOrigin.Manual, f.AssignmentOrigin));

        // Named filter, Enter opens the person page.
        Press(window, PhysicalKey.Digit3, RawInputModifiers.Alt);
        await WaitAsync(() => people.IsNamedFilter && people.Persons.Count == 1);
        Press(window, PhysicalKey.Enter);
        var page = Assert.IsType<PersonPageViewModel>(shell.CurrentPage);
        await WaitAsync(() => page.Years.Sum(y => y.Faces.Count) == 7);
        Focus<PersonPageView>(window);
        Assert.Equal("Mia Simi", page.Name);

        // S separates the focused face into a new person; Del takes another away ("not this person").
        Press(window, PhysicalKey.S);
        await WaitAsync(() => page.Years.Sum(y => y.Faces.Count) == 6);
        Press(window, PhysicalKey.Delete);
        await WaitAsync(() => page.Years.Sum(y => y.Faces.Count) == 5);
        var persons = await host.Catalog.People.ListPersonsAsync(ct);
        Assert.Contains(persons, p => p.Person.Name is null && p.FaceCount == 1);
        Assert.Equal(5, persons.Single(p => p.Person.Id == mia.Person.Id).FaceCount);

        // Enter (↵ Apri foto, board 4b): the photo of the focused face with the face boxed; Esc goes back to the faces.
        Press(window, PhysicalKey.Enter);
        await WaitAsync(() => page.IsShowingPhoto);
        Assert.Equal((400d, 300d), (page.PhotoWidth, page.PhotoHeight));
        Assert.Equal((120d, 90d, 80d, 90d), (page.FaceLeft, page.FaceTop, page.FaceWidth, page.FaceHeight), new ToleranceComparer());
        Assert.EndsWith(".jpg", page.PhotoTitle, StringComparison.Ordinal);

        // Where it is, its tags (Italian when available); V hides and shows the face boxes.
        Assert.StartsWith(root, page.PhotoPath, StringComparison.OrdinalIgnoreCase);
        var beach = (await host.Catalog.Tags.FindAsync("beach", ct))!;
        Assert.Equal([beach.Italian ?? beach.English], page.PhotoTags);
        Assert.Contains("Mia Simi", page.PhotoPeople, StringComparison.Ordinal);
        SaveFrame(window, "people-photo");
        Assert.True(page.ShowFaceBox);
        Press(window, PhysicalKey.V);
        Assert.False(page.ShowFaceBox);
        Press(window, PhysicalKey.V);
        Assert.True(page.ShowFaceBox);
        Assert.Single(page.PhotoFaces, f => f.IsCurrent && f.Label == "Mia Simi");
        Press(window, PhysicalKey.Escape);
        Assert.False(page.IsShowingPhoto);
        Assert.IsType<PersonPageViewModel>(shell.CurrentPage); // Esc closed the photo, not the page

        // Ctrl M: a list of the people (new person first); a new one by name, then an existing one from the list.
        Assert.Equal(1, page.ActionCount); // the focused face, clearly marked
        Press(window, PhysicalKey.M, RawInputModifiers.Control);
        await WaitAsync(() => page.IsMoving && page.MoveTargets.Count > 0);
        Assert.Null(page.MoveTargets[0].Id);
        Assert.DoesNotContain(page.MoveTargets, t => t.Id == mia.Person.Id);
        page.MoveTarget = page.MoveTargets[0];
        Assert.False(page.CanConfirmMove); // a new person needs a name
        page.NewPersonName = "Luca Neri";
        SaveFrame(window, "people-move");
        await page.ConfirmMoveCommand.ExecuteAsync(null);
        await WaitAsync(() => page.Years.Sum(y => y.Faces.Count) == 4 && !page.IsMoving);
        var luca = (await host.Catalog.People.ListPersonsAsync(ct)).Single(p => p.Person.Name == "Luca Neri");
        Assert.Equal(1, luca.FaceCount);

        Focus<PersonPageView>(window);
        Press(window, PhysicalKey.M, RawInputModifiers.Control);
        await WaitAsync(() => page.IsMoving && page.MoveTargets.Any(t => t.Id == luca.Person.Id));
        page.MoveTarget = page.MoveTargets.Single(t => t.Id == luca.Person.Id);
        await page.ConfirmMoveCommand.ExecuteAsync(null);
        await WaitAsync(() => page.Years.Sum(y => y.Faces.Count) == 3);
        Assert.Equal(2, (await host.Catalog.People.ListPersonsAsync(ct)).Single(p => p.Person.Id == luca.Person.Id).FaceCount);

        // F2 then a name typed letter by letter: every letter stays (a refocus at each letter used to keep only one).
        Focus<PersonPageView>(window);
        Press(window, PhysicalKey.F2);
        Assert.True(page.IsEditing);
        foreach (var letter in "Anna")
        {
            window.KeyTextInput(letter.ToString());
            for (var i = 0; i < 10; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
        }

        Assert.EndsWith("Anna", page.EditText, StringComparison.Ordinal);
        page.BackCommand.Execute(null); // leave the rename

        // Esc goes back to the list.
        Focus<PersonPageView>(window);
        Press(window, PhysicalKey.Escape);
        Assert.IsType<PeopleViewModel>(shell.CurrentPage);
    }

    public void Dispose() => _temp.Dispose();

    private sealed class ToleranceComparer : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) x, (double, double, double, double) y) =>
            Math.Abs(x.Item1 - y.Item1) < 0.5 && Math.Abs(x.Item2 - y.Item2) < 0.5 && Math.Abs(x.Item3 - y.Item3) < 0.5 && Math.Abs(x.Item4 - y.Item4) < 0.5;

        public int GetHashCode((double, double, double, double) obj) => 0;
    }

    private static void SaveFrame(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        if (frame is not null && Environment.GetEnvironmentVariable("PHOTOFLOW_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static void Focus<TView>(Window window)
        where TView : Control
    {
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendantsOf<TView>()?.Focus();
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
