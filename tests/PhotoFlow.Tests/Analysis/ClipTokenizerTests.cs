// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Tagging.Clip;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>The byte-level BPE of the CLIP tokenizer on a tiny vocabulary (the real one is checked in the parity tests).</summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class ClipTokenizerTests
{
    private static readonly ClipTokenizer Tokenizer = new(
        new Dictionary<string, int>
        {
            ["c"] = 1, ["a"] = 2, ["t"] = 3, ["ca"] = 4, ["t</w>"] = 5, ["cat</w>"] = 6, ["!</w>"] = 7, ["a</w>"] = 8, ["Ã</w>"] = 9,
        },
        [("c", "a"), ("ca", "t</w>")]);

    [Fact]
    public void Words_are_merged_by_rank_with_the_end_of_word_suffix()
    {
        Assert.Equal([ClipTokenizer.StartOfText, 6, 6, 7, ClipTokenizer.EndOfText], Tokenizer.Encode("Cat  CAT!"));
    }

    [Fact]
    public void Unknown_symbols_become_end_of_text_and_long_texts_are_truncated_to_77()
    {
        Assert.Equal([ClipTokenizer.StartOfText, 8, ClipTokenizer.EndOfText], Tokenizer.Encode("a"));
        var ids = Tokenizer.Encode(string.Join(' ', Enumerable.Repeat("cat", 200)));
        Assert.Equal(ClipTokenizer.ContextLength, ids.Count);
        Assert.Equal(ClipTokenizer.EndOfText, ids[^1]);
    }
}
