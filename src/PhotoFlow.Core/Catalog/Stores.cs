// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.Core.Catalog;

/// <summary>The catalog (spec §9). All SQL lives behind these interfaces, in PhotoFlow.Data.</summary>
public interface ICatalog : IAsyncDisposable
{
    ILibraryStore Libraries { get; }

    IAssetStore Assets { get; }

    IJobStore Jobs { get; }

    IDecisionStore Decisions { get; }

    IXmpSyncStore XmpSync { get; }

    IDeferredCopyStore DeferredCopies { get; }

    IAnalysisStore Analysis { get; }

    IGroupStore Groups { get; }

    IPeopleStore People { get; }

    ILabelStore Labels { get; }

    ITagStore Tags { get; }

    IDuplicateStore Duplicates { get; }

    IMotorsportStore Motorsport { get; }

    ICaptionStore Captions { get; }

    IReanalysisStore Reanalysis { get; }

    /// <summary>Search over the photos of the catalog (spec §3: only through this interface).</summary>
    Search.ISearchIndex Search { get; }

    /// <summary>Consistent copy of the open catalog into <paramref name="backupsDirectory"/> (spec §9); returns its path.</summary>
    Task<string> BackupAsync(string backupsDirectory, string reason, CancellationToken ct);
}

public interface ILibraryStore
{
    Task<long> AddAsync(Library library, CancellationToken ct);

    Task<Library?> GetAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<Library>> ListAsync(CancellationToken ct);

    /// <summary>The library with this root (compared case-insensitively, as on Windows), if any.</summary>
    Task<Library?> FindByRootAsync(string rootPath, CancellationToken ct);

    /// <summary>Photos (assets) with a location in the library.</summary>
    Task<int> CountAssetsAsync(long libraryId, CancellationToken ct);

    /// <summary>Assets of the library by format (NEF, JPG, TIFF, HEIC…), board 5a.</summary>
    Task<IReadOnlyDictionary<string, int>> CountFormatsAsync(long libraryId, CancellationToken ct);

    /// <summary>XMP policy and read-only flag (D21: the caller confirms a switch to embedded XMP).</summary>
    Task UpdatePolicyAsync(long libraryId, Metadata.WritePolicy policy, bool isReadOnly, CancellationToken ct);

    /// <summary>The library moved (catalog imported on another PC, spec §9): its photos are found under the new root.</summary>
    Task UpdateRootAsync(long libraryId, string rootPath, CancellationToken ct);
}

public interface IAssetStore
{
    Task<long> AddAsync(Asset asset, CancellationToken ct);

    Task<Asset?> GetAsync(long id, CancellationToken ct);

    /// <summary>Several assets at once (missing ids are skipped).</summary>
    Task<IReadOnlyDictionary<long, Asset>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken ct);

    /// <summary>Camera models in the catalog, for the EXIF filter.</summary>
    Task<IReadOnlyList<string>> ListCamerasAsync(CancellationToken ct);

    /// <summary>"Already imported" identity: quick hash plus capture time (spec §4).</summary>
    Task<Asset?> FindByIdentityAsync(string quickHash, DateTimeOffset? capturedAt, CancellationToken ct);

    Task<Asset?> FindByFullHashAsync(string fullHash, CancellationToken ct);

    Task AddLocationAsync(AssetLocation location, CancellationToken ct);

    /// <summary>The file of the asset was moved inside its library (explicit user action, P6).</summary>
    Task MoveLocationAsync(long libraryId, string fromRelativePath, string toRelativePath, CancellationToken ct);

    Task<IReadOnlyList<AssetLocation>> GetLocationsAsync(long assetId, CancellationToken ct);

    Task AddPairAsync(long rawAssetId, long jpegAssetId, CancellationToken ct);

    /// <summary>The other asset of a RAW+JPEG pair, if any.</summary>
    Task<long?> GetTwinAsync(long assetId, CancellationToken ct);

    /// <summary>Assets of a library still without the full hash (computed in background, spec §10).</summary>
    Task<IReadOnlyList<(long AssetId, string RelativePath)>> ListWithoutFullHashAsync(long libraryId, int limit, CancellationToken ct);

    /// <summary>The active photos of a library, optionally under a folder (relative path prefix).</summary>
    Task<IReadOnlyList<(long AssetId, string RelativePath)>> ListInLibraryAsync(long libraryId, string? folder, CancellationToken ct);

    Task SetFullHashAsync(long assetId, string fullHash, CancellationToken ct);
}

