// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Ingest;

namespace PhotoFlow.Sources.Ingest;

/// <summary>
/// Asynchronous copies to deferred destinations (NAS, spec §4): made from the verified primary copy (the card may be
/// gone), verified against the hash recorded at import, retried with growing delays while the destination is
/// unreachable. A primary file that no longer matches its hash is never propagated.
/// </summary>
public sealed class DeferredCopyService
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(30);

    private readonly ICopyEngine _copyEngine;
    private readonly IDeferredCopyStore _store;
    private readonly TimeProvider _time;

    public DeferredCopyService(ICopyEngine copyEngine, IDeferredCopyStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(copyEngine);
        ArgumentNullException.ThrowIfNull(store);
        _copyEngine = copyEngine;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Queues the deferred copies of a verified file.</summary>
    public async Task EnqueueAsync(long? jobId, VerifiedFile file, IEnumerable<IngestDestination> deferred, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        foreach (var destination in deferred.Where(d => d.Kind == DestinationKind.Deferred))
        {
            await _store.EnqueueAsync(
                new DeferredCopy(0, jobId, file.PrimaryPath, destination.Id, destination.RootPath, file.File.TargetRelativePath,
                    QuickHash.Format(file.Hash), 0, _time.GetUtcNow(), DeferredCopyState.Pending, null),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>Processes the copies that are due; returns how many completed.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var completed = 0;
        foreach (var copy in await _store.ListDueAsync(_time.GetUtcNow(), 100, ct).ConfigureAwait(false))
        {
            var destination = new IngestDestination(copy.DestinationId, copy.DestinationRoot, DestinationKind.Primary);
            var expected = ulong.Parse(copy.ContentHash, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            var result = await _copyEngine.CopyAsync(new IngestFile(copy.SourcePath, copy.TargetRelativePath, 0, expected), [destination], ct).ConfigureAwait(false);
            var attempts = copy.Attempts + 1;

            if (result.IsPrimaryVerified && result.SourceHash is { } hash && QuickHash.Format(hash) == copy.ContentHash)
            {
                await _store.UpdateAsync(copy with { Attempts = attempts, State = DeferredCopyState.Done, LastError = null }, ct).ConfigureAwait(false);
                completed++;
            }
            else if (result.SourceHash is { } changed && QuickHash.Format(changed) != copy.ContentHash)
            {
                await _store.UpdateAsync(
                    copy with { Attempts = attempts, State = DeferredCopyState.Failed, LastError = "The library copy no longer matches its verified hash." },
                    ct).ConfigureAwait(false);
            }
            else
            {
                var error = result.SourceError ?? result.Primary?.Error ?? "Copy not verified.";
                await _store.UpdateAsync(copy with { Attempts = attempts, NextAttemptAt = _time.GetUtcNow() + Delay(attempts), LastError = error }, ct).ConfigureAwait(false);
            }
        }

        return completed;
    }

    /// <summary>30 s, 1 min, 2 min… up to 30 min between attempts.</summary>
    public static TimeSpan Delay(int attempts)
    {
        var factor = Math.Pow(2, Math.Max(0, attempts - 1));
        var delay = TimeSpan.FromTicks((long)Math.Min(FirstRetry.Ticks * factor, MaxRetry.Ticks));
        return delay;
    }
}
