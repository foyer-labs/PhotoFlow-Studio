// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using PhotoFlow.App.Services.Develop;
using PhotoFlow.App.ViewModels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Develop;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Develop;

/// <summary>PhotoFlow Studio: the development module (decisions S1–S13 of docs/studio/progetto.md).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class DevelopUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Sections_follow_the_order_of_the_work()
    {
        var ids = SectionCatalog.All.Select(s => s.Id).ToList();
        Assert.True(ids.IndexOf(SectionCatalog.Review) < ids.IndexOf(SectionCatalog.Develop));
        Assert.True(ids.IndexOf(SectionCatalog.Develop) < ids.IndexOf(SectionCatalog.Export));
        Assert.Equal(4, SectionCatalog.All.Single(s => s.Id == SectionCatalog.Develop).ShortcutDigit);
        Assert.Equal(5, SectionCatalog.All.Single(s => s.Id == SectionCatalog.Export).ShortcutDigit);
        // Ctrl+digit follow the visible order of the main sections.
        var digits = SectionCatalog.All.Where(s => s.ShortcutDigit is not null).Select(s => s.ShortcutDigit!.Value).ToList();
        Assert.Equal(Enumerable.Range(1, digits.Count), digits);
    }

    [Fact]
    public void Noise_off_sends_zero_strengths_and_keeps_the_rest()
    {
        var settings = new JsonObject
        {
            ["denoise"] = new JsonObject { ["luma_strength"] = 1.2, ["chroma_strength"] = 1.5, ["algorithm"] = "nlm" },
            ["tone"] = new JsonObject { ["contrast"] = 30 },
            ["studio"] = new JsonObject { ["noise_enabled"] = false },
        };
        var engine = DevelopJson.ForEngine(settings);
        Assert.Equal(0.0, Json.Double(engine["denoise"]!["luma_strength"]));
        Assert.Equal(0.0, Json.Double(engine["denoise"]!["chroma_strength"]));
        Assert.Equal("nlm", Json.String(engine["denoise"]!["algorithm"]));
        Assert.Equal(30, Json.Int(engine["tone"]!["contrast"]));
        Assert.Null(engine["studio"]);
        Assert.Equal(1.2, Json.Double(settings["denoise"]!["luma_strength"])); // the original is untouched

        settings["studio"]!["noise_enabled"] = true;
        Assert.Equal(1.2, Json.Double(DevelopJson.ForEngine(settings)["denoise"]!["luma_strength"]));
    }

    [Fact]
    public void Only_the_changed_fields_go_to_the_other_photos()
    {
        var opened = new JsonObject { ["tone"] = new JsonObject { ["exposure_ev"] = 0.0, ["shadows"] = 10.0 }, ["denoise"] = new JsonObject { ["algorithm"] = "wavelet" } };
        var now = (JsonObject)opened.DeepClone();
        now["tone"]!["exposure_ev"] = -0.7;
        Assert.Equal([("tone", "exposure_ev")], DevelopViewModel.ChangedFields(opened, now));
    }

    [Fact]
    public void Recipes_give_the_output_of_the_engine()
    {
        var web = ExportRecipes.Get(ExportRecipes.Web).ToOutput();
        Assert.Equal("jpeg", Json.String(web["format"]));
        Assert.Equal(2048, Json.Int(web["max_side"]));
        Assert.True(Json.Bool(web["strip_gps"]));
        Assert.False(Json.Bool(web["overwrite"], true));
        var lightroom = ExportRecipes.Get(ExportRecipes.Lightroom);
        Assert.Equal("dng", lightroom.Format);
        Assert.Equal(string.Empty, lightroom.Folder); // next to the original
        Assert.Equal("{nome}-PF-NR", lightroom.NameTemplate);
        Assert.Equal(ExportRecipes.All.Count, ExportRecipes.All.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Working_selection_keeps_the_open_photo_inside()
    {
        var set = new WorkingSet();
        var changes = 0;
        set.Changed += (_, _) => changes++;
        set.Set("Da Revisione", [3, 1, 3, 2], activeId: 2, eventName: "Monza");
        Assert.Equal([3L, 1L, 2L], set.AssetIds);
        Assert.Equal(2, set.ActiveId);
        Assert.Equal("Monza", set.EventName);
        set.SetActive(99); // not in the selection: ignored
        Assert.Equal(2, set.ActiveId);
        set.Set("Altro", [5], activeId: 9);
        Assert.Equal(5, set.ActiveId);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Protocol_answers_progress_errors_and_closing()
    {
        var toEngine = new LineChannel();
        var fromEngine = new LineChannel();
        using var connection = new DevelopEngineConnection(toEngine.Writer, fromEngine.Reader);
        var engineIn = new Reader(toEngine);
        var engineOut = fromEngine.Writer;

        await engineOut.WriteLineAsync("{\"id\": 0, \"event\": \"ready\", \"version\": \"9.9\"}");
        Assert.Equal("9.9", await connection.WaitReadyAsync(Ct));

        var progress = new List<double>();
        var call = connection.CallAsync("export", new JsonObject { ["x"] = 1 }, new SyncProgress(p => progress.Add(p.Fraction)), Ct);
        var request = JsonNode.Parse((await engineIn.ReadLineAsync(Ct))!)!.AsObject();
        Assert.Equal("export", Json.String(request["cmd"]));
        var id = Json.Long(request["id"]);
        await engineOut.WriteLineAsync("rumore che non è json");
        await engineOut.WriteLineAsync($"{{\"id\": {id}, \"event\": \"progress\", \"fraction\": 0.5, \"message\": \"a metà\"}}");
        await engineOut.WriteLineAsync($"{{\"id\": {id}, \"ok\": true, \"result\": {{\"n\": 3}}}}");
        var result = await call;
        Assert.Equal(3, Json.Int(result["n"]));
        Assert.Equal([0.5], progress);

        var failing = connection.CallAsync("render", null, null, Ct);
        var second = Json.Long(JsonNode.Parse((await engineIn.ReadLineAsync(Ct))!)!["id"]);
        await engineOut.WriteLineAsync($"{{\"id\": {second}, \"ok\": false, \"error\": \"Foto non aperta\"}}");
        var error = await Assert.ThrowsAsync<DevelopEngineException>(() => failing);
        Assert.Equal("Foto non aperta", error.Message);

        using var cancel = new CancellationTokenSource();
        var cancelled = connection.CallAsync("render_full", null, null, cancel.Token);
        var third = Json.Long(JsonNode.Parse((await engineIn.ReadLineAsync(Ct))!)!["id"]);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var cancelRequest = JsonNode.Parse((await engineIn.ReadLineAsync(Ct))!)!.AsObject();
        Assert.Equal("cancel", Json.String(cancelRequest["cmd"]));
        Assert.Equal(third, Json.Long(cancelRequest["target"]));

        var pending = connection.CallAsync("hello", null, null, Ct);
        await engineIn.ReadLineAsync(Ct);
        fromEngine.Complete(); // the engine closes
        await Assert.ThrowsAsync<DevelopEngineException>(() => pending);
        Assert.True(connection.IsClosed);
    }

    private sealed class SyncProgress(Action<DevelopProgress> report) : IProgress<DevelopProgress>
    {
        public void Report(DevelopProgress value) => report(value);
    }

    /// <summary>What the fake engine reads (the lines written by the connection).</summary>
    private sealed class Reader(LineChannel channel)
    {
        public async Task<string?> ReadLineAsync(CancellationToken ct) => await channel.NextAsync(ct);
    }
}

[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DevelopStoreTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-develop");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Settings_and_exports_are_kept_in_the_catalog()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var a = await catalog.Assets.AddAsync(new Asset(0, "qa", "fa", "NEF", "raw", 6000, 4000, null, DateSource.Unknown, null, AssetState.Active), Ct);
        var b = await catalog.Assets.AddAsync(new Asset(0, "qb", "fb", "JPG", "jpeg", 6000, 4000, null, DateSource.Unknown, null, AssetState.Active), Ct);
        Assert.Null(await catalog.Develop.GetSettingsAsync(a, Ct));
        await catalog.Develop.SetSettingsAsync(a, "{\"tone\":{\"contrast\":10}}", Ct);
        await catalog.Develop.SetSettingsAsync(a, "{\"tone\":{\"contrast\":20}}", Ct);
        Assert.Equal("{\"tone\":{\"contrast\":20}}", await catalog.Develop.GetSettingsAsync(a, Ct));
        Assert.Equal(new HashSet<long> { a }, await catalog.Develop.ListDevelopedAsync([a, b], Ct));

        var first = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        await catalog.Develop.AddExportAsync(new ExportRecord(b, "web/b.jpg", ExportRecipes.Web, first), Ct);
        await catalog.Develop.AddExportAsync(new ExportRecord(b, "stampa/b.tif", ExportRecipes.Print, first.AddHours(1)), Ct);
        var last = await catalog.Develop.LastExportsAsync([a, b], Ct);
        Assert.False(last.ContainsKey(a));
        Assert.Equal("stampa/b.tif", last[b].OutputPath);
        Assert.Equal(ExportRecipes.Print, last[b].Recipe);
    }

    public void Dispose() => _temp.Dispose();
}

/// <summary>The real engine (Python environment engine/.venv or the packaged engine); skipped when it is not there.</summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class DevelopEngineTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-engine");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Opens_develops_and_exports_a_photo()
    {
        Assert.SkipUnless(DevelopEngineClient.IsAvailable, "Motore di sviluppo non disponibile (engine/.venv)");
        var photo = Path.Combine(_temp.Path, "foto.jpg");
        WriteNoisyJpeg(photo, 320, 240);
        var paths = new global::PhotoFlow.Core.Paths.DefaultAppPaths(_temp.Sub("dati"));
        await using var engine = new DevelopEngineClient(paths);
        var info = await engine.HelloAsync(Ct);
        Assert.Contains(info.Algorithms, a => a.Key == "wavelet" && a.Available);

        var opened = await engine.OpenAsync(photo, null, Ct);
        Assert.Equal(320, opened.Width);
        Assert.False(opened.IsRaw);
        var settings = opened.Settings;
        Json.Section(settings, "tone")["contrast"] = 25;
        var preview = await engine.RenderAsync(photo, settings, (0, 0, 64, 48), 0, true, null, Ct);
        Assert.True(File.Exists(preview.BeforeFile));
        Assert.NotNull(preview.AfterFile);
        var histogram = await engine.HistogramAsync(photo, settings, Ct);
        Assert.Equal(4, histogram.Length);
        Assert.Equal(256, histogram[0].Length);

        var output = ExportRecipes.Get(ExportRecipes.Web).ToOutput(copyMetadata: false);
        output["folder"] = _temp.Sub("uscita");
        output["name_template"] = "{evento}_{seq}_{nome}";
        var progress = new List<double>();
        var results = await engine.ExportAsync(
            [new DevelopExportItem(1, photo, DevelopJson.ForEngine(settings), new Dictionary<string, string> { ["evento"] = "Prova", ["seq"] = "001" })],
            output, "rename", "tiff", new SyncProgress(p => progress.Add(p.Fraction)), Ct);
        var result = Assert.Single(results);
        Assert.Equal("ok", result.Outcome);
        Assert.Equal("Prova_001_foto.jpg", Path.GetFileName(result.Output));
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Rotates_crops_and_exports_the_framing()
    {
        Assert.SkipUnless(DevelopEngineClient.IsAvailable, "Motore di sviluppo non disponibile (engine/.venv)");
        var photo = Path.Combine(_temp.Path, "foto.jpg");
        WriteNoisyJpeg(photo, 320, 240);
        await using var engine = new DevelopEngineClient(new global::PhotoFlow.Core.Paths.DefaultAppPaths(_temp.Sub("dati")));
        var settings = (await engine.OpenAsync(photo, null, Ct)).Settings;
        var geometry = Json.Section(settings, "geometry");
        geometry["rotation"] = 90;
        geometry["crop"] = new JsonArray(0.0, 0.5, 1.0, 0.5);

        var fit = await engine.RenderAsync(photo, settings, null, 160, false, null, Ct);
        Assert.Equal((240, 160), fit.Frame);
        Assert.Equal(160, fit.Width);
        Assert.InRange(fit.Height, 106, 107); // 240 × 160 at 2/3, rounded
        var region = await engine.RenderAsync(photo, settings, (10, 10, 50, 40), 0, false, null, Ct);
        Assert.Equal((10, 10, 50, 40), region.Region);
        var (temperature, _) = await engine.PickWhiteBalanceAsync(photo, 100, 100, settings, Ct);
        Assert.InRange(temperature, 2000, 15000);
        Assert.True(await engine.AutoLevelAsync(photo, settings, Ct) is null or (>= -45 and <= 45));

        var output = ExportRecipes.Get(ExportRecipes.Client).ToOutput(copyMetadata: false);
        output["folder"] = _temp.Sub("uscita");
        var result = Assert.Single(await engine.ExportAsync(
            [new DevelopExportItem(1, photo, DevelopJson.ForEngine(settings), new Dictionary<string, string>())], output, "rename", "tiff", null, Ct));
        Assert.Equal("ok", result.Outcome);
        using var exported = SkiaSharp.SKBitmap.Decode(result.Output);
        Assert.Equal((240, 160), (exported.Width, exported.Height));
    }

    private static void WriteNoisyJpeg(string path, int width, int height)
    {
        var random = new Random(1);
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                var value = 60 + x * 150 / width;
                for (var c = 0; c < 3; c++)
                {
                    pixels[i + c] = (byte)Math.Clamp(value + random.Next(-25, 26), 0, 255);
                }
            }
        }

        var image = new global::PhotoFlow.Core.Imaging.RgbImage(width, height, pixels);
        File.WriteAllBytes(path, new global::PhotoFlow.Imaging.SkiaImageOperations().EncodeJpeg(image, 95));
    }

    private sealed class SyncProgress(Action<DevelopProgress> report) : IProgress<DevelopProgress>
    {
        public void Report(DevelopProgress value) => report(value);
    }

    public void Dispose() => _temp.Dispose();
}