public interface IJobStore
{
    Task<long> CreateAsync(Job job, CancellationToken ct);

    Task<Job?> GetAsync(long id, CancellationToken ct);

    Task SetStateAsync(long jobId, JobState state, CancellationToken ct);

    /// <summary>Automatic profile per group (D8) with its mandatory fallback.</summary>
    Task SetAutomaticProfileAsync(long jobId, bool automatic, string? fallbackProfileId, CancellationToken ct);

    /// <summary>Jobs left running or paused, e.g. after an unexpected shutdown (board 3c).</summary>
    Task<IReadOnlyList<Job>> ListUnfinishedAsync(CancellationToken ct);

    /// <summary>The most recent jobs, newest first.</summary>
    Task<IReadOnlyList<Job>> ListRecentAsync(int limit, CancellationToken ct);

    Task AddItemsAsync(long jobId, IReadOnlyList<JobItem> items, CancellationToken ct);

    Task UpdateItemAsync(JobItem item, CancellationToken ct);

    Task<IReadOnlyList<JobItem>> GetItemsAsync(long jobId, CancellationToken ct);
}

/// <summary>Culling decisions. Automatic processes can never overwrite the user's decision (invariant I5).</summary>
public interface IDecisionStore
{
    Task<Decision?> GetAsync(long assetId, CancellationToken ct);

    /// <summary>Stores the automatic proposal only; user outcome, rating and label are never touched.</summary>
    Task SetAutomaticAsync(long assetId, CullingOutcome outcome, IReadOnlyList<string> reasons, string modelVersion, CancellationToken ct);

    /// <summary>Stores the user's decision; it wins over any present or future automatic proposal.</summary>
    Task SetUserAsync(long assetId, CullingOutcome? outcome, int? rating, string? label, CancellationToken ct);

    /// <summary>Effective outcomes (the user's, else the automatic one) of the assets of a job, by outcome.</summary>
    Task<IReadOnlyDictionary<CullingOutcome, int>> CountOutcomesForJobAsync(long jobId, CancellationToken ct);

    /// <summary>A correction of the user with the signals of the time (spec §6, learning from the corrections).</summary>
    Task AddCorrectionAsync(Correction correction, CancellationToken ct);

    Task<IReadOnlyList<Correction>> ListCorrectionsAsync(CancellationToken ct);
}

public interface IXmpSyncStore
{
    Task<XmpSyncState?> GetAsync(long assetId, CancellationToken ct);

    Task MarkPendingAsync(long assetId, string? error, CancellationToken ct);

    /// <param name="flatKeywords">Keywords PhotoFlow wrote (recorded to remove them later if their tag goes away).</param>
    Task MarkWrittenAsync(long assetId, string signature, CancellationToken ct, IReadOnlyList<string>? flatKeywords = null, IReadOnlyList<string>? hierarchicalKeywords = null);

    Task<IReadOnlyList<long>> ListPendingAsync(CancellationToken ct);

    /// <summary>The state and content of the XMP file after a write or a sync (base of the next sync).</summary>
    Task RecordFileAsync(long assetId, string? fileStampJson, string snapshotJson, CancellationToken ct);

    /// <summary>Assets with a recorded XMP state (the ones a sync looks at).</summary>
    Task<IReadOnlyList<long>> ListRecordedAsync(CancellationToken ct);

    Task AddLogAsync(IReadOnlyList<XmpSyncLogEntry> entries, CancellationToken ct);

    Task<IReadOnlyList<XmpSyncLogEntry>> ListLogAsync(long? assetId, CancellationToken ct);
}

/// <summary>Persistent queue of copies to deferred destinations (NAS), retried until verified.</summary>
public interface IDeferredCopyStore
{
    Task EnqueueAsync(DeferredCopy copy, CancellationToken ct);

    Task<IReadOnlyList<DeferredCopy>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct);

    Task UpdateAsync(DeferredCopy copy, CancellationToken ct);

    Task<int> CountPendingAsync(CancellationToken ct);
}

