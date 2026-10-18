// SPDX-License-Identifier: Apache-2.0

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoFlow.App.Services;
using PhotoFlow.App.ViewModels;
using PhotoFlow.App.Views;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Tests.Support;
using SkiaSharp;

namespace PhotoFlow.Tests.Docs;

/// <summary>
/// Screenshots of the user guide and of the README (release 0.1.0), made with the real interface on a demo catalog:
/// stylized pictures drawn here (no photo, no real face: CLAUDE.md §8), analyzed by the real jobs with a demo
/// processor that gives realistic results (tags, captions, faces of invented people, quality). Runs only when
/// <c>PHOTOFLOW_DOCS_SCREENSHOTS</c> names the output folder.
/// </summary>
[Collection(AvaloniaCollection.Name)]
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DocsScreenshots : IDisposable
{
    private readonly TempFolder _temp = new("pf-docs");

    [AvaloniaFact]
    public async Task Screens_of_the_guide()
    {
        if (Environment.GetEnvironmentVariable("PHOTOFLOW_DOCS_SCREENSHOTS") is not { Length: > 0 } output)
        {
            Assert.Skip("Set PHOTOFLOW_DOCS_SCREENSHOTS to the output folder to make the screenshots of the guide.");
            return;
        }

        Directory.CreateDirectory(output);
        var ct = CancellationToken.None;

        // The demo lives on a virtual drive (subst), so that the screens show "P:\Foto\..." and no path of this PC.
        var drive = MountDemoDrive(_temp.Path);
        try
        {
            await ShootAsync(drive, output, ct);
        }
        finally
        {
            if (drive != _temp.Path)
            {
                RunSubst($"{drive[..2]} /d");
            }
        }
    }

    private static string MountDemoDrive(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return folder;
        }

        foreach (var letter in "PQRSTUVW")
        {
            if (!Directory.Exists($"{letter}:\\") && RunSubst($"{letter}: \"{folder}\""))
            {
                return $"{letter}:\\";
            }
        }

        return folder;
    }

    private static bool RunSubst(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("subst", arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task ShootAsync(string drive, string output, CancellationToken ct)
    {
        var root = Path.Combine(drive, "Foto");
        Directory.CreateDirectory(root);
        var shots = DemoCatalog.Shots;
        await using (var exif = ExifToolFixture.Start())
        {
            foreach (var shot in shots)
            {
                var folder = Path.Combine(root, shot.Folder);
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, shot.File);
                DemoScenes.Draw(shot.Scene, shot.Seed, path);
                await exif.ExecuteAsync(["-overwrite_original", $"-DateTimeOriginal={shot.Taken:yyyy:MM:dd HH:mm:ss}", "-Make=Nikon", "-Model=Z 5_2",
                    $"-ExposureTime={shot.Exposure}", $"-FNumber={shot.Aperture}", $"-ISO={shot.Iso}", $"-FocalLength={shot.Focal}", path], ct);
            }
        }

        // An exact copy, for the duplicates.
        Directory.CreateDirectory(Path.Combine(root, "Copia di sicurezza"));
        File.Copy(Path.Combine(root, shots[0].Folder, shots[0].File), Path.Combine(root, "Copia di sicurezza", shots[0].File));

        var paths = new DefaultAppPaths(Path.Combine(drive, "PhotoFlow"));
        await using var host = await PhotoFlowHost.StartAsync(paths, ct);
        await host.UpdateSettingsAsync(s => s with { SetupCompleted = true, Import = s.Import with { PrimaryRoot = root } }, ct);
        host.ProcessorOverride = new DemoProcessor(host);
        var catalog = host.Catalog;
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Foto", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), ct);
        var jobId = await host.StartArchiveAnalysisAsync(libraryId, "events", ArchiveOrder.ByFolder, ct);
        await WaitAsync(async () => (await catalog.Jobs.GetAsync(jobId, ct))?.State == JobState.Completed, 120);

        // Events, people and the user's touches.
        var byFile = new Dictionary<string, long>();
        foreach (var item in await catalog.Jobs.GetItemsAsync(jobId, ct))
        {
            if (item.AssetId is { } id)
            {
                byFile.TryAdd(Path.GetFileName(item.SourcePath), id);
            }
        }

        foreach (var byEvent in shots.GroupBy(s => s.Event))
        {
            var key = PhotoFlow.Tagging.Vocabulary.TaggingStage.EventPrefix + byEvent.Key;
            await catalog.Tags.EnsureAsync(key, TagSource.Job, PhotoFlow.Tagging.Vocabulary.TaggingStage.EventCategory, byEvent.Key, reviewed: true, ct);
            foreach (var shot in byEvent.Where(s => byFile.ContainsKey(s.File)))
            {
                await catalog.Tags.ReplaceAutomaticAsync(byFile[shot.File], TagSource.Job, null, [(key, null)], ct);
            }
        }

        var persons = new Dictionary<string, long>();
        foreach (var name in DemoCatalog.People)
        {
            persons[name] = await catalog.People.CreatePersonAsync(name, ct);
        }

        foreach (var shot in shots.Where(s => byFile.ContainsKey(s.File)))
        {
            var faces = await catalog.Analysis.GetFacesAsync(byFile[shot.File], ct);
            for (var i = 0; i < Math.Min(faces.Count, shot.People.Length); i++)
            {
                if (shot.People[i] is { } person)
                {
                    await catalog.People.AssignManualAsync([faces[i].Id], persons[person], ct);
                }
            }
        }

        foreach (var shot in shots.Where(s => s.Stars > 0 && byFile.ContainsKey(s.File)))
        {
            await catalog.Decisions.SetUserAsync(byFile[shot.File], CullingOutcome.Kept, shot.Stars, null, ct);
        }

        foreach (var key in DemoCatalog.ReviewedTags)
        {
            if (await catalog.Tags.FindAsync(key, ct) is { } tag)
            {
                await catalog.Tags.UpdateAsync(tag.Id, tag.Italian, reviewed: true, tag.Category, tag.Blacklisted, ct);
            }
        }

        // The tour of the screens.
        using var shell = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        async Task Shot(string name, Func<Task>? settle = null)
        {
            for (var i = 0; i < 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }

            if (settle is not null)
            {
                await settle();
            }

            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        shell.ShowSection(SectionCatalog.Home);
        shell.RefreshStatus();
        await Shot("01-home");

        shell.ShowSection(SectionCatalog.Review);
        var review = Assert.IsType<ReviewViewModel>(shell.CurrentPage);
        await WaitAsync(() => Task.FromResult(review.Rows.Count > 0), 30);
        await Shot("02-revisione");

        shell.ShowSection(SectionCatalog.Browse);
        var browse = Assert.IsType<BrowseViewModel>(shell.CurrentPage);
        await WaitAsync(() => Task.FromResult(browse.Rows.Sum(r => r.Items.Count) > 10 && browse.Facets.Tags.Count > 0), 30);
        var group = byFile[DemoCatalog.GroupPhoto];
        browse.Selected = browse.Rows.SelectMany(r => r.Items).First(i => i.AssetId == group);
        await WaitAsync(() => Task.FromResult(browse.Editor.FaceBoxes.Count >= 3), 30);
        await Shot("03-consultazione");

        await browse.Facets.TogglePersonCommand.ExecuteAsync(browse.Facets.Persons.First(p => p.Label == "Giulia"));
        await WaitAsync(() => Task.FromResult(browse.Facets.Persons.Any(p => p.IsSelected)), 30);
        var beach = (await catalog.Tags.FindAsync("beach", ct))!.Id;
        await WaitAsync(() => Task.FromResult(browse.Facets.Tags.Any(t => t.Id == beach)), 30);
        await browse.Facets.ClickTagAsync(browse.Facets.Tags.First(t => t.Id == beach), other: false);
        await Shot("04-filtri");
        await browse.Facets.ClearCommand.ExecuteAsync(null);

        await WaitAsync(() => Task.FromResult(browse.Rows.SelectMany(r => r.Items).Any(i => i.AssetId == group)), 30);
        browse.Selected = browse.Rows.SelectMany(r => r.Items).First(i => i.AssetId == group);
        await WaitAsync(() => Task.FromResult(browse.Editor.FaceBoxes.Count >= 3), 30);
        browse.TogglePreviewCommand.Execute(null);
        await Shot("05-volti");
        browse.TogglePreviewCommand.Execute(null);

        shell.ShowSection(SectionCatalog.People);
        var people = Assert.IsType<PeopleViewModel>(shell.CurrentPage);
        await people.SetFilterCommand.ExecuteAsync(nameof(PeopleFilter.Named));
        await WaitAsync(() => Task.FromResult(people.Persons.Count > 0), 30);
        await Shot("06-persone");

        shell.ShowPage(new PersonPageViewModel(host, shell, persons["Giulia"], new FaceCrops(host)));
        var page = Assert.IsType<PersonPageViewModel>(shell.CurrentPage);
        await WaitAsync(() => Task.FromResult(page.Years.Sum(y => y.Faces.Count) >= 2), 30);
        await Shot("07-persona");
        page.Focused = page.Years.SelectMany(y => y.Faces).First(f => f.Face.AssetId == group);
        await page.OpenPhotoCommand.ExecuteAsync(null);
        await Shot("08-persona-foto");
        page.ClosePhotoCommand.Execute(null);

        shell.ShowSection(SectionCatalog.Duplicates);
        var duplicates = Assert.IsType<DuplicatesViewModel>(shell.CurrentPage);
        await WaitAsync(() => Task.FromResult(duplicates.Copies.Count > 1), 60);
        await Shot("09-duplicati");

        shell.ShowSection(SectionCatalog.Vocabulary);
        await Shot("10-vocabolario");

        shell.ShowSection(SectionCatalog.Settings);
        var settings = Assert.IsType<SettingsViewModel>(shell.CurrentPage);
        settings.SelectedTab = SettingsViewModel.ModelsTab;
        await Shot("11-impostazioni-modelli");
        settings.SelectedTab = SettingsViewModel.CatalogTab;
        await Shot("12-impostazioni-catalogo");

        // An SD card with new pictures for the import.
        var card = Path.Combine(drive, "Scheda SD", "DCIM", "100NIKON");
        Directory.CreateDirectory(card);
        for (var i = 0; i < 12; i++)
        {
            DemoScenes.Draw(i % 2 == 0 ? DemoScene.Track : DemoScene.City, 100 + i, Path.Combine(card, $"DSC_{1200 + i:0000}.jpg"));
        }

        shell.ShowSection(SectionCatalog.Home);
        shell.ShowPage(new ImportWizardViewModel(host, shell, null, card));
        await Shot("13-import");
    }

    public void Dispose() => _temp.Dispose();

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the demo catalog.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }

    /// <summary>The demo analysis: what the models would say about each picture, from the description of the shot.</summary>
    private sealed class DemoProcessor(PhotoFlowHost host) : IPhotoProcessor
    {
        private readonly SkiaImageOperations _operations = new();

        public async Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct)
        {
            var shot = DemoCatalog.Shots.First(s => s.File == Path.GetFileName(photo.Path));
            if (photo.ContentHash is { } hash)
            {
                await using var stream = File.OpenRead(photo.Path);
                await host.Thumbnails.StoreAsync(hash, await _operations.DecodeAsync(stream, ct), ct);
            }

            var faces = shot.Faces.Select(b => new Face(0, 0, b, null, 0.95, EyeState.Open, EyeState.Open, null, null, null, null, "demo@1")).ToList();
            var signals = new CullingSignals(faces.Count > 0 ? SharpnessZoneKind.Eyes : SharpnessZoneKind.Subject, shot.Sharpness, shot.Sharpness * 0.9, 0.6,
                0.01, 0.02, 0.01, 0.03, 0.5, 1, 0.3, faces.Count, faces.Count, faces.Count > 0 ? 0.08 : null,
                faces.Count > 0 ? EyeState.Open : EyeState.Unknown, faces.Count > 0 ? EyeState.Open : EyeState.Unknown, null, null, 0, 0);
            return new PhotoProcessingResult(
                new AnalyzerResult(PhotoFlow.Analysis.Culling.CullingAnalyzer.AnalyzerId, "demo@1", PhotoFlow.Analysis.Culling.CullingScores.ToScores(signals)),
                faces,
                new DecisionProposal(shot.Outcome, shot.Outcome == CullingOutcome.Discard ? ["blur"] : []),
                "engine+demo@1",
                null,
                shot.Tags.Select((t, i) => new TagResult(t, TagSource.RamPlus, 0.95 - (i * 0.05), "ramplus@demo")).ToList(),
                null,
                new GeneratedCaption("it", shot.Caption, "qwen2.5-vl-7b@demo"));
        }
    }
}

