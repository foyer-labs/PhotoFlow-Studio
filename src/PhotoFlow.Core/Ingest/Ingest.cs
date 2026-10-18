// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Ingest;

/// <summary>A source file to import, with its destination path relative to each destination root.</summary>
/// <param name="ExpectedHash">When set, the source must have this xxHash3 or nothing is finalized (e.g. a deferred copy made from a verified library file).</param>
public sealed record IngestFile(string SourcePath, string TargetRelativePath, long Length, ulong? ExpectedHash = null);

public enum DestinationKind
{
    /// <summary>The library copy: later stages only run on files verified here (invariant I3).</summary>
    Primary,

    /// <summary>Additional local copy (e.g. backup disk), written in the same pass.</summary>
    Secondary,

    /// <summary>Asynchronous copy with retry (e.g. NAS), made later from the verified primary copy.</summary>
    Deferred,
}

public sealed record IngestDestination(string Id, string RootPath, DestinationKind Kind);

public enum CopyOutcome
{
    /// <summary>Written and verified by re-reading (xxHash3).</summary>
    Verified,

    /// <summary>An identical file was already there: nothing written (P9).</summary>
    AlreadyPresent,

    /// <summary>Queued for asynchronous copy (deferred destination).</summary>
    Queued,

    /// <summary>Copy or verification failed; the source is untouched and the reason is recorded.</summary>
    Failed,

    /// <summary>The destination became unavailable (e.g. disk disconnected): the job pauses (P8).</summary>
    DestinationUnavailable,
}

public sealed record DestinationResult(string DestinationId, CopyOutcome Outcome, string? FinalPath, string? Error);

/// <summary>Result of copying one file to all destinations.</summary>
public sealed record CopyResult(
    IngestFile File,
    ulong? SourceHash,
    string PrimaryDestinationId,
    IReadOnlyList<DestinationResult> Destinations,
    string? SourceError)
{
    public DestinationResult? Primary => Destinations.FirstOrDefault(d => d.DestinationId == PrimaryDestinationId);

    /// <summary>The primary copy exists and matches the source: the only condition for later stages (I3).</summary>
    public bool IsPrimaryVerified =>
        SourceError is null && SourceHash is not null && Primary is { Outcome: CopyOutcome.Verified or CopyOutcome.AlreadyPresent };
}

/// <summary>Copies one file: single read of the source, parallel writes, hash while reading, verification by re-reading.</summary>
public interface ICopyEngine
{
    Task<CopyResult> CopyAsync(IngestFile file, IReadOnlyList<IngestDestination> destinations, CancellationToken ct);
}

/// <summary>A file whose primary copy is verified: the hand-off point to the next pipeline stages.</summary>
public sealed record VerifiedFile(IngestFile File, ulong Hash, string PrimaryPath, IReadOnlyList<DestinationResult> Destinations);

/// <param name="SourceRoot">Root of the import source (card or folder): no destination may lie inside it (I1).</param>
public sealed record IngestRequest(IReadOnlyList<IngestFile> Files, IReadOnlyList<IngestDestination> Destinations, string? SourceRoot, int MaxParallelFiles = 2);

public sealed record IngestProgress(int Completed, int Total, string? CurrentFile);

public sealed record IngestSummary(int Verified, int AlreadyPresent, IReadOnlyList<CopyResult> Failed, bool PausedForUnavailableDestination);

/// <summary>Runs the copy of a set of files; only verified files are handed to <c>onVerified</c>.</summary>
public interface IIngestService
{
    Task<IngestSummary> RunAsync(
        IngestRequest request,
        Func<VerifiedFile, CancellationToken, ValueTask> onVerified,
        IProgress<IngestProgress>? progress,
        CancellationToken ct);
}
