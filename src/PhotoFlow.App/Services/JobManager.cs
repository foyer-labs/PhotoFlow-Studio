// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.App.Services;

/// <summary>Live state of a job for the views (board 3g): progress, state, pause reason, estimate.</summary>
public sealed record JobStatus(
    long JobId,
    JobState State,
    JobProgress Progress,
    JobPauseReason? PauseReason,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    string? Error);

/// <summary>
/// Runs jobs in the background, one task per job (the import has priority on the GPU through the order of start).
/// Pause cancels the run: work already finished is kept and a later start resumes from the job items.
/// Events are raised on worker threads; view models marshal them to the UI thread.
/// </summary>
public sealed class JobManager(Func<IJobRunner> runnerFactory, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<long, Running> _running = [];

    public event EventHandler<JobStatus>? StatusChanged;

    public bool IsRunning(long jobId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(jobId);
        }
    }

    /// <summary>True while any job runs (the models cannot be changed meanwhile).</summary>
    public bool AnyRunning
    {
        get
        {
            lock (_gate)
            {
                return _running.Count > 0;
            }
        }
    }

    /// <summary>True while a normal-priority job (an import) runs: low-priority jobs wait (spec §10, archives).</summary>
    public bool HasNormalPriorityRunning
    {
        get
        {
            lock (_gate)
            {
                return _running.Values.Any(r => r.Priority == JobPriority.Normal);
            }
        }
    }

    /// <summary>Waits while a normal-priority job runs (called by low-priority work before each photo).</summary>
    public async Task YieldToNormalAsync(CancellationToken ct)
    {
        while (HasNormalPriorityRunning)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Starts or resumes the job; returns the task that completes with the run summary.</summary>
    public Task<JobRunSummary> StartAsync(long jobId, JobPriority priority = JobPriority.Normal)
    {
        Running run;
        lock (_gate)
        {
            if (_running.TryGetValue(jobId, out var existing))
            {
                return existing.Task;
            }

            run = new Running(jobId, _time.GetTimestamp(), priority);
            _running[jobId] = run;
        }

        run.Task = Task.Run(() => ExecuteAsync(run));
        return run.Task;
    }

    /// <summary>Requests a pause: the run stops after the photos in progress.</summary>
    public void Pause(long jobId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(jobId, out var run))
            {
                run.Cancellation.Cancel();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Running> runs;
        lock (_gate)
        {
            runs = [.. _running.Values];
        }

        foreach (var run in runs)
        {
            await run.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        foreach (var run in runs)
        {
            try
            {
                await run.Task.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or IOException)
            {
                // Shutting down: the job stays resumable from its items.
            }
        }
    }

    /// <summary>Remaining time from the throughput of this run (completed files per second), once some files are done.</summary>
    public static TimeSpan? Estimate(JobProgress progress, int completedAtStart, TimeSpan elapsed)
    {
        var done = progress.Completed - completedAtStart;
        var left = progress.Total - progress.Completed - progress.Failed;
        if (done < 3 || elapsed <= TimeSpan.Zero || left <= 0)
        {
            return left <= 0 ? TimeSpan.Zero : null;
        }

        return TimeSpan.FromSeconds(elapsed.TotalSeconds / done * left);
    }

    private async Task<JobRunSummary> ExecuteAsync(Running run)
    {
        var progress = new Progress(this, run);
        try
        {
            var summary = await runnerFactory().RunAsync(run.JobId, progress, run.Cancellation.Token).ConfigureAwait(false);
            Raise(run, summary.State, summary.PauseReason, null);
            return summary;
        }
        catch (Exception e)
        {
            Raise(run, JobState.Failed, null, e.Message);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(run.JobId);
            }

            run.Cancellation.Dispose();
        }
    }

    private void Raise(Running run, JobState state, JobPauseReason? reason, string? error)
    {
        var elapsed = _time.GetElapsedTime(run.StartedAt);
        var last = run.Last ?? new JobProgress(0, 0, 0, 0, null);
        StatusChanged?.Invoke(this, new JobStatus(run.JobId, state, last, reason, elapsed,
            state == JobState.Running ? Estimate(last, run.CompletedAtStart ?? last.Completed, elapsed) : null, error));
    }

    private sealed class Running(long jobId, long startedAt, JobPriority priority)
    {
        public long JobId { get; } = jobId;

        public JobPriority Priority { get; } = priority;

        public long StartedAt { get; } = startedAt;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<JobRunSummary> Task { get; set; } = null!;

        public JobProgress? Last { get; set; }

        public int? CompletedAtStart { get; set; }
    }

    private sealed class Progress(JobManager owner, Running run) : IProgress<JobProgress>
    {
        public void Report(JobProgress value)
        {
            run.CompletedAtStart ??= value.Completed;
            run.Last = value;
            owner.Raise(run, JobState.Running, null, null);
        }
    }
}
