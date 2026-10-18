// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>
/// I5 and "Ripartire da zero con le persone" (P22): the user's assignments, names and drawn faces go only when the user
/// chose the reset for a complete regeneration of the faces, never with a regeneration without it nor with a trial on
/// a sample. Then every person goes, manual ones included, in the whole catalog; the detected faces stay; the names and
/// the regions PhotoFlow wrote leave the XMP, the regions of other programs stay.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Integration)]
public sealed class PeopleResetInvariantTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-people-reset");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_people_are_reset_only_on_the_users_explicit_choice()
    {
        await using var exifTool = ExifToolFixture.Start();
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, "c.pfdb"), Path.Combine(_temp.Path, "b")), Ct);
        var keys = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var (libraries, ids) = (new List<long>(), new List<long>());
        foreach (var name in new[] { "family", "trip" })
        {
            // Two libraries: the reset and the regeneration cover the whole catalog.
            var root = _temp.Sub(name);
            SyntheticImages.WriteJpeg(Path.Combine(root, $"{name}.jpg"), 400, 300);
            var libraryId = await catalog.Libraries.AddAsync(new Library(0, name, root, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), Ct);
            var id = await catalog.Assets.AddAsync(new Asset(0, $"q-{name}", $"f-{name}", "JPG", "jpeg", 400, 300, null, DateSource.Unknown, null, AssetState.Active), Ct);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, id, libraryId, $"{name}.jpg", DateTimeOffset.UtcNow), Ct);
            await catalog.Analysis.ReplaceAutomaticFacesAsync(id, [Face(id, keys, "arc@1/arcface")], Ct);
            await catalog.Decisions.SetUserAsync(id, CullingOutcome.Kept, 4, null, Ct);
            libraries.Add(libraryId);
            ids.Add(id);
        }

        var familyPath = Path.Combine(_temp.Path, "family", "family.jpg");
        await exifTool.ExecuteAsync(["-overwrite_original",
            "-XMP-mwg-rs:RegionInfo={AppliedToDimensions={W=400,H=300,Unit=pixel},RegionList=[{Area={X=0.8,Y=0.2,W=0.1,H=0.1,Unit=normalized},Name=Zia Lina,Type=Face}]}",
            familyPath], Ct);

        // The user named Sara on the first photo and drew Luca; the second photo has an unnamed group.
        var sara = await catalog.People.CreatePersonAsync("Sara", Ct);
        var luca = await catalog.People.CreatePersonAsync("Luca", Ct);
        var group = await catalog.People.CreatePersonAsync(null, Ct);
        await catalog.People.AssignManualAsync([(await catalog.Analysis.GetFacesAsync(ids[0], Ct)).Single().Id], sara, Ct);
        await catalog.Analysis.AddManualFaceAsync(ids[0], new BoundingBox(0.6f, 0.5f, 0.75f, 0.8f), luca, Ct);
        await catalog.People.AssignAutomaticAsync([((await catalog.Analysis.GetFacesAsync(ids[1], Ct)).Single().Id, group, 0.7)], Ct);
        var publisher = new XmpPublisher(catalog, new ExifToolMetadataWriter(exifTool, new PhysicalFileSystem()));
        await publisher.WriteAsync(ids, prudentMode: true, null, Ct);
        Assert.Contains("<mwg-rs:Name>Sara</mwg-rs:Name>", await PacketAsync(exifTool, familyPath), StringComparison.Ordinal);

        var current = new ModelVersions("culling-v1", "culling-v1+det@1", null, "ada@1/adaface", null, null, null);
        var runner = new ReanalysisJobRunner(catalog, new PhysicalFileSystem(), new NewRecognizer(keys), _ => Task.FromResult(current));
        async Task<ReanalysisApplied> RegenerateAsync(ReanalysisSettings settings)
        {
            var jobId = await ReanalysisJobRunner.CreateAsync(catalog, settings, Ct);
            await runner.RunAsync(jobId, null, Ct);
            return await Reanalysis.ApplyAsync(catalog, jobId, () => keys, publisher, true, Ct);
        }

        // Without the choice, and with it on a sample: the user's people stay (the new faces take over the assignments).
        await RegenerateAsync(new ReanalysisSettings(ReanalysisParts.Faces, ReanalysisMode.ForceAll, AllCatalog: true));
        Assert.Null((await RegenerateAsync(new ReanalysisSettings(ReanalysisParts.Faces, ReanalysisMode.ForceAll, Sample: 1, AllCatalog: true, ResetPeople: true))).Reset);
        Assert.Equal(3, (await catalog.People.ListPersonsAsync(Ct)).Count);
        var before = await catalog.People.CountResetAsync(Ct);
        Assert.Equal((3, 2, 1, 1), (before.Persons, before.NamedPersons, before.ManualAssignments, before.DrawnFaces));
        Assert.Equal(["Luca", "Sara"], before.Names);
        Assert.Equal(ids, before.AssetIds);
        Assert.Contains(await catalog.Analysis.GetFacesAsync(ids[0], Ct), f => f.PersonId == sara && f.AssignmentOrigin == AssignmentOrigin.Manual);

        // The reset, chosen and confirmed: everything about the people goes, in every library.
        var applied = await RegenerateAsync(new ReanalysisSettings(ReanalysisParts.Faces, ReanalysisMode.ForceAll, AllCatalog: true, ResetPeople: true));
        Assert.Equal(2, applied.Photos);
        Assert.Equal((3, 1, 1), (applied.Reset!.Persons, applied.Reset.ManualAssignments, applied.Reset.DrawnFaces));
        Assert.Empty(await catalog.People.ListPersonsAsync(Ct));
        Assert.True((await catalog.People.CountResetAsync(Ct)).IsEmpty);
        foreach (var id in ids)
        {
            var face = (await catalog.Analysis.GetFacesAsync(id, Ct)).Single(); // the drawn face is gone, the detected one stays
            Assert.Equal((null, null, null, "ada@1/adaface"), (face.PersonId, face.AssignmentOrigin, face.SuggestedPersonId, face.EmbeddingModel));
        }

        // The XMP: Sara and Luca leave the regions and the keywords; Zia Lina, written by another program, stays.
        var xmp = await PacketAsync(exifTool, familyPath);
        Assert.DoesNotContain("Sara", xmp, StringComparison.Ordinal);
        Assert.DoesNotContain("Luca", xmp, StringComparison.Ordinal);
        Assert.Contains("<mwg-rs:Name>Zia Lina</mwg-rs:Name>", xmp, StringComparison.Ordinal);
    }

    public void Dispose() => _temp.Dispose();

    private static Face Face(long assetId, IFingerprintProtector keys, string embeddingModel) =>
        new(0, assetId, new BoundingBox(0.1f, 0.2f, 0.3f, 0.5f), null, 0.9, EyeState.Open, EyeState.Open, keys.Protect([1f, 0f, 0f]), null, null, null,
            "culling-v1+det@1", [new Point2(0.1f, 0.2f)], embeddingModel);

    private static async Task<string> PacketAsync(ExifToolSession exifTool, string path) =>
        (await exifTool.ExecuteAsync(["-xmp", "-b", path], Ct)).Output;

    /// <summary>The new recognizer: the same face, a fingerprint of another model.</summary>
    private sealed class NewRecognizer(IFingerprintProtector keys) : IPhotoProcessor
    {
        public Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct) =>
            Task.FromResult(new PhotoProcessingResult(
                new AnalyzerResult("culling-v1", "culling-v1+det@1", new Dictionary<string, double> { ["sharpness"] = 0.5 }),
                [Face(0, keys, "ada@1/adaface")],
                new DecisionProposal(CullingOutcome.Kept, []),
                "engine+culling-v1+det@1",
                null,
                null,
                null,
                null));
    }
}
