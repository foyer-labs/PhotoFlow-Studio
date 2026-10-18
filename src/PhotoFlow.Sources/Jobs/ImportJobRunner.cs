// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Threading.Channels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Core.Sources;
using PhotoFlow.Sources.Ingest;

namespace PhotoFlow.Sources.Jobs;

/// <summary>
/// Import job (spec §3 pipeline, §13 phase 1): verified copy of every file, then preview, analysis and proposed decision
/// per photo (RAW+JPEG twins together), with the progress of each file in <c>JobItems</c> so that an interrupted job
/// resumes where it stopped. A photo enters the analysis only when all its files are verified (I3); the analysis
/// runs on bounded channels while the copy goes on. Files already completed are never processed again.
/// </summary>
public sealed class ImportJobRunner : IJobRunner
{
    public const int ChannelCapacity = 8;

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);

    private readonly ICatalog _catalog;
    private readonly IIngestService _ingest;
    private readonly IExifReader _exifReader;
    private readonly IFileSystem _fileSystem;
    private readonly IPhotoProcessor _processor;
    private readonly IReadOnlyList<IBatchStage> _batchStages;
    private readonly DeferredCopyService? _deferred;

    public ImportJobRunner(ICatalog catalog, IIngestService ingest, IExifReader exifReader, IFileSystem fileSystem, IPhotoProcessor processor, DeferredCopyService? deferred = null, IReadOnlyList<IBatchStage>? batchStages = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(exifReader);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(processor);
        _catalog = catalog;
        _ingest = ingest;
        _exifReader = exifReader;
        _fileSystem = fileSystem;
        _processor = processor;
        _batchStages = batchStages ?? [];
        _deferred = deferred;
    }

    public static string SerializeSettings(ImportJobSettings settings) => JsonSerializer.Serialize(settings, SettingsJson);

    public static ImportJobSettings DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<ImportJobSettings>(json, SettingsJson) ?? throw new InvalidDataException("Empty import job settings.");

    public async Task<JobRunSummary> RunAsync(long jobId, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var job = await _catalog.Jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Job {jobId} not found.");
        if (job.Type != JobType.Import)
        {
            throw new InvalidOperationException($"Job {jobId} is not an import job.");
        }

        // A completed job can run again to retry its failed files (summary 3h); a cancelled job is closed.
        if (job.State == JobState.Cancelled)
        {
            throw new InvalidOperationException($"Job {jobId} was cancelled.");
        }

        var settings = DeserializeSettings(job.SettingsSnapshotJson);
        var primary = settings.Destinations.Single(d => d.Kind == DestinationKind.Primary);
        await _catalog.Jobs.SetStateAsync(jobId, JobState.Running, CancellationToken.None).ConfigureAwait(false);

        var run = new Run(this, job, settings, primary, progress);
        JobPauseReason? pause = null;
        try
        {
            pause = await run.ExecuteAsync(ct).ConfigureAwait(false);
            if (pause is null && _batchStages.Count > 0)
            {
                await BatchStages.RunAsync(_batchStages, job, run.LastProgress, progress, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            pause = JobPauseReason.UserRequest;
        }
        catch
        {
            await _catalog.Jobs.SetStateAsync(jobId, JobState.Failed, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var state = pause is null ? JobState.Completed : JobState.Paused;
        await _catalog.Jobs.SetStateAsync(jobId, state, CancellationToken.None).ConfigureAwait(false);
        return new JobRunSummary(state, run.Completed, run.Failed, pause);
    }

    /// <summary>Jobs left running by an unexpected shutdown become paused (interrupted); the caller offers to resume them (3c).</summary>
    public static async Task<IReadOnlyList<Job>> RecoverInterruptedAsync(ICatalog catalog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var interrupted = (await catalog.Jobs.ListUnfinishedAsync(ct).ConfigureAwait(false)).Where(j => j.State == JobState.Running).ToList();
        foreach (var job in interrupted)
        {
            await catalog.Jobs.SetStateAsync(job.Id, JobState.Paused, ct).ConfigureAwait(false);
        }

        return interrupted;
    }

    /// <summary>One photo: the files sharing directory and name (RAW+JPEG twins), in job order.</summary>
    private sealed class PhotoGroup(string key, List<JobItem> items)
    {
        public string Key { get; } = key;

        public List<JobItem> Items { get; } = items;

        public Dictionary<long, (long AssetId, MediaKind Kind, string PrimaryPath, ExifInfo? Exif, string? Hash)> Verified { get; } = [];

        public int Resolved { get; set; }

        public bool Queued { get; set; }
    }

    private sealed class Run(ImportJobRunner owner, Job job, ImportJobSettings settings, IngestDestination primary, IProgress<JobProgress>? progress)
    {
        private readonly object _gate = new();
        private readonly Channel<PhotoGroup> _channel = Channel.CreateBounded<PhotoGroup>(new BoundedChannelOptions(ChannelCapacity) { SingleWriter = false });
        private int _total;
        private int _copied;

        public int Completed { get; private set; }

        public int Failed { get; private set; }

        public async Task<JobPauseReason?> ExecuteAsync(CancellationToken ct)
        {
            var items = (await owner._catalog.Jobs.GetItemsAsync(job.Id, ct).ConfigureAwait(false)).OrderBy(i => i.Id).ToList();
            _total = items.Count;
            Completed = items.Count(i => i.State is JobItemState.Done or JobItemState.Skipped);
            _copied = items.Count(i => i.AssetId is not null);
            var groups = items
                .GroupBy(i => PairKey(i.SourcePath), StringComparer.OrdinalIgnoreCase)
                .Select(g => new PhotoGroup(g.Key, g.ToList()))
                .Where(g => g.Items.Any(i => i.State is not (JobItemState.Done or JobItemState.Skipped)))
                .ToList();
            Report(null);

            // A failing worker stops the whole run (the producer could otherwise wait forever on a full channel).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var workerCount = settings.AnalysisWorkers > 0 ? settings.AnalysisWorkers : PipelineDefaults.DefaultAnalysisWorkers;
            var analysis = Enumerable.Range(0, workerCount).Select(_ => AnalyzeAsync(linked)).ToList();
            ct = linked.Token;
            JobPauseReason? pause = null;
            Exception? producerError = null;
            try
            {
                // Photos already copied in an earlier run go straight to the analysis.
                var toCopy = new List<JobItem>();
                foreach (var group in groups)
                {
                    foreach (var item in group.Items.Where(i => i.State is not (JobItemState.Done or JobItemState.Skipped)))
                    {
                        if (item.AssetId is { } assetId)
                        {
                            var path = Path.Combine(primary.RootPath, item.TargetRelativePath!);
                            group.Verified[item.Id] = (assetId, KindOf(path), path, null, item.ContentHash);
                            group.Resolved++;
                        }
                        else
                        {
                            toCopy.Add(item);
                        }
                    }

                    group.Resolved += group.Items.Count(i => i.State is JobItemState.Done or JobItemState.Skipped);
                    if (group.Resolved == group.Items.Count)
                    {
                        await EnqueueAsync(group, ct).ConfigureAwait(false);
                    }
                }

                if (toCopy.Count > 0)
                {
                    pause = await CopyAsync(toCopy, groups, ct).ConfigureAwait(false);
                }

                // Groups with a failed file: analyze the files that were verified.
                foreach (var group in groups.Where(g => !g.Queued && g.Verified.Count > 0))
                {
                    await EnqueueAsync(group, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                producerError = e;
            }
            finally
            {
                _channel.Writer.TryComplete();
            }

            // Never return while a worker is still writing: the photos in progress finish first.
            Exception? workerError = null;
            try
            {
                await Task.WhenAll(analysis).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                workerError = e;
            }

            var error = workerError is not null and not OperationCanceledException ? workerError : producerError ?? workerError;
            if (error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }

            return pause;
        }

        private async Task<JobPauseReason?> CopyAsync(List<JobItem> toCopy, List<PhotoGroup> groups, CancellationToken ct)
        {
            if (!owner._fileSystem.DirectoryExists(settings.SourceRoot))
            {
                return JobPauseReason.SourceUnavailable;
            }

            var byItem = groups.SelectMany(g => g.Items.Select(i => (i.Id, Group: g))).ToDictionary(x => x.Id, x => x.Group);
            var bySource = toCopy.ToDictionary(i => i.SourcePath, StringComparer.OrdinalIgnoreCase);
            var exif = (await owner._exifReader.ReadAsync(toCopy.Select(i => i.SourcePath).ToList(), ct).ConfigureAwait(false))
                .ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

            var files = new List<IngestFile>();
            foreach (var item in toCopy)
            {
                if (!owner._fileSystem.FileExists(item.SourcePath))
                {
                    await FailAsync(item, byItem[item.Id], "Source file not found.", ct).ConfigureAwait(false);
                    continue;
                }

                var length = owner._fileSystem.GetFileMetadata(item.SourcePath).Length;
                files.Add(new IngestFile(item.SourcePath, item.TargetRelativePath!, length));
            }

            var request = new IngestRequest(files, settings.Destinations, settings.SourceRoot, settings.MaxParallelFiles);
            var summary = await owner._ingest.RunAsync(
                request,
                async (verified, token) =>
                {
                    var item = bySource[verified.File.SourcePath];
                    await OnVerifiedAsync(item, byItem[item.Id], verified, exif.GetValueOrDefault(item.SourcePath), token).ConfigureAwait(false);
                },
                null,
                ct).ConfigureAwait(false);

            foreach (var failed in summary.Failed)
            {
                var item = bySource[failed.File.SourcePath];
                var error = failed.SourceError ?? failed.Primary?.Error ?? string.Join("; ", failed.Destinations.Where(d => d.Error is not null).Select(d => $"{d.DestinationId}: {d.Error}"));
                await FailAsync(item, byItem[item.Id], error, ct).ConfigureAwait(false);
            }

            return summary.PausedForUnavailableDestination ? JobPauseReason.DestinationUnavailable : null;
        }

        private async Task OnVerifiedAsync(JobItem item, PhotoGroup group, VerifiedFile verified, ExifInfo? exif, CancellationToken ct)
        {
            var catalog = owner._catalog;
            var kind = KindOf(verified.PrimaryPath);
            var quick = await QuickHash.ComputeAsync(owner._fileSystem, verified.PrimaryPath, ct).ConfigureAwait(false);
            var captured = exif?.CaptureDateSource == DateSource.Exif ? exif.CapturedAt : null;
            var existing = await catalog.Assets.FindByIdentityAsync(quick, captured, ct).ConfigureAwait(false);
            var assetId = existing?.Id ?? await catalog.Assets.AddAsync(new Asset(
                0,
                quick,
                QuickHash.Format(verified.Hash),
                AssetFormat.Of(exif?.FileType, item.SourcePath),
                kind switch { MediaKind.Raw => "raw", MediaKind.Jpeg => "jpeg", MediaKind.Video => "video", _ => kind.ToString().ToLowerInvariant() },
                exif?.Width,
                exif?.Height,
                exif?.CapturedAt,
                exif?.CaptureDateSource ?? DateSource.Unknown,
                exif?.ToSummary(),
                AssetState.Active), ct).ConfigureAwait(false);

            // The copy is verified: record it even if a pause was requested meanwhile.
            ct = CancellationToken.None;
            var relative = Path.GetRelativePath(primary.RootPath, verified.PrimaryPath);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, settings.LibraryId, relative, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            if (owner._deferred is not null && settings.Destinations.Any(d => d.Kind == DestinationKind.Deferred))
            {
                await owner._deferred.EnqueueAsync(job.Id, verified, settings.Destinations.Where(d => d.Kind == DestinationKind.Deferred), ct).ConfigureAwait(false);
            }

            var analyzable = kind is MediaKind.Raw or MediaKind.Jpeg;
            await catalog.Jobs.UpdateItemAsync(item with
            {
                AssetId = assetId,
                ContentHash = QuickHash.Format(verified.Hash),
                TargetRelativePath = relative,
                LastCompletedStage = PipelineStage.CopyAndVerify,
                State = analyzable ? JobItemState.InProgress : JobItemState.Done,
                Error = null,
            }, ct).ConfigureAwait(false);

            bool complete;
            lock (_gate)
            {
                _copied++;
                if (!analyzable)
                {
                    Completed++;
                }

                group.Verified[item.Id] = (assetId, kind, verified.PrimaryPath, exif, QuickHash.Format(verified.Hash));
                group.Resolved++;
                complete = group.Resolved == group.Items.Count;
            }

            Report(item.SourcePath);
            if (complete)
            {
                await EnqueueAsync(group, ct).ConfigureAwait(false);
            }
        }

        private async Task FailAsync(JobItem item, PhotoGroup group, string error, CancellationToken ct)
        {
            await owner._catalog.Jobs.UpdateItemAsync(item with { State = JobItemState.Failed, Error = error }, ct).ConfigureAwait(false);
            lock (_gate)
            {
                Failed++;
                group.Resolved++;
            }

            Report(item.SourcePath);
        }

        private async Task EnqueueAsync(PhotoGroup group, CancellationToken ct)
        {
            lock (_gate)
            {
                if (group.Queued)
                {
                    return;
                }

                group.Queued = true;
            }

            await _channel.Writer.WriteAsync(group, ct).ConfigureAwait(false);
        }

        private async Task AnalyzeAsync(CancellationTokenSource run)
        {
            try
            {
                await foreach (var group in _channel.Reader.ReadAllAsync(run.Token).ConfigureAwait(false))
                {
                    await ProcessGroupAsync(group, run.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await run.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Analyzes the JPEG of the photo if there is one (same camera rendering, no RAW parsing), the RAW otherwise.</summary>
        private async Task ProcessGroupAsync(PhotoGroup group, CancellationToken ct)
        {
            var catalog = owner._catalog;
            var members = group.Items.Where(i => group.Verified.ContainsKey(i.Id)).ToList();
            var analyzable = members.Where(i => group.Verified[i.Id].Kind is MediaKind.Raw or MediaKind.Jpeg).ToList();
            if (analyzable.Count == 0)
            {
                return;
            }

            var source = analyzable.FirstOrDefault(i => group.Verified[i.Id].Kind == MediaKind.Jpeg) ?? analyzable[0];
            var (assetId, _, path, exif, hash) = group.Verified[source.Id];
            var exifSummary = exif?.ToSummary() ?? (await catalog.Assets.GetAsync(assetId, ct).ConfigureAwait(false))?.Exif;
            PhotoProcessingResult result;
            try
            {
                result = await owner._processor.ProcessAsync(new PhotoInput(path, hash, exifSummary), new ProcessingOptions(settings.ProfileId, settings.Faces, (settings.Tags ?? TagLevels.Default).HasFlag(TagLevels.Tagger), (settings.Tags ?? TagLevels.Default).HasFlag(TagLevels.Caption)), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                foreach (var item in analyzable)
                {
                    // The verified copy stays recorded: a resume retries the analysis.
                    await owner._catalog.Jobs.UpdateItemAsync(item with
                    {
                        AssetId = group.Verified[item.Id].AssetId,
                        TargetRelativePath = Path.GetRelativePath(primary.RootPath, group.Verified[item.Id].PrimaryPath),
                        LastCompletedStage = PipelineStage.CopyAndVerify,
                        State = JobItemState.Failed,
                        Error = $"Analysis: {e.Message}",
                    }, CancellationToken.None).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    Failed += analyzable.Count;
                }

                Report(path);
                return;
            }

            // The work is done: record it even if a pause was requested meanwhile (short writes), so a resume does not redo it.
            var done = CancellationToken.None;
            await catalog.Analysis.SaveResultAsync(new AnalyzerResultRecord(0, assetId, result.Analysis.AnalyzerId, result.Analysis.ModelVersion, settings.ProfileId, result.Analysis.Scores), done).ConfigureAwait(false);
            await catalog.Analysis.ReplaceAutomaticFacesAsync(assetId, result.Faces, done).ConfigureAwait(false);
            if (result.Embedding is { } embedding)
            {
                await catalog.Analysis.SaveEmbeddingAsync(assetId, embedding.ModelVersion, embedding.Vector, done).ConfigureAwait(false);
            }

            if (result.Caption is { } caption)
            {
                await catalog.Captions.SetAutomaticAsync(assetId, caption.Language, caption.Text, caption.ModelVersion, done).ConfigureAwait(false);
            }

            if (result.Readings is { } readings)
            {
                await catalog.Motorsport.ReplaceReadingsAsync(assetId, readings.Select(r => r with { AssetId = assetId }).ToList(), done).ConfigureAwait(false);
            }

            if (result.Tags is { } tags)
            {
                await catalog.Tags.ReplaceAutomaticAsync(assetId, TagSource.RamPlus, tags.FirstOrDefault()?.ModelVersion, tags.Select(t => (t.TagKey, (double?)t.Confidence)).ToList(), done).ConfigureAwait(false);
            }

            var assetIds = analyzable.Select(i => group.Verified[i.Id].AssetId).Distinct().ToList();
            foreach (var id in assetIds)
            {
                await catalog.Decisions.SetAutomaticAsync(id, result.Decision.Outcome, result.Decision.Reasons, result.DecisionVersion, done).ConfigureAwait(false);
            }

            var raw = analyzable.FirstOrDefault(i => group.Verified[i.Id].Kind == MediaKind.Raw);
            var jpeg = analyzable.FirstOrDefault(i => group.Verified[i.Id].Kind == MediaKind.Jpeg);
            if (raw is not null && jpeg is not null)
            {
                await catalog.Assets.AddPairAsync(group.Verified[raw.Id].AssetId, group.Verified[jpeg.Id].AssetId, done).ConfigureAwait(false);
            }

            foreach (var item in analyzable)
            {
                var current = item with
                {
                    AssetId = group.Verified[item.Id].AssetId,
                    TargetRelativePath = Path.GetRelativePath(primary.RootPath, group.Verified[item.Id].PrimaryPath),
                    LastCompletedStage = PipelineStage.Decision,
                    State = JobItemState.Done,
                    Error = null,
                };
                await catalog.Jobs.UpdateItemAsync(current, done).ConfigureAwait(false);
            }

            lock (_gate)
            {
                Completed += analyzable.Count;
            }

            Report(path);
        }

        /// <summary>The last progress reported (the batch stages report on top of it).</summary>
        public JobProgress LastProgress { get; private set; } = new(0, 0, 0, 0, null);

        private void Report(string? current)
        {
            int copied, completed, failed;
            lock (_gate)
            {
                (copied, completed, failed) = (_copied, Completed, Failed);
            }

            LastProgress = new JobProgress(_total, copied, completed, failed, current);
            progress?.Report(LastProgress);
        }

        private static string PairKey(string sourcePath) =>
            Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, Path.GetFileNameWithoutExtension(sourcePath));

        /// <summary>Kind from the content of the verified copy (spec §3), as the scanner does on the source.</summary>
        private MediaKind KindOf(string path)
        {
            Span<byte> header = stackalloc byte[MediaClassifier.HeaderLength];
            var read = 0;
            try
            {
                using var stream = owner._fileSystem.OpenRead(path);
                read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            }
            catch (IOException)
            {
                // Unreadable: classify by extension only.
            }

            return MediaClassifier.Classify(header[..read], Path.GetExtension(path));
        }
    }
}
