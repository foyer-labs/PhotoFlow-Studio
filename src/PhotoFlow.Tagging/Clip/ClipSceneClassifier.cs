// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;

namespace PhotoFlow.Tagging.Clip;

/// <summary>
/// Zero-shot scene profile with CLIP (D8): each built-in scene profile is described by a few English prompts (CLIP was
/// trained on English captions), their text embeddings are averaged (prompt ensembling), and the probabilities are the
/// softmax of 100 × cosine (CLIP's logit scale) over the profiles. The archive profile is not a scene and is never
/// proposed. Text embeddings are computed once.
/// </summary>
public sealed class ClipSceneClassifier(ITextEmbedder text) : ISceneClassifier
{
    public const double LogitScale = 100;

    /// <summary>Prompts per profile id (spec §6, built-in profiles).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Prompts = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["events"] = ["a photo of people at a wedding", "a photo of guests at a party", "a photo of a group of people at a ceremony", "a candid photo of people at an event"],
        ["portrait"] = ["a portrait photo of a person", "a close-up portrait of a face", "a studio portrait", "a headshot of a person"],
        ["motorsport"] = ["a photo of a racing car on a track", "a photo of a motorbike racing", "a photo of a rally car", "a motorsport photo"],
        ["animals"] = ["a photo of an animal", "a photo of a dog", "a photo of a bird", "a wildlife photo"],
        ["landscape"] = ["a landscape photo", "a photo of mountains", "a photo of the sea and the coast", "a photo of a city skyline"],
        ["night"] = ["a photo of the night sky with stars", "a photo of the northern lights", "a long exposure photo at night", "an astrophotography photo of the milky way"],
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<(string ProfileId, float[] Embedding)>? _profiles;

    public string ModelVersion => $"{text.ModelVersion}+scenes-v1";

    public async Task<IReadOnlyList<(string ProfileId, double Probability)>> ClassifyAsync(ReadOnlyMemory<float> imageEmbedding, CancellationToken ct)
    {
        var profiles = await ProfilesAsync(ct).ConfigureAwait(false);
        var logits = profiles.Select(p => LogitScale * Vectors.Cosine(imageEmbedding.Span, p.Embedding)).ToArray();
        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        var sum = exp.Sum();
        return profiles.Select((p, i) => (p.ProfileId, exp[i] / sum)).OrderByDescending(p => p.Item2).ToList();
    }

    private async Task<IReadOnlyList<(string ProfileId, float[] Embedding)>> ProfilesAsync(CancellationToken ct)
    {
        if (_profiles is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_profiles is null)
            {
                var profiles = new List<(string ProfileId, float[] Embedding)>();
                foreach (var (id, prompts) in Prompts)
                {
                    var embeddings = new List<float[]>();
                    foreach (var prompt in prompts)
                    {
                        embeddings.Add(await text.EmbedAsync(prompt, ct).ConfigureAwait(false));
                    }

                    profiles.Add((id, Vectors.Mean(embeddings)));
                }

                _profiles = profiles;
            }

            return _profiles;
        }
        finally
        {
            _gate.Release();
        }
    }
}