/// <summary>Automatic analysis results and detected faces. Manual face assignments are never removed (invariant I5).</summary>
public interface IAnalysisStore
{
    /// <summary>Stores the result, replacing the previous result of the same analyzer for the asset.</summary>
    Task SaveResultAsync(AnalyzerResultRecord result, CancellationToken ct);

    Task<AnalyzerResultRecord?> GetResultAsync(long assetId, string analyzerId, CancellationToken ct);

    /// <summary>Latest result of the analyzer for each of the assets that have one.</summary>
    Task<IReadOnlyDictionary<long, AnalyzerResultRecord>> GetResultsAsync(IReadOnlyCollection<long> assetIds, string analyzerId, CancellationToken ct);

    /// <summary>Replaces the automatically detected faces of the asset; faces with a manual assignment are kept.</summary>
    Task ReplaceAutomaticFacesAsync(long assetId, IReadOnlyList<Face> faces, CancellationToken ct);

    /// <summary>
    /// Replaces all the faces of a photo in one transaction (new face model, spec §5): <paramref name="faces"/> are
    /// inserted as given (with the person they take over), <paramref name="keep"/> stay, every other face goes; covers
    /// follow <paramref name="replaces"/> (index of a new face → the old face it takes over); prototypes of removed faces
    /// go (to rebuild). Returns the ids of the new faces, in order.
    /// </summary>
    Task<IReadOnlyList<long>> ReplaceFacesAsync(long assetId, IReadOnlyList<Face> faces, IReadOnlyCollection<long> keep,
        IReadOnlyDictionary<int, long> replaces, CancellationToken ct);

    Task<IReadOnlyList<Face>> GetFacesAsync(long assetId, CancellationToken ct);

    /// <summary>
    /// A face drawn by the user where no face was detected (spec §7): manual assignment to <paramref name="personId"/>,
    /// version <see cref="FaceDefaults.ManualModelVersion"/>, no fingerprint; kept by every analysis (I5). Returns its id.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The box is not inside the photo, or too small.</exception>
    Task<long> AddManualFaceAsync(long assetId, Analysis.BoundingBox box, long? personId, CancellationToken ct);

    /// <summary>Stores the image embedding of the asset for a model version, replacing an earlier one of the same version.</summary>
    Task SaveEmbeddingAsync(long assetId, string modelVersion, ReadOnlyMemory<float> vector, CancellationToken ct);

    /// <summary>Embeddings of a model version for the given assets (assets without one are absent).</summary>
    Task<IReadOnlyDictionary<long, float[]>> GetEmbeddingsAsync(IReadOnlyCollection<long> assetIds, string modelVersion, CancellationToken ct);

    /// <summary>Photos with an embedding of this model.</summary>
    Task<IReadOnlyList<long>> ListEmbeddedAssetsAsync(string modelVersion, CancellationToken ct);
}

/// <summary>Groups of a job (spec §9 <c>Groups</c>, <c>GroupMembers</c>).</summary>
public interface IGroupStore
{
    /// <summary>
    /// Replaces the automatic groups of the job; manual groups (and their photos) are kept and the proposed groups
    /// leave out the photos already in a manual group. Returns the new groups with their members.
    /// </summary>
    Task<IReadOnlyList<(AssetGroup Group, IReadOnlyList<GroupMember> Members)>> ReplaceAutomaticAsync(
        long jobId, IReadOnlyList<(GroupType Type, IReadOnlyList<long> AssetIds)> groups, CancellationToken ct);

    /// <summary>Groups of the job with their members in position order.</summary>
    Task<IReadOnlyList<(AssetGroup Group, IReadOnlyList<GroupMember> Members)>> ListAsync(long jobId, CancellationToken ct);

    Task<long> AddManualAsync(long jobId, GroupType type, IReadOnlyList<long> assetIds, CancellationToken ct);

    Task SetScoresAsync(long groupId, IReadOnlyDictionary<long, double> scores, CancellationToken ct);

    Task SetProfileAsync(long groupId, string? profileId, double? confidence, CancellationToken ct);

    Task<IReadOnlyDictionary<long, PhotoProfile>> GetPhotoProfilesAsync(long jobId, CancellationToken ct);

