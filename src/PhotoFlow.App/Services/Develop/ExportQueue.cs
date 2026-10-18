// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Develop;

namespace PhotoFlow.App.Services.Develop;

public enum ExportState
{
    Queued,
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// An export in the queue (decision S11): the photos with their settings taken when it joined the queue, the
/// recipe, and once running the progress and the results.
/// </summary>
public sealed class ExportRun(long id, string title, string recipeId, IReadOnlyList<DevelopExportItem> items, JsonObject output, string policy, string nonRawFormat)
{
    private CancellationTokenSource? _cancel;

    public long Id { get; } = id;

    public string Title { get; } = title;

    public string RecipeId { get; } = recipeId;

    public IReadOnlyList<DevelopExportItem> Items { get; } = items;

    public JsonObject Output { get; } = output;

    public string Policy { get; } = policy;

    public string NonRawFormat { get; } = nonRawFormat;

    public ExportState State { get; internal set; }

    public double Fraction { get; internal set; }

    public string Message { get; internal set; } = string.Empty;

    public IReadOnlyList<DevelopExportResult> Results { get; internal set; } = [];

    public string? Error { get; internal set; }

    public DateTimeOffset QueuedAt { get; } = DateTimeOffset.Now;

    public int Exported => Results.Count(r => r.Outcome == "ok");

    public int Skipped => Results.Count(r => r.Outcome == "saltata");

    public int Failed => Results.Count(r => r.Outcome == "errore");

    /// <summary>Folder of the last file written (for «Apri cartella»).</summary>
    public string? LastFolder => Results.LastOrDefault(r => r.Output is not null)?.Output is { } file ? Path.GetDirectoryName(file) : null;

    internal CancellationToken Start()
    {
        _cancel = new CancellationTokenSource();
        return _cancel.Token;
    }

    public void Cancel()
    {
        if (State == ExportState.Queued)
        {
            State = ExportState.Cancelled;
        }

        _cancel?.Cancel();
    }

    internal void Finish()
    {
        _cancel?.Dispose();
        _cancel = null;
    }
}

/// <summary>Runs the exports one at a time in the background; the results are recorded in the catalog.</summary>
public sealed class ExportQueue(IDevelopEngine engine, ICatalog catalog)
{
    private readonly Lock _lock = new();
    private readonly List<ExportRun> _runs = [];
    private long _nextId;
    private Task _worker = Task.CompletedTask;

    /// <summary>Raised (from any thread) when a run is added or changes.</summary>
    public event EventHandler<ExportRun>? Changed;

    public IReadOnlyList<ExportRun> Runs
    {
        get
        {
            lock (_lock)
            {
                return [.. _runs];
            }
        }
    }

    public ExportRun? Current => Runs.FirstOrDefault(r => r.State == ExportState.Running);

    public ExportRun Enqueue(string title, string recipeId, IReadOnlyList<DevelopExportItem> items, JsonObject output, string policy, string nonRawFormat)
    {
        ArgumentNullException.ThrowIfNull(items);
        ExportRun run;
        lock (_lock)
        {
            run = new ExportRun(++_nextId, title, recipeId, items, output, policy, nonRawFormat);
            _runs.Insert(0, run);
            _worker = _worker.ContinueWith(_ => RunAsync(run), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }

        Changed?.Invoke(this, run);
        return run;
    }

    /// <summary>Waits for every queued run (tests, closing).</summary>
    public Task WhenIdleAsync()
    {
        lock (_lock)
        {
            return _worker;
        }
    }

    private async Task RunAsync(ExportRun run)
    {
        if (run.State == ExportState.Cancelled)
        {
            Changed?.Invoke(this, run);
            return;
        }

        var token = run.Start();
        run.State = ExportState.Running;
        Changed?.Invoke(this, run);
        var progress = new Progress(p =>
        {
            run.Fraction = p.Fraction;
            run.Message = p.Message;
            Changed?.Invoke(this, run);
        });
        try
        {
            run.Results = await engine.ExportAsync(run.Items, run.Output, run.Policy, run.NonRawFormat, progress, token).ConfigureAwait(false);
            run.Fraction = 1;
            run.State = ExportState.Completed;
            var byPath = run.Items.ToDictionary(i => i.Path, i => i.AssetId, StringComparer.OrdinalIgnoreCase);
            foreach (var result in run.Results.Where(r => r.Outcome == "ok" && r.Output is not null))
            {
                if (byPath.TryGetValue(result.Path, out var assetId))
                {
                    await catalog.Develop.AddExportAsync(new ExportRecord(assetId, result.Output!, run.RecipeId, DateTimeOffset.Now), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            run.State = ExportState.Cancelled;
        }
        catch (Exception e) when (e is DevelopEngineException or IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            run.State = ExportState.Failed;
            run.Error = e.Message;
        }
        finally
        {
            run.Finish();
            Changed?.Invoke(this, run);
        }
    }

    /// <summary>Synchronous progress (no UI context): the run is updated in place and listeners marshal to the UI.</summary>
    private sealed class Progress(Action<DevelopProgress> report) : IProgress<DevelopProgress>
    {
        public void Report(DevelopProgress value) => report(value);
    }
}
