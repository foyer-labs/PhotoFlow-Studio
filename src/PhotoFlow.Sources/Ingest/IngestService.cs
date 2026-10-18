// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using PhotoFlow.Core.Ingest;

namespace PhotoFlow.Sources.Ingest;

/// <summary>
/// Copies a set of files with bounded parallelism and hands only files whose primary copy is verified to the
/// next stage (invariant I3). A local destination that becomes unavailable pauses the job (P8): no new file
/// is started, files in flight complete, nothing unverified is handed over.
/// </summary>
public sealed class IngestService : IIngestService
{
    private readonly ICopyEngine _copyEngine;

    public IngestService(ICopyEngine copyEngine)
    {
        ArgumentNullException.ThrowIfNull(copyEngine);
        _copyEngine = copyEngine;
    }

    public async Task<IngestSummary> RunAsync(
        IngestRequest request,
        Func<VerifiedFile, CancellationToken, ValueTask> onVerified,
        IProgress<IngestProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onVerified);
        Validate(request);

        var results = new ConcurrentBag<CopyResult>();
        var completed = 0;
        var paused = 0;
        using var pause = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await Parallel.ForEachAsync(
                request.Files,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, request.MaxParallelFiles), CancellationToken = pause.Token },
                async (file, token) =>
                {
                    var result = await _copyEngine.CopyAsync(file, request.Destinations, ct).ConfigureAwait(false);
                    results.Add(result);
                    if (result.Destinations.Any(d => d.Outcome == CopyOutcome.DestinationUnavailable))
                    {
                        Interlocked.Exchange(ref paused, 1);
                        await pause.CancelAsync().ConfigureAwait(false);
                    }

                    if (result.IsPrimaryVerified)
                    {
                        var primary = result.Primary!;
                        await onVerified(new VerifiedFile(file, result.SourceHash!.Value, primary.FinalPath!, result.Destinations), ct).ConfigureAwait(false);
                    }

                    progress?.Report(new IngestProgress(Interlocked.Increment(ref completed), request.Files.Count, file.SourcePath));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (paused == 1 && !ct.IsCancellationRequested)
        {
            // Paused for an unavailable destination: the remaining files wait for "Resume" or "Continue without".
        }

        var all = results.ToList();
        return new IngestSummary(
            Verified: all.Count(r => r.IsPrimaryVerified && r.Primary!.Outcome == CopyOutcome.Verified),
            AlreadyPresent: all.Count(r => r.IsPrimaryVerified && r.Primary!.Outcome == CopyOutcome.AlreadyPresent),
            Failed: all.Where(r => !r.IsPrimaryVerified || r.Destinations.Any(d => d.Outcome == CopyOutcome.Failed)).ToList(),
            PausedForUnavailableDestination: paused == 1);
    }

    /// <summary>Exactly one primary destination; no destination may lie inside the import source (I1).</summary>
    private static void Validate(IngestRequest request)
    {
        if (request.Destinations.Count(d => d.Kind == DestinationKind.Primary) != 1)
        {
            throw new ArgumentException("Exactly one primary destination is required.", nameof(request));
        }

        if (request.Destinations.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != request.Destinations.Count)
        {
            throw new ArgumentException("Destination ids must be unique.", nameof(request));
        }

        if (request.SourceRoot is { } sourceRoot)
        {
            var root = Normalize(sourceRoot);
            foreach (var destination in request.Destinations)
            {
                var target = Normalize(destination.RootPath);
                if (target.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Destination '{destination.Id}' lies inside the import source: the source is never written.", nameof(request));
                }
            }
        }

        foreach (var file in request.Files)
        {
            if (Path.IsPathRooted(file.TargetRelativePath) || file.TargetRelativePath.Split('/', '\\').Contains(".."))
            {
                throw new ArgumentException($"Invalid target path '{file.TargetRelativePath}'.", nameof(request));
            }
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