    /// <summary>Automatic profiles and suggestions; a profile chosen by the user is kept (only its suggestion changes).</summary>
    Task SetAutomaticProfilesAsync(long jobId, IReadOnlyList<PhotoProfile> profiles, CancellationToken ct);

    /// <summary>The user's profile for photos of the job ("re-analyze as…"); null gives them back to the automatic choice.</summary>
    Task SetUserProfileAsync(long jobId, IReadOnlyList<long> assetIds, string? profileId, CancellationToken ct);
}

/// <summary>People and face assignments (spec §7). Automatic assignments never replace manual ones (I5).</summary>
public interface IPeopleStore
{
    Task<IReadOnlyList<FaceFingerprint>> ListFingerprintsForAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct);

    /// <summary>Faces with a fingerprint, no person and no manual decision, captured in <paramref name="year"/> (null: unknown date).</summary>
    Task<IReadOnlyList<FaceFingerprint>> ListUnassignedFingerprintsAsync(int? year, CancellationToken ct);

    /// <summary>Faces that can have a fingerprint (with keypoints) and those that have one.</summary>
    Task<(int WithKeypoints, int WithFingerprint)> CountFingerprintsAsync(CancellationToken ct);

    /// <summary>Every face with a fingerprint (export of the catalog: encrypted again with another key).</summary>
    Task<IReadOnlyList<FaceFingerprint>> ListAllFingerprintsAsync(CancellationToken ct);

    Task<IReadOnlyList<PersonPrototype>> ListPrototypesAsync(CancellationToken ct);

    Task ReplacePrototypesAsync(long personId, IReadOnlyList<PersonPrototype> prototypes, CancellationToken ct);

    Task<long> CreatePersonAsync(string? name, CancellationToken ct);

    /// <summary>Automatic assignments (recognition, clustering): faces decided by the user are skipped.</summary>
    Task AssignAutomaticAsync(IReadOnlyList<(long FaceId, long PersonId, double? Similarity)> faces, CancellationToken ct);

    /// <summary>Suggestion below the threshold (amber chip, 4b) or none.</summary>
    Task SuggestAsync(IReadOnlyList<(long FaceId, long? PersonId, double? Similarity)> faces, CancellationToken ct);

    /// <summary>The user's assignment (or "not this person": null), always kept.</summary>
    Task AssignManualAsync(IReadOnlyList<long> faceIds, long? personId, CancellationToken ct);

    Task<IReadOnlyList<PersonSummary>> ListPersonsAsync(CancellationToken ct);

    Task<Person?> GetPersonAsync(long personId, CancellationToken ct);

    Task<IReadOnlyList<Face>> ListFacesOfPersonAsync(long personId, CancellationToken ct);

    Task RenameAsync(long personId, string? name, CancellationToken ct);

    Task SetIgnoredAsync(long personId, bool ignored, CancellationToken ct);

    Task SetCoverAsync(long personId, long faceId, CancellationToken ct);

    /// <summary>Moves faces and prototypes of <paramref name="sourceIds"/> into <paramref name="targetId"/> and removes the sources.</summary>
    Task MergeAsync(long targetId, IReadOnlyList<long> sourceIds, CancellationToken ct);

    /// <summary>Removes the person, its prototypes and every fingerprint of its faces (§7: no residue); the photos stay.</summary>
    Task DeletePersonAsync(long personId, CancellationToken ct);

    /// <summary>Faces with keypoints, for recomputing the fingerprints with a new key.</summary>
    Task<IReadOnlyList<Face>> ListFacesWithKeypointsAsync(CancellationToken ct);

    Task SetFingerprintAsync(long faceId, byte[]? encryptedEmbedding, string? embeddingModel, CancellationToken ct);

    /// <summary>Drops every fingerprint and prototype (key reset): assignments and names stay.</summary>
    Task ClearFingerprintsAsync(CancellationToken ct);

    /// <summary>What <see cref="ResetAsync"/> would remove, for the confirmation (P22).</summary>
    Task<PeopleReset> CountResetAsync(CancellationToken ct);

    /// <summary>
    /// "Ripartire da zero con le persone" (P22), only on the user's explicit request: every person, every assignment
    /// (manual ones included), suggestion and prototype, and the faces drawn by the user, in the whole catalog. The
    /// detected faces and their fingerprints stay. Returns what was removed (the former names, for the XMP).
    /// </summary>
    Task<PeopleReset> ResetAsync(CancellationToken ct);
}

