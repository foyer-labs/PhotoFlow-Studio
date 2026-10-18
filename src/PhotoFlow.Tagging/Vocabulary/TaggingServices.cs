// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Catalog;
using PhotoFlow.Core.Pipeline;
using PhotoFlow.Core.Tagging;

namespace PhotoFlow.Tagging.Vocabulary;

/// <summary>The RAM++ vocabulary shipped with the application: keys, Italian labels to review, default categories (F3-2, F3-3).</summary>
public static class RamPlusVocabulary
{
    private const string Resource = "PhotoFlow.Vocabulary.ramplus.tsv";
    private const string BlacklistResource = "PhotoFlow.Vocabulary.blacklist.txt";

    public static IReadOnlyList<VocabularyEntry> Entries { get; } = Load();

    private static List<VocabularyEntry> Load()
    {
        var blacklist = Lines(BlacklistResource).ToHashSet(StringComparer.Ordinal);
        using var stream = typeof(RamPlusVocabulary).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Missing resource {Resource}.");
        using var reader = new StreamReader(stream);
        var entries = new List<VocabularyEntry>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            entries.Add(new VocabularyEntry(parts[0], parts.Length > 1 ? parts[1] : parts[0], parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null, blacklist.Contains(parts[0])));
        }

        return entries;
    }

    private static IEnumerable<string> Lines(string resource)
    {
        using var stream = typeof(RamPlusVocabulary).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}.");
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0 && !line.StartsWith('#'))
            {
                lines.Add(line.Trim());
            }
        }

        return lines;
    }
}

/// <summary>A personal tag with the score of a photo.</summary>
public sealed record CustomTagScore(string Key, double Score);

/// <summary>
/// Level A (spec §5, §8): personal tags recognized by CLIP zero-shot on the stored image embedding (no new inference).
/// The score is the cosine between the image and the English description; a tag is assigned when its score reaches the
/// threshold and beats the best other personal tag by at least its margin. Text embeddings are computed once.
/// </summary>
public sealed class CustomTagClassifier(ITextEmbedder text)
{
    private readonly ConcurrentDictionary<string, float[]> _prompts = new(StringComparer.Ordinal);

    public string ModelVersion => text.ModelVersion + "+custom-v1";

    public async Task<IReadOnlyList<CustomTagScore>> ScoreAsync(ReadOnlyMemory<float> image, IReadOnlyList<(Tag Tag, CustomTag Custom)> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var scores = new List<CustomTagScore>(tags.Count);
        foreach (var (tag, custom) in tags)
        {
            if (!_prompts.TryGetValue(custom.ClipDescription, out var prompt))
            {
                prompt = Vectors.Normalize(await text.EmbedAsync(custom.ClipDescription, ct).ConfigureAwait(false));
                _prompts[custom.ClipDescription] = prompt;
            }

            scores.Add(new CustomTagScore(tag.Key, Vectors.Cosine(image.Span, prompt)));
        }

        return scores;
    }

    /// <summary>The tags assigned from the scores (threshold, then margin over the best other personal tag).</summary>
    public static IReadOnlyList<CustomTagScore> Assign(IReadOnlyList<CustomTagScore> scores, IReadOnlyList<(Tag Tag, CustomTag Custom)> tags)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(tags);
        var assigned = new List<CustomTagScore>();
        for (var i = 0; i < scores.Count; i++)
        {
            var custom = tags[i].Custom;
            var second = scores.Where((_, j) => j != i).Select(s => s.Score).DefaultIfEmpty(double.NegativeInfinity).Max();
            if (scores[i].Score >= custom.Threshold && scores[i].Score - second >= custom.Margin)
            {
                assigned.Add(scores[i]);
            }
        }

        return assigned;
    }
}

