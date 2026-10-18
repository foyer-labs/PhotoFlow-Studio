// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Faces.Clustering;

namespace PhotoFlow.Faces.Recognition;

/// <summary>Matching parameters per fingerprint model (spec §7 <c>matchThreshold</c>, to be calibrated on real data).</summary>
public static class FaceMatching
{
    /// <summary>ArcFace (buffalo_l): cosine from which two faces are the same person.</summary>
    public const double ArcFaceThreshold = 0.45;

    /// <summary>SFace: OpenCV's recommended cosine threshold.</summary>
    public const double SFaceThreshold = 0.363;

    /// <summary>
    /// AdaFace IR-101 (P22): the false-match rate of ArcFace at 0.45, measured on LFW (13,215 faces, 3 million pairs of
    /// different people, 15 Oct 2026): 0.42 (with it 99.79% of the pairs of the same person match, ArcFace 99.56%).
    /// </summary>
    public const double AdaFaceThreshold = 0.42;

    /// <summary>The best person must lead the second by this much, otherwise the face is only suggested.</summary>
    public const double AmbiguityMargin = 0.05;

    /// <summary>Below the threshold by at most this much: suggestion (dashed amber chip, 4b).</summary>
    public const double SuggestionBand = 0.10;

    public const int ClusterMinSize = 3;
    public const int ClusterMinSamples = 2;

    /// <summary>Exact HDBSCAN up to this many faces per block; above, a 15-neighbor graph.</summary>
    public const int ExactClusteringLimit = 3000;
    public const int ApproximateNeighbors = 15;

    public const int PrototypesPerYear = 5;

    public static double Threshold(string embeddingModel) =>
        embeddingModel.EndsWith("/sface", StringComparison.Ordinal) ? SFaceThreshold
        : embeddingModel.EndsWith("/adaface", StringComparison.Ordinal) ? AdaFaceThreshold
        : ArcFaceThreshold;
}

/// <summary>
/// Pipeline stage 7 (barrier, spec §7 "Flusso"): the new faces of the job are compared with the samples of the known
/// people (named, unnamed groups, ignored strangers) — a clear match is assigned, a near one suggested — then the
/// faces still unknown are clustered per year of capture with HDBSCAN together with the unknown faces of the same
/// year already in the catalog; each cluster becomes an unnamed person ("Persona N"). Manual assignments are never
/// touched (I5). Without the key nothing happens: recognition is suspended (P13).
/// </summary>
public sealed class FacesStage(ICatalog catalog, Func<IFingerprintProtector> protector) : IBatchStage
{
    public PipelineStage Stage => PipelineStage.Faces;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var keys = protector();
        if (!keys.IsAvailable)
        {
            return;
        }

        var assets = (await catalog.Jobs.GetItemsAsync(job.JobId, ct).ConfigureAwait(false))
            .Where(i => i.State == JobItemState.Done && i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct().ToList();
        var faces = (await catalog.People.ListFingerprintsForAssetsAsync(assets, ct).ConfigureAwait(false))
            .Where(f => f.PersonId is null && f.Origin != AssignmentOrigin.Manual)
            .ToList();
        progress?.Report(new StageProgress(Stage, 0, faces.Count, null));

        var touched = await new PeopleEngine(catalog, keys).RecognizeAsync(faces, ct).ConfigureAwait(false);
        var years = faces.Select(f => f.Year).Distinct().ToList();
        foreach (var year in years)
        {
            touched.UnionWith(await new PeopleEngine(catalog, keys).ClusterYearAsync(year, ct).ConfigureAwait(false));
        }

        foreach (var personId in touched)
        {
            await PeopleEngine.RefreshPrototypesAsync(catalog, personId, ct).ConfigureAwait(false);
        }

        progress?.Report(new StageProgress(Stage, faces.Count, faces.Count, null));
    }
}