/// <summary>The labelled set of the culling metrics (spec §14).</summary>
public interface ILabelStore
{
    Task SetAsync(long assetId, PhotoLabel label, string sessionId, CancellationToken ct);

    /// <summary>Removes a label (undo in the labelling mode).</summary>
    Task RemoveAsync(long assetId, CancellationToken ct);

    Task<IReadOnlyList<LabeledPhoto>> ListAsync(CancellationToken ct);
}

public sealed class CatalogLockedException(string message) : IOException(message);

public sealed class CatalogCorruptException(string message) : IOException(message);

/// <summary>
/// Tags and their assignments (spec §8, §9). I5: an analysis replaces only the automatic tags of its own source; manual
/// tags are never removed by it, and a tag the user removed stays suppressed until the user adds it again.
/// </summary>
public interface ITagStore
{
    /// <summary>Adds the entries not yet in the catalog; existing tags (and the user's edits) are left as they are.</summary>
    Task SeedVocabularyAsync(IReadOnlyList<VocabularyEntry> entries, TagSource source, CancellationToken ct);

    Task<Tag?> FindAsync(string key, CancellationToken ct);

    /// <summary>The tag with this key, created with these values when missing.</summary>
    Task<long> EnsureAsync(string key, TagSource source, string? category, string? italian, bool reviewed, CancellationToken ct);

    /// <summary>Every tag with the number of photos where it is active.</summary>
    Task<IReadOnlyList<Tag>> ListAsync(CancellationToken ct);

    /// <summary>The automatic tags of <paramref name="source"/> on the photo become <paramref name="tags"/> (I5 above).</summary>
    Task ReplaceAutomaticAsync(long assetId, TagSource source, string? modelVersion, IReadOnlyList<(string Key, double? Confidence)> tags, CancellationToken ct);

    /// <summary>The user's tag (created as a manual tag when the key is new); a suppressed tag becomes active again.</summary>
    Task AddManualAsync(long assetId, string key, CancellationToken ct);

    /// <summary>The user removes a tag from the photo: it stays as "suppressed" so that no analysis adds it again.</summary>
    Task SuppressAsync(long assetId, long tagId, CancellationToken ct);

    /// <summary>Tags of the photo, active and suppressed.</summary>
    Task<IReadOnlyList<AssetTagInfo>> GetForAssetAsync(long assetId, CancellationToken ct);

    /// <summary>Active tags of several photos.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<Tag>>> GetActiveForAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct);

    /// <summary>Vocabulary review (board 5c): Italian label, reviewed, category, blacklist.</summary>
    Task UpdateAsync(long tagId, string? italian, bool reviewed, string? category, bool blacklisted, CancellationToken ct);

    /// <summary>Photos where the tag is active with the lowest confidence (board 5c: the doubtful examples above the threshold).</summary>
    Task<IReadOnlyList<(long AssetId, double? Confidence)>> ListExamplesAsync(long tagId, int count, CancellationToken ct);

    /// <summary>Photos with at least one active automatic tag (P15: the core vocabulary forms after 1,000).</summary>
    Task<int> CountTaggedAssetsAsync(CancellationToken ct);

    Task<IReadOnlyList<(Tag Tag, CustomTag Custom)>> ListCustomAsync(CancellationToken ct);

    /// <summary>Creates or updates a personal CLIP tag (board 5d); returns its id.</summary>
    Task<long> SaveCustomAsync(string key, string italian, string? category, string clipDescription, double threshold, double margin, CancellationToken ct);
}

/// <summary>What the duplicate search keeps between runs (spec §12 view 7, P5).</summary>
public interface IDuplicateStore
{
    /// <summary>Copies of the same content in several folders of the same library (exact duplicates, F3-6).</summary>
    Task<IReadOnlyList<IReadOnlyList<AssetLocation>>> ListExactCopiesAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<long, ulong>> GetHashesAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct);

    Task SetHashAsync(long assetId, ulong hash, CancellationToken ct);

    /// <summary>Groups marked "not duplicates" by the user, by key.</summary>
    Task<IReadOnlySet<string>> ListExclusionsAsync(CancellationToken ct);

    Task ExcludeAsync(string key, CancellationToken ct);
}

