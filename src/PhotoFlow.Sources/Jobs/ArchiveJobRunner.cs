// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Threading.Channels;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.IO;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;
using PhotoFlow.Core.Sources;
using PhotoFlow.Sources.Ingest;
using PhotoFlow.Sources.Scanning;

namespace PhotoFlow.Sources.Jobs;

/// <summary>
/// In-place analysis of an existing library (job <c>AnalyzeInPlace</c>, spec §3, §10): the files are only read — no copy,
/// no write — and analyzed photo by photo (RAW+JPEG twins together) on bounded channels, with the progress of every
/// file in <c>JobItems</c> so that the job resumes where it stopped. Photos already analyzed (for example imported
/// before) are not analyzed again; damaged files go to the report and the job goes on. When the library itself becomes
/// unreachable (disk or NAS disconnected) the job pauses instead, and the photos wait for it. A final, resumable pass
/// computes the full hash of the photos that only have the quick one.
/// </summary>
public sealed class ArchiveJobRunner : IJobRunner
{
    public const int ChannelCapacity = 16;
    public const int ExifBatch = 32;
    private const string AnalyzerId = "culling-v1";

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);

    private readonly ICatalog _catalog;
    private readonly IExifReader _exifReader;
    private readonly IFileSystem _fileSystem;
    private readonly IPhotoProcessor _processor;
    private readonly IReadOnlyList<IBatchStage> _batchStages;

    public ArchiveJobRunner(ICatalog catalog, IExifReader exifReader, IFileSystem fileSystem, IPhotoProcessor processor, IReadOnlyList<IBatchStage>? batchStages = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(exifReader);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(processor);
        _catalog = catalog;
        _exifReader = exifReader;
        _fileSystem = fileSystem;
        _processor = processor;
        _batchStages = batchStages ?? [];
    }

    public static string SerializeSettings(ArchiveJobSettings settings) => JsonSerializer.Serialize(settings, SettingsJson);

    public static ArchiveJobSettings DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<ArchiveJobSettings>(json, SettingsJson) ?? throw new InvalidDataException("Empty archive job settings.");

    /// <summary>Creates the job and one item per photo file of the library, in the requested order.</summary>
    public static async Task<long> CreateAsync(ICatalog catalog, IFileSystem fileSystem, ArchiveJobSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        var library = await catalog.Libraries.GetAsync(settings.LibraryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Library {settings.LibraryId} not found.");
        var files = new List<ScannedFile>();
        await foreach (var file in new SourceScanner(fileSystem, library.RootPath).ScanAsync(ct).ConfigureAwait(false))
        {
            // A file named like a photo but unreadable stays in the job: it ends in the report as damaged.
            if (file.IsAnalyzable || (file.Kind == MediaKind.Other && MediaClassifier.HasPhotoExtension(Path.GetExtension(file.Item.FullPath))))
            {
                files.Add(file);
            }
        }

        IEnumerable<ScannedFile> ordered = settings.Order == ArchiveOrder.NewestFirst
            ? files.OrderByDescending(f => f.Item.LastWriteTime).ThenBy(f => f.Item.RelativePath, StringComparer.OrdinalIgnoreCase)
            : files.OrderBy(f => f.Item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var jobId = await catalog.Jobs.CreateAsync(new Job(
            0, JobType.AnalyzeInPlace, library.RootPath, library.Name, null, settings.ProfileId, false, null, JobState.Pending, JobPriority.Low,
            SerializeSettings(settings), DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        var items = ordered
            .Select(f => new JobItem(0, jobId, null, f.Item.FullPath, f.Item.RelativePath, null, PipelineStage.None, JobItemState.Pending, null))
            .ToList();
        foreach (var chunk in items.Chunk(5000))
        {
            await catalog.Jobs.AddItemsAsync(jobId, chunk, ct).ConfigureAwait(false);
        }

        return jobId;
    }

    public async Task<JobRunSummary> RunAsync(long jobId, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var job = await _catalog.Jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Job {jobId} not found.");
        if (job.Type != JobType.AnalyzeInPlace)
        {
            throw new InvalidOperationException($"Job {jobId} is not an in-place analysis.");
        }

        if (job.State == JobState.Cancelled)
        {
            throw new InvalidOperationException($"Job {jobId} was cancelled.");
        }

        var settings = DeserializeSettings(job.SettingsSnapshotJson);
        var library = await _catalog.Libraries.GetAsync(settings.LibraryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Library {settings.LibraryId} not found.");
        await _catalog.Jobs.SetStateAsync(jobId, JobState.Running, CancellationToken.None).ConfigureAwait(false);

        var run = new Run(this, settings, library, progress);
        JobPauseReason? pause;
        try
        {
            pause = await run.ExecuteAsync(jobId, ct).ConfigureAwait(false);
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

    private sealed record Photo(List<JobItem> Items, IReadOnlyDictionary<string, ExifInfo> Exif);

    private sealed class Run(ArchiveJobRunner owner, ArchiveJobSettings settings, Library library, IProgress<JobProgress>? progress)
    {
        private readonly object _gate = new();
        private readonly Channel<Photo> _channel = Channel.CreateBounded<Photo>(ChannelCapacity);
        private int _total;
        private int _identified;
        private volatile bool _sourceLost;

        public int Completed { get; private set; }

        public int Failed { get; private set; }

        public async Task<JobPauseReason?> ExecuteAsync(long jobId, CancellationToken ct)
        {
            if (!owner._fileSystem.DirectoryExists(library.RootPath))
            {
                return JobPauseReason.SourceUnavailable;
            }

            var items = (await owner._catalog.Jobs.GetItemsAsync(jobId, ct).ConfigureAwait(false)).OrderBy(i => i.Id).ToList();
            _total = items.Count;
            Completed = items.Count(i => i.State is JobItemState.Done or JobItemState.Skipped);
            _identified = items.Count(i => i.AssetId is not null);
            var photos = items
                .Where(i => i.State is not (JobItemState.Done or JobItemState.Skipped))
                .GroupBy(i => Path.Combine(Path.GetDirectoryName(i.SourcePath) ?? string.Empty, Path.GetFileNameWithoutExtension(i.SourcePath)), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.ToList())
                .ToList();
            Report(null);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var workers = settings.AnalysisWorkers > 0 ? settings.AnalysisWorkers : PipelineDefaults.DefaultAnalysisWorkers;
            var analysis = Enumerable.Range(0, workers).Select(_ => AnalyzeAsync(linked)).ToList();
            Exception? producerError = null;
            try
            {
                // EXIF in batches (one exiftool call for many photos), then the photos go to the bounded channel.
                foreach (var batch in photos.Chunk(ExifBatch))
                {
                    var paths = batch.SelectMany(p => p).Where(i => owner._fileSystem.FileExists(i.SourcePath)).Select(i => i.SourcePath).ToList();
                    var exif = (await owner._exifReader.ReadAsync(paths, linked.Token).ConfigureAwait(false))
                        .ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
                    foreach (var photo in batch)
                    {
                        await _channel.Writer.WriteAsync(new Photo(photo, exif), linked.Token).ConfigureAwait(false);
                    }
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

            Exception? workerError = null;
            try
            {
                await Task.WhenAll(analysis).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                workerError = e;
            }

            if (_sourceLost && !ct.IsCancellationRequested)
            {
                return JobPauseReason.SourceUnavailable; // the photos not finished wait for the library to come back
            }

            var error = workerError is not null and not OperationCanceledException ? workerError : producerError ?? workerError;
            if (error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }

            if (settings.FullHash)
            {
                await FullHashPassAsync(ct).ConfigureAwait(false);
            }

            return null;
        }

        private async Task AnalyzeAsync(CancellationTokenSource run)
        {
            try
            {
                await foreach (var photo in _channel.Reader.ReadAllAsync(run.Token).ConfigureAwait(false))
                {
                    await ProcessAsync(photo, run.Token).ConfigureAwait(false);
                }
            }
            catch (SourceLostException)
            {
                _sourceLost = true;
                await run.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await run.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task ProcessAsync(Photo photo, CancellationToken ct)
        {
            var catalog = owner._catalog;
            var done = CancellationToken.None;
            var members = new List<(JobItem Item, long AssetId, MediaKind Kind)>();
            foreach (var item in photo.Items)
            {
                try
                {
                    var kind = await KindAsync(item.SourcePath, ct).ConfigureAwait(false);
                    var exif = photo.Exif.GetValueOrDefault(item.SourcePath);
                    var identified = await IdentifyAsync(item, kind, exif, ct).ConfigureAwait(false);
                    members.Add((identified, identified.AssetId!.Value, kind));
                    lock (_gate)
                    {
                        _identified++;
                    }
                }
                catch (IOException e)
                {
                    ThrowIfSourceLost();
                    await FailAsync([item], $"Read: {e.Message}").ConfigureAwait(false);
                }
            }

            if (members.Count == 0)
            {
                return;
            }

            var analyzed = members.FirstOrDefault(m => m.Kind == MediaKind.Jpeg);
            if (analyzed.Item is null)
            {
                analyzed = members[0];
            }

            // Already analyzed and decided (e.g. imported before, or analyzed in an earlier run): nothing to redo. A run
            // stopped between the analysis and the decision analyzes again; twins without a decision get the one of the
            // analyzed photo (a stop between the members' decisions).
            var existing = await catalog.Analysis.GetResultAsync(analyzed.AssetId, AnalyzerId, ct).ConfigureAwait(false) is not null
                ? await catalog.Decisions.GetAsync(analyzed.AssetId, ct).ConfigureAwait(false)
                : null;
            if (existing is { AutomaticOutcome: { } outcome })
            {
                foreach (var id in members.Select(m => m.AssetId).Distinct().Where(id => id != analyzed.AssetId))
                {
                    if (await catalog.Decisions.GetAsync(id, ct).ConfigureAwait(false) is not { AutomaticOutcome: not null })
                    {
                        await catalog.Decisions.SetAutomaticAsync(id, outcome, existing.Reasons, existing.ModelVersion ?? string.Empty, done).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                var asset = await catalog.Assets.GetAsync(analyzed.AssetId, ct).ConfigureAwait(false);
                PhotoProcessingResult result;
                try
                {
                    result = await owner._processor.ProcessAsync(
                        new PhotoInput(analyzed.Item.SourcePath, asset?.FullHash ?? asset?.QuickHash, asset?.Exif),
                        new ProcessingOptions(settings.ProfileId, settings.Faces, (settings.Tags ?? TagLevels.Default).HasFlag(TagLevels.Tagger), (settings.Tags ?? TagLevels.Default).HasFlag(TagLevels.Caption)), ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ThrowIfSourceLost();
                    await FailAsync(members.Select(m => m.Item).ToList(), $"Analysis: {e.Message}").ConfigureAwait(false);
                    return;
                }

                await catalog.Analysis.SaveResultAsync(new AnalyzerResultRecord(0, analyzed.AssetId, result.Analysis.AnalyzerId, result.Analysis.ModelVersion, settings.ProfileId, result.Analysis.Scores), done).ConfigureAwait(false);
                await catalog.Analysis.ReplaceAutomaticFacesAsync(analyzed.AssetId, result.Faces, done).ConfigureAwait(false);
                if (result.Embedding is { } embedding)
                {
                    await catalog.Analysis.SaveEmbeddingAsync(analyzed.AssetId, embedding.ModelVersion, embedding.Vector, done).ConfigureAwait(false);
                }

                if (result.Caption is { } caption)
                {
                    await catalog.Captions.SetAutomaticAsync(analyzed.AssetId, caption.Language, caption.Text, caption.ModelVersion, done).ConfigureAwait(false);
                }

                if (result.Readings is { } readings)
                {
                    await catalog.Motorsport.ReplaceReadingsAsync(analyzed.AssetId, readings.Select(r => r with { AssetId = analyzed.AssetId }).ToList(), done).ConfigureAwait(false);
                }

                if (result.Tags is { } tags)
                {
                    await catalog.Tags.ReplaceAutomaticAsync(analyzed.AssetId, TagSource.RamPlus, tags.FirstOrDefault()?.ModelVersion, tags.Select(t => (t.TagKey, (double?)t.Confidence)).ToList(), done).ConfigureAwait(false);
                }

                foreach (var id in members.Select(m => m.AssetId).Distinct())
                {
                    await catalog.Decisions.SetAutomaticAsync(id, result.Decision.Outcome, result.Decision.Reasons, result.DecisionVersion, done).ConfigureAwait(false);
                }
            }

            var raw = members.FirstOrDefault(m => m.Kind == MediaKind.Raw);
            var jpeg = members.FirstOrDefault(m => m.Kind == MediaKind.Jpeg);
            if (raw.Item is not null && jpeg.Item is not null)
            {
                await catalog.Assets.AddPairAsync(raw.AssetId, jpeg.AssetId, done).ConfigureAwait(false);
            }

            foreach (var member in members)
            {
                await catalog.Jobs.UpdateItemAsync(member.Item with { LastCompletedStage = PipelineStage.Decision, State = JobItemState.Done, Error = null }, done).ConfigureAwait(false);
            }

            lock (_gate)
            {
                Completed += members.Count;
            }

            Report(analyzed.Item.SourcePath);
        }

        /// <summary>Asset of the file: same quick hash and capture time as a known photo, or a new asset; location in the library.</summary>
        private async Task<JobItem> IdentifyAsync(JobItem item, MediaKind kind, ExifInfo? exif, CancellationToken ct)
        {
            var catalog = owner._catalog;
            if (item.AssetId is not null)
            {
                return item;
            }

            var quick = await QuickHash.ComputeAsync(owner._fileSystem, item.SourcePath, ct).ConfigureAwait(false);
            var captured = exif?.CaptureDateSource == DateSource.Exif ? exif.CapturedAt : null;
            var existing = await catalog.Assets.FindByIdentityAsync(quick, captured, ct).ConfigureAwait(false);
            var assetId = existing?.Id ?? await catalog.Assets.AddAsync(new Asset(
                0, quick, null,
                AssetFormat.Of(exif?.FileType, item.SourcePath),
                kind.ToString().ToLowerInvariant(),
                exif?.Width, exif?.Height, exif?.CapturedAt, exif?.CaptureDateSource ?? DateSource.Unknown, exif?.ToSummary(), AssetState.Active), ct).ConfigureAwait(false);
            await catalog.Assets.AddLocationAsync(new AssetLocation(0, assetId, library.Id, item.TargetRelativePath!, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            var identified = item with { AssetId = assetId, ContentHash = quick, State = JobItemState.InProgress };
            await catalog.Jobs.UpdateItemAsync(identified, CancellationToken.None).ConfigureAwait(false);
            return identified;
        }

        /// <summary>Full xxHash3 of the photos that only have the quick hash; stored as it goes, so a pause loses nothing.</summary>
        private async Task FullHashPassAsync(CancellationToken ct)
        {
            while (true)
            {
                var pending = await owner._catalog.Assets.ListWithoutFullHashAsync(library.Id, 200, ct).ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    return;
                }

                var progressMade = false;
                foreach (var (assetId, relative) in pending)
                {
                    var path = Path.Combine(library.RootPath, relative);
                    if (!owner._fileSystem.FileExists(path))
                    {
                        continue;
                    }

                    var hash = await QuickHash.ComputeFullAsync(owner._fileSystem, path, ct).ConfigureAwait(false);
                    await owner._catalog.Assets.SetFullHashAsync(assetId, hash, CancellationToken.None).ConfigureAwait(false);
                    progressMade = true;
                }

                if (!progressMade)
                {
                    return; // only missing files left: they stay without full hash
                }
            }
        }

        /// <summary>A read error with the library root gone is not a damaged file: the whole job waits.</summary>
        private void ThrowIfSourceLost()
        {
            if (!owner._fileSystem.DirectoryExists(library.RootPath))
            {
                throw new SourceLostException();
            }
        }

        private async Task FailAsync(IReadOnlyList<JobItem> items, string error)
        {
            foreach (var item in items)
            {
                await owner._catalog.Jobs.UpdateItemAsync(item with { State = JobItemState.Failed, Error = error }, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_gate)
            {
                Failed += items.Count;
            }

            Report(items[0].SourcePath);
        }

        private async Task<MediaKind> KindAsync(string path, CancellationToken ct)
        {
            var header = new byte[MediaClassifier.HeaderLength];
            await using var stream = owner._fileSystem.OpenRead(path);
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            return MediaClassifier.Classify(header.AsSpan(0, read), Path.GetExtension(path));
        }

        /// <summary>The last progress reported (the batch stages report on top of it).</summary>
        public JobProgress LastProgress { get; private set; } = new(0, 0, 0, 0, null);

        private void Report(string? current)
        {
            int identified, completed, failed;
            lock (_gate)
            {
                (identified, completed, failed) = (_identified, Completed, Failed);
            }

            LastProgress = new JobProgress(_total, identified, completed, failed, current);
            progress?.Report(LastProgress);
        }
    }
}

/// <summary>The library root disappeared while the job ran.</summary>
internal sealed class SourceLostException : Exception
{
}

/// <summary>Runs every job type with its runner (the job manager starts any job by id).</summary>
public sealed class JobRunnerRouter(ICatalog catalog, IJobRunner import, IJobRunner archive, IJobRunner? reanalyze = null) : IJobRunner
{
    public async Task<JobRunSummary> RunAsync(long jobId, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var job = await catalog.Jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Job {jobId} not found.");
        var runner = job.Type switch
        {
            JobType.Import => import,
            JobType.AnalyzeInPlace => archive,
            JobType.Reanalyze when reanalyze is not null => reanalyze,
            _ => throw new NotSupportedException($"Job type {job.Type} arrives in a later phase."),
        };
        return await runner.RunAsync(jobId, progress, ct).ConfigureAwait(false);
    }
}
