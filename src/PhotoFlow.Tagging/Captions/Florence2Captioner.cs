// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Imaging;
using PhotoFlow.Core.Inference;
using PhotoFlow.Core.Tagging;

namespace PhotoFlow.Tagging.Captions;

/// <summary>
/// Florence-2 captions (level C, spec §8, decision F5-1): adapters for the four ONNX parts of
/// <c>onnx-community/Florence-2-base-ft</c> on <see cref="IInferenceProvider"/>.
/// <list type="number">
/// <item>The image is resized to 768×768 (Pillow bicubic, aspect not kept) and normalized with the ImageNet mean and
/// standard deviation; the vision encoder gives 577 image features.</item>
/// <item>The task prompt "&lt;DETAILED_CAPTION&gt;" becomes "Describe in detail what is shown in the image." (token ids
/// below, from the original processor). Its embeddings follow the image features in the input of the text encoder.</item>
/// <item>The decoder generates with beam search exactly as transformers' <c>generate</c> with the model's configuration:
/// 3 beams, 6 candidates per step, no repeated trigram, forced &lt;s&gt; first and &lt;/s&gt; at the maximum length,
/// scores divided by the generated length, early stopping when 3 sentences are finished.</item>
/// </list>
/// Parity: the same token ids as the original PyTorch model (<c>tools/lab/florence_refs.py</c>, <c>CaptionParityTests</c>).
/// </summary>
public sealed class Florence2Captioner : IImageCaptioner
{
    public const string AdapterId = "florence2-v1";
    public const string VisionFile = "vision_encoder_pf.onnx";
    public const string EmbedFile = "embed_tokens.onnx";
    public const string EncoderFile = "encoder_model.onnx";
    public const string DecoderFile = "decoder_model_merged.onnx";
    public const string TokenizerFile = "tokenizer.json";

    public const int InputSize = 768;
    public const int NumBeams = 3;
    public const int NoRepeatNgram = 3;
    public const int MaxNewTokens = 256;
    public const long BosId = 0;
    public const long PadId = 1;
    public const long EosId = 2;
    public const long DecoderStartId = 2;

    /// <summary>"&lt;s&gt;Describe in detail what is shown in the image.&lt;/s&gt;" (task &lt;DETAILED_CAPTION&gt;).</summary>
    public static readonly long[] DetailedCaptionPrompt = [0, 47066, 21700, 11, 4617, 99, 16, 2343, 11, 5, 2274, 4, 2];

    private const int Layers = 6;
    private const int Heads = 12;
    private const int HeadSize = 64;
    private const int Hidden = 768;
    private const float Masked = -1e9f;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly IInferenceSession _vision;
    private readonly IInferenceSession _embed;
    private readonly IInferenceSession _encoder;
    private readonly IInferenceSession _decoder;
    private readonly ByteLevelDecoder _tokenizer;
    private readonly IImageOperations _imageOperations;

    public Florence2Captioner(IInferenceSession vision, IInferenceSession embed, IInferenceSession encoder, IInferenceSession decoder,
        ByteLevelDecoder tokenizer, IImageOperations imageOperations)
    {
        _vision = vision ?? throw new ArgumentNullException(nameof(vision));
        _embed = embed ?? throw new ArgumentNullException(nameof(embed));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _imageOperations = imageOperations ?? throw new ArgumentNullException(nameof(imageOperations));
    }

    public string ModelVersion => $"{_vision.Model.Id}@{_vision.Model.Version}";

    public string Language => "en";

    public async Task<string> CaptionAsync(RgbImage image, CancellationToken ct) =>
        _tokenizer.Decode(await GenerateAsync(image, ct).ConfigureAwait(false)).Trim();

