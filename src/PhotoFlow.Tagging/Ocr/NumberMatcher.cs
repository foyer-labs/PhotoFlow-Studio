// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using PhotoFlow.Core.Catalog;

namespace PhotoFlow.Tagging.Ocr;

/// <summary>
/// The race number of a vehicle from its OCR readings (spec §8, decision F4-3). Readings give digit groups of 1-3
/// digits; with an entry list only its numbers count (sponsors and plates are ignored); a number is certain when it is
/// read clearly and beats the next number of the list by a margin; digits often confused (6/9, 1/7) and partial readings
/// (a prefix or suffix of numbers of the list) give candidates to review. Without a list, only a clear reading of a
/// number standing alone counts (a model name such as "959 Panigale" is not a race number); numbers inside other text
/// stay as candidates. No number is ever written unless certain.
/// </summary>
public static partial class NumberMatcher
{
    public const double MinConfidence = 0.7;
    public const double Margin = 0.1;
    public const double ConfusionPenalty = 0.9;

    /// <summary>Penalty of a confused variant when the number read is itself in the list: a candidate, not a rival.</summary>
    public const double ListedConfusionPenalty = 0.5;
    public const double NoListConfidence = 0.85;
    public const int MaxDigits = 3;

    private static readonly Dictionary<char, char> Confusions = new() { ['6'] = '9', ['9'] = '6', ['1'] = '7', ['7'] = '1' };

    public sealed record Result(NumberState State, string? Number, IReadOnlyList<NumberCandidate> Candidates);

    public static Result Choose(IReadOnlyList<(string Text, double Confidence)> readings, IReadOnlySet<string>? list)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var read = new Dictionary<string, double>(StringComparer.Ordinal);
        var alone = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (text, confidence) in readings)
        {
            foreach (Match match in Digits().Matches(text))
            {
                Keep(read, match.Value, confidence);
            }

            if (Alone().Match(text) is { Success: true } number)
            {
                Keep(alone, number.Groups[1].Value, confidence);
            }
        }

        if (read.Count == 0)
        {
            return new Result(NumberState.NoNumber, null, []);
        }

        if (list is null)
        {
            if (alone.Count == 0)
            {
                return new Result(NumberState.NoNumber, null, Ranked(read));
            }

            var best = alone.MaxBy(r => r.Value);
            return best.Value >= NoListConfidence
                ? new Result(NumberState.Certain, best.Key, [new NumberCandidate(best.Key, best.Value)])
                : new Result(NumberState.ToReview, null, Ranked(alone));
        }

        // Numbers of the list read as such, or through a common confusion (with a penalty).
        var candidates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (number, confidence) in read)
        {
            if (list.Contains(number))
            {
                Keep(candidates, number, confidence);
            }

            foreach (var variant in Variants(number).Where(list.Contains))
            {
                Keep(candidates, variant, confidence * (list.Contains(number) ? ListedConfusionPenalty : ConfusionPenalty));
            }
        }

        if (candidates.Count == 0)
        {
            // Partial readings: a part of numbers of the list (the rest hidden or cut).
            var partial = read.Keys.SelectMany(r => list.Where(n => n.Length > r.Length && (n.StartsWith(r, StringComparison.Ordinal) || n.EndsWith(r, StringComparison.Ordinal)))
                .Select(n => new NumberCandidate(n, read[r] * 0.5))).DistinctBy(c => c.Number).ToList();
            return partial.Count > 0
                ? new Result(NumberState.ToReview, null, [.. partial.OrderByDescending(c => c.Score)])
                : new Result(NumberState.OutOfList, null, Ranked(read));
        }

        var ranked = Ranked(candidates);
        var first = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Score : 0;
        return first.Score >= MinConfidence && first.Score - second >= Margin
            ? new Result(NumberState.Certain, first.Number, ranked)
            : new Result(NumberState.ToReview, null, ranked);
    }

    /// <summary>The number most photos of a burst agree on (more than half of the certain ones), or null.</summary>
    public static string? Majority(IReadOnlyList<Result> burst)
    {
        ArgumentNullException.ThrowIfNull(burst);
        var certain = burst.Where(r => r.State == NumberState.Certain && r.Number is not null).Select(r => r.Number!).ToList();
        var top = certain.GroupBy(n => n, StringComparer.Ordinal).OrderByDescending(g => g.Count()).FirstOrDefault();
        return top is not null && top.Count() * 2 > certain.Count ? top.Key : null;
    }

    /// <summary>A photo of the burst takes the majority when it has no certain number of its own and does not exclude it.</summary>
    public static bool CanTake(Result result, string number)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.State switch
        {
            NumberState.NoNumber => true,
            NumberState.ToReview => result.Candidates.Count == 0 || result.Candidates.Any(c => c.Number == number),
            _ => false,
        };
    }

    private static IEnumerable<string> Variants(string number)
    {
        for (var i = 0; i < number.Length; i++)
        {
            if (Confusions.TryGetValue(number[i], out var other))
            {
                yield return string.Concat(number.AsSpan(0, i), other.ToString(), number.AsSpan(i + 1));
            }
        }
    }

    private static void Keep(Dictionary<string, double> scores, string number, double confidence)
    {
        if (!scores.TryGetValue(number, out var current) || current < confidence)
        {
            scores[number] = confidence;
        }
    }

    private static List<NumberCandidate> Ranked(Dictionary<string, double> scores) =>
        [.. scores.Select(s => new NumberCandidate(s.Key, s.Value)).OrderByDescending(c => c.Score).ThenBy(c => c.Number, StringComparer.Ordinal)];

    /// <summary>Groups of 1-3 digits not part of longer digit runs (plates and years are longer).</summary>
    [GeneratedRegex(@"(?<!\d)\d{1,3}(?!\d)")]
    private static partial Regex Digits();

    /// <summary>A reading that is only a number of 1-3 digits (a leading # or "N." allowed).</summary>
    [GeneratedRegex(@"^\s*(?:#|[Nn][°o.]?\s*)?(\d{1,3})\s*$")]
    private static partial Regex Alone();
}
