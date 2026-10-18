// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Metadata;
using PhotoFlow.Core.Pipeline;

namespace PhotoFlow.Core.Catalog;

// Catalog entities of spec §9. Plain data only: behavior and persistence come in later phases.
// Every automatic result carries the version of the model that produced it.

// --- Libraries and assets ---

public sealed record Library(
    long Id,
    string Name,
    string RootPath,
    WritePolicy XmpPolicy,
    bool IsReadOnly,
    LibraryOrigin Origin,
    DateTimeOffset? LastScanAt);

/// <summary>Determines the default XMP policy (D21).</summary>
public enum LibraryOrigin
{
    Import,
    ExistingArchive,
}

/// <summary>A photo, identified by content hash: data follows the file when moved or renamed.</summary>
public sealed record Asset(
    long Id,
    string QuickHash,
    string? FullHash,
    string Format,
    string Handler,
    int? Width,
    int? Height,
    DateTimeOffset? CapturedAt,
    DateSource CaptureDateSource,
    ExifSummary? Exif,
    AssetState State);

public enum DateSource
{
    Unknown,
    Exif,
    File,
}

public enum AssetState
{
    Active,
    Missing,
    Corrupt,
}

public sealed record ExifSummary(
    string? CameraModel,
    string? Lens,
    int? Iso,
    double? ExposureTimeSeconds,
    double? Aperture,
    double? FocalLengthMm,
    double? ExposureCompensationEv,
    string? AfPoint,
    BoundingBox? AfArea = null);

public sealed record AssetLocation(long Id, long AssetId, long LibraryId, string RelativePath, DateTimeOffset LastSeenAt);

/// <summary>RAW+JPEG twins treated as a single photo.</summary>
public sealed record AssetPair(long Id, long RawAssetId, long JpegAssetId);

// --- Jobs ---

public sealed record Job(
    long Id,
    JobType Type,
    string? Source,
    string? EventName,
    DateOnly? EventDate,
    string? ProfileId,
    bool AutomaticProfile,
    string? FallbackProfileId,
    JobState State,
    JobPriority Priority,
    string SettingsSnapshotJson,
    DateTimeOffset CreatedAt);

public enum JobType
{
    Import,
    AnalyzeInPlace,
    Reanalyze,
}

public enum JobState
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public enum JobPriority
{
    Normal,
    Low,
}

/// <summary>Progress per photo and stage: the resume point.</summary>
public sealed record JobItem(
    long Id,
    long JobId,
    long? AssetId,
    string SourcePath,
    string? TargetRelativePath,
    string? ContentHash,
    PipelineStage LastCompletedStage,
    JobItemState State,
    string? Error);

public enum JobItemState
{
    Pending,
    InProgress,
    Done,
    Failed,
    Skipped,
}

/// <summary>A pending copy of a verified primary file to a deferred destination (NAS).</summary>
public sealed record DeferredCopy(
    long Id,
    long? JobId,
    string SourcePath,
    string DestinationId,
    string DestinationRoot,
    string TargetRelativePath,
    string ContentHash,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DeferredCopyState State,
    string? LastError);

public enum DeferredCopyState
{
    Pending,
    Done,

    /// <summary>The primary copy no longer matches its verified hash: never propagated, reported.</summary>
    Failed,
}

// --- Models and analysis ---

public sealed record ModelRecord(
    long Id,
    string Name,
    string Version,
    string Sha256,
    string License,
    ModelFunction Function,
    ModelStatus Status);

public enum ModelStatus
{
    Trial,
    Active,
    Retired,
}

public sealed record AnalyzerResultRecord(
    long Id,
    long AssetId,
    string AnalyzerId,
    string ModelVersion,
    string ProfileId,
    IReadOnlyDictionary<string, double> Scores);

public sealed record EmbeddingRecord(long Id, long AssetId, string ModelVersion, ReadOnlyMemory<float> Vector);

// --- Groups and decisions ---

