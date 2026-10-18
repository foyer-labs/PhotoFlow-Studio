// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Core.Pipeline;

/// <summary>
/// Batch barrier stage (groupings, decisions per group, faces): runs when every photo of the job has been analyzed.
/// Stages are idempotent: a resumed job runs them again on the whole set (F2-4).
/// </summary>
public interface IBatchStage
{
    PipelineStage Stage { get; }

    Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct);
}

/// <param name="ProfileId">Main profile of the job (the stage resolves it: profiles live outside Core).</param>
public sealed record JobContext(long JobId, JobType Type, string ProfileId);

public sealed record StageProgress(PipelineStage Stage, int Completed, int Total, string? CurrentItem);

/// <summary>Pipeline stages of spec §3, in order.</summary>
public enum PipelineStage
{
    None = 0,
    CopyAndVerify = 1,
    Preview = 2,
    Analyzers = 3,
    Grouping = 4,
    Decision = 5,
    TaggingAndOcr = 6,
    Faces = 7,
    Review = 8,
    XmpWrite = 9,
}
