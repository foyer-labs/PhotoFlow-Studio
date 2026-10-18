// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PhotoFlow.App.Services;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Paths;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Data;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Faces.Recognition;
using PhotoFlow.Imaging;
using PhotoFlow.Metadata.ExifTool;
using PhotoFlow.Sources.Jobs;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Quality;

/// <summary>
/// Phase 5 measurements (plan 5.8) on the local photos, read in place: the cost of the captions (Florence-2) in the
/// analysis; a real change of face model (buffalo_l → YuNet + SFace) on the analyzed catalog through the regeneration,
/// with the people found before (one face per person confirmed by the "user"): how many assignments move, whether any
/// person is lost; the export and import of the resulting catalog. The folders are only read.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Performance)]
public sealed class Phase5PerformanceTests : IDisposable
{
    private static readonly int Workers = Math.Max(1, PipelineDefaults.DefaultAnalysisWorkers / 2);

    private readonly TempFolder _temp = new("pf-phase5-perf");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Captions_cost_on_the_local_set()
    {
        var source = TestData.RequireDirectory("photos");
        var files = Directory.EnumerateFiles(source, "2015*.jpg").Order(StringComparer.Ordinal).Take(24).ToList();
        var fs = new PhotoFlow.Core.IO.PhysicalFileSystem();
        var report = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"# Phase 5 performance — captions — {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
        var times = new Dictionary<bool, TimeSpan>();
        foreach (var captions in new[] { false, true })
        {
            var library = _temp.Sub(captions ? "with" : "without");
            foreach (var file in files)
            {
                File.Copy(file, Path.Combine(library, Path.GetFileName(file)));
            }

            await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(Path.Combine(_temp.Path, $"c{captions}.pfdb"), _temp.Sub($"b{captions}")), Ct);
            await using var analysis = await LocalAnalysis.CreateAsync(fs, null, Ct, captions: captions);
            var runner = new ArchiveJobRunner(catalog, new ExifToolExifReader(analysis.ExifTool), fs, analysis.Processor);
            var warm = _temp.Sub($"warm{captions}");
            File.Copy(files[0], Path.Combine(warm, "w.jpg"));
            var warmId = await catalog.Libraries.AddAsync(new Library(0, "w", warm, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
            var tags = captions ? PhotoFlow.Core.Tagging.TagLevels.Caption : PhotoFlow.Core.Tagging.TagLevels.None;
            await runner.RunAsync(await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(warmId, "events", ArchiveOrder.ByFolder, FaceMode.DetectOnly, Workers, false, tags), Ct), null, Ct);
            var libraryId = await catalog.Libraries.AddAsync(new Library(0, "L", library, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);
            var jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FaceMode.DetectOnly, Workers, false, tags), Ct);
            var clock = Stopwatch.StartNew();
            await runner.RunAsync(jobId, null, Ct);
            times[captions] = clock.Elapsed;
            if (captions)
            {
                var assets = (await catalog.Jobs.GetItemsAsync(jobId, Ct)).Select(i => i.AssetId).OfType<long>().ToList();
                var written = await catalog.Captions.GetForAssetsAsync(assets, Ct);
                Assert.Equal(assets.Count, written.Count);
                report.AppendLine("Captions (first 5):").AppendLine();
                foreach (var caption in written.Values.Take(5))
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"- {caption.Text}");
                }
            }
        }

        report.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{files.Count} photos, {Workers} workers, after a warm-up: without captions {times[false].TotalSeconds:0.0} s, with {times[true].TotalSeconds:0.0} s")
            .AppendLine(CultureInfo.InvariantCulture, $"Captions cost {(times[true] - times[false]).TotalMilliseconds / files.Count:0} ms per photo (wall clock).");
        await Write("phase5-captions", report.ToString());
    }

    [Fact]
    public async Task A_change_of_face_model_keeps_every_person()
    {
        var source = Environment.GetEnvironmentVariable("PHOTOFLOW_PERF_PHOTOS") is { Length: > 0 } custom ? custom : TestData.RequireDirectory("photos");
        var before = FolderSnapshot.Take(source);
        var fs = new PhotoFlow.Core.IO.PhysicalFileSystem();
        var paths = new DefaultAppPaths(_temp.Sub("profile"));
        var key = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var report = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"# Phase 5 performance — face model change — {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
        await using var catalog = await SqliteCatalog.OpenAsync(new CatalogOpenOptions(paths.CatalogPath, paths.BackupsDirectory), Ct);
        var libraryId = await catalog.Libraries.AddAsync(new Library(0, "Photos", source, WritePolicy.SidecarOnly, false, LibraryOrigin.ExistingArchive, null), Ct);

        // buffalo_l: analysis, recognition and grouping.
        long jobId;
        TimeSpan analyzed;
        await using (var buffalo = await LocalAnalysis.CreateAsync(fs, null, Ct, recognition: true, protector: key))
        {
            jobId = await ArchiveJobRunner.CreateAsync(catalog, fs, new ArchiveJobSettings(libraryId, "events", ArchiveOrder.ByFolder, FaceMode.Recognize, Workers, false, PhotoFlow.Core.Tagging.TagLevels.None), Ct);
            var clock = Stopwatch.StartNew();
            await new ArchiveJobRunner(catalog, new ExifToolExifReader(buffalo.ExifTool), fs, buffalo.Processor, [new FacesStage(catalog, () => key)]).RunAsync(jobId, null, Ct);
            analyzed = clock.Elapsed;
        }

        // The "user" names every group and confirms one face of each.
        var persons = (await catalog.People.ListPersonsAsync(Ct)).ToList();
        foreach (var (person, n) in persons.Select((p, i) => (p, i)))
        {
            await catalog.People.RenameAsync(person.Person.Id, $"Persona {n + 1}", Ct);
            var first = (await catalog.People.ListFacesOfPersonAsync(person.Person.Id, Ct)).OrderBy(f => f.Id).First();
            await catalog.People.AssignManualAsync([first.Id], person.Person.Id, Ct);
        }

        var facesBefore = await FacesAsync(catalog);
        report.AppendLine(CultureInfo.InvariantCulture, $"Folder {source}; {Workers} workers. Analysis with buffalo_l (recognition and grouping): {analyzed.TotalSeconds:0.0} s.")
            .AppendLine(CultureInfo.InvariantCulture, $"Before: {facesBefore.Count} faces, {persons.Count} people, {facesBefore.Count(f => f.PersonId is not null)} assigned ({facesBefore.Count(f => f.AssignmentOrigin == AssignmentOrigin.Manual)} by the user).")
            .AppendLine();

        // YuNet + SFace through the regeneration.
        ReanalysisPreview preview;
        ReanalysisApplied applied;
        TimeSpan regenerated;
        await using (var yunet = await LocalAnalysis.CreateAsync(fs, null, Ct, recognition: true, yunet: true, protector: key))
        {
            var reanalysis = await ReanalysisJobRunner.CreateAsync(catalog, new ReanalysisSettings(ReanalysisParts.Culling | ReanalysisParts.Faces, LibraryId: libraryId, Workers: Workers), Ct);
            var clock = Stopwatch.StartNew();
            await new ReanalysisJobRunner(catalog, fs, yunet.Processor, _ => Task.FromResult(yunet.Versions!)).RunAsync(reanalysis, null, Ct);
            regenerated = clock.Elapsed;
            preview = await Reanalysis.PreviewAsync(catalog, reanalysis, Ct);
            applied = await Reanalysis.ApplyAsync(catalog, reanalysis, () => key, null, true, Ct);
        }

        var facesAfter = await FacesAsync(catalog);
        var personsAfter = await catalog.People.ListPersonsAsync(Ct);
        var lostPeople = persons.Count(p => personsAfter.All(a => a.Person.Id != p.Person.Id || a.FaceCount == 0));
        report.AppendLine(CultureInfo.InvariantCulture, $"Regeneration with YuNet + SFace: {regenerated.TotalSeconds:0.0} s, {preview.Photos} photos staged.")
            .AppendLine(CultureInfo.InvariantCulture, $"Preview: {preview.FacesTransferred} assignments transferred, {preview.FacesNew} new faces, {preview.FacesLost} automatic faces not found again, {preview.FacesKeptManual} user faces kept.")
            .AppendLine(CultureInfo.InvariantCulture, $"After: {facesAfter.Count} faces, {personsAfter.Count} people, {facesAfter.Count(f => f.PersonId is not null)} assigned ({facesAfter.Count(f => f.AssignmentOrigin == AssignmentOrigin.Manual)} by the user); people without faces: {lostPeople}.")
            .AppendLine(CultureInfo.InvariantCulture, $"Faces with SFace fingerprints: {facesAfter.Count(f => f.EmbeddingModel?.EndsWith("/sface", StringComparison.Ordinal) == true)}.");

        // Export and import of the catalog.
        var package = Path.Combine(_temp.Path, "archive.pfcatalog");
        var clockExport = Stopwatch.StartNew();
        var exported = await CatalogPackage.ExportAsync(catalog, paths, key, package, new CatalogExportOptions(false, "password"), Ct);
        var exportTime = clockExport.Elapsed;
        var other = new DefaultAppPaths(_temp.Sub("profile-b"));
        var clockImport = Stopwatch.StartNew();
        await CatalogPackage.PrepareImportAsync(package, other, new Dictionary<long, string>(), "password", new FingerprintCipher(RandomNumberGenerator.GetBytes(32)), Ct);
        report.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Export: {exportTime.TotalSeconds:0.0} s, {exported.Bytes / 1024.0 / 1024.0:0.0} MB ({exported.Assets} photos, {exported.Fingerprints} fingerprints); import prepared in {clockImport.Elapsed.TotalSeconds:0.0} s.");
        await Write("phase5-faces", report.ToString());

        Assert.Equal(0, lostPeople);
        Assert.Equal(facesBefore.Count(f => f.AssignmentOrigin == AssignmentOrigin.Manual), facesAfter.Count(f => f.AssignmentOrigin == AssignmentOrigin.Manual));
        Assert.Equal(before, FolderSnapshot.Take(source)); // the folder is only read
    }

    public void Dispose() => _temp.Dispose();

    private static async Task<List<Face>> FacesAsync(ICatalog catalog)
    {
        var faces = new List<Face>();
        foreach (var person in await catalog.People.ListPersonsAsync(Ct))
        {
            faces.AddRange(await catalog.People.ListFacesOfPersonAsync(person.Person.Id, Ct));
        }

        foreach (var face in await catalog.People.ListUnassignedFingerprintsAsync(null, Ct))
        {
            faces.Add(new Face(face.FaceId, face.AssetId, default, null, 0, EyeState.Unknown, EyeState.Unknown, face.EncryptedEmbedding, null, null, null, string.Empty, null, face.EmbeddingModel));
        }

        return faces;
    }

    private async Task Write(string name, string report)
    {
        var folder = Path.Combine(TestData.Root ?? _temp.Path, "lab", "performance");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.md"), report, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
    }
}
