// SPDX-License-Identifier: Apache-2.0

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoFlow.App.Localization;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.App;

/// <summary>
/// Faces on a photo in Consultazione (spec §7, 13 Oct 2026): the large preview boxes and names the faces and says who
/// is on the photo ("?" and "non riconosciuto" for the others); V hides the boxes; N then a box dragged with the mouse
/// adds a face where none was found, for a person chosen from the list (here a new one): the user's face (I5).
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class BrowseFacesTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-browse-faces");

    [AvaloniaFact]
    public async Task A_face_is_drawn_and_given_a_person()
    {
        var ct = CancellationToken.None;
        await using var host = await PhotoFlowHost.StartAsync(new DefaultAppPaths(_temp.Sub("profile")), ct);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true }, ct);
        var catalog = host.Catalog;
        var root = _temp.Sub("lib");
        var library = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.Import, null), ct);
        var asset = await catalog.Assets.AddAsync(new Asset(0, "q1", "f1", "JPG", "jpeg", 800, 600, DateTimeOffset.UtcNow, DateSource.Exif, null, AssetState.Active), ct);
        SyntheticImages.WriteJpeg(Path.Combine(root, "p.jpg"), 800, 600);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, asset, library, "p.jpg", DateTimeOffset.UtcNow), ct);
        var thumbnail = host.Thumbnails.PathFor("f1");
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
        SyntheticImages.WriteJpeg(thumbnail, 800, 600);

        // One face found and recognized (Ripa), one not recognized.
        var ripa = await catalog.People.CreatePersonAsync("Ripa", ct);
        await catalog.Analysis.ReplaceAutomaticFacesAsync(asset,
        [
            new Face(0, asset, new PhotoFlow.Core.Analysis.BoundingBox(0.1f, 0.1f, 0.25f, 0.35f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "t"),
            new Face(0, asset, new PhotoFlow.Core.Analysis.BoundingBox(0.4f, 0.1f, 0.55f, 0.35f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "t"),
        ], ct);
        await catalog.People.AssignManualAsync([(await catalog.Analysis.GetFacesAsync(asset, ct))[0].Id], ripa, ct);

        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.ShowSection(SectionCatalog.Browse);
        var browse = Assert.IsType<BrowseViewModel>(shell.CurrentPage);
        await WaitAsync(() => browse.FaceBoxes.Count == 2 && browse.DetailImage is not null);
        Assert.Equal(["Ripa", "?"], browse.FaceBoxes.Select(f => f.Label));
        Assert.Equal("Ripa · " + Strings.Format("Faces.UnknownOne", 1), browse.DetailPeople);

        // N: the large preview, then a box dragged on the right of the photo.
        await browse.StartAddFaceCommand.ExecuteAsync(null);
        Assert.True(browse.IsLargePreview && browse.IsDrawingFace);
        Dispatcher.UIThread.RunJobs();
        var photo = window.GetVisualDescendants().OfType<FacesPhotoView>().Single(v => v.Name == "LargePhoto");
        var surface = photo.GetVisualDescendants().OfType<Canvas>().First(c => c.Name == "Surface");
        Point At(double x, double y) => surface.TranslatePoint(new Point(x * photo.ImageWidth, y * photo.ImageHeight), window)!.Value;
        window.MouseDown(At(0.7, 0.2), MouseButton.Left);
        window.MouseMove(At(0.8, 0.3));
        window.MouseMove(At(0.85, 0.45));
        window.MouseUp(At(0.85, 0.45), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(browse.IsChoosingFacePerson);
        var drawn = browse.DrawnFace!.Value;
        Assert.InRange(drawn.X1, 0.69, 0.71);
        Assert.InRange(drawn.Y2, 0.44, 0.46);

        // The person: a new one.
        Assert.Contains(browse.FacePeople, p => p.Id == ripa);
        browse.FacePerson = browse.FacePeople[0];
        Assert.False(browse.CanSaveFace);
        browse.NewFacePersonName = "Luca";
        Save(window, "faces-1-draw");
        await browse.SaveFaceCommand.ExecuteAsync(null);
        await WaitAsync(() => browse.FaceBoxes.Count == 3);
        Assert.False(browse.IsDrawingFace);
        Assert.Equal("Ripa, Luca · " + Strings.Format("Faces.UnknownOne", 1), browse.DetailPeople);
        var added = (await catalog.Analysis.GetFacesAsync(asset, ct)).Single(f => f.ModelVersion == FaceDefaults.ManualModelVersion);
        Assert.Equal(AssignmentOrigin.Manual, added.AssignmentOrigin);
        Save(window, "faces-2-saved");

        // A click on the "?" face: who is it? Ripa, from the list.
        browse.IsLargePreview = true;
        Dispatcher.UIThread.RunJobs();
        var unknown = browse.FaceBoxes.Single(f => f.Name is null);
        var center = surface.TranslatePoint(new Point(unknown.X + (unknown.Width / 2), unknown.Y + (unknown.Height / 2)), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await WaitAsync(() => browse.IdentifyingFace is not null && browse.FacePeople.Count > 0);
        Assert.Equal(unknown.FaceId, browse.IdentifyingFace!.FaceId);
        Assert.False(browse.CanSaveFace); // a person first
        browse.FacePerson = browse.FacePeople.Single(p => p.Id == ripa);
        Save(window, "faces-3-identify");
        await browse.SaveFaceCommand.ExecuteAsync(null);
        await WaitAsync(() => browse.FaceBoxes.All(f => f.Name is not null));
        Assert.Equal("Ripa, Luca", browse.DetailPeople);
        var identified = (await catalog.Analysis.GetFacesAsync(asset, ct)).Single(f => f.Id == unknown.FaceId);
        Assert.Equal((ripa, AssignmentOrigin.Manual), (identified.PersonId!.Value, identified.AssignmentOrigin!.Value));

        // V hides the boxes.
        browse.ToggleFacesCommand.Execute(null);
        Assert.False(browse.ShowFaces);
    }

    public void Dispose() => _temp.Dispose();

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        if (frame is not null && Environment.GetEnvironmentVariable("PHOTOFLOW_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
