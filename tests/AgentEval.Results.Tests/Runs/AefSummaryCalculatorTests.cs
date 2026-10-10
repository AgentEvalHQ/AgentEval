using System.Text.Json.Nodes;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Tests.Runs;

/// <summary>[SUM-3]–[SUM-8]: which lines an entry counts, their values, N, n, notMeasured, sum and value.</summary>
public class AefSummaryCalculatorTests
{
    private static readonly string[] OneLane = ["main"];

    [Fact]
    public void NotApplicable_IsLeftOut_TypedAbsences_AreNotMeasured_AndTheValueIsTheMeanOfTheMeasured()
    {
        var lines = new[]
        {
            Line("passed", 0.8), Line("failed", 0.4), Line("not_applicable"), Line("not_measured"), Line("skipped"),
            Line("error"), Line("pending"),
        };

        var figures = AefSummaryCalculator.Compute(lines, OneLane, "main", "m", "score", "q");

        Assert.Equal(6, figures.N);   // not_applicable left out
        Assert.Equal(2, figures.Measured);
        Assert.Equal(4, figures.NotMeasured);
        Assert.Equal(1.2, figures.Sum, 15);
        Assert.Equal(0.6, figures.Value!.Value, 15);
        Assert.True(figures.ValueDefined);
    }

    [Fact]
    public void ForACount_TheValueIsTheSum()
    {
        var figures = AefSummaryCalculator.Compute([Line("scored", 3), Line("scored", 4)], OneLane, "main", "m", "count", "q");

        Assert.Equal(7, figures.Value);
    }

    [Theory]
    [InlineData("rate")]
    [InlineData("verdict")]
    public void ForARateOrAVerdict_PassedIs1_FailedWarnAndInconclusiveAre0_AndScoredIsNotMeasured(string kind)
    {
        var lines = new[] { Line("passed"), Line("failed"), Line("warn"), Line("inconclusive"), Line("scored", 0.9) };

        var figures = AefSummaryCalculator.Compute(lines, OneLane, "main", "pass", kind, "q");

        Assert.Equal(5, figures.N);
        Assert.Equal(4, figures.Measured);
        Assert.Equal(new[] { 1.0, 0, 0, 0 }, figures.Values);
        Assert.Equal(0.25, figures.Value);
    }

    [Fact]
    public void ForAScore_ALineWithoutAScoreForTheMetric_OrScoringItTwice_IsNotMeasured()
    {
        var twice = Line("passed", 0.9);
        twice["scores"]!.AsArray().Add(new JsonObject { ["metric"] = "m", ["value"] = 0.95 });
        var other = Line("passed");
        other["scores"] = new JsonArray(new JsonObject { ["metric"] = "other", ["value"] = 1 });

        var figures = AefSummaryCalculator.Compute([Line("failed", 0.4), twice, other], OneLane, "main", "m", "score", "q");

        Assert.Equal(3, figures.N);
        Assert.Equal(1, figures.Measured);
        Assert.Equal(0.4, figures.Value);
    }

    [Fact]
    public void TheMedianOfAnOddCount_IsTheMiddleValue()
    {
        var figures = AefSummaryCalculator.Compute([Line("passed", 0.9), Line("failed", 0.1), Line("passed", 0.5)], OneLane, "main", "m", "score", "q", "median");

        Assert.Equal(0.5, figures.Value);
    }

    [Fact]
    public void TheMedianOfAnEvenCount_IsTheMeanOfTheTwoMiddleValues()
    {
        var lines = new[] { Line("passed", 0.9), Line("failed", 0.1), Line("passed", 0.5), Line("failed", 0.2) };

        var figures = AefSummaryCalculator.Compute(lines, OneLane, "main", "m", "score", "q", "median");

        Assert.Equal((0.2 + 0.5) / 2, figures.Value);
        Assert.Equal(double.MaxValue, AefSummaryCalculator.Median([double.MaxValue, double.MaxValue]));   // no overflow
    }