/// <summary>A group of photos of a job (spec §9 <c>Groups</c>); the profile is the one assigned to the group (D8).</summary>
public sealed record AssetGroup(long Id, long JobId, GroupType Type, string? ProfileId, double? ProfileConfidence, GroupOrigin Origin)
{
    /// <summary>Bracketing, timelapse and panorama are never ranked (spec §6, special sequences).</summary>
    public bool IsRanked => Type == GroupType.Burst;
}

public enum GroupType
{
    Burst,
    Bracketing,
    Timelapse,
    Panorama,
    Duplicates,
}

public enum GroupOrigin
{
    Automatic,
    Manual,
}

public sealed record GroupMember(long GroupId, long AssetId, int Position, double? Score);

/// <summary>
/// Profile of an analyzed photo in a job (D8): <paramref name="ProfileId"/> null means the job profile; set by CLIP in
/// automatic mode or by the user for its group (<paramref name="ByUser"/>, never overwritten). The suggestion is shown in
/// manual mode when the photo looks like another profile.
/// </summary>
public sealed record PhotoProfile(long JobId, long AssetId, string? ProfileId, double? Confidence, bool ByUser, string? SuggestedProfileId, double? SuggestedConfidence);

/// <summary>Automatic proposal and user decision; the user decision always wins (invariant I5).</summary>
public sealed record Decision(
    long Id,
    long AssetId,
    CullingOutcome? AutomaticOutcome,
    IReadOnlyList<string> Reasons,
    string? ModelVersion,
    CullingOutcome? UserOutcome,
    int? Rating,
    string? Label)
{
    /// <summary>The user's decision when present, otherwise the automatic proposal (invariant I5).</summary>
    public CullingOutcome? EffectiveOutcome => UserOutcome ?? AutomaticOutcome;

    /// <summary>When the user last changed outcome, rating or label (conflicts with other programs: the most recent wins).</summary>
    public DateTimeOffset? UserUpdatedAt { get; init; }
}

/// <summary>Culling outcomes of spec §6.</summary>
public enum CullingOutcome
{
    Discard,
    ToReview,
    Best,
    Similar,
    Kept,
}

/// <summary>Labels of the labelling mode (spec §14: 1 good, 2 blurred, 3 eyes closed, 4 exposure, 5 other).</summary>
public enum PhotoLabel
{
    Good,
    Blurred,
    EyesClosed,
    Exposure,
    Other,

    /// <summary>Chosen as the best of its burst.</summary>
    BurstBest,
}

public sealed record LabeledPhoto(long AssetId, PhotoLabel Label, string SessionId, DateTimeOffset LabeledAt);

public sealed record Correction(
    long Id,
    long AssetId,
    CullingOutcome? AutomaticOutcome,
    CullingOutcome UserOutcome,
    string ProfileId,
    IReadOnlyDictionary<string, double> Signals,
    DateTimeOffset CorrectedAt);

// --- Tags ---

/// <summary>
/// Stable internal English key (spec §8), with its Italian label: written only once <see cref="Reviewed"/> (the English
/// key is always written). Blacklisted tags are never written. <see cref="PhotoCount"/>: photos where it is active.
/// </summary>
public sealed record Tag(long Id, string Key, string? Category, TagSource Source, bool Reviewed, bool Blacklisted)
{
    public string? Italian { get; init; }

    public int PhotoCount { get; init; }

    /// <summary>The English label: the key, without the prefix of custom and event tags.</summary>
    public string English => Key.Contains(':', StringComparison.Ordinal) ? Key[(Key.IndexOf(':', StringComparison.Ordinal) + 1)..] : Key;
}

/// <summary>A tag of a photo with its assignment (active or suppressed).</summary>
public sealed record AssetTagInfo(Tag Tag, AssetTag Assignment);

/// <summary>A tag of a shipped vocabulary (RAM++): key, Italian label to review, default category, blacklisted at first start.</summary>
public sealed record VocabularyEntry(string Key, string Italian, string? Category, bool Blacklisted = false);

public enum TagSource
{
    RamPlus,
    ClipZeroShot,
    Job,
    Faces,
    Ocr,
    Manual,
}

