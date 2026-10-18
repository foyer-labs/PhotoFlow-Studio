// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Core.Search;

/// <summary>
/// Search over the catalog (spec §3, §8, P14; decision F3-4): exact filters (people, tags, dates, camera, rating,
/// outcome), free text matched on names and labels (full text) and ordered by meaning (CLIP text embedding against the
/// image embeddings), and "find similar". Behind this interface so that a PostgreSQL implementation can replace SQLite.
/// </summary>
public interface ISearchIndex
{
    Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken ct);

    /// <summary>
    /// Facets of Consultazione: for every active tag, how many photos of the result of <paramref name="query"/> have it,
    /// leaving out the photos also in the result of <paramref name="excluding"/> (the "+N" of an OR).
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> CountTagsAsync(SearchQuery query, SearchQuery? excluding, CancellationToken ct);

    /// <summary>Facets of Consultazione: for every person, on how many photos of the result of <paramref name="query"/> it appears.</summary>
    Task<IReadOnlyDictionary<long, int>> CountPersonsAsync(SearchQuery query, CancellationToken ct);

    /// <summary>Tags and people of the result of <paramref name="query"/> counted together (the result is computed once).</summary>
    Task<FacetCounts> CountFacetsAsync(SearchQuery query, CancellationToken ct);

    /// <summary>Photos with an embedding of <paramref name="embeddingModelVersion"/> and photos in the catalog (4e2: index being built).</summary>
    Task<(int Embedded, int Total)> CoverageAsync(string embeddingModelVersion, CancellationToken ct);
}

/// <param name="PersonIds">Exact filter: every person on the photo.</param>
/// <param name="TagIds">Exact filter: every tag active on the photo (events are tags too).</param>
/// <param name="Text">Free text: full-text words (prefixes) on file names, folders, tags, people, and meaning when <paramref name="TextEmbedding"/> is given.</param>
/// <param name="TextEmbedding">CLIP embedding of <paramref name="Text"/> (L2-normalized), computed by the caller.</param>
/// <param name="SimilarTo">"Trova simili": ordered by similarity to this photo's embedding.</param>
/// <param name="EmbeddingModelVersion">Embeddings to use for meaning and similarity.</param>
/// <param name="TagGroups">Groups of tags of the facets: a photo has at least one tag of each group (OR inside a group, AND between groups).</param>
public sealed record SearchQuery(
    IReadOnlyList<long> PersonIds,
    IReadOnlyList<long> TagIds,
    string? Text = null,
    float[]? TextEmbedding = null,
    long? SimilarTo = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Camera = null,
    int? MinRating = null,
    CullingOutcome? Outcome = null,
    string? EmbeddingModelVersion = null,
    int Limit = 5000,
    IReadOnlyList<IReadOnlyList<long>>? TagGroups = null)
{
    public static SearchQuery Empty { get; } = new([], []);
}

/// <summary>Photos per tag and per person of a result (facets of Consultazione).</summary>
public sealed record FacetCounts(IReadOnlyDictionary<long, int> Tags, IReadOnlyDictionary<long, int> Persons);

public sealed record SearchHit(long AssetId, double Score);

/// <param name="Total">Photos matching, before <see cref="SearchQuery.Limit"/>.</param>
/// <param name="ByMeaning">The order is by meaning (free text with an embedding, or similar photos).</param>
public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, int Total, bool ByMeaning, TimeSpan Elapsed);