    /// <summary>The <c>pixel_values</c> tensor [1,3,768,768].</summary>
    public static NamedTensor PrepareInput(RgbImage image, IImageOperations imageOperations)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(imageOperations);
        var resized = imageOperations.Resize(image, InputSize, InputSize, ResampleMode.Bicubic);
        const int plane = InputSize * InputSize;
        var data = new float[plane * 3];
        var pixels = resized.Pixels.Span;
        for (var i = 0; i < plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                // rescale in double then float32, normalize in float32 (as transformers' numpy pipeline).
                var rescaled = (float)(pixels[(i * 3) + c] * (1 / 255.0));
                data[(c * plane) + i] = (rescaled - Mean[c]) / Std[c];
            }
        }

        return NamedTensor.Float32("pixel_values", data, 1, 3, InputSize, InputSize);
    }

    /// <summary>The generated token ids: the decoder start token, &lt;s&gt;, the caption, &lt;/s&gt;.</summary>
    public async Task<long[]> GenerateAsync(RgbImage image, CancellationToken ct)
    {
        // Encoder: image features followed by the prompt embeddings.
        var features = (await _vision.RunAsync([PrepareInput(image, _imageOperations)], ct).ConfigureAwait(false))[0];
        var prompt = await EmbedAsync(DetailedCaptionPrompt, 1, ct).ConfigureAwait(false);
        var imageTokens = features.Shape[1];
        var length = imageTokens + DetailedCaptionPrompt.Length;
        var input = new float[length * Hidden];
        features.AsFloat32().Span.CopyTo(input);
        prompt.CopyTo(input.AsSpan(imageTokens * Hidden));
        var mask = Enumerable.Repeat(1L, length).ToArray();
        var encoded = (await _encoder.RunAsync(
            [NamedTensor.Int64("attention_mask", mask, 1, length), NamedTensor.Float32("inputs_embeds", input, 1, length, Hidden)],
            ["last_hidden_state"], ct).ConfigureAwait(false))[0].AsFloat32();

        // The beams share the encoder output.
        var states = new float[NumBeams * length * Hidden];
        var beamMask = new long[NumBeams * length];
        Array.Fill(beamMask, 1L);
        for (var b = 0; b < NumBeams; b++)
        {
            encoded.Span.CopyTo(states.AsSpan(b * length * Hidden));
        }

        return await BeamSearchAsync(NamedTensor.Float32("encoder_hidden_states", states, NumBeams, length, Hidden),
            NamedTensor.Int64("encoder_attention_mask", beamMask, NumBeams, length), ct).ConfigureAwait(false);
    }

    private async Task<float[]> EmbedAsync(long[] ids, int batch, CancellationToken ct) =>
        (await _embed.RunAsync([NamedTensor.Int64("input_ids", ids, batch, ids.Length / batch)], ct).ConfigureAwait(false))[0].AsFloat32().ToArray();

    private async Task<long[]> BeamSearchAsync(NamedTensor states, NamedTensor statesMask, CancellationToken ct)
    {
        const int candidates = 2 * NumBeams;
        var maxLength = 1 + MaxNewTokens;
        var running = new long[NumBeams][];
        var finished = new long[NumBeams][];
        for (var b = 0; b < NumBeams; b++)
        {
            running[b] = new long[maxLength];
            finished[b] = new long[maxLength];
            Array.Fill(running[b], PadId);
            Array.Fill(finished[b], PadId);
            running[b][0] = DecoderStartId;
            finished[b][0] = DecoderStartId;
        }

        var runningScores = new float[NumBeams];
        runningScores.AsSpan(1).Fill(Masked);
        var finishedScores = Enumerable.Repeat(Masked, NumBeams).ToArray();
        var isFinished = new bool[NumBeams];
        var improvementPossible = true;
        var curLen = 1;

        // Self-attention cache of the decoder (grows by one per step) and cross-attention cache (from the first step).
        var selfCache = new NamedTensor[Layers * 2];
        var crossCache = new NamedTensor[Layers * 2];
        for (var l = 0; l < Layers; l++)
        {
            foreach (var (i, kind) in new[] { (0, "key"), (1, "value") })
            {
                selfCache[(l * 2) + i] = NamedTensor.Float32($"past_key_values.{l}.decoder.{kind}", Array.Empty<float>(), NumBeams, Heads, 0, HeadSize);
                crossCache[(l * 2) + i] = NamedTensor.Float32($"past_key_values.{l}.encoder.{kind}", Array.Empty<float>(), NumBeams, Heads, 0, HeadSize);
            }
        }

        var selfOutputs = Enumerable.Range(0, Layers).SelectMany(l => new[] { $"present.{l}.decoder.key", $"present.{l}.decoder.value" }).ToArray();
        var crossOutputs = Enumerable.Range(0, Layers).SelectMany(l => new[] { $"present.{l}.encoder.key", $"present.{l}.encoder.value" }).ToArray();
        while (true)
        {
            // Decoder step on the last token of every beam.
            var last = running.Select(r => r[curLen - 1]).ToArray();
            var embeds = await EmbedAsync(last, NumBeams, ct).ConfigureAwait(false);
            var first = curLen == 1;
            var inputs = new List<NamedTensor>
            {
                statesMask,
                states,
                NamedTensor.Float32("inputs_embeds", embeds, NumBeams, 1, Hidden),
                NamedTensor.Bool("use_cache_branch", new[] { !first }, 1),
            };
            for (var l = 0; l < Layers; l++)
            {
                inputs.AddRange([selfCache[l * 2], selfCache[(l * 2) + 1], crossCache[l * 2], crossCache[(l * 2) + 1]]);
            }

            var outputNames = first ? ["logits", .. selfOutputs, .. crossOutputs] : new[] { "logits" }.Concat(selfOutputs).ToArray();
            var outputs = await _decoder.RunAsync(inputs, outputNames, ct).ConfigureAwait(false);
            var logits = outputs[0];
            var vocabulary = logits.Shape[^1];
            if (first)
            {
                for (var i = 0; i < Layers * 2; i++)
                {
                    crossCache[i] = Rename(outputs[1 + selfOutputs.Length + i], crossCache[i].Name);
                }
            }

            // Log-probabilities with the logits processors, plus the running beam scores.
            var scores = new float[NumBeams * vocabulary];
            for (var b = 0; b < NumBeams; b++)
            {
                var row = scores.AsSpan(b * vocabulary, vocabulary);
                LogSoftmax(logits.AsFloat32().Span.Slice(b * vocabulary, vocabulary), row);
                Process(row, running[b].AsSpan(0, curLen), curLen, maxLength);
                for (var v = 0; v < vocabulary; v++)
                {
                    row[v] += runningScores[b];
                }
            }

            // The 6 best continuations over all beams.
            var top = TopK(scores, candidates);
            var topSequences = new long[candidates][];
            var topBeams = new int[candidates];
            var topScores = new float[candidates];
            var hits = new bool[candidates];
            for (var k = 0; k < candidates; k++)
            {
                topBeams[k] = top[k] / vocabulary;
                var token = top[k] % vocabulary;
                topSequences[k] = (long[])running[topBeams[k]].Clone();
                topSequences[k][curLen] = token;
                topScores[k] = scores[top[k]];
                hits[k] = token == EosId || curLen + 1 >= maxLength;
            }

            // Running beams for the next step: the best 3 that did not finish.
            var runningCandidates = topScores.Select((s, k) => hits[k] ? s + Masked : s).ToArray();
            var next = TopK(runningCandidates, NumBeams);
            var sourceBeams = new int[NumBeams];
            for (var b = 0; b < NumBeams; b++)
            {
                running[b] = topSequences[next[b]];
                runningScores[b] = runningCandidates[next[b]];
                sourceBeams[b] = topBeams[next[b]];
            }

            // Finished sentences: only the first 3 candidates count, score divided by the generated length.
            var allFinished = isFinished.All(f => f);
            var merged = new List<(long[] Sequence, float Score, bool Finished)>();
            for (var b = 0; b < NumBeams; b++)
            {
                merged.Add((finished[b], finishedScores[b], isFinished[b]));
            }

            for (var k = 0; k < candidates; k++)
            {
                var did = hits[k] && k < NumBeams;
                var score = topScores[k] / curLen;
                score += allFinished ? Masked : 0;
                score += improvementPossible ? 0 : Masked;
                score += did ? 0 : Masked;
                merged.Add((topSequences[k], score, did));
            }

            var best = TopK(merged.Select(m => m.Score).ToArray(), NumBeams);
            for (var b = 0; b < NumBeams; b++)
            {
                (finished[b], finishedScores[b], isFinished[b]) = merged[best[b]];
            }

            // The decoder cache follows the beams that continue.
            for (var i = 0; i < Layers * 2; i++)
            {
                selfCache[i] = Reorder(outputs[1 + i], selfCache[i].Name, sourceBeams);
            }

            curLen++;

            // Early stop heuristic, then the stopping conditions.
            var bestRunning = runningScores[0] / (curLen - 1);
            var worstFinished = finishedScores.Min();
            improvementPossible &= isFinished.Any(f => bestRunning > (f ? worstFinished : Masked));
            var openBeam = !isFinished.All(f => f);
            if (!improvementPossible || !openBeam || hits.All(h => h))
            {
                break;
            }
        }

        var result = finished[0];
        var end = Array.IndexOf(result, EosId, 1);
        return end < 0 ? result.TakeWhile((t, i) => i < curLen).ToArray() : result[..(end + 1)];
    }

    /// <summary>No repeated trigram, &lt;s&gt; first, &lt;/s&gt; at the maximum length (transformers' logits processors).</summary>
    private static void Process(Span<float> row, ReadOnlySpan<long> tokens, int curLen, int maxLength)
    {
        if (curLen + 1 >= NoRepeatNgram)
        {
            var (a, b) = (tokens[curLen - 2], tokens[curLen - 1]);
            for (var i = 0; i + NoRepeatNgram <= curLen; i++)
            {
                if (tokens[i] == a && tokens[i + 1] == b)
                {
                    row[(int)tokens[i + 2]] = float.NegativeInfinity;
                }
            }
        }

        if (curLen == 1)
        {
            Force(row, BosId);
        }

        if (curLen == maxLength - 1)
        {
            Force(row, EosId);
        }

        static void Force(Span<float> row, long token)
        {
            row.Fill(float.NegativeInfinity);
            row[(int)token] = 0;
        }
    }

    private static void LogSoftmax(ReadOnlySpan<float> logits, Span<float> result)
    {
        var max = float.NegativeInfinity;
        foreach (var value in logits)
        {
            max = Math.Max(max, value);
        }

        double sum = 0;
        foreach (var value in logits)
        {
            sum += Math.Exp(value - max);
        }

        var log = (float)Math.Log(sum);
        for (var i = 0; i < logits.Length; i++)
        {
            result[i] = logits[i] - max - log;
        }
    }

    /// <summary>Indices of the k largest values, largest first (lower index first on ties, as torch.topk on CPU).</summary>
    private static int[] TopK(float[] values, int k)
    {
        var best = new List<int>(k + 1);
        for (var i = 0; i < values.Length; i++)
        {
            if (best.Count == k && !(values[i] > values[best[^1]]))
            {
                continue;
            }

            var position = best.Count;
            while (position > 0 && values[i] > values[best[position - 1]])
            {
                position--;
            }

            best.Insert(position, i);
            if (best.Count > k)
            {
                best.RemoveAt(k);
            }
        }

        return [.. best];
    }

    private static NamedTensor Rename(NamedTensor tensor, string name) =>
        NamedTensor.Float32(name, tensor.AsFloat32(), [.. tensor.Shape]);

    /// <summary>The cache rows of the beams that continue, in their new order.</summary>
    private static NamedTensor Reorder(NamedTensor present, string name, int[] beams)
    {
        var data = present.AsFloat32().Span;
        var rowSize = data.Length / NumBeams;
        var result = new float[data.Length];
        for (var b = 0; b < NumBeams; b++)
        {
            data.Slice(beams[b] * rowSize, rowSize).CopyTo(result.AsSpan(b * rowSize));
        }

        return NamedTensor.Float32(name, result, [.. present.Shape]);
    }
}