/// <summary>Entry lists, OCR readings and race numbers (spec §8, §9).</summary>
/// <summary>The results of a regeneration waiting for the user's confirmation (spec §5).</summary>
public interface IReanalysisStore
{
    Task StageAsync(long jobId, long assetId, Pipeline.ReanalysisParts part, string payloadJson, CancellationToken ct);

    Task<IReadOnlyList<(long AssetId, Pipeline.ReanalysisParts Part, string PayloadJson)>> ListAsync(long jobId, CancellationToken ct);

    Task<int> CountAsync(long jobId, CancellationToken ct);

    /// <summary>Removes the staged results of a job (applied or discarded).</summary>
    Task ClearAsync(long jobId, CancellationToken ct);
}

/// <summary>Captions (spec §8, levels C/D): one per photo and language; the user's are never replaced (I5).</summary>
public interface ICaptionStore
{
    /// <summary>Saves an automatic caption; false (nothing written) when the user wrote or cleared the caption.</summary>
    Task<bool> SetAutomaticAsync(long assetId, string language, string text, string modelVersion, CancellationToken ct);

    /// <summary>The user's caption (an empty text clears it for good).</summary>
    Task SetManualAsync(long assetId, string language, string text, CancellationToken ct);

    /// <summary>The caption of a photo: Italian first (U5), then the user's, then the most recent.</summary>
    Task<Caption?> GetAsync(long assetId, CancellationToken ct);

    /// <summary>Every caption of a photo, one per language, in the order of <see cref="GetAsync"/>.</summary>
    Task<IReadOnlyList<Caption>> GetAllAsync(long assetId, CancellationToken ct);

    Task<IReadOnlyDictionary<long, Caption>> GetForAssetsAsync(IReadOnlyList<long> assetIds, CancellationToken ct);
}

public interface IMotorsportStore
{
    Task<long> CreateListAsync(string name, string? sourceFile, CancellationToken ct);

    Task<IReadOnlyList<EntryList>> ListListsAsync(CancellationToken ct);

    /// <summary>The entries of the list become <paramref name="entries"/> (numbers unique in a list).</summary>
    Task ReplaceEntriesAsync(long listId, IReadOnlyList<Entry> entries, CancellationToken ct);

    Task<IReadOnlyList<Entry>> GetEntriesAsync(long listId, CancellationToken ct);

    /// <summary>The list used by a job (an event); null to detach it.</summary>
    Task SetJobListAsync(long jobId, long? listId, CancellationToken ct);

    Task<long?> GetJobListAsync(long jobId, CancellationToken ct);

    /// <summary>The OCR readings of the photo become <paramref name="readings"/>.</summary>
    Task ReplaceReadingsAsync(long assetId, IReadOnlyList<OcrReading> readings, CancellationToken ct);

    Task<IReadOnlyList<OcrReading>> GetReadingsAsync(long assetId, CancellationToken ct);

    /// <summary>The automatic numbers of the photo become <paramref name="numbers"/>; a vehicle with a manual number keeps it (I5).</summary>
    Task ReplaceAutomaticNumbersAsync(long assetId, IReadOnlyList<VehicleNumber> numbers, CancellationToken ct);

    /// <summary>The user's number for a vehicle (null: no number), always kept.</summary>
    Task SetManualNumberAsync(long assetId, int vehicle, string? number, CancellationToken ct);

    Task<IReadOnlyList<VehicleNumber>> GetNumbersAsync(long assetId, CancellationToken ct);

    /// <summary>Numbers of every vehicle of the photos of a job.</summary>
    Task<IReadOnlyList<VehicleNumber>> GetJobNumbersAsync(long jobId, CancellationToken ct);

    /// <summary>Writes a vehicle's number exactly as given, or removes it when null (undo of a correction).</summary>
    Task PutNumberAsync(long assetId, int vehicle, VehicleNumber? number, CancellationToken ct);
}
