// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Imaging;
using PhotoFlow.Tests.Support;
using SkiaSharp;

namespace PhotoFlow.Tests.Faces;

/// <summary>
/// Recognition, clustering and the lost key (phase 2 acceptance criterion: "chiave delle impronte rimossa: ricalcolo
/// riuscito con assegnazioni preservate"). Faces are synthetic: each person is a colour, and a stand-in embedder turns
/// the colour under the aligned keypoints into a fingerprint, read from the real preview of the file.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class PeopleEngineTests : IDisposable
{
    private static readonly DateTimeOffset Taken = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new("pf-people");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_the_key_recognition_waits_and_a_recompute_keeps_every_assignment()
    {
        var root = _temp.Sub("library");
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var keyStore = new FingerprintKeyStore(Path.Combine(_temp.Path, "face-key.json"));
        keyStore.Create("password of the catalog");
        var embedder = new ColourEmbedder();
        var previews = new PreviewProvider(new PhysicalFileSystem(), new ImageHandlerRegistry([new JpegImageHandler(new PhysicalFileSystem()), new SkiaImageHandler(new PhysicalFileSystem())]));

        // Two people, Sara (red) and Luca (blue), three photos each; the user names Sara and Luca.
        var faces = new Dictionary<string, long>();
        foreach (var (name, colour) in new[] { ("sara1", SKColors.Red), ("sara2", SKColors.Red), ("sara3", SKColors.Red), ("luca1", SKColors.Blue), ("luca2", SKColors.Blue), ("luca3", SKColors.Blue) })
        {
            faces[name] = await AddPhotoAsync(catalog, libraryId, root, name, colour, embedder, previews, keyStore.Protector);
        }

        var sara = await catalog.People.CreatePersonAsync("Sara", Ct);
        var luca = await catalog.People.CreatePersonAsync("Luca", Ct);
        await catalog.People.AssignManualAsync([faces["sara1"], faces["sara2"], faces["sara3"]], sara, Ct);
        await catalog.People.AssignManualAsync([faces["luca1"], faces["luca2"]], luca, Ct);
        await catalog.People.AssignManualAsync([faces["luca3"]], null, Ct); // "non è lui": kept as decided
        await PeopleEngine.RefreshPrototypesAsync(catalog, sara, Ct);
        await PeopleEngine.RefreshPrototypesAsync(catalog, luca, Ct);
        var before = await AssignmentsAsync(catalog);

        // The key is lost (another PC without the password): a new key replaces it, old fingerprints are unreadable.
        keyStore.Reset(null);
        var newSara = await AddPhotoAsync(catalog, libraryId, root, "sara4", SKColors.Red, embedder, previews, keyStore.Protector);
        var jobId = await JobWithAsync(catalog, newSara);
        await new FacesStage(catalog, () => keyStore.Protector).RunAsync(new JobContext(jobId, JobType.AnalyzeInPlace, "events"), null, Ct);
        Assert.Null((await FaceAsync(catalog, newSara)).PersonId); // nothing readable to compare with: no guess

        // Recompute with the new key: same rows, same people, same manual decisions; recognition works again.
        var (recomputed, missing) = await new FingerprintRecompute(catalog, new PhysicalFileSystem(), previews, embedder, keyStore.Protector).RunAsync(null, Ct);
        Assert.Equal(7, recomputed);
        Assert.Equal(0, missing);
        Assert.Equal(before, (await AssignmentsAsync(catalog)).Where(a => a.Key != newSara).ToDictionary());
        await new FacesStage(catalog, () => keyStore.Protector).RunAsync(new JobContext(jobId, JobType.AnalyzeInPlace, "events"), null, Ct);
        var recognized = await FaceAsync(catalog, newSara);
        Assert.Equal(sara, recognized.PersonId);
        Assert.Equal(AssignmentOrigin.Automatic, recognized.AssignmentOrigin);
        Assert.Null((await FaceAsync(catalog, faces["luca3"])).PersonId); // the user's "not him" stays
    }

    [Fact]
    public async Task Unknown_faces_of_a_year_become_unnamed_groups_and_strangers_stay_alone()
    {
        var root = _temp.Sub("library2");
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "d.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", root, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
        var keys = new FingerprintCipher(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var embedder = new ColourEmbedder();
        var previews = new PreviewProvider(new PhysicalFileSystem(), new ImageHandlerRegistry([new JpegImageHandler(new PhysicalFileSystem())]));
        var ids = new List<long>();
        var colours = new[] { SKColors.Green, SKColors.Green, SKColors.Green, SKColors.Green, SKColors.Yellow, SKColors.Yellow, SKColors.Yellow, SKColors.Purple };
        for (var i = 0; i < colours.Length; i++)
        {
            ids.Add(await AddPhotoAsync(catalog, libraryId, root, $"p{i}", colours[i], embedder, previews, keys, jitter: i));
        }

        var jobId = await JobWithAsync(catalog, ids.ToArray());
        await new FacesStage(catalog, () => keys).RunAsync(new JobContext(jobId, JobType.AnalyzeInPlace, "events"), null, Ct);

        var assigned = await AssignmentsAsync(catalog);
        Assert.Single(assigned.Where(a => ids[..4].Contains(a.Key)).Select(a => a.Value.PersonId).Distinct());
        Assert.Single(assigned.Where(a => ids[4..7].Contains(a.Key)).Select(a => a.Value.PersonId).Distinct());
        Assert.NotEqual(assigned[ids[0]].PersonId, assigned[ids[4]].PersonId);
        Assert.NotNull(assigned[ids[0]].PersonId);
        Assert.Null(assigned[ids[7]].PersonId); // a stranger seen once
        var persons = await catalog.People.ListPersonsAsync(Ct);
        Assert.Equal(2, persons.Count);
        Assert.All(persons, p => Assert.Null(p.Person.Name));
    }

    public void Dispose() => _temp.Dispose();

    private static async Task<Dictionary<long, (long? PersonId, AssignmentOrigin? Origin)>> AssignmentsAsync(ICatalog catalog)
    {
        var result = new Dictionary<long, (long?, AssignmentOrigin?)>();
        foreach (var assetId in Enumerable.Range(1, 64))
        {
            foreach (var face in await catalog.Analysis.GetFacesAsync(assetId, Ct))
            {
                result[face.Id] = (face.PersonId, face.AssignmentOrigin);
            }
        }

        return result;
    }

    private static async Task<Face> FaceAsync(ICatalog catalog, long faceId)
    {
        foreach (var assetId in Enumerable.Range(1, 64))
        {
            if ((await catalog.Analysis.GetFacesAsync(assetId, Ct)).FirstOrDefault(f => f.Id == faceId) is { } face)
            {
                return face;
            }
        }

        throw new KeyNotFoundException(faceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<long> JobWithAsync(ICatalog catalog, params long[] faceIds)
    {
        var jobId = await catalog.Jobs.CreateAsync(new Job(0, JobType.AnalyzeInPlace, null, "t", null, "events", false, null, JobState.Completed, JobPriority.Low, "{}", DateTimeOffset.UtcNow), Ct);
        var items = new List<JobItem>();
        foreach (var faceId in faceIds)
        {
            var face = await FaceAsync(catalog, faceId);
            items.Add(new JobItem(0, jobId, face.AssetId, $"x{faceId}.jpg", $"x{faceId}.jpg", null, PipelineStage.Decision, JobItemState.Done, null));
        }

        await catalog.Jobs.AddItemsAsync(jobId, items, Ct);
        return jobId;
    }

    /// <summary>A 200×200 photo of one colour with a face box in the middle; its fingerprint as the analysis stores it.</summary>
    private static async Task<long> AddPhotoAsync(ICatalog catalog, long libraryId, string root, string name, SKColor colour, IFaceEmbedder embedder, IPreviewProvider previews,
        IFingerprintProtector keys, int jitter = 0)
    {
        var path = Path.Combine(root, name + ".jpg");
        using (var bitmap = new SKBitmap(200, 200))
        {
            bitmap.Erase(new SKColor((byte)Math.Clamp(colour.Red + jitter, 0, 255), colour.Green, colour.Blue));
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            await File.WriteAllBytesAsync(path, data.ToArray(), Ct);
        }

        var assetId = await catalog.Assets.AddAsync(new Asset(0, name, null, "JPG", "jpeg", 200, 200, Taken, DateSource.Exif, null, AssetState.Active), Ct);
        await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, libraryId, name + ".jpg", DateTimeOffset.UtcNow), Ct);
        var keypoints = new[] { new Point2(0.4f, 0.4f), new Point2(0.6f, 0.4f), new Point2(0.5f, 0.5f), new Point2(0.42f, 0.6f), new Point2(0.58f, 0.6f) };
        var preview = await previews.GetPreviewAsync(path, CullingSignals.ReferenceLongSide, Ct);
        var detected = new DetectedFace(new PixelBox(60, 60, 140, 140), 0.9f, keypoints.Select(p => new Point2(p.X * 200, p.Y * 200)).ToArray(), null, null, null);
        var fingerprint = keys.Protect(await embedder.EmbedAsync(preview.Image, detected, Ct));
        await catalog.Analysis.ReplaceAutomaticFacesAsync(assetId,
            [new Face(0, assetId, new BoundingBox(0.3f, 0.3f, 0.7f, 0.7f), null, 0.9, EyeState.Open, EyeState.Open, fingerprint, null, null, null, "test@1", keypoints, embedder.ModelVersion)], Ct);
        return (await catalog.Analysis.GetFacesAsync(assetId, Ct)).Single().Id;
    }

    /// <summary>Fingerprint = normalized mean colour of the aligned crop (plus a constant to keep vectors apart from zero).</summary>
    private sealed class ColourEmbedder : IFaceEmbedder
    {
        public string ModelVersion => "colour@1/arcface";

        public int Dimensions => 4;

        public Task<float[]> EmbedAsync(RgbImage image, DetectedFace face, CancellationToken ct)
        {
            var crop = FaceAlignment.AlignedCrop(image, face.Keypoints).Pixels.Span;
            double r = 0, g = 0, b = 0;
            for (var i = 0; i < crop.Length; i += 3)
            {
                r += crop[i];
                g += crop[i + 1];
                b += crop[i + 2];
            }

            return Task.FromResult(Vectors.Normalize([(float)r, (float)g, (float)b, 0.05f * (float)(r + g + b)]));
        }
    }
}
