// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Comparison;

/// <summary>
/// A dimension <see cref="ModelComparer"/> did not measure for a model is not the best score on it. A model without a
/// price used to be ranked as <c>AverageCost ?? 0m</c>: cost score 100, the "cheapest" beside a priced model, so every
/// Bitdeer model (no price in the table) scored 100 and a mix put the unpriced one on top. A model none of whose runs
/// returned had an average latency of zero: speed score 100, the "fastest", and with speed-focused weights it won. Speed
/// and cost are now scored only when every model was measured on them; otherwise no model is scored on that dimension
/// and the composite rescales the other weights.
/// </summary>
public class ModelComparerNotMeasuredTests
{
    private static readonly TestCase Case = new() { Name = "Calculator Test", Input = "What is 42 multiplied by 17?" };

    [Fact]
    public async Task AnUnpricedModel_IsNotRankedAsTheCheapest()
    {
        // Priced has the better quality; Unpriced has no price. With the old $0 reading, cost-focused weights put
        // Unpriced first on cost alone (50 to 75 here). Ranked on the measured dimensions, Priced wins.
        var result = await CompareAsync(ScoringWeights.CostFocused,
            ("priced", "Priced", MeanScore: 90, PassRate: 1.0, LatencyMs: 200, Cost: 0.01m),
            ("unpriced", "Unpriced", MeanScore: 85, PassRate: 1.0, LatencyMs: 200, Cost: null));

        Assert.Equal("Priced", result.Winner.ModelName);
        Assert.All(result.Ranking, r => Assert.Null(r.CostScore));
        Assert.NotNull(result.CostNotMeasuredReason);
        Assert.Contains("Unpriced", result.CostNotMeasuredReason);
    }

    [Fact]
    public async Task WhenAnyModelIsUnpriced_NoModelIsScoredOnCost_AndTheCompositeRescalesTheOtherWeights()
    {
        // Quality 90 vs 80 (normalised 100 vs 0), latency and pass rate equal (100 each). Default weights 0.4/0.2/0.2/0.2;
        // without cost the other three are rescaled by 1/0.8: Best = 1.25 × (40 + 20 + 20) = 100, Other = 1.25 × 40 = 50.
        var result = await CompareAsync(ScoringWeights.Default,
            ("best", "Best", MeanScore: 90, PassRate: 1.0, LatencyMs: 300, Cost: 0.002m),
            ("other", "Other", MeanScore: 80, PassRate: 1.0, LatencyMs: 300, Cost: null));

        var best = result.Ranking.Single(r => r.ModelName == "Best");
        var other = result.Ranking.Single(r => r.ModelName == "Other");
        Assert.Null(best.CostScore);
        Assert.Null(other.CostScore);
        Assert.Equal(100.0, best.CompositeScore, precision: 6);
        Assert.Equal(50.0, other.CompositeScore, precision: 6);
    }

    [Fact]
    public async Task WhenNoModelIsPriced_CostIsNotMeasured_NotAHundredForEveryone()
    {
        var result = await CompareAsync(ScoringWeights.Default,
            ("a", "GLM", MeanScore: 90, PassRate: 1.0, LatencyMs: 200, Cost: null),
            ("b", "DeepSeek", MeanScore: 70, PassRate: 0.5, LatencyMs: 400, Cost: null));

        Assert.All(result.Ranking, r => Assert.Null(r.CostScore));
        Assert.Contains("GLM", result.CostNotMeasuredReason);
        Assert.Contains("DeepSeek", result.CostNotMeasuredReason);
        // The best model on every measured dimension scores 100, not 100 plus a free cost bonus or 80 for a missing one.
        Assert.Equal(100.0, result.Winner.CompositeScore, precision: 6);
    }

    [Fact]
    public async Task WhenEveryModelIsMeasured_SpeedAndCostAreScoredAsBefore()
    {
        var result = await CompareAsync(ScoringWeights.Default,
            ("cheap", "Cheap", MeanScore: 80, PassRate: 1.0, LatencyMs: 200, Cost: 0.001m),
            ("dear", "Dear", MeanScore: 80, PassRate: 1.0, LatencyMs: 200, Cost: 0.010m));

        Assert.Null(result.CostNotMeasuredReason);
        Assert.Null(result.SpeedNotMeasuredReason);
        Assert.Equal(100.0, result.Ranking.Single(r => r.ModelName == "Cheap").CostScore);
        Assert.Equal(0.0, result.Ranking.Single(r => r.ModelName == "Dear").CostScore);
        Assert.All(result.Ranking, r => Assert.Equal(100.0, r.SpeedScore));
        // 0.4 × 100 + 0.2 × 100 + 0.2 × 100 + 0.2 × cost score: the unscaled composite of earlier versions.
        Assert.Equal(100.0, result.Ranking.Single(r => r.ModelName == "Cheap").CompositeScore, precision: 6);
        Assert.Equal(80.0, result.Ranking.Single(r => r.ModelName == "Dear").CompositeScore, precision: 6);
        Assert.Equal("Cheap", result.Winner.ModelName);
    }

