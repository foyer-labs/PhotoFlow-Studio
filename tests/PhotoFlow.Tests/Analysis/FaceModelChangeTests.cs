// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Acceptance of phase 5 "Cambio modello volti: assegnazioni trasferite, nessuna persona persa" (spec §5, written before
/// the code): the faces found by a new model take the person of the old face they overlap (IoU ≥ 0.5, the best pair
/// first), the user's assignments stay the user's, a face the new model does not find is kept when the user decided it,
/// people are never deleted, covers and prototypes move to the new faces (prototypes of the new model).
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class FaceModelChangeTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-face-change");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Assignments_move_to_the_faces_of_the_new_model_and_no_person_is_lost()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var keys = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var assetId = await catalog.Assets.AddAsync(new Asset(0, "q", "f", "JPG", "jpeg", 1000, 800, new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero), DateSource.Exif, null, AssetState.Active), Ct);
        float[] Vector(int seed) => [.. Enumerable.Range(0, 128).Select(i => MathF.Sin(i * seed))];
        Face Old(float x, int seed) => new(0, assetId, new BoundingBox(x, 0.2f, x + 0.1f, 0.35f), null, 0.9, EyeState.Open, EyeState.Open,
            keys.Protect(Vector(seed)), null, null, null, "buffalo@1", [new Point2(x, 0.25f)], "buffalo@1/arcface");

        // Old model: A (Anna, by the user), B and C (Bruno, automatic), D (Carla, by the user).
        await catalog.Analysis.ReplaceAutomaticFacesAsync(assetId, [Old(0.1f, 1), Old(0.3f, 2), Old(0.5f, 3), Old(0.7f, 4)], Ct);
        var old = (await catalog.Analysis.GetFacesAsync(assetId, Ct)).OrderBy(f => f.Box.X1).ToList();
        var anna = await catalog.People.CreatePersonAsync("Anna", Ct);
        var bruno = await catalog.People.CreatePersonAsync("Bruno", Ct);
        var carla = await catalog.People.CreatePersonAsync("Carla", Ct);
        await catalog.People.AssignManualAsync([old[0].Id], anna, Ct);
        await catalog.People.AssignAutomaticAsync([(old[1].Id, bruno, 0.8), (old[2].Id, bruno, 0.7)], Ct);
        await catalog.People.AssignManualAsync([old[3].Id], carla, Ct);
        await catalog.People.SetCoverAsync(anna, old[0].Id, Ct);
        foreach (var person in new[] { anna, bruno, carla })
        {
            await PeopleEngine.RefreshPrototypesAsync(catalog, person, Ct);
        }

        // New model (YuNet + SFace): A' and B' a little shifted, C and D not found, a new face E.
        Face New(float x, float y, int seed) => new(0, assetId, new BoundingBox(x, y, x + 0.1f, y + 0.15f), null, 0.8, EyeState.Unknown, EyeState.Unknown,
            keys.Protect(Vector(seed + 10)), null, null, null, "yunet@1", [new Point2(x, y)], "sface@1/sface");
        var result = await FaceTransfer.ApplyAsync(catalog, assetId, [New(0.105f, 0.21f, 1), New(0.29f, 0.19f, 2), New(0.9f, 0.5f, 5)], Ct);
        Assert.Equal((2, 1, 1, 1), (result.Transferred, result.KeptManual, result.Unmatched, result.New));

        var faces = (await catalog.Analysis.GetFacesAsync(assetId, Ct)).OrderBy(f => f.Box.X1).ToList();
        Assert.Equal(4, faces.Count);
        Assert.Equal((anna, AssignmentOrigin.Manual, "yunet@1"), (faces[0].PersonId!.Value, faces[0].AssignmentOrigin!.Value, faces[0].ModelVersion));
        Assert.Equal((bruno, AssignmentOrigin.Automatic, "sface@1/sface"), (faces[1].PersonId!.Value, faces[1].AssignmentOrigin!.Value, faces[1].EmbeddingModel));
        Assert.Equal((carla, AssignmentOrigin.Manual, "buffalo@1"), (faces[2].PersonId!.Value, faces[2].AssignmentOrigin!.Value, faces[2].ModelVersion)); // D kept
        Assert.Null(faces[3].PersonId);                                                                                                                       // E: to recognize

        // No person lost: everyone still exists and has faces; the cover follows the face; prototypes of the new model.
        var persons = await catalog.People.ListPersonsAsync(Ct);
        Assert.All(new[] { anna, bruno, carla }, id => Assert.Contains(persons, p => p.Person.Id == id && p.FaceCount > 0));
        Assert.Equal(faces[0].Id, (await catalog.People.GetPersonAsync(anna, Ct))!.CoverFaceId);
        var prototypes = await catalog.People.ListPrototypesAsync(Ct);
        Assert.Contains(prototypes, p => p.PersonId == anna && p.ModelVersion == "sface@1/sface" && p.FaceId == faces[0].Id);
        Assert.DoesNotContain(prototypes, p => p.PersonId == bruno && p.ModelVersion == "buffalo@1/arcface");
        Assert.All(prototypes, p => Assert.Contains(faces, f => f.Id == p.FaceId));
    }

    public void Dispose() => _temp.Dispose();
}