public sealed record TagLabel(long TagId, string Language, string Label);

public sealed record AssetTag(long AssetId, long TagId, TagSource Source, double? Confidence, string? ModelVersion, AssetTagState State);

/// <summary>Suppressed = removed by the user, never re-added automatically.</summary>
public enum AssetTagState
{
    Active,
    Suppressed,
}

/// <summary>A personal tag recognized by CLIP zero-shot (level A, board 5d): English description for the model, threshold, margin over the second candidate.</summary>
public sealed record CustomTag(long TagId, string ClipDescription, double Threshold, double Margin);

// --- Motorsport ---

/// <summary>An entry list (spec §8): reusable on several events.</summary>
public sealed record EntryList(long Id, string Name, string? SourceFile, DateTimeOffset CreatedAt);

/// <summary>A car of an entry list; two or more drivers are a crew (D18: only the number and team in the XMP).</summary>
public sealed record Entry(long Id, long ListId, string Number, IReadOnlyList<string> Drivers, string? Team, string? Category, string? Car)
{
    public bool IsCrew => Drivers.Count > 1;
}

public enum NumberState
{
    /// <summary>One number of the list, clearly.</summary>
    Certain,

    /// <summary>Several candidates in the list, or a partial reading: the user decides in the Numbers view.</summary>
    ToReview,

    /// <summary>No readable number.</summary>
    NoNumber,

    /// <summary>Numbers read, none in the list (sponsors, plates): ignored.</summary>
    OutOfList,
}

public enum NumberOrigin
{
    Automatic,

    /// <summary>From the majority of the burst.</summary>
    Propagated,

    /// <summary>Typed or confirmed by the user: never replaced by an analysis (I5).</summary>
    Manual,
}

public sealed record NumberCandidate(string Number, double Score);

/// <summary>The race number of a vehicle of a photo; <see cref="Box"/> is the vehicle, normalized.</summary>
public sealed record VehicleNumber(long AssetId, int Vehicle, BoundingBox Box, string? Number, NumberState State, NumberOrigin Origin, IReadOnlyList<NumberCandidate> Candidates);

/// <summary>An OCR reading on a vehicle (<see cref="VehicleBox"/>) of a photo; <see cref="Box"/> in the photo, normalized.</summary>
public sealed record OcrReading(long AssetId, int Vehicle, BoundingBox VehicleBox, BoundingBox Box, string Text, double Confidence, string ModelVersion);

// --- Duplicates ---

public enum DuplicateKind
{
    /// <summary>Same content (same hash) in several folders of a library.</summary>
    Exact,

    /// <summary>Different files of the same picture: exports, resized or cropped copies (F3-5).</summary>
    Near,
}

/// <summary>A copy in a duplicate group: the asset and the file.</summary>
public sealed record DuplicateMember(long AssetId, AssetLocation Location);

/// <param name="Key">Stable key of the group (its files), used to remember "not duplicates".</param>
/// <param name="Similarity">CLIP cosine for near duplicates, 1 for exact ones.</param>
/// <param name="Recommended">The copy proposed to keep.</param>
public sealed record DuplicateGroup(string Key, DuplicateKind Kind, IReadOnlyList<DuplicateMember> Members, double Similarity, int Recommended);

// --- Faces ---

public readonly record struct Point2(float X, float Y);

public sealed record Face(
    long Id,
    long AssetId,
    BoundingBox Box,
    IReadOnlyList<Point2>? Landmarks,
    double Confidence,
    EyeState LeftEye,
    EyeState RightEye,
    byte[]? EncryptedEmbedding,
    long? PersonId,
    AssignmentOrigin? AssignmentOrigin,
    double? Similarity,
    string ModelVersion,
    IReadOnlyList<Point2>? Keypoints = null,
    string? EmbeddingModel = null,
    long? SuggestedPersonId = null);

/// <summary>Values of the faces drawn by the user (spec §7, 13 Oct 2026).</summary>
public static class FaceDefaults
{
    /// <summary>Model version of a face drawn by the user: no detector, no fingerprint.</summary>
    public const string ManualModelVersion = "manual";