/// <summary>Recognition, clustering and samples of the people (spec §7, P12).</summary>
public sealed class PeopleEngine(ICatalog catalog, IFingerprintProtector keys)
{
    /// <summary>Assigns or suggests known people; returns the people that received faces.</summary>
    public async Task<HashSet<long>> RecognizeAsync(IReadOnlyList<FaceFingerprint> faces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(faces);
        var touched = new HashSet<long>();
        if (faces.Count == 0)
        {
            return touched;
        }

        var prototypes = new List<(long PersonId, string Model, float[] Vector)>();
        foreach (var prototype in await catalog.People.ListPrototypesAsync(ct).ConfigureAwait(false))
        {
            if (TryRead(prototype.EncryptedEmbedding) is { } vector)
            {
                prototypes.Add((prototype.PersonId, prototype.ModelVersion, vector));
            }
        }

        var assign = new List<(long FaceId, long PersonId, double? Similarity)>();
        var suggest = new List<(long FaceId, long? PersonId, double? Similarity)>();
        foreach (var face in faces)
        {
            if (TryRead(face.EncryptedEmbedding) is not { } vector)
            {
                continue;
            }

            var best = prototypes.Where(p => p.Model == face.EmbeddingModel)
                .GroupBy(p => p.PersonId)
                .Select(g => (PersonId: g.Key, Similarity: (double)g.Max(p => Vectors.Cosine(vector, p.Vector))))
                .OrderByDescending(x => x.Similarity)
                .Take(2)
                .ToList();
            if (best.Count == 0)
            {
                continue;
            }

            var threshold = FaceMatching.Threshold(face.EmbeddingModel);
            var (person, similarity) = best[0];
            var clear = best.Count < 2 || best[0].Similarity - best[1].Similarity >= FaceMatching.AmbiguityMargin;
            if (similarity >= threshold && clear)
            {
                assign.Add((face.FaceId, person, similarity));
                touched.Add(person);
            }
            else if (similarity >= threshold - FaceMatching.SuggestionBand)
            {
                suggest.Add((face.FaceId, person, similarity));
            }
        }

        await catalog.People.AssignAutomaticAsync(assign, ct).ConfigureAwait(false);
        await catalog.People.SuggestAsync(suggest, ct).ConfigureAwait(false);
        return touched;
    }

    /// <summary>Clusters the unknown faces of a year (per fingerprint model); returns the new unnamed people.</summary>
    public async Task<HashSet<long>> ClusterYearAsync(int? year, CancellationToken ct)
    {
        var created = new HashSet<long>();
        var unknown = await catalog.People.ListUnassignedFingerprintsAsync(year, ct).ConfigureAwait(false);
        foreach (var model in unknown.GroupBy(f => f.EmbeddingModel))
        {
            var readable = model.Select(f => (Face: f, Vector: TryRead(f.EncryptedEmbedding))).Where(x => x.Vector is not null).ToList();
            if (readable.Count < FaceMatching.ClusterMinSize)
            {
                continue;
            }

            var neighbors = readable.Count <= FaceMatching.ExactClusteringLimit ? readable.Count : FaceMatching.ApproximateNeighbors;
            var labels = Hdbscan.Cluster(readable.Select(x => x.Vector!).ToList(), FaceMatching.ClusterMinSize, FaceMatching.ClusterMinSamples, neighbors, ClusterSelection.ExcessOfMass);
            foreach (var cluster in labels.Select((label, i) => (label, i)).Where(x => x.label >= 0).GroupBy(x => x.label))
            {
                var members = cluster.Select(x => readable[x.i]).ToList();
                var centroid = Vectors.Mean(members.Select(m => m.Vector!).ToList());
                var personId = await catalog.People.CreatePersonAsync(null, ct).ConfigureAwait(false);
                await catalog.People.AssignAutomaticAsync(
                    members.Select(m => (m.Face.FaceId, personId, (double?)Vectors.Cosine(m.Vector!, centroid))).ToList(), ct).ConfigureAwait(false);
                created.Add(personId);
            }
        }

        return created;
    }

