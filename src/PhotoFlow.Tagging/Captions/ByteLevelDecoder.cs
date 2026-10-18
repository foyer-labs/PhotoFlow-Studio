// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace PhotoFlow.Tagging.Captions;

/// <summary>
/// Decoding of a byte-level BPE vocabulary (GPT-2/BART, Hugging Face <c>tokenizer.json</c>): token ids → the
/// characters of their tokens → the bytes those characters stand for (GPT-2 <c>bytes_to_unicode</c>) → UTF-8 text.
/// Special and added tokens (Florence-2's &lt;loc_*&gt;, &lt;s&gt;, &lt;/s&gt;, …) are skipped, as
/// <c>skip_special_tokens</c>; invalid UTF-8 becomes U+FFFD (<c>errors="replace"</c>).
/// </summary>
public sealed class ByteLevelDecoder
{
    private readonly string?[] _tokens;
    private readonly bool[] _special;
    private readonly Dictionary<char, byte> _bytes;

    private ByteLevelDecoder(string?[] tokens, bool[] special)
    {
        _tokens = tokens;
        _special = special;
        _bytes = BytesToUnicode().ToDictionary(p => p.Value, p => p.Key);
    }

    public int Count => _tokens.Length;

    /// <summary>Reads the vocabulary and the added tokens of a Hugging Face <c>tokenizer.json</c>.</summary>
    public static ByteLevelDecoder Load(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var entries = new Dictionary<int, (string Token, bool Special)>();
        foreach (var token in root.GetProperty("model").GetProperty("vocab").EnumerateObject())
        {
            entries[token.Value.GetInt32()] = (token.Name, false);
        }

        foreach (var added in root.GetProperty("added_tokens").EnumerateArray())
        {
            entries[added.GetProperty("id").GetInt32()] = (added.GetProperty("content").GetString()!, true);
        }

        var size = entries.Keys.Max() + 1;
        var tokens = new string?[size];
        var special = new bool[size];
        foreach (var (id, (token, isSpecial)) in entries)
        {
            tokens[id] = token;
            special[id] = isSpecial;
        }

        return new ByteLevelDecoder(tokens, special);
    }

    public string Decode(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (id < 0 || id >= _tokens.Length || _special[id] || _tokens[id] is not { } token)
            {
                continue;
            }

            foreach (var c in token)
            {
                if (_bytes.TryGetValue(c, out var b))
                {
                    bytes.Add(b);
                }
            }
        }

        return new UTF8Encoding(false, false).GetString([.. bytes]);
    }

    /// <summary>GPT-2's reversible map from the 256 byte values to printable characters.</summary>
    private static Dictionary<byte, char> BytesToUnicode()
    {
        var printable = Enumerable.Range('!', '~' - '!' + 1).Concat(Enumerable.Range('¡', '¬' - '¡' + 1)).Concat(Enumerable.Range('®', 'ÿ' - '®' + 1)).ToHashSet();
        var map = new Dictionary<byte, char>();
        var next = 0;
        for (var b = 0; b < 256; b++)
        {
            map[(byte)b] = printable.Contains(b) ? (char)b : (char)(256 + next++);
        }

        return map;
    }
}
