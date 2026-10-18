// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Analysis;
using PhotoFlow.Core.Inference;

namespace PhotoFlow.Tagging.Clip;

/// <summary>
/// Adapter for the CLIP ViT-B/32 text model (Xenova ONNX conversion, fp32): <c>input_ids</c> int64 [1,L] without
/// padding (the model pools at the end-of-text token) → <c>text_embeds</c> (512), L2-normalized.
/// </summary>
public sealed class ClipTextEmbedder(IInferenceSession session, ClipTokenizer tokenizer) : ITextEmbedder
{
    public const string InputName = "input_ids";
    public const string OutputName = "text_embeds";

    public string ModelVersion => $"{session.Model.Id}@{session.Model.Version}";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var ids = tokenizer.Encode(text).ToArray();
        var outputs = await session.RunAsync([NamedTensor.Int64(InputName, ids, 1, ids.Length)], ct).ConfigureAwait(false);
        return Vectors.Normalize(outputs.First(o => o.Name == OutputName).AsFloat32().Span[..512]);
    }
}
