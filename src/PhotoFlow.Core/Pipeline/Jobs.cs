// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;

namespace PhotoFlow.Core.Pipeline;

/// <summary>
/// Settings of an import job, stored as the job's settings snapshot: a resumed job uses exactly these, even if the
/// application settings changed in the meantime.
/// </summary>
/// <param name="LibraryId">Library of the primary destination (assets get their location there).</param>
/// <param name="SourceRoot">Card or folder imported (never written, I1).</param>
/// <param name="AnalysisWorkers">Photos analyzed in parallel; 0 = automatic (see <see cref="PipelineDefaults.DefaultAnalysisWorkers"/>).</param>
public sealed record ImportJobSettings(
    long LibraryId,
    string SourceRoot,
    IReadOnlyList<IngestDestination> Destinations,
    string ProfileId,
    int MaxParallelFiles = 2,
    int AnalysisWorkers = 0,
    FaceMode? Faces = null,
    Tagging.TagLevels? Tags = null);

/// <summary>Order of an archive analysis (spec §10, archives).</summary>
public enum ArchiveOrder
{
    /// <summary>Folder by folder, in path order.</summary>
    ByFolder,

    /// <summary>Most recent files first (file date: no EXIF read needed to plan).</summary>
    NewestFirst,
}

/// <summary>
/// Settings of an in-place analysis of an existing library (<c>AnalyzeInPlace</c>): its files are only read, nothing is
/// copied; XMP follow the library policy (SidecarOnly by default, D21) and are written only after a confirmation.
/// </summary>
/// <param name="FullHash">Second pass at the end: full xxHash3 of every photo still without it (resumable).</param>
public sealed record ArchiveJobSettings(
    long LibraryId,
    string ProfileId,
    ArchiveOrder Order = ArchiveOrder.NewestFirst,
    FaceMode? Faces = null,
    int AnalysisWorkers = 0,
    bool FullHash = true,
    Tagging.TagLevels? Tags = null);

public static class PipelineDefaults
{
    /// <summary>
    /// A third of the logical processors, 2 to 8: the analysis is CPU bound (previews, resampling, signals) with the
    /// models on the GPU. Measured on 230 real photos, 24 threads + RTX 3090: 2 workers 4.8 photos/s, 4 → 7.4,
    /// 8 → 10.1-10.9, 12 → 9.1.
    /// </summary>
    public static int DefaultAnalysisWorkers => Math.Clamp(Environment.ProcessorCount / 3, 2, 8);
}

/// <summary>Runs or resumes a job until it completes, pauses (cancellation, unavailable destination or source) or fails.</summary>
public interface IJobRunner
{
    Task<JobRunSummary> RunAsync(long jobId, IProgress<JobProgress>? progress, CancellationToken ct);
}

/// <param name="Total">Files of the job.</param>
/// <param name="Copied">Files whose primary copy is verified (including earlier runs).</param>
/// <param name="Completed">Files with every stage done.</param>
/// <param name="Stage">Batch stage in progress once every photo is analyzed (grouping, decisions, faces), with its own counts.</param>
public sealed record JobProgress(int Total, int Copied, int Completed, int Failed, string? CurrentItem,
    PipelineStage Stage = PipelineStage.None, int StageCompleted = 0, int StageTotal = 0);

public sealed record JobRunSummary(JobState State, int Completed, int Failed, JobPauseReason? PauseReason);

public enum JobPauseReason
{
    /// <summary>Paused by the user (cancellation of the run).</summary>
    UserRequest,

    /// <summary>A destination disappeared (disk disconnected, P8): resume when it is back.</summary>
    DestinationUnavailable,

    /// <summary>The card or source folder is not reachable: files still to copy wait for it.</summary>
    SourceUnavailable,

    /// <summary>The application closed while the job was running (detected at the next start, board 3c).</summary>
    Interrupted,
}

/// <summary>Preview, analysis and proposed decision of one verified photo (stages Preview → Analyzers → Decision).</summary>
public interface IPhotoProcessor
{
    Task<PhotoProcessingResult> ProcessAsync(PhotoInput photo, ProcessingOptions options, CancellationToken ct);
}

/// <param name="Faces">Face analysis chosen for the job (board 3e); null keeps the profile's mode.</param>
/// <param name="Tagger">Run the image tagger (level B, RAM++) on the photo.</param>
/// <param name="Captions">Level C: a caption of the photo (when the model is installed).</param>
public sealed record ProcessingOptions(string ProfileId, FaceMode? Faces = null, bool Tagger = false, bool Captions = false);

/// <param name="Path">Verified copy in the library (never the source).</param>
/// <param name="ContentHash">xxHash3 of the file (thumbnail key).</param>
public sealed record PhotoInput(string Path, string? ContentHash, ExifSummary? Exif);

/// <param name="Faces">Detected faces, ids and asset not yet assigned.</param>
/// <param name="DecisionVersion">Version of the decision engine and of the models behind the signals.</param>
public sealed record PhotoProcessingResult(
    AnalyzerResult Analysis,
    IReadOnlyList<Face> Faces,
    DecisionProposal Decision,
    string DecisionVersion,
    ImageEmbedding? Embedding = null,
    IReadOnlyList<Tagging.TagResult>? Tags = null,
    IReadOnlyList<OcrReading>? Readings = null,
    GeneratedCaption? Caption = null);

/// <summary>An automatic caption (spec §8) with its language and the model that wrote it.</summary>
public sealed record GeneratedCaption(string Language, string Text, string ModelVersion);

/// <summary>An L2-normalized image embedding and the model version that produced it.</summary>
public sealed record ImageEmbedding(string ModelVersion, float[] Vector);