/// <summary>Runs the personal tags over photos with an embedding: during a job, after a new tag, or as a trial (board 5d).</summary>
public sealed class CustomTagScan(ICatalog catalog, CustomTagClassifier classifier, string embeddingModelVersion)
{
    /// <summary>Recomputes the personal tags of these photos (all the photos with an embedding when null).</summary>
    public async Task<int> RunAsync(IReadOnlyCollection<long>? assetIds, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var tags = await catalog.Tags.ListCustomAsync(ct).ConfigureAwait(false);
        var ids = assetIds ?? await catalog.Analysis.ListEmbeddedAssetsAsync(embeddingModelVersion, ct).ConfigureAwait(false);
        var done = 0;
        var tagged = 0;
        foreach (var chunk in ids.Chunk(500))
        {
            var embeddings = await catalog.Analysis.GetEmbeddingsAsync(chunk, embeddingModelVersion, ct).ConfigureAwait(false);
            foreach (var (assetId, vector) in embeddings)
            {
                var assigned = tags.Count == 0 ? [] : CustomTagClassifier.Assign(await classifier.ScoreAsync(vector, tags, ct).ConfigureAwait(false), tags);
                await catalog.Tags.ReplaceAutomaticAsync(assetId, TagSource.ClipZeroShot, classifier.ModelVersion, assigned.Select(a => (a.Key, (double?)a.Score)).ToList(), CancellationToken.None).ConfigureAwait(false);
                tagged += assigned.Count > 0 ? 1 : 0;
            }

            done += chunk.Length;
            progress?.Report((done, ids.Count));
        }

        return tagged;
    }

    /// <summary>"Prova sull'archivio": how many photos the tag would get, without saving anything.</summary>
    public async Task<int> TrialAsync(Tag tag, CustomTag custom, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(custom);
        var others = (await catalog.Tags.ListCustomAsync(ct).ConfigureAwait(false)).Where(t => t.Tag.Key != tag.Key).ToList();
        IReadOnlyList<(Tag Tag, CustomTag Custom)> tags = [(tag, custom), .. others];
        var ids = await catalog.Analysis.ListEmbeddedAssetsAsync(embeddingModelVersion, ct).ConfigureAwait(false);
        var count = 0;
        foreach (var chunk in ids.Chunk(500))
        {
            foreach (var (_, vector) in await catalog.Analysis.GetEmbeddingsAsync(chunk, embeddingModelVersion, ct).ConfigureAwait(false))
            {
                if (CustomTagClassifier.Assign(await classifier.ScoreAsync(vector, tags, ct).ConfigureAwait(false), tags).Any(a => a.Key == tag.Key))
                {
                    count++;
                }
            }
        }

        return count;
    }
}

/// <summary>
/// Batch stage of a job (spec §3, phase 6): the event name becomes a tag of every photo (source Job, category
/// Eventi), then the personal CLIP tags (level A) when the job asks for them. RAM++ (level B) ran per photo.
/// </summary>
public sealed class TaggingStage(ICatalog catalog, Func<CancellationToken, Task<CustomTagScan?>> customScan) : IBatchStage
{
    public const string EventPrefix = "event:";
    public const string EventCategory = "Eventi";

    public PipelineStage Stage => PipelineStage.TaggingAndOcr;

    public async Task RunAsync(JobContext job, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var record = await catalog.Jobs.GetAsync(job.JobId, ct).ConfigureAwait(false);
        var assets = (await catalog.Jobs.GetItemsAsync(job.JobId, ct).ConfigureAwait(false))
            .Where(i => i.State == JobItemState.Done && i.AssetId is not null).Select(i => i.AssetId!.Value).Distinct().ToList();
        if (record is null || assets.Count == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(record.EventName))
        {
            var key = EventPrefix + record.EventName.Trim();
            await catalog.Tags.EnsureAsync(key, TagSource.Job, EventCategory, record.EventName.Trim(), reviewed: true, ct).ConfigureAwait(false);
            foreach (var assetId in assets)
            {
                await catalog.Tags.ReplaceAutomaticAsync(assetId, TagSource.Job, null, [(key, null)], CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (Levels(record).HasFlag(TagLevels.ZeroShot) && await customScan(ct).ConfigureAwait(false) is { } scan)
        {
            await scan.RunAsync(assets, new Progress<(int Done, int Total)>(p => progress?.Report(new StageProgress(Stage, p.Done, p.Total, null))), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The levels chosen for the job (its settings, camelCase JSON); the default when not recorded.</summary>
    public static TagLevels Levels(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            using var document = JsonDocument.Parse(job.SettingsSnapshotJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Number
                ? (TagLevels)tags.GetInt32()
                : TagLevels.Default;
        }
        catch (JsonException)
        {
            return TagLevels.Default;
        }
    }
}