    [Fact]
    public async Task WithCostAnalysisOff_CostIsNotMeasured_AndTheReasonSaysSo()
    {
        var result = await CompareAsync(ScoringWeights.Default, enableCostAnalysis: false,
            ("a", "A", MeanScore: 90, PassRate: 1.0, LatencyMs: 200, Cost: 0.001m),
            ("b", "B", MeanScore: 80, PassRate: 1.0, LatencyMs: 200, Cost: 0.002m));

        Assert.All(result.Ranking, r => Assert.Null(r.CostScore));
        Assert.Contains("cost analysis is off", result.CostNotMeasuredReason);
    }

    [Fact]
    public async Task WeightsOnCostAlone_WithAnUnpricedModel_Throw_RatherThanRankOnNothing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CompareAsync(
            new ScoringWeights(Quality: 0, Speed: 0, Cost: 1.0, Reliability: 0),
            ("a", "A", MeanScore: 90, PassRate: 1.0, LatencyMs: 200, Cost: 0.001m),
            ("b", "B", MeanScore: 80, PassRate: 1.0, LatencyMs: 200, Cost: null)));

        Assert.Contains("No measured dimension carries weight", ex.Message);
    }

    [Fact]
    public async Task TheReports_PrintAnUnmeasuredCostAsNa_NotAsAScore()
    {
        var result = await CompareAsync(ScoringWeights.QualityFocused,
            ("a", "GLM", MeanScore: 90, PassRate: 1.0, LatencyMs: 200, Cost: null),
            ("b", "DeepSeek", MeanScore: 70, PassRate: 1.0, LatencyMs: 400, Cost: 0.002m));

        var table = result.ToRankingsTable();
        Assert.Contains("| n/a |", table);
        Assert.Contains("Cost n/a: not measured", table);
        Assert.Contains("Cost: n/a", result.Summary);
        Assert.Contains("not measured", result.ToMarkdown());

        var aggregate = new[] { result, result }.ToMarkdown();
        Assert.Contains("| n/a |", aggregate);
    }

    [Fact]
    public async Task AModelWithNoCompletedRun_IsNotRankedAsTheFastest()
    {
        // Dead never returned: no latency, quality 0, pass rate 0. Its old average latency of zero made it the fastest,
        // and speed-focused weights then put it first (60 to 50). Speed is not measured for it, so no model is ranked on
        // speed, and the model that answered wins on the dimensions that were measured.
        var result = await CompareAsync(ScoringWeights.SpeedFocused,
            ("working", "Working", MeanScore: 60, PassRate: 0.5, LatencyMs: 2000, Cost: null),
            ("dead", "Dead", MeanScore: 0, PassRate: 0.0, LatencyMs: null, Cost: null));

        Assert.Equal("Working", result.Winner.ModelName);
        Assert.Null(result.ModelResults.Single(m => m.ModelName == "Dead").AverageLatency);
        Assert.All(result.Ranking, r => Assert.Null(r.SpeedScore));
        Assert.Contains("Dead", result.SpeedNotMeasuredReason);
    }

    [Fact]
    public async Task WhenSpeedAndCostAreBothUnmeasured_TheCompositeWeighsQualityAndReliability()
    {
        // Default weights: quality 0.4 and reliability 0.2 are what was measured, rescaled by 1/0.6.
        // A = (0.4 × 100 + 0.2 × 100) / 0.6 = 100; B = 0. The old code gave A 80 and B 40 (B's zero latency was "fastest").
        var result = await CompareAsync(ScoringWeights.Default,
            ("a", "A", MeanScore: 90, PassRate: 1.0, LatencyMs: 200, Cost: null),
            ("b", "B", MeanScore: 30, PassRate: 0.5, LatencyMs: null, Cost: null));

        Assert.Equal(100.0, result.Ranking.Single(r => r.ModelName == "A").CompositeScore, precision: 6);
        Assert.Equal(0.0, result.Ranking.Single(r => r.ModelName == "B").CompositeScore, precision: 6);
        Assert.Contains("the composite weighs quality and reliability", result.SpeedNotMeasuredReason);
        Assert.Contains("the composite weighs quality and reliability", result.CostNotMeasuredReason);
    }

    [Fact]
    public async Task WeightsOnSpeedAlone_WithAModelThatNeverReturned_Throw_RatherThanRankOnNothing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CompareAsync(
            new ScoringWeights(Quality: 0, Speed: 1.0, Cost: 0, Reliability: 0),
            ("a", "A", MeanScore: 90, PassRate: 1.0, LatencyMs: 200, Cost: null),
            ("b", "B", MeanScore: 0, PassRate: 0.0, LatencyMs: null, Cost: null)));

        Assert.Contains("No measured dimension carries weight", ex.Message);
        Assert.Contains("speed", ex.Message);
    }

    [Fact]
    public async Task TheReports_PrintAnUnmeasuredSpeedAsNa_AndNoLatencyAsNa_NotZero()
    {
        var result = await CompareAsync(ScoringWeights.Default,
            ("working", "Working", MeanScore: 60, PassRate: 0.5, LatencyMs: 2000, Cost: null),
            ("dead", "Dead", MeanScore: 0, PassRate: 0.0, LatencyMs: null, Cost: null));

        Assert.Contains("Speed n/a: not measured", result.ToRankingsTable());
        Assert.Contains("Speed: n/a", result.Summary);
        var deadRow = result.ToDetailedMetricsTable().Split('\n').Single(l => l.Contains("| Dead |", StringComparison.Ordinal));
        Assert.DoesNotContain("0ms", deadRow);   // the old zero average latency
        Assert.Contains("N/A", deadRow);
    }

    private static Task<ModelComparisonResult> CompareAsync(
        ScoringWeights weights,
        params (string Id, string Name, double MeanScore, double PassRate, int? LatencyMs, decimal? Cost)[] models) =>
        CompareAsync(weights, enableCostAnalysis: true, models);

    private static Task<ModelComparisonResult> CompareAsync(
        ScoringWeights weights,
        bool enableCostAnalysis,
        params (string Id, string Name, double MeanScore, double PassRate, int? LatencyMs, decimal? Cost)[] models)
    {
        var runner = new FixedResultRunner(models.ToDictionary(m => m.Id, m => (m.MeanScore, m.PassRate, m.LatencyMs, m.Cost)));
        var factories = models
            .Select(m => (IAgentFactory)new DelegateAgentFactory(m.Id, m.Name,
                () => throw new InvalidOperationException("the fixed-result runner never creates an agent")))
            .ToList();

        return new ModelComparer(runner).CompareModelsAsync(factories, Case,
            new ModelComparisonOptions(RunsPerModel: 2, ScoringWeights: weights, EnableCostAnalysis: enableCostAnalysis));
    }

    /// <summary>
    /// Returns a fixed stochastic result per model id: this tests the ranking arithmetic, so the run results are
    /// inputs, not a model being graded. A null latency is a model none of whose runs returned: no performance data.
    /// </summary>
    private sealed class FixedResultRunner(
        IReadOnlyDictionary<string, (double MeanScore, double PassRate, int? LatencyMs, decimal? Cost)> byModel) : IStochasticRunner
    {
        public Task<StochasticResult> RunStochasticTestAsync(
            IEvaluableAgent agent, TestCase testCase, StochasticOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StochasticResult> RunStochasticTestAsync(
            IAgentFactory factory, TestCase testCase, StochasticOptions? options = null, CancellationToken cancellationToken = default)
        {
            var (meanScore, passRate, latencyMs, cost) = byModel[factory.ModelId];
            var runs = options?.Runs ?? 2;
            var start = DateTimeOffset.UnixEpoch;
            var results = Enumerable.Range(0, runs).Select(i => new TestResult
            {
                TestName = $"{testCase.Name} #{i + 1}",
                Passed = i < passRate * runs,
                Score = (int)meanScore,
                Performance = latencyMs is { } ms
                    ? new PerformanceMetrics
                    {
                        StartTime = start,
                        EndTime = start + TimeSpan.FromMilliseconds(ms),
                        EstimatedCost = cost,
                    }
                    : null,
            }).ToList();

            var statistics = new StochasticStatistics(
                PassRate: passRate, MeanScore: meanScore, MedianScore: meanScore, StandardDeviation: 0,
                MinScore: (int)meanScore, MaxScore: (int)meanScore, Percentile25: meanScore, Percentile75: meanScore,
                Percentile95: meanScore, ConfidenceInterval: null, SampleSize: runs);

            return Task.FromResult(new StochasticResult(testCase, results, statistics,
                options ?? new StochasticOptions(Runs: runs), Passed: passRate >= 0.8));
        }
    }
}