    /// <summary>
    /// Samples of a person per year (P12): up to <see cref="FaceMatching.PrototypesPerYear"/> per year and model, the
    /// faces confirmed by the user first, then the most typical automatic ones. The encrypted blobs are reused.
    /// </summary>
    public static async Task RefreshPrototypesAsync(ICatalog catalog, long personId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var faces = (await catalog.People.ListFacesOfPersonAsync(personId, ct).ConfigureAwait(false))
            .Where(f => f.EncryptedEmbedding is not null && f.EmbeddingModel is not null)
            .ToList();
        var years = new Dictionary<long, int?>();
        foreach (var assetId in faces.Select(f => f.AssetId).Distinct())
        {
            years[assetId] = (await catalog.Assets.GetAsync(assetId, ct).ConfigureAwait(false))?.CapturedAt?.Year;
        }

        var prototypes = faces
            .GroupBy(f => (Year: years[f.AssetId], f.EmbeddingModel))
            .SelectMany(g => g
                .OrderByDescending(f => f.AssignmentOrigin == AssignmentOrigin.Manual)
                .ThenByDescending(f => f.Similarity ?? 0)
                .ThenBy(f => f.Id)
                .Take(FaceMatching.PrototypesPerYear)
                .Select(f => new PersonPrototype(0, personId, f.Id, g.Key.Year, f.EncryptedEmbedding!, f.EmbeddingModel!)))
            .ToList();
        await catalog.People.ReplacePrototypesAsync(personId, prototypes, ct).ConfigureAwait(false);
    }

    private float[]? TryRead(byte[] blob)
    {
        try
        {
            return keys.Unprotect(blob);
        }
        catch (FingerprintKeyUnavailableException)
        {
            return null; // written with another key: waits for the recompute
        }
    }
}

/// <summary>
/// Recomputes every fingerprint with the current key (after a key reset, spec §7 "chiave non disponibile"): each face
/// keeps its row, its person and its assignment; only the encrypted fingerprint changes. The preview is read again
/// from the library and the stored keypoints give the alignment, so no new detection is needed.
/// </summary>
public sealed class FingerprintRecompute(ICatalog catalog, IFileSystem fileSystem, IPreviewProvider previews, IFaceEmbedder embedder, IFingerprintProtector keys)
{
    public async Task<(int Recomputed, int Missing)> RunAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var faces = await catalog.People.ListFacesWithKeypointsAsync(ct).ConfigureAwait(false);
        var (done, missing) = (0, 0);
        foreach (var asset in faces.GroupBy(f => f.AssetId))
        {
            var path = await AssetFiles.LocateAsync(catalog, fileSystem, asset.Key, ct).ConfigureAwait(false);
            if (path is null)
            {
                missing += asset.Count();
                continue;
            }

            var preview = await previews.GetPreviewAsync(path, CullingSignals.ReferenceLongSide, ct).ConfigureAwait(false);
            var (width, height) = (preview.Image.Width, preview.Image.Height);
            foreach (var face in asset)
            {
                var keypoints = face.Keypoints!.Select(p => new Point2(p.X * width, p.Y * height)).ToArray();
                var detected = new DetectedFace(new PixelBox(face.Box.X1 * width, face.Box.Y1 * height, face.Box.X2 * width, face.Box.Y2 * height), (float)face.Confidence, keypoints, null, null, null);
                var fingerprint = keys.Protect(await embedder.EmbedAsync(preview.Image, detected, ct).ConfigureAwait(false));
                await catalog.People.SetFingerprintAsync(face.Id, fingerprint, embedder.ModelVersion, CancellationToken.None).ConfigureAwait(false);
                done++;
                progress?.Report((done, faces.Count));
            }
        }

        var persons = await catalog.People.ListPersonsAsync(ct).ConfigureAwait(false);
        foreach (var person in persons)
        {
            await PeopleEngine.RefreshPrototypesAsync(catalog, person.Person.Id, ct).ConfigureAwait(false);
        }

        return (done, missing);
    }
}