/// <summary>The shots of the demo catalog: invented places, events and people.</summary>
internal static class DemoCatalog
{
    public static readonly string[] People = ["Giulia", "Marco", "Sofia"];

    public static readonly string[] ReviewedTags = ["mountain", "lake", "sunset", "beach", "sea", "city", "night", "car", "race car", "flower", "boat", "person"];

    public const string GroupPhoto = "DSC_0412.jpg";

    public static readonly DemoShot[] Shots =
    [
        // Vacanze in Trentino: a burst at sunset, the lake, a group photo.
        new("Montagna", "DSC_0101.jpg", DemoScene.Mountains, 1, new(2025, 8, 12, 19, 40, 0), "Vacanze in Trentino", ["mountain", "sunset", "sky", "lake"],
            "Il sole tramonta dietro le montagne e colora il cielo di arancione sopra il lago.", [], [], CullingOutcome.Kept, 0.82, 4),
        new("Montagna", "DSC_0102.jpg", DemoScene.Mountains, 2, new(2025, 8, 12, 19, 40, 1), "Vacanze in Trentino", ["mountain", "sunset", "sky"],
            "Montagne al tramonto con il cielo arancione e viola.", [], [], CullingOutcome.Kept, 0.78, 0),
        new("Montagna", "DSC_0103.jpg", DemoScene.Mountains, 3, new(2025, 8, 12, 19, 40, 2), "Vacanze in Trentino", ["mountain", "sunset", "sky"],
            "Le montagne in controluce al tramonto.", [], [], CullingOutcome.Discard, 0.18, 0),
        new("Montagna", "DSC_0120.jpg", DemoScene.Lake, 4, new(2025, 8, 13, 10, 5, 0), "Vacanze in Trentino", ["lake", "mountain", "tree", "boat"],
            "Un lago azzurro tra i boschi, con una barca a remi vicino alla riva.", [], [], CullingOutcome.Kept, 0.9, 5),
        new("Montagna", "DSC_0121.jpg", DemoScene.Lake, 5, new(2025, 8, 13, 10, 12, 0), "Vacanze in Trentino", ["lake", "tree", "sky"],
            "Il lago e il bosco in una giornata limpida.", [], [], CullingOutcome.Kept, 0.84, 0),
        new("Montagna", "DSC_0130.jpg", DemoScene.Group, 6, new(2025, 8, 13, 12, 30, 0), "Vacanze in Trentino", ["person", "mountain", "lake"],
            "Due persone sorridono davanti al lago di montagna.", [Box(0.30, 0.30), Box(0.58, 0.32)], ["Giulia", "Marco"], CullingOutcome.Kept, 0.88, 4),
        new("Montagna", "DSC_0140.jpg", DemoScene.Flowers, 7, new(2025, 8, 14, 9, 0, 0), "Vacanze in Trentino", ["flower", "field", "sky"],
            "Un prato di fiori colorati sotto il cielo azzurro.", [], [], CullingOutcome.Kept, 0.86, 0),
        new("Montagna", "DSC_0141.jpg", DemoScene.Flowers, 8, new(2025, 8, 14, 9, 0, 1), "Vacanze in Trentino", ["flower", "field"],
            "Fiori di campo in primo piano.", [], [], CullingOutcome.ToReview, 0.42, 0),

        // Mare in Sardegna.
        new("Mare", "DSC_0401.jpg", DemoScene.Beach, 9, new(2025, 7, 20, 11, 0, 0), "Mare in Sardegna", ["beach", "sea", "umbrella", "sky"],
            "Una spiaggia con ombrelloni colorati e il mare turchese.", [], [], CullingOutcome.Kept, 0.9, 5),
        new("Mare", "DSC_0402.jpg", DemoScene.Beach, 10, new(2025, 7, 20, 11, 0, 1), "Mare in Sardegna", ["beach", "sea", "umbrella"],
            "Ombrelloni sulla spiaggia vicino al mare.", [], [], CullingOutcome.Kept, 0.85, 0),
        new("Mare", "DSC_0403.jpg", DemoScene.Beach, 11, new(2025, 7, 20, 11, 0, 2), "Mare in Sardegna", ["beach", "sea"],
            "La spiaggia e il mare, leggermente mossa.", [], [], CullingOutcome.Discard, 0.2, 0),
        new("Mare", DemoCatalog.GroupPhoto, DemoScene.Group, 12, new(2025, 7, 20, 17, 30, 0), "Mare in Sardegna", ["person", "beach", "sea", "sunset"],
            "Tre amici in spiaggia al tramonto, con il mare alle spalle.", [Box(0.22, 0.32), Box(0.45, 0.30), Box(0.68, 0.33)], ["Giulia", "Sofia", null],
            CullingOutcome.Kept, 0.87, 5),
        new("Mare", "DSC_0420.jpg", DemoScene.Sailing, 13, new(2025, 7, 21, 16, 0, 0), "Mare in Sardegna", ["boat", "sea", "sky"],
            "Una barca a vela al largo sul mare blu.", [], [], CullingOutcome.Kept, 0.83, 4),
        new("Mare", "DSC_0430.jpg", DemoScene.Portrait, 14, new(2025, 7, 22, 18, 10, 0), "Mare in Sardegna", ["person", "sea", "sunset"],
            "Ritratto di una ragazza al tramonto sul mare.", [Box(0.47, 0.30, 0.14)], ["Sofia"], CullingOutcome.Kept, 0.91, 5),

        // Gara a Monza.
        new("Monza", "DSC_0801.jpg", DemoScene.Track, 15, new(2025, 9, 7, 14, 2, 0), "Gara a Monza", ["race car", "race track", "car"],
            "Due auto da corsa in curva sulla pista.", [], [], CullingOutcome.Kept, 0.8, 4),
        new("Monza", "DSC_0802.jpg", DemoScene.Track, 16, new(2025, 9, 7, 14, 2, 1), "Gara a Monza", ["race car", "race track"],
            "Le auto da corsa escono dalla curva.", [], [], CullingOutcome.Kept, 0.76, 0),
        new("Monza", "DSC_0803.jpg", DemoScene.Track, 17, new(2025, 9, 7, 14, 2, 2), "Gara a Monza", ["race car", "race track"],
            "Auto da corsa sulla pista, foto mossa.", [], [], CullingOutcome.Discard, 0.15, 0),
        new("Monza", "DSC_0810.jpg", DemoScene.Group, 18, new(2025, 9, 7, 16, 45, 0), "Gara a Monza", ["person", "crowd"],
            "Due amici sorridenti tra il pubblico della gara.", [Box(0.33, 0.31), Box(0.60, 0.30)], ["Marco", "Sofia"], CullingOutcome.Kept, 0.85, 0),

        // Milano by night.
        new("Milano", "DSC_0901.jpg", DemoScene.City, 19, new(2025, 10, 3, 21, 15, 0), "Milano by night", ["city", "night", "building", "skyline"],
            "I grattacieli della città illuminati di notte.", [], [], CullingOutcome.Kept, 0.82, 4),
        new("Milano", "DSC_0902.jpg", DemoScene.City, 20, new(2025, 10, 3, 21, 20, 0), "Milano by night", ["city", "night", "building"],
            "Lo skyline notturno con le finestre accese.", [], [], CullingOutcome.Kept, 0.79, 0),
        new("Milano", "DSC_0910.jpg", DemoScene.Portrait, 21, new(2025, 10, 3, 21, 40, 0), "Milano by night", ["person", "city", "night"],
            "Ritratto di un ragazzo in città di sera.", [Box(0.47, 0.30, 0.14)], ["Marco"], CullingOutcome.ToReview, 0.48, 0),
    ];

