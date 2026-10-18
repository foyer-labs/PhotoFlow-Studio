// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>
/// I5 for a face drawn by the user (spec §7, 13 Oct 2026): it is the user's (manual assignment, version "manual", no
/// fingerprint); a new analysis neither removes it nor adds the same face twice; a regeneration that finds the same
/// face puts the detected face in its place keeping the person and the manual assignment, and keeps it when it does not.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class ManualFaceInvariantTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-manual-face");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_face_drawn_by_the_user_survives_every_analysis()
    {
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), _temp.Sub("b")), Ct);
        var keys = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var asset = await catalog.Assets.AddAsync(new Asset(0, "q", null, "JPG", "jpeg", 400, 300, null, DateSource.Unknown, null, AssetState.Active), Ct);
        var luca = await catalog.People.CreatePersonAsync("Luca", Ct);

        var drawn = new BoundingBox(0.6f, 0.2f, 0.8f, 0.5f);
        var id = await catalog.Analysis.AddManualFaceAsync(asset, drawn, luca, Ct);
        var face = (await catalog.Analysis.GetFacesAsync(asset, Ct)).Single();
        Assert.Equal((id, luca, AssignmentOrigin.Manual, FaceDefaults.ManualModelVersion), (face.Id, face.PersonId!.Value, face.AssignmentOrigin!.Value, face.ModelVersion));
        Assert.Null(face.EncryptedEmbedding);

        Face Detected(BoundingBox box) => new(0, asset, box, null, 0.9, EyeState.Open, EyeState.Open, keys.Protect([1f, 0f, 0f]), null, null, null, "det@2", [], "arc@1");

        // A new analysis finds another face elsewhere, and the same face: the drawn one stays, not doubled.
        await catalog.Analysis.ReplaceAutomaticFacesAsync(asset, [Detected(new BoundingBox(0.1f, 0.1f, 0.3f, 0.4f)), Detected(new BoundingBox(0.61f, 0.21f, 0.8f, 0.5f))], Ct);
        var faces = await catalog.Analysis.GetFacesAsync(asset, Ct);
        Assert.Equal(2, faces.Count);
        Assert.Contains(faces, f => f.Id == id && f.PersonId == luca && f.AssignmentOrigin == AssignmentOrigin.Manual);

        // A regeneration that does not find it: kept.
        await FaceTransfer.ApplyAsync(catalog, asset, [Detected(new BoundingBox(0.1f, 0.1f, 0.3f, 0.4f))], Ct);
        Assert.Contains(await catalog.Analysis.GetFacesAsync(asset, Ct), f => f.Id == id && f.PersonId == luca);

        // A regeneration that finds it: the detected face takes its place, with Luca and the manual assignment.
        await FaceTransfer.ApplyAsync(catalog, asset, [Detected(new BoundingBox(0.62f, 0.2f, 0.8f, 0.52f))], Ct);
        var after = (await catalog.Analysis.GetFacesAsync(asset, Ct)).Single();
        Assert.Equal((luca, AssignmentOrigin.Manual, "det@2"), (after.PersonId!.Value, after.AssignmentOrigin!.Value, after.ModelVersion));

        // A box outside the photo is refused.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => catalog.Analysis.AddManualFaceAsync(asset, new BoundingBox(0.5f, 0.5f, 0.5f, 0.9f), luca, Ct));
    }

    public void Dispose() => _temp.Dispose();
}
