// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Sources.Ingest;

namespace PhotoFlow.Sources.Jobs;

/// <summary>Creates import jobs from a plan (step 3f "Start"): one job item per file to copy, in shooting order.</summary>
public static class ImportJobs
{
    /// <summary>
    /// Creates the job and its items. The library of the primary destination is created on first use, with the
    /// <see cref="WritePolicy.EmbeddedForNonRaw"/> policy of import libraries (D21).
    /// </summary>
    public static async Task<long> CreateAsync(
        ICatalog catalog,
        ImportPlan plan,
        ImportSelection selection,
        string sourceRoot,
        IReadOnlyList<IngestDestination> destinations,
        string eventName,
        DateOnly eventDate,
        string profileId,
        FaceMode? faces,
        CancellationToken ct,
        TagLevels? tags = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destinations);
        var primary = destinations.SingleOrDefault(d => d.Kind == DestinationKind.Primary)
            ?? throw new ArgumentException("Exactly one primary destination is required.", nameof(destinations));

        var library = await catalog.Libraries.FindByRootAsync(primary.RootPath, ct).ConfigureAwait(false);
        var libraryId = library?.Id ?? await catalog.Libraries.AddAsync(
            new Library(0, Path.GetFileName(Path.TrimEndingDirectorySeparator(primary.RootPath)) is { Length: > 0 } name ? name : primary.RootPath,
                primary.RootPath, WritePolicy.EmbeddedForNonRaw, false, LibraryOrigin.Import, null), ct).ConfigureAwait(false);

        var settings = new ImportJobSettings(libraryId, sourceRoot, destinations, profileId, Faces: faces, Tags: tags);
        var jobId = await catalog.Jobs.CreateAsync(new Job(
            0, JobType.Import, sourceRoot, eventName, eventDate, profileId, false, null, JobState.Pending, JobPriority.Normal,
            ImportJobRunner.SerializeSettings(settings), DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        var items = plan.Selected(selection)
            .Select(f => new JobItem(0, jobId, null, f.File.Item.FullPath, f.TargetRelativePath, null, PipelineStage.None, JobItemState.Pending, null))
            .ToList();
        await catalog.Jobs.AddItemsAsync(jobId, items, ct).ConfigureAwait(false);
        return jobId;
    }
}
