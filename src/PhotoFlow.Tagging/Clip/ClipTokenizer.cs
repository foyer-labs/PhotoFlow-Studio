// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoFlow.Tagging.Clip;

/// <summary>
/// CLIP byte-level BPE tokenizer, read from the Hugging Face <c>tokenizer.json</c> of the model: NFC, whitespace
/// collapsed, lowercase; split with the CLIP pattern; bytes mapped to printable characters (GPT-2 table); BPE with the
/// <c>&lt;/w&gt;</c> end-of-word suffix; <c>&lt;|startoftext|&gt;</c> … <c>&lt;|endoftext|&gt;</c>, at most 77 ids.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int StartOfText = 49406;
    public const int EndOfText = 49407;
    public const int ContextLength = 77;
    private const string EndOfWord = "</w>";

    private static readonly string[] ByteToUnicode = BuildByteMap();

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly Dictionary<string, string[]> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ClipTokenizer(IReadOnlyDictionary<string, int> vocab, IReadOnlyList<(string Left, string Right)> merges)
    {
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(merges);
        _vocab = new Dictionary<string, int>(vocab, StringComparer.Ordinal);
        _ranks = new Dictionary<(string, string), int>(merges.Count);
        for (var i = 0; i < merges.Count; i++)
        {
            _ranks.TryAdd(merges[i], i);
        }
    }

    /// <summary>Loads vocabulary and merges from a Hugging Face <c>tokenizer.json</c> (BPE model).</summary>
    public static ClipTokenizer Load(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var document = JsonDocument.Parse(stream);
        var model = document.RootElement.GetProperty("model");
        var vocab = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        var merges = model.GetProperty("merges").EnumerateArray().Select(m =>
        {
            if (m.ValueKind == JsonValueKind.Array)
            {
                return (m[0].GetString()!, m[1].GetString()!);
            }

            var text = m.GetString()!;
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            return (text[..space], text[(space + 1)..]);
        }).ToList();
        return new ClipTokenizer(vocab, merges);
    }

    /// <summary>Token ids with start and end markers, truncated to <see cref="ContextLength"/> (the end marker kept).</summary>
    public IReadOnlyList<long> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = Whitespace().Replace(text.Normalize(NormalizationForm.FormC), " ").ToLower(CultureInfo.InvariantCulture);
        var ids = new List<long> { StartOfText };
        foreach (Match piece in Pieces().Matches(normalized))
        {
            if (piece.Value is "<|startoftext|>")
            {
                ids.Add(StartOfText);
                continue;
            }

            if (piece.Value is "<|endoftext|>")
            {
                ids.Add(EndOfText);
                continue;
            }

            var mapped = string.Concat(Encoding.UTF8.GetBytes(piece.Value).Select(b => ByteToUnicode[b]));
            foreach (var token in Bpe(mapped))
            {
                ids.Add(_vocab.TryGetValue(token, out var id) ? id : EndOfText);
            }
        }

        if (ids.Count >= ContextLength)
        {
            ids.RemoveRange(ContextLength - 1, ids.Count - (ContextLength - 1));
        }

        ids.Add(EndOfText);
        return ids;
    }

    private string[] Bpe(string word)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(word, out var cached))
            {
                return cached;
            }
        }

        // Symbols are text elements of the mapped word (one per byte); the last carries the end-of-word suffix.
        var symbols = word.Select(c => c.ToString()).ToList();
        symbols[^1] += EndOfWord;
        while (symbols.Count > 1)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (_ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            // Merge every occurrence of the best pair, left to right (as the reference implementation).
            var (left, right) = (symbols[best], symbols[best + 1]);
            var merged = new List<string>(symbols.Count);
            for (var i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == left && symbols[i + 1] == right)
                {
                    merged.Add(left + right);
                    i++;
                }
                else
                {
                    merged.Add(symbols[i]);
                }
            }

            symbols = merged;
        }

        var result = symbols.ToArray();
        lock (_gate)
        {
            _cache[word] = result;
        }

        return result;
    }

    /// <summary>GPT-2 <c>bytes_to_unicode</c>: printable bytes map to themselves, the others to U+0100 onwards.</summary>
    private static string[] BuildByteMap()
    {
        var printable = Enumerable.Range('!', '~' - '!' + 1).Concat(Enumerable.Range('¡', '¬' - '¡' + 1)).Concat(Enumerable.Range('®', 'ÿ' - '®' + 1)).ToHashSet();
        var map = new string[256];
        var next = 0;
        for (var b = 0; b < 256; b++)
        {
            map[b] = printable.Contains(b) ? ((char)b).ToString() : ((char)(256 + next++)).ToString();
        }

        return map;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+")]
    private static partial Regex Pieces();
}
