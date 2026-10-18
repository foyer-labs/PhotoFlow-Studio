// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.Sources.Jobs;

/// <summary>
/// Runs the batch stages of a job (spec §3: groupings, decisions per group, faces) once every photo is analyzed. A
/// cancellation pauses the job; the stages run again, whole, when it resumes (F2-4).
/// </summary>
internal static class BatchStages
{
    public static async Task RunAsync(IReadOnlyList<IBatchStage> stages, Job job, JobProgress last, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var context = new JobContext(job.Id, job.Type, job.ProfileId ?? "events");
        foreach (var stage in stages)
        {
            ct.ThrowIfCancellationRequested();
            var adapter = progress is null
                ? null
                : new Progress(p => progress.Report(last with { Stage = p.Stage, StageCompleted = p.Completed, StageTotal = p.Total, CurrentItem = p.CurrentItem }));
            await stage.RunAsync(context, adapter, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reports synchronously (the job progress is already thread safe).</summary>
    private sealed class Progress(Action<StageProgress> report) : IProgress<StageProgress>
    {
        public void Report(StageProgress value) => report(value);
    }
}
