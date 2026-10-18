// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Search;
using PhotoFlow.Data;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Data;

/// <summary>
/// Search (spec §8, P14, acceptance of phase 3 "Ricerca &lt; 1 s su 60.000 foto"): exact filters, words on names and
/// labels in both languages, meaning by CLIP embedding, "find similar"; the RAW of a pair is not a separate result.
/// </summary>
public sealed class SearchTests : IDisposable
{
    private const string Clip = "clip@test";
    private readonly TempFolder _temp = new("pf-search");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait(TestCategory.Name, TestCategory.Integration)]
    public async Task Filters_words_meaning_and_similar_photos()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var library = await catalog.Libraries.AddAsync(new Library(0, "L", _temp.Path, WritePolicy.SidecarOnly, false, LibraryOrigin.Import, null), Ct);
        async Task<long> Photo(string path, int day, float[] vector, string camera = "Z 5II")
        {
            var id = await catalog.Assets.AddAsync(new Asset(0, path, null, "JPG", "jpeg", 10, 10, new DateTimeOffset(2025, 8, day, 12, 0, 0, TimeSpan.Zero), DateSource.Exif,
                new ExifSummary(camera, null, null, null, null, null, null, null), AssetState.Active), Ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, library, path, DateTimeOffset.UtcNow), Ct);
            await catalog.Analysis.SaveEmbeddingAsync(id, Clip, Vectors.Normalize(vector), Ct);
            return id;
        }

        var sea = await Photo("2025/Ferragosto/mare_0001.jpg", 15, [1, 0, 0, 0]);
        var seaSunset = await Photo("2025/Ferragosto/mare_0002.jpg", 15, [0.95f, 0.31f, 0, 0]);
        var mountain = await Photo("2025/Dolomiti/IMG_0003.jpg", 20, [0, 0, 1, 0], "Z 6");
        var raw = await Photo("2025/Ferragosto/mare_0002.nef", 15, [0.95f, 0.31f, 0, 0]);
        await catalog.Assets.AddPairAsync(raw, seaSunset, Ct);

        await catalog.Tags.SeedVocabularyAsync([new("sunset", "tramonto", "Natura"), new("beach", "spiaggia", "Natura")], TagSource.RamPlus, Ct);
        var sunset = (await catalog.Tags.FindAsync("sunset", Ct))!;
        await catalog.Tags.UpdateAsync(sunset.Id, "tramonto", true, "Natura", false, Ct);
        await catalog.Tags.ReplaceAutomaticAsync(seaSunset, TagSource.RamPlus, "r", [("sunset", 0.9), ("beach", 0.8)], Ct);
        await catalog.Tags.ReplaceAutomaticAsync(sea, TagSource.RamPlus, "r", [("beach", 0.8)], Ct);
        var sara = await catalog.People.CreatePersonAsync("Sara Bianchi", Ct);
        await catalog.Analysis.ReplaceAutomaticFacesAsync(seaSunset, [new Face(0, seaSunset, new BoundingBox(0.1f, 0.1f, 0.2f, 0.2f), null, 0.9, EyeState.Open, EyeState.Open, null, null, null, null, "t")], Ct);
        await catalog.People.AssignManualAsync([(await catalog.Analysis.GetFacesAsync(seaSunset, Ct))[0].Id], sara, Ct);
        await catalog.Decisions.SetUserAsync(mountain, CullingOutcome.Kept, 4, null, Ct);

        async Task<long[]> Ids(SearchQuery q) => (await catalog.Search.SearchAsync(q with { EmbeddingModelVersion = Clip }, Ct)).Hits.Select(h => h.AssetId).ToArray();

        Assert.Equal([mountain, seaSunset, sea], await Ids(SearchQuery.Empty));                       // newest first, the RAW twin is not a result
        Assert.Equal([seaSunset], await Ids(SearchQuery.Empty with { PersonIds = [sara] }));
        Assert.Equal([seaSunset, sea], await Ids(SearchQuery.Empty with { TagIds = [(await catalog.Tags.FindAsync("beach", Ct))!.Id] }));
        Assert.Equal([seaSunset], await Ids(SearchQuery.Empty with { Text = "tramonto" }));             // Italian label of a reviewed tag
        Assert.Equal([seaSunset], await Ids(SearchQuery.Empty with { Text = "sunse" }));                // prefix of the English key
        Assert.Equal([seaSunset, sea], await Ids(SearchQuery.Empty with { Text = "ferragosto" }));      // folder name, accents/case ignored
        Assert.Equal([seaSunset], await Ids(SearchQuery.Empty with { Text = "bianchi" }));              // person name
        Assert.Equal([mountain], await Ids(SearchQuery.Empty with { Camera = "Z 6" }));
        Assert.Equal([mountain], await Ids(SearchQuery.Empty with { MinRating = 3 }));
        Assert.Equal([seaSunset, sea], await Ids(SearchQuery.Empty with { From = new DateTimeOffset(2025, 8, 15, 0, 0, 0, TimeSpan.Zero), To = new DateTimeOffset(2025, 8, 16, 0, 0, 0, TimeSpan.Zero) }));

        // Meaning: "una montagna" is near the mountain only; the order is by score.
        var mountainText = Vectors.Normalize([0, 0.2f, 1, 0]);
        Assert.Equal([mountain], await Ids(SearchQuery.Empty with { Text = "una montagna", TextEmbedding = mountainText }));
        // Words match the sunset photo only; meaning adds the other sea photo, and the order is by score (the plain sea is closer).
        var results = await catalog.Search.SearchAsync(SearchQuery.Empty with { Text = "tramonto sul mare", TextEmbedding = Vectors.Normalize([1, 0.05f, 0, 0]), EmbeddingModelVersion = Clip }, Ct);
        Assert.True(results.ByMeaning);
        Assert.Equal([sea, seaSunset], results.Hits.Select(h => h.AssetId));

        // Similar photos, the photo itself excluded.
        Assert.Equal([seaSunset], await Ids(SearchQuery.Empty with { SimilarTo = sea }));

        // Renaming a person or tagging later is found at the next search (the text index follows the catalog).
        await catalog.People.RenameAsync(sara, "Sara Rossi", Ct);
        Assert.Equal([seaSunset], await Ids(SearchQuery.Empty with { Text = "rossi" }));
        Assert.Empty(await Ids(SearchQuery.Empty with { Text = "bianchi" }));
        Assert.Equal((4, 3), await catalog.Search.CoverageAsync(Clip, Ct));
    }

    /// <summary>
    /// 60,000 photos with tags, people, dates, cameras, ratings and 512-d embeddings (synthetic, written in bulk), then
    /// the typical searches, each timed from the request to the results. The report goes to the local test data.
    /// </summary>
    [Fact]
    [Trait(TestCategory.Name, TestCategory.Performance)]
    public async Task Searches_on_sixty_thousand_photos_take_less_than_a_second()
    {
        const int photos = 60_000;
        var path = Path.Combine(_temp.Path, "big.pfdb");
        await (await SqliteCatalog.OpenAsync(new CatalogOpenOptions(path, Path.Combine(_temp.Path, "b")), Ct)).DisposeAsync();
        var fill = Stopwatch.StartNew();
        Fill(path, photos);
        fill.Stop();

        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(path, Path.Combine(_temp.Path, "b")), Ct);
        var random = new Random(7);
        var tag = (await catalog.Tags.FindAsync("tag17", Ct))!.Id;
        var text = RandomVector(random);
        var report = new StringBuilder($"# Search on {photos:N0} photos — {DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}{Environment.NewLine}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Synthetic catalog written in {fill.Elapsed.TotalSeconds:0.0} s ({new FileInfo(path).Length / 1e6:0} MB).").AppendLine();
        report.AppendLine("| Search | Time | Results |").AppendLine("|---|---|---|");

        var first = await Timed("First search (builds the text index and loads the embeddings)", SearchQuery.Empty with { Text = "tramonto", TextEmbedding = text });
        var queries = new (string Name, SearchQuery Query)[]
        {
            ("Everything, newest first", SearchQuery.Empty),
            ("Person", SearchQuery.Empty with { PersonIds = [3] }),
            ("Person + tag", SearchQuery.Empty with { PersonIds = [3], TagIds = [tag] }),
            ("Word (Italian label)", SearchQuery.Empty with { Text = "tramonto" }),
            ("Word (folder / event)", SearchQuery.Empty with { Text = "ferragosto" }),
            ("Meaning (free text)", SearchQuery.Empty with { Text = "bambini che ridono al tramonto", TextEmbedding = text }),
            ("Person + tag + meaning", SearchQuery.Empty with { PersonIds = [3], TagIds = [tag], Text = "al mare", TextEmbedding = text }),
            ("Dates + camera + rating", SearchQuery.Empty with { From = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), To = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), Camera = "Camera 2", MinRating = 3 }),
            ("Similar photos", SearchQuery.Empty with { SimilarTo = 12_345 }),
        };
        var slowest = TimeSpan.Zero;
        foreach (var (name, query) in queries)
        {
            slowest = Max(slowest, await Timed(name, query));
        }

        // The facets panel refreshed as Consultazione does it: tags, events and people over the whole result (AND), then
        // with a group in OR ("+N", one more count).
        slowest = Max(slowest, await TimedFacets("Facets of everything (tags, events, people)", SearchQuery.Empty, null));
        slowest = Max(slowest, await TimedFacets("Facets in OR over a group of one tag", SearchQuery.Empty with { TagGroups = [[tag]] }, SearchQuery.Empty));

        // Changes made by the user (a person renamed, tags added and removed): the next search updates only those photos.
        await catalog.People.RenameAsync(3, "Sara Bianchi", Ct);
        for (var id = 1; id <= 200; id++)
        {
            await catalog.Tags.AddManualAsync(id, "vacanze", Ct);
            await catalog.Tags.SuppressAsync(id + 1000, tag, Ct);
        }

        slowest = Max(slowest, await Timed("After a person renamed and 400 tag changes", SearchQuery.Empty with { Text = "bianchi" }));

        var folder = Path.Combine(TestData.Root ?? _temp.Path, "lab", "performance");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"search-{DateTime.Now:yyyyMMdd-HHmmss}.md"), report.ToString(), Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        Assert.True(slowest < TimeSpan.FromSeconds(1), $"Slowest search {slowest.TotalMilliseconds:0} ms");
        _ = first;

        async Task<TimeSpan> Timed(string name, SearchQuery query)
        {
            var clock = Stopwatch.StartNew();
            var result = await catalog.Search.SearchAsync(query with { EmbeddingModelVersion = Clip }, Ct);
            clock.Stop();
            report.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {clock.Elapsed.TotalMilliseconds:0} ms | {result.Total:N0} |");
            return clock.Elapsed;
        }

        async Task<TimeSpan> TimedFacets(string name, SearchQuery query, SearchQuery? orBase)
        {
            // As the panel does it: tags and people together, plus the "+N" in OR.
            var clock = Stopwatch.StartNew();
            var counts = await catalog.Search.CountFacetsAsync(query, Ct);
            if (orBase is not null)
            {
                await catalog.Search.CountTagsAsync(orBase, query, Ct);
            }

            clock.Stop();
            report.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {clock.Elapsed.TotalMilliseconds:0} ms | {counts.Tags.Count:N0} tags, {counts.Persons.Count:N0} people |");
            return clock.Elapsed;
        }

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    public void Dispose() => _temp.Dispose();

    private static float[] RandomVector(Random random)
    {
        var v = new float[512];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)((random.NextDouble() * 2) - 1);
        }

        return Vectors.Normalize(v);
    }

    /// <summary>Bulk content: 200 tags (8 per photo), 300 people (0-3 faces per photo), 10 years, 5 cameras, ratings, events.</summary>
    private static void Fill(string path, int photos)
    {
        var random = new Random(42);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        void Exec(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        Exec("INSERT INTO Libraries (Id, Name, RootPath, XmpPolicy, IsReadOnly, Origin) VALUES (1, 'L', 'X:/foto', 'SidecarOnly', 0, 'ExistingArchive');");
        for (var t = 0; t < 200; t++)
        {
            Exec($"INSERT INTO Tags (Id, Key, Category, Source, Reviewed, Blacklisted) VALUES ({t + 1}, 'tag{t}', 'Natura', 'RamPlus', {(t % 2)}, 0);");
            Exec($"INSERT INTO TagLabels (TagId, Language, Label) VALUES ({t + 1}, 'it', '{(t == 17 ? "tramonto" : "etichetta" + t)}');");
        }

        for (var p = 1; p <= 300; p++)
        {
            Exec($"INSERT INTO Persons (Id, Name, Ignored, CreatedAtMs) VALUES ({p}, 'Persona {p}', 0, 0);");
        }

        string[] events = ["Ferragosto", "Natale", "Matrimonio", "Gita", "Compleanno", "Vacanze"];
        using var asset = Command("INSERT INTO Assets (Id, QuickHash, Format, Handler, Width, Height, CapturedAtMs, CaptureDateSource, CameraModel, State, CreatedAtMs) VALUES ($id, $q, 'JPG', 'jpeg', 6000, 4000, $t, 'Exif', $cam, 'Active', 0);", "$id", "$q", "$t", "$cam");
        using var location = Command("INSERT INTO AssetLocations (AssetId, LibraryId, RelativePath, LastSeenMs) VALUES ($id, 1, $path, 0);", "$id", "$path");
        using var tag = Command("INSERT OR IGNORE INTO AssetTags (AssetId, TagId, Source, Confidence, ModelVersion, State, UpdatedAtMs) VALUES ($id, $tag, 'RamPlus', 0.8, 'r', 'Active', 1);", "$id", "$tag");
        using var face = Command("INSERT INTO Faces (AssetId, X1, Y1, X2, Y2, Confidence, LeftEye, RightEye, PersonId, AssignmentOrigin, ModelVersion) VALUES ($id, 0.1, 0.1, 0.2, 0.2, 0.9, 'Open', 'Open', $p, 'Automatic', 'f');", "$id", "$p");
        using var decision = Command("INSERT INTO Decisions (AssetId, AutomaticOutcome, Rating, UpdatedAtMs) VALUES ($id, 'Kept', $r, 0);", "$id", "$r");
        using var embedding = Command("INSERT INTO Embeddings (AssetId, ModelVersion, Vector, CreatedAtMs) VALUES ($id, $m, $v, 0);", "$id", "$m", "$v");
        var start = new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        for (var id = 1; id <= photos; id++)
        {
            var taken = start + (long)(random.NextDouble() * 10 * 365.25 * 86_400_000);
            var day = DateTimeOffset.FromUnixTimeMilliseconds(taken);
            Run(asset, id, $"q{id}", taken, $"Camera {random.Next(5)}");
            Run(location, id, $"{day:yyyy}/{day:yyyy-MM-dd}_{events[random.Next(events.Length)]}/IMG_{id:00000}.jpg");
            for (var k = 0; k < 8; k++)
            {
                Run(tag, id, 1 + random.Next(200));
            }

            for (var k = random.Next(4); k > 0; k--)
            {
                Run(face, id, 1 + random.Next(300));
            }

            Run(decision, id, random.Next(6));
            Run(embedding, id, Clip, MemoryMarshal.AsBytes(RandomVector(random).AsSpan()).ToArray());
        }

        transaction.Commit();

        SqliteCommand Command(string sql, params string[] names)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var name in names)
            {
                command.Parameters.Add(new SqliteParameter { ParameterName = name });
            }

            command.Prepare();
            return command;
        }

        static void Run(SqliteCommand command, params object[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                command.Parameters[i].Value = values[i];
            }

            command.ExecuteNonQuery();
        }
    }
}