    /// <summary>Smallest side of a drawn face, as a fraction of the photo.</summary>
    public const float MinManualSide = 0.01f;
}

/// <summary>Unknown with sunglasses, small faces, or with YuNet/SFace (spec §6).</summary>
public enum EyeState
{
    Unknown,
    Open,
    Closed,
}

public enum AssignmentOrigin
{
    Automatic,
    Manual,
}

public sealed record Person(long Id, string? Name, bool Ignored, long? CoverFaceId);

/// <summary>An encrypted fingerprint representing a person in a year (P12: the face changes over time).</summary>
public sealed record PersonPrototype(long Id, long PersonId, long FaceId, int? Year, byte[] EncryptedEmbedding, string ModelVersion);

/// <summary>
/// The people of the catalog removed by a reset (P22): persons, named ones, faces assigned by the user, faces drawn by
/// the user; the former names and the photos whose faces had a person or were drawn.
/// </summary>
public sealed record PeopleReset(int Persons, int NamedPersons, int ManualAssignments, int DrawnFaces, IReadOnlyList<string> Names, IReadOnlyList<long> AssetIds)
{
    public bool IsEmpty => Persons == 0 && ManualAssignments == 0 && DrawnFaces == 0;
}

/// <summary>A person with the size of its gallery (Persone, 4a): unnamed persons are clusters ("Persona N").</summary>
public sealed record PersonSummary(Person Person, int FaceCount, int PhotoCount, int? FirstYear, int? LastYear, int ManualFaces);

/// <summary>A face with its encrypted fingerprint, for recognition and clustering.</summary>
public sealed record FaceFingerprint(long FaceId, long AssetId, int? Year, byte[] EncryptedEmbedding, string EmbeddingModel, long? PersonId, AssignmentOrigin? Origin);

// --- Text ---

public enum CaptionOrigin
{
    Automatic,

    /// <summary>Written by the user (or imported from another program): never replaced by an analysis (I5).</summary>
    Manual,
}

/// <param name="ModelVersion">Model that wrote an automatic caption; null for the user's.</param>
public sealed record Caption(long AssetId, string Language, string Text, string? ModelVersion, CaptionOrigin Origin, DateTimeOffset UpdatedAt);

public sealed record OcrResult(long Id, long AssetId, BoundingBox Box, string Text, double Confidence, string ModelVersion);

public sealed record EntryListItem(
    long Id,
    string EventName,
    string Number,
    IReadOnlyList<string> Drivers,
    string? Team,
    string? Category,
    string? Car);

// --- Sync and system ---

public enum XmpSyncWinner
{
    /// <summary>The change made by the other program was imported.</summary>
    External,

    /// <summary>PhotoFlow's value was kept (more recent, or a manual tag that is never removed).</summary>
    Catalog,
}

/// <summary>A change found in the XMP of a photo and what was done with it (spec §4: "con log").</summary>
public sealed record XmpSyncLogEntry(long AssetId, DateTimeOffset At, string Field, string? Value, XmpSyncWinner Winner, string? Note);

public sealed record XmpSyncState(long AssetId, DateTimeOffset? LastWrittenAt, string? XmpSignature, bool WritePending, string? LastError)
{
    /// <summary>State of the file holding the XMP after the last write or sync (JSON of <c>XmpFileStamp</c>).</summary>
    public string? FileStampJson { get; init; }

    /// <summary>What the file contained then (JSON of <c>XmpSnapshot</c>): the base to see what others changed.</summary>
    public string? SnapshotJson { get; init; }

    /// <summary>Keywords written by PhotoFlow at the last write (flat, then hierarchical).</summary>
    public IReadOnlyList<string> WrittenFlat { get; init; } = [];

    public IReadOnlyList<string> WrittenHierarchical { get; init; } = [];
}

public sealed record UndoLogEntry(long Id, DateTimeOffset At, string Operation, string PayloadJson);

public sealed record SchemaVersion(int Version, string ScriptName, DateTimeOffset AppliedAt);