    [Fact]
    public void MinAndMax_AreRecomputed_AnyOtherMethodIsTheProducers()
    {
        var lines = new[] { Line("passed", 0.9), Line("failed", 0.1), Line("passed", 0.5) };

        Assert.Equal(0.1, AefSummaryCalculator.Compute(lines, OneLane, "main", "m", "score", "q", "min").Value);
        Assert.Equal(0.9, AefSummaryCalculator.Compute(lines, OneLane, "main", "m", "score", "q", "max").Value);
        var passAtK = AefSummaryCalculator.Compute(lines, OneLane, "main", "m", "score", "q", "pass@k");
        Assert.False(passAtK.ValueDefined);
        Assert.Null(passAtK.Value);
        Assert.Equal(3, passAtK.Measured);   // N, n, notMeasured and sum are still recomputed
        Assert.Equal(1.5, passAtK.Sum, 15);
    }

    [Fact]
    public void WithNothingMeasured_TheValueIsNull_WhateverTheMethod()
    {
        foreach (var method in new string?[] { null, "median", "pass@k" })
        {
            var figures = AefSummaryCalculator.Compute([Line("error"), Line("not_applicable")], OneLane, "main", "m", "score", "q", method);

            Assert.Equal((1, 0, 1), (figures.N, figures.Measured, figures.NotMeasured));
            Assert.Null(figures.Value);
            Assert.True(figures.ValueDefined);
        }
    }

    [Fact]
    public void TrialLines_AreNotCounted_TheirRollupIs_AndOnlyLinesAtThePath()
    {
        var trial0 = Line("passed", 1);
        trial0["trial"] = 0;
        var trial1 = Line("failed", 0);
        trial1["trial"] = 1;
        var elsewhere = Line("passed", 1);
        elsewhere["path"] = "q/a";
        var rollup = Line("passed", 0.5);
        rollup["trials"] = new JsonObject { ["n"] = 2, ["passed"] = 1, ["aggregation"] = "Mean", ["agree"] = false };

        var figures = AefSummaryCalculator.Compute([trial0, trial1, elsewhere, rollup], OneLane, "main", "m", "score", "q");

        Assert.Equal((1, 1), (figures.N, figures.Measured));
        Assert.Equal(0.5, figures.Value);
    }

    [Fact]
    public void ALineBelongsToTheLaneItNames_OrWithoutOne_ToTheOnlyLane()
    {
        var inB = Line("passed", 1);
        inB["lane"] = "b";
        var none = Line("failed", 0);

        Assert.True(AefSummaryCalculator.Belongs(none, "main", ["main"]));
        Assert.False(AefSummaryCalculator.Belongs(none, "a", ["a", "b"]));   // two lanes: a line without lane is in neither
        Assert.True(AefSummaryCalculator.Belongs(inB, "b", ["a", "b"]));
        Assert.False(AefSummaryCalculator.Belongs(inB, "a", ["a"]));
        Assert.Equal(1, AefSummaryCalculator.Compute([inB, none], ["a", "b"], "b", "m", "score", "q").N);
    }

    [Fact]
    public void TheSum_IsExact_AndRoundedOnce()
    {
        Assert.Equal(1.0, AefSummaryCalculator.ExactSum([1e20, 1, -1e20]));   // in order: 0
        Assert.Equal(1.0, AefSummaryCalculator.ExactSum(Enumerable.Repeat(0.1, 10)));   // in order: 0.9999999999999999
        Assert.Equal(10000000000000002.0, AefSummaryCalculator.ExactSum([1e16, 1.0, 1e-16]));   // the tie goes up, past the half
        Assert.Equal(0, AefSummaryCalculator.ExactSum([]));
        Assert.Equal(-0.5, AefSummaryCalculator.ExactSum([0.25, -1, 0.25]));
    }

    [Theory]
    [InlineData(0.42000000000000004, 0.42000000000000004, true)]
    [InlineData(0.42, 0.42000000000000004, false)]   // the exact mean where SUM-5 gives the division
    [InlineData(1.0, 1.0 + 1e-10, false)]
    [InlineData(1.7e308, double.PositiveInfinity, false)]
    public void SumAndValue_AreTheRecomputedBinary64ValuesExactly(double written, double recomputed, bool same) =>
        Assert.Equal(same, AefSummaryCalculator.Same(written, recomputed));

