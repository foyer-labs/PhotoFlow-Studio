// SPDX-License-Identifier: Apache-2.0

using PhotoFlow.Core.Catalog;
using PhotoFlow.Tagging.Ocr;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Analysis;

/// <summary>
/// Race numbers (spec §8, acceptance of phase 4 "Numeri riconosciuti solo se presenti in elenco; casi ambigui in 'da
/// rivedere'"): only numbers of the entry list are accepted; sponsors and plates are ignored; 6/9 and 1/7 confusions and
/// partial readings become candidates to review; without a list, only a clear number; bursts vote by majority.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class NumberMatcherTests
{
    private static readonly IReadOnlySet<string> List = new HashSet<string> { "6", "9", "12", "27", "44", "112", "17", "11" };

    [Fact]
    public void A_clear_number_of_the_list_is_certain()
    {
        var result = NumberMatcher.Choose([("27", 0.95), ("ACME", 0.9)], List);
        Assert.Equal((NumberState.Certain, "27"), (result.State, result.Number));
    }

    [Fact]
    public void Numbers_outside_the_list_are_ignored()
    {
        Assert.Equal(NumberState.OutOfList, NumberMatcher.Choose([("348", 0.97), ("AB123CD", 0.9)], List).State);
        Assert.Equal(NumberState.NoNumber, NumberMatcher.Choose([("SPONSOR", 0.9)], List).State);
        Assert.Equal(NumberState.NoNumber, NumberMatcher.Choose([], List).State);

        // A sponsor's number next to the car's: the one in the list wins.
        var mixed = NumberMatcher.Choose([("2024", 0.99), ("44", 0.8)], List);
        Assert.Equal((NumberState.Certain, "44"), (mixed.State, mixed.Number));
    }

    [Fact]
    public void Confused_digits_give_candidates_to_review()
    {
        var sixNine = NumberMatcher.Choose([("6", 0.48)], List);
        Assert.Equal(NumberState.ToReview, sixNine.State);
        Assert.Null(sixNine.Number);
        Assert.Equal(["6", "9"], sixNine.Candidates.Select(c => c.Number).Order(StringComparer.Ordinal));

        // A clear reading of a number in the list stays certain even when its confused twin is in the list too.
        var nine = NumberMatcher.Choose([("9", 0.95)], List);
        Assert.Equal((NumberState.Certain, "9"), (nine.State, nine.Number));
        Assert.Contains(nine.Candidates, c => c.Number == "6");

        var oneSeven = NumberMatcher.Choose([("17", 0.6), ("11", 0.55)], List);
        Assert.Equal(NumberState.ToReview, oneSeven.State);
        Assert.Contains(oneSeven.Candidates, c => c.Number == "17");
        Assert.Contains(oneSeven.Candidates, c => c.Number == "11");
    }

    [Fact]
    public void A_partial_reading_gives_the_numbers_it_can_be()
    {
        var partial = NumberMatcher.Choose([("2", 0.9)], new HashSet<string> { "27", "112", "44" });
        Assert.Equal(NumberState.ToReview, partial.State);
        Assert.Equal(["112", "27"], partial.Candidates.Select(c => c.Number).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Without_a_list_only_a_clear_number_is_taken()
    {
        var clear = NumberMatcher.Choose([("348", 0.95)], null);
        Assert.Equal((NumberState.Certain, "348"), (clear.State, clear.Number));
        Assert.Equal(NumberState.ToReview, NumberMatcher.Choose([("348", 0.6)], null).State);
        Assert.Equal(NumberState.NoNumber, NumberMatcher.Choose([("PIRELLI", 0.99)], null).State);

        // A number inside other text (a model name, a plate, a stand) is not a race number without a list.
        var model = NumberMatcher.Choose([("959 Panigale", 0.99)], null);
        Assert.Equal(NumberState.NoNumber, model.State);
        Assert.Equal("959", Assert.Single(model.Candidates).Number);
        Assert.Equal((NumberState.Certain, "7"), (NumberMatcher.Choose([("#7", 0.9), ("959 Panigale", 0.99)], null) is var r ? (r.State, r.Number) : default));
    }

    [Fact]
    public void A_burst_fills_its_doubtful_photos_with_the_majority()
    {
        var votes = new[]
        {
            new NumberMatcher.Result(NumberState.Certain, "9", []),
            new NumberMatcher.Result(NumberState.Certain, "9", []),
            new NumberMatcher.Result(NumberState.ToReview, null, [new NumberCandidate("6", 0.48), new NumberCandidate("9", 0.44)]),
            new NumberMatcher.Result(NumberState.NoNumber, null, []),
            new NumberMatcher.Result(NumberState.Certain, "44", []),
        };
        Assert.Equal("9", NumberMatcher.Majority(votes));
        Assert.True(NumberMatcher.CanTake(votes[2], "9"));   // among its candidates
        Assert.True(NumberMatcher.CanTake(votes[3], "9"));   // nothing read
        Assert.False(NumberMatcher.CanTake(votes[4], "9"));  // another certain number: left alone

        Assert.Null(NumberMatcher.Majority([new NumberMatcher.Result(NumberState.Certain, "9", []), new NumberMatcher.Result(NumberState.Certain, "6", [])]));
    }
}