    private static BoundingBox Box(double x, double y, double side = 0.1) =>
        new((float)x, (float)y, (float)(x + side), (float)(y + (side * 1.4)));
}

internal enum DemoScene
{
    Mountains,
    Lake,
    Beach,
    Sailing,
    Track,
    City,
    Flowers,
    Group,
    Portrait,
}

internal sealed record DemoShot(
    string Folder, string File, DemoScene Scene, int Seed, DateTime Taken, string Event, string[] Tags, string Caption,
    BoundingBox[] Faces, string?[] People, CullingOutcome Outcome, double Sharpness, int Stars)
{
    public string Exposure => Scene is DemoScene.City ? "1/30" : "1/500";

    public string Aperture => Scene is DemoScene.Portrait or DemoScene.Group ? "2.8" : "8";

    public int Iso => Scene is DemoScene.City ? 3200 : 100;

    public string Focal => Scene is DemoScene.Portrait ? "85" : "35";
}

/// <summary>Stylized pictures drawn with SkiaSharp: landscapes, sea, track, city, and people as simple figures.</summary>
internal static class DemoScenes
{
    public const int Width = 1800;
    public const int Height = 1200;

    public static void Draw(DemoScene scene, int seed, string path)
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            var random = new Random(seed);
            switch (scene)
            {
                case DemoScene.Mountains: Mountains(canvas, random); break;
                case DemoScene.Lake: Lake(canvas, random); break;
                case DemoScene.Beach: Beach(canvas, random); break;
                case DemoScene.Sailing: Sailing(canvas, random); break;
                case DemoScene.Track: Track(canvas, random); break;
                case DemoScene.City: City(canvas, random); break;
                case DemoScene.Flowers: Flowers(canvas, random); break;
                case DemoScene.Group: Group(canvas, random, seed); break;
                case DemoScene.Portrait: Portrait(canvas, random, seed); break;
                default: break;
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    private static void Sky(SKCanvas c, SKColor top, SKColor bottom, float horizon)
    {
        using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, horizon), [top, bottom], SKShaderTileMode.Clamp) };
        c.DrawRect(0, 0, Width, horizon + 2, paint);
    }

    private static void Ridge(SKCanvas c, Random r, float baseY, float amplitude, SKColor color, int peaks)
    {
        using var path = new SKPath();
        path.MoveTo(0, Height);
        path.LineTo(0, baseY);
        for (var i = 1; i <= peaks; i++)
        {
            var x = Width * i / (float)peaks;
            path.LineTo(x - (Width / (float)peaks / 2), baseY - (amplitude * (0.5f + (float)r.NextDouble())));
            path.LineTo(x, baseY - (amplitude * 0.2f * (float)r.NextDouble()));
        }

        path.LineTo(Width, Height);
        path.Close();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawPath(path, paint);
    }

    private static void Sun(SKCanvas c, float x, float y, float radius, SKColor color)
    {
        using var glow = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(x, y), radius * 3, [color.WithAlpha(140), color.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawCircle(x, y, radius * 3, glow);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawCircle(x, y, radius, paint);
    }

    private static void Mountains(SKCanvas c, Random r)
    {
        Sky(c, new SKColor(58, 42, 110), new SKColor(255, 150, 80), 700);
        Sun(c, 900 + r.Next(-200, 200), 620, 70, new SKColor(255, 220, 140));
        Ridge(c, r, 760, 330, new SKColor(110, 70, 120), 5);
        Ridge(c, r, 860, 260, new SKColor(70, 45, 90), 7);
        Ridge(c, r, 980, 150, new SKColor(40, 28, 60), 9);
        using var lake = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 1000), new SKPoint(0, Height), [new SKColor(255, 160, 100), new SKColor(60, 40, 90)], SKShaderTileMode.Clamp) };
        c.DrawRect(0, 1000, Width, 200, lake);
    }

    private static void Lake(SKCanvas c, Random r)
    {
        Sky(c, new SKColor(70, 140, 220), new SKColor(190, 225, 250), 560);
        Ridge(c, r, 600, 260, new SKColor(110, 140, 170), 4);
        Ridge(c, r, 680, 120, new SKColor(40, 110, 60), 10);
        using var water = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 680), new SKPoint(0, Height), [new SKColor(60, 150, 190), new SKColor(20, 70, 110)], SKShaderTileMode.Clamp) };
        c.DrawRect(0, 680, Width, 520, water);
        using var tree = new SKPaint { Color = new SKColor(25, 80, 45), IsAntialias = true };
        for (var i = 0; i < 18; i++)
        {
            var x = r.Next(0, Width);
            using var t = new SKPath();
            t.MoveTo(x, 520 + r.Next(0, 80));
            t.LineTo(x - 30, 690);
            t.LineTo(x + 30, 690);
            t.Close();
            c.DrawPath(t, tree);
        }

        using var boat = new SKPaint { Color = new SKColor(150, 80, 40), IsAntialias = true };
        c.DrawRoundRect(new SKRect(1100, 900, 1300, 940), 20, 20, boat);
    }

    private static void Beach(SKCanvas c, Random r)
    {
        Sky(c, new SKColor(60, 150, 235), new SKColor(200, 235, 255), 520);
        using var sea = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 520), new SKPoint(0, 800), [new SKColor(20, 120, 170), new SKColor(60, 210, 200)], SKShaderTileMode.Clamp) };
        c.DrawRect(0, 520, Width, 280, sea);
        using var sand = new SKPaint { Color = new SKColor(238, 214, 170) };
        c.DrawRect(0, 800, Width, 400, sand);
        SKColor[] colors = [new(230, 70, 70), new(250, 190, 40), new(60, 130, 220), new(240, 120, 180), new(80, 190, 120)];
        for (var i = 0; i < 6; i++)
        {
            var x = 150 + (i * 280) + r.Next(-40, 40);
            var y = 880 + r.Next(-20, 60);
            using var pole = new SKPaint { Color = new SKColor(120, 90, 60), StrokeWidth = 8 };
            c.DrawLine(x, y, x, y + 160, pole);
            using var top = new SKPaint { Color = colors[(i + r.Next(5)) % colors.Length], IsAntialias = true };
            c.DrawArc(new SKRect(x - 110, y - 70, x + 110, y + 70), 180, 180, true, top);
        }
    }

    private static void Sailing(SKCanvas c, Random r)
    {
        Sky(c, new SKColor(40, 110, 200), new SKColor(170, 210, 245), 650);
        using var sea = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 650), new SKPoint(0, Height), [new SKColor(30, 90, 160), new SKColor(10, 40, 90)], SKShaderTileMode.Clamp) };
        c.DrawRect(0, 650, Width, 550, sea);
        using var hull = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo(780, 760);
        path.LineTo(1080, 760);
        path.LineTo(1040, 800);
        path.LineTo(820, 800);
        path.Close();
        c.DrawPath(path, hull);
        using var sail = new SKPath();
        sail.MoveTo(930, 300);
        sail.LineTo(930, 740);
        sail.LineTo(1120, 740);
        sail.Close();
        c.DrawPath(sail, hull);
        using var sail2 = new SKPath();
        sail2.MoveTo(910, 360);
        sail2.LineTo(910, 740);
        sail2.LineTo(780, 740);
        sail2.Close();
        using var orange = new SKPaint { Color = new SKColor(250, 140, 60), IsAntialias = true };
        c.DrawPath(sail2, orange);
    }

    private static void Track(SKCanvas c, Random r)
    {
        Sky(c, new SKColor(120, 170, 220), new SKColor(220, 235, 245), 380);
        using var grass = new SKPaint { Color = new SKColor(70, 140, 60) };
        c.DrawRect(0, 380, Width, 820, grass);
        using var stands = new SKPaint { Color = new SKColor(90, 90, 100) };
        c.DrawRect(0, 300, Width, 120, stands);
        for (var i = 0; i < 200; i++)
        {
            using var dot = new SKPaint { Color = new SKColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256)) };
            c.DrawCircle(r.Next(0, Width), r.Next(310, 410), 7, dot);
        }

        using var asphalt = new SKPaint { Color = new SKColor(70, 70, 75), Style = SKPaintStyle.Stroke, StrokeWidth = 300, IsAntialias = true };
        using var road = new SKPath();
        road.MoveTo(-100, 1100);
        road.CubicTo(500, 600, 1300, 1100, 1900, 600);
        c.DrawPath(road, asphalt);
        Car(c, 650, 830, new SKColor(220, 30, 40));
        Car(c, 1150, 880, new SKColor(30, 90, 220));
    }

    private static void Car(SKCanvas c, float x, float y, SKColor color)
    {
        using var body = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRoundRect(new SKRect(x - 150, y - 40, x + 150, y + 30), 30, 30, body);
        c.DrawRoundRect(new SKRect(x - 60, y - 80, x + 60, y - 30), 20, 20, body);
        using var wheel = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        c.DrawCircle(x - 95, y + 30, 32, wheel);
        c.DrawCircle(x + 95, y + 30, 32, wheel);
    }

    private static void City(SKCanvas c, Random r)
    {
        Sky(c, new SKColor(10, 15, 45), new SKColor(60, 40, 110), 900);
        for (var i = 0; i < 60; i++)
        {
            using var star = new SKPaint { Color = SKColors.White.WithAlpha((byte)r.Next(80, 255)) };
            c.DrawCircle(r.Next(0, Width), r.Next(0, 500), 2, star);
        }

        var x = 0;
        while (x < Width)
        {
            var w = r.Next(110, 220);
            var h = r.Next(300, 850);
            using var building = new SKPaint { Color = new SKColor((byte)r.Next(20, 50), (byte)r.Next(20, 50), (byte)r.Next(50, 80)) };
            c.DrawRect(x, Height - h, w, h, building);
            for (var wy = Height - h + 20; wy < Height - 20; wy += 38)
            {
                for (var wx = x + 14; wx < x + w - 20; wx += 30)
                {
                    if (r.NextDouble() < 0.55)
                    {
                        using var light = new SKPaint { Color = new SKColor(255, (byte)r.Next(190, 235), (byte)r.Next(90, 160)) };
                        c.DrawRect(wx, wy, 16, 20, light);
                    }
                }
            }

            x += w + r.Next(4, 20);
        }
    }

    private static void Flowers(SKCanvas c, Random r)
    {
        Sky(c, new SKColor(80, 160, 240), new SKColor(210, 235, 255), 450);
        using var field = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 450), new SKPoint(0, Height), [new SKColor(120, 190, 80), new SKColor(40, 120, 40)], SKShaderTileMode.Clamp) };
        c.DrawRect(0, 450, Width, 750, field);
        SKColor[] colors = [new(240, 60, 80), new(255, 210, 40), new(250, 250, 250), new(170, 90, 220), new(255, 140, 40)];
        for (var i = 0; i < 260; i++)
        {
            var y = r.Next(480, Height);
            var size = 6 + ((y - 480) / 40f);
            using var petal = new SKPaint { Color = colors[r.Next(colors.Length)], IsAntialias = true };
            c.DrawCircle(r.Next(0, Width), y, size, petal);
        }
    }

    /// <summary>People as simple figures (a round head, a body): a face box in the catalog, never a real face.</summary>
    private static void Figure(SKCanvas c, float cx, float headY, float headRadius, SKColor shirt, SKColor skin, SKColor hair)
    {
        using var body = new SKPaint { Color = shirt, IsAntialias = true };
        c.DrawRoundRect(new SKRect(cx - (headRadius * 1.8f), headY + (headRadius * 1.2f), cx + (headRadius * 1.8f), Height), headRadius, headRadius, body);
        using var head = new SKPaint { Color = skin, IsAntialias = true };
        c.DrawCircle(cx, headY, headRadius, head);
        using var hairPaint = new SKPaint { Color = hair, IsAntialias = true };
        c.DrawArc(new SKRect(cx - headRadius, headY - headRadius, cx + headRadius, headY + headRadius), 180, 180, true, hairPaint);
        using var eye = new SKPaint { Color = new SKColor(40, 30, 30), IsAntialias = true };
        c.DrawCircle(cx - (headRadius * 0.35f), headY + (headRadius * 0.1f), headRadius * 0.08f, eye);
        c.DrawCircle(cx + (headRadius * 0.35f), headY + (headRadius * 0.1f), headRadius * 0.08f, eye);
        using var smile = new SKPaint { Color = new SKColor(120, 40, 40), Style = SKPaintStyle.Stroke, StrokeWidth = headRadius * 0.08f, IsAntialias = true };
        c.DrawArc(new SKRect(cx - (headRadius * 0.4f), headY + (headRadius * 0.1f), cx + (headRadius * 0.4f), headY + (headRadius * 0.6f)), 20, 140, false, smile);
    }

    private static void Group(SKCanvas c, Random r, int seed)
    {
        switch (seed)
        {
            case 6: Lake(c, r); break;
            case 18: Track(c, r); break;
            default: Beach(c, r); break;
        }

        SKColor[] shirts = [new(220, 70, 90), new(60, 120, 210), new(250, 190, 60)];
        SKColor[] skins = [new(240, 200, 170), new(200, 150, 110), new(250, 215, 185)];
        SKColor[] hair = [new(90, 50, 30), new(30, 25, 25), new(200, 150, 60)];
        var count = seed == 12 ? 3 : 2;
        for (var i = 0; i < count; i++)
        {
            var x = Width * (count == 3 ? 0.27f + (i * 0.23f) : 0.35f + (i * 0.28f));
            Figure(c, x, Height * 0.42f, 95, shirts[i], skins[i], hair[i]);
        }
    }

    private static void Portrait(SKCanvas c, Random r, int seed)
    {
        if (seed % 2 == 0)
        {
            Beach(c, r);
        }
        else
        {
            City(c, r);
        }

        Figure(c, Width * 0.54f, Height * 0.45f, 135, new SKColor(70, 70, 160), new SKColor(235, 195, 160), new SKColor(60, 35, 25));
    }
}