    [Theory]
    [InlineData(new[] { 1.5e308, 1.5e308 }, double.PositiveInfinity)]                       // beyond binary64
    [InlineData(new[] { -1.5e308, -1.5e308 }, double.NegativeInfinity)]
    [InlineData(new[] { 1e308, 1e308, -1e308 }, 1e308)]                                      // a partial sum leaves binary64, the sum does not
    [InlineData(new[] { 1e308, 1e308, -1e308, -1e308, 5e-324 }, 5e-324)]                    // exactly, to the least subnormal
    [InlineData(new[] { 1.7976931348623157e308, 9.9792015476736e291 }, double.PositiveInfinity)]   // max + half its unit: the tie goes to even, beyond
    [InlineData(new[] { 1.7976931348623157e308, 4.9896007738368e291 }, 1.7976931348623157e308)]    // max + a quarter: max
    public void AnExactSum_BeyondBinary64_IsAnInfinity_AndOneWithinIt_IsFoundThoughAPartialSumLeavesIt(double[] values, double sum) =>
        Assert.Equal(sum, AefSummaryCalculator.ExactSum(values));

    [Fact]
    public void AnEntryWhoseSumIsBeyondBinary64_HasNoMean_AndReadsAsNotMeasured_ButAnAggregateKeepsItsValue()
    {
        // [SUM-5], [SUM-6], [SUM-8]: two scores of 1.5e308.
        var lines = new[] { 1.5e308, 1.5e308 }.Select(v => Line("scored", v)).ToList();

        var mean = AefSummaryCalculator.Compute(lines, ["a"], "a", "m", "score", "q");
        Assert.Equal((double.PositiveInfinity, (double?)null, true), (mean.Sum, mean.Value, mean.ReadsNotMeasured));

        var median = AefSummaryCalculator.Compute(lines, ["a"], "a", "m", "score", "q", "median");
        Assert.Equal((1.5e308, false), (median.Value, median.ReadsNotMeasured));
    }

    [Fact]
    public void TheMean_IsTheRoundedSumDividedByN_InOneDivision()
    {
        // [SUM-5]: 0.4 + 0.5 + 0.6 + 0.3 + 0.3 is 2.1 rounded once; 2.1 / 5 is 0.42000000000000004, not 0.42.
        var lines = new[] { 0.4, 0.5, 0.6, 0.3, 0.3 }.Select(v => Line("passed", v)).ToList();
        var figures = AefSummaryCalculator.Compute(lines, ["a"], "a", "m", "score", "q");

        Assert.Equal(2.1, figures.Sum);
        Assert.Equal(0.42000000000000004, figures.Value);
    }

    [Theory]
    [InlineData(new[] { 0.1, 0.2 }, 0.15000000000000002)]                    // (0.1 + 0.2) rounded once, halved
    [InlineData(new[] { 1.7976931348623157e308, 1.7976931348623157e308 }, 1.7976931348623157e308)]   // the sum overflows; the mean does not
    [InlineData(new[] { 5e-324, 1e-323 }, 1e-323)]                           // 1.5 × the least subnormal: the tie goes to even
    [InlineData(new[] { 3.0, 1.0, 2.0 }, 2.0)]
    public void AnEvenCountsMedian_IsTheExactMeanOfTheTwoMiddleValues_RoundedOnce(double[] values, double median) =>
        Assert.Equal(median, AefSummaryCalculator.Median(values));

    [Theory]
    [InlineData(1.0, 1.0 + 1e-10, true)]
    [InlineData(1.0, 1.0 + 2e-9, false)]
    [InlineData(1e12, 1e12 + 900, true)]   // relative to |recomputed| beyond 1
    [InlineData(1e12, 1e12 + 1100, false)]
    [InlineData(0.0, 9e-10, true)]          // absolute below 1
    [InlineData(1.7e308, double.PositiveInfinity, false)]   // a sum that overflows: no written number is it
    public void ASumOfSquares_MatchesWithin1e9TimesMaxOf1AndTheRecomputed(double written, double recomputed, bool matches) =>
        Assert.Equal(matches, AefSummaryCalculator.Matches(written, recomputed));

    private static JsonObject Line(string state, double? score = null)
    {
        var line = new JsonObject { ["caseId"] = Guid.NewGuid().ToString("N"), ["path"] = "q", ["state"] = state };
        if (score is { } value)
        {
            line["scores"] = new JsonArray(new JsonObject { ["metric"] = "m", ["value"] = value });
        }

        return line;
    }
}
