// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Core.Pipeline;

/// <summary>What a regeneration computes again (spec §5, "Cosa": selectable).</summary>
[Flags]
public enum ReanalysisParts
{
    None = 0,

    /// <summary>Culling scores and the automatic proposal (the user's decisions stay).</summary>
    Culling = 1,

    Embedding = 2,

    /// <summary>Faces and fingerprints (new face model: assignments transferred, spec §5).</summary>
    Faces = 4,

    /// <summary>Tags of level B (manual and suppressed tags stay, I5).</summary>
    Tags = 8,

    Captions = 16,

    Ocr = 32,

    All = Culling | Embedding | Faces | Tags | Captions | Ocr,
}

public enum ReanalysisMode
{
    /// <summary>Only the results made by a model older than the current one (default).</summary>
    OlderModelOnly,

    ForceAll,
}

/// <summary>
/// A regeneration (job <c>Reanalyze</c>, spec §5, D14). Perimeter: a library, a folder of it (relative path prefix),
/// the photos of a job, chosen photos (a search result, one photo), or the whole catalog (P22).
/// </summary>
/// <param name="ExcludeReviewed">Leave out the photos already reviewed (a decision of the user or an XMP written).</param>
/// <param name="Sample">A trial on that many photos of the perimeter, chosen at random (repeatable).</param>
/// <param name="AllCatalog">Perimeter: every library of the catalog (when no library, job or photos are given).</param>
/// <param name="ResetPeople">
/// "Ripartire da zero con le persone" (P22, the user's explicit choice, confirmed with the counts): when the new faces
/// are applied, every person and assignment of the catalog goes first, manual ones included. Never on a sample.
/// </param>
public sealed record ReanalysisSettings(
    ReanalysisParts Parts,
    ReanalysisMode Mode = ReanalysisMode.OlderModelOnly,
    long? LibraryId = null,
    string? Folder = null,
    long? SourceJobId = null,
    IReadOnlyList<long>? AssetIds = null,
    bool ExcludeReviewed = false,
    int? Sample = null,
    string ProfileId = "events",
    int Workers = 1,
    bool AllCatalog = false,
    bool ResetPeople = false);

/// <summary>New results of a photo waiting for confirmation (one JSON per part).</summary>
public sealed record StagedCulling(Analysis.AnalyzerResult Analysis, string ProfileId, Analysis.DecisionProposal Decision, string DecisionVersion);

/// <summary>The versions the current models would record (to find the results of older models).</summary>
/// <param name="AnalyzerId">Id of the culling analyzer whose results carry <paramref name="Culling"/>.</param>
/// <param name="VlmCaptions">Version of the level D captioner (phase 5b), when available.</param>
/// <param name="CaptionsLanguage">Language of the level C captions: a caption is compared with the model of its language.</param>
/// <param name="VlmCaptionsLanguage">Language of the level D captions.</param>
public sealed record ModelVersions(string AnalyzerId, string? Culling, string? Embedding, string? Fingerprints, string? Tags, string? Captions, string? Ocr,
    string? VlmCaptions = null, string CaptionsLanguage = "en", string VlmCaptionsLanguage = "it")
{
    /// <summary>
    /// Which captioners a regeneration runs on a photo: the one of a language whose automatic caption an older model
    /// wrote (with "forza tutto", every captioner available). A language without a current model is left alone.
    /// </summary>
    public (bool Captions, bool VlmCaptions) CaptionsToRedo(IEnumerable<Caption> captions, bool forceAll)
    {
        var automatic = captions.Where(c => c.Origin == CaptionOrigin.Automatic).ToList();
        bool Redo(string? version, string language) => version is not null
            && (forceAll || automatic.Any(c => c.Language == language && c.ModelVersion != version));
        return (Redo(Captions, CaptionsLanguage), Redo(VlmCaptions, VlmCaptionsLanguage));
    }
}

/// <summary>Differences between the staged results of a regeneration and the catalog (spec §5: "Anteprima delle differenze").</summary>
public sealed record ReanalysisPreview(
    int Photos,
    int OutcomesChanged,
    int UserDecisionsKept,
    int TagsAdded,
    int TagsRemoved,
    int TagsProtected,
    int FacesTransferred,
    int FacesNew,
    int FacesLost,
    int FacesKeptManual,
    int CaptionsChanged,
    int CaptionsProtected,
    int Embeddings,
    int OcrChanged,
    IReadOnlyList<string> Examples);
