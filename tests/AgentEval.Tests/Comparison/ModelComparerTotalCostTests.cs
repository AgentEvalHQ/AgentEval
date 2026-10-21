// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Tests.MAF;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Comparison;

public sealed class ModelComparerTotalCostTests
{
    private static readonly TestCase Case = new() { Name = "cost completeness", Input = "hello" };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Compare_WithAnUnpricedRun_TotalIsAbsent(bool crashed)
    {
        var result = await CompareAsync([Priced(0.01m), Unpriced(crashed), Priced(0.01m)]);
        var model = result.ModelResults.Single(m => m.ModelId == "subject");
        Assert.Null(model.TotalCost);
        Assert.Equal(0.01m, model.AverageCost);
    }

    [Fact]
    public async Task Compare_MoreCrashes_DoesNotAppearCheaperInTotal()
    {
        var result = await CompareAsync([Priced(0.02m), Unpriced(true), Unpriced(true)]);
        Assert.Null(result.ModelResults.Single(m => m.ModelId == "subject").TotalCost);
        Assert.Equal(0.03m, result.ModelResults.Single(m => m.ModelId == "reference").TotalCost);
        Assert.Null(result.CostNotMeasuredReason);
        Assert.Equal(0, result.Ranking.Single(m => m.ModelId == "subject").CostScore);
        Assert.Equal(100, result.Ranking.Single(m => m.ModelId == "reference").CostScore);
    }

    [Fact]
    public async Task Compare_AllRunsPriced_SumsEveryRunAndPreservesZero()
    {
        var result = await CompareAsync([Priced(0.01m), Priced(0.02m), Priced(0.03m)]);
        var model = result.ModelResults.Single(m => m.ModelId == "subject");
        Assert.Equal(0.06m, model.TotalCost);
        Assert.Equal(model.TotalCost / 3, model.AverageCost);
        var zero = (await CompareAsync([Priced(0m), Priced(0m)])).ModelResults.Single(m => m.ModelId == "subject");
        Assert.Equal(0m, zero.TotalCost);
        Assert.Equal(0m, zero.AverageCost);
    }

    [Fact]
    public async Task DetailedTable_PartiallyPricedRuns_DoesNotPresentPartialSumAsTotal()
    {
        var result = await CompareAsync([Priced(0.0125m), Unpriced(true), Priced(0.0125m)]);
        var row = result.ToDetailedMetricsTable().Split('\n').Single(l => l.StartsWith("| subject |", StringComparison.Ordinal));
        Assert.Contains("not priced", row);
        Assert.Contains("(2 of 3 runs priced)", row);
        Assert.DoesNotContain("0.0250", row);
        Assert.DoesNotContain("0,0250", row);
    }

    [Fact]
    public async Task TotalCostNotPricedReason_MixedMissingData_ReportsCountsAndCauses()
    {
        var untracked = new TestResult { TestName = "untracked", Score = 80, Passed = true };
        var result = await CompareAsync([Priced(0.01m), Unpriced(true), Unpriced(false), untracked]);
        var model = result.ModelResults.Single(m => m.ModelId == "subject");
        Assert.Equal(1, model.PricedRunCount);
        Assert.Contains("3 of 4 runs were not priced", model.TotalCostNotPricedReason);
        Assert.Contains("1 threw", model.TotalCostNotPricedReason);
        Assert.Contains("1 had no price", model.TotalCostNotPricedReason);
        Assert.Contains("1 had no performance data", model.TotalCostNotPricedReason);
        Assert.Contains(model.TotalCostNotPricedReason!, result.ToDetailedMetricsTable());
        Assert.Null(result.ModelResults.Single(m => m.ModelId == "reference").TotalCostNotPricedReason);
    }

    [Fact]
    public async Task Compare_CostAnalysisDisabled_ExplainsAbsentTotal()
    {
        foreach (var runs in new[] { new[] { Priced(0.01m) }, new[] { Priced(0.01m), Unpriced(false) } })
        {
            var model = (await CompareAsync(runs, enabled: false)).ModelResults.Single(m => m.ModelId == "subject");
            Assert.Null(model.TotalCost);
            Assert.Null(model.AverageCost);
            Assert.Contains("cost analysis is off", model.TotalCostNotPricedReason);
        }
    }

    [Fact]
    public async Task Compare_NoRunPriced_LeavesBothCostsAbsent()
    {
        var model = (await CompareAsync([Unpriced(true), Unpriced(true)])).ModelResults.Single(m => m.ModelId == "subject");
        Assert.Null(model.TotalCost);
        Assert.Null(model.AverageCost);
        Assert.Equal(0, model.PricedRunCount);
        Assert.Contains("None of the 2 runs was priced", model.TotalCostNotPricedReason);
        var empty = model with { StochasticResult = model.StochasticResult with { IndividualResults = [] } };
        Assert.Equal("No run, so nothing was priced.", empty.TotalCostNotPricedReason);
    }

    [Fact]
    public async Task Compare_ThroughRealHarnessWithOneCrash_TotalRemainsAbsent()
    {
        var sequence = 0;
        static IEvaluableAgent Agent(string model) => new MAFIdentifiableAgentAdapter(
            new FakeAIAgent("test", "Hello.", new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 1000 }), model, model);
        var factories = new IAgentFactory[]
        {
            new DelegateAgentFactory("gpt-4o", "GPT-4o", () => Interlocked.Increment(ref sequence) == 2 ? new CrashedAgent() : Agent("gpt-4o")),
            new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => Agent("gpt-4o-mini")),
        };
        var runner = new StochasticRunner(new MAFEvaluationHarness(verbose: false), statisticsCalculator: null,
            new EvaluationOptions { TrackPerformance = true, EvaluateResponse = false, Verbose = false });
        var result = await new ModelComparer(runner).CompareModelsAsync(factories, Case,
            new ModelComparisonOptions(RunsPerModel: 3, EnableCostAnalysis: true, EnableStatistics: false));
        var model = result.ModelResults.Single(m => m.ModelId == "gpt-4o");
        Assert.Equal(2, model.PricedRunCount);
        Assert.Equal(0.0125m, model.AverageCost);
        Assert.Null(model.TotalCost);
        Assert.Contains("1 threw", model.TotalCostNotPricedReason);
        Assert.Equal(0.00225m, result.ModelResults.Single(m => m.ModelId == "gpt-4o-mini").TotalCost);
        Assert.Null(result.CostNotMeasuredReason);
    }

    private sealed class CrashedAgent : IEvaluableAgent
    {
        public string Name => "crashed";
        public Task<AgentEval.Core.AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("recorded agent failure");
    }

    private static TestResult Priced(decimal cost) => new()
    {
        TestName = "priced", Score = 80, Passed = true,
        Performance = new PerformanceMetrics { StartTime = DateTimeOffset.UnixEpoch, EndTime = DateTimeOffset.UnixEpoch.AddSeconds(1), EstimatedCost = cost },
    };

    private static TestResult Unpriced(bool crashed) => new()
    {
        TestName = "unpriced", Score = crashed ? 0 : 80, Passed = !crashed,
        Error = crashed ? new InvalidOperationException("agent failed") : null,
        Performance = crashed ? null : new PerformanceMetrics { StartTime = DateTimeOffset.UnixEpoch, EndTime = DateTimeOffset.UnixEpoch.AddSeconds(1) },
    };

    private static Task<ModelComparisonResult> CompareAsync(IReadOnlyList<TestResult> subject, bool enabled = true)
    {
        var runner = new RecordedRunner(subject);
        var factories = new[] { "subject", "reference" }
            .Select(id => (IAgentFactory)new DelegateAgentFactory(id, id, () => throw new InvalidOperationException("No live agent"))).ToArray();
        return new ModelComparer(runner).CompareModelsAsync(factories, Case,
            new ModelComparisonOptions(RunsPerModel: 3, EnableCostAnalysis: enabled, EnableStatistics: false));
    }

    private sealed class RecordedRunner(IReadOnlyList<TestResult> subject) : IStochasticRunner
    {
        public Task<StochasticResult> RunStochasticTestAsync(IEvaluableAgent agent, TestCase testCase, StochasticOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StochasticResult> RunStochasticTestAsync(IAgentFactory factory, TestCase testCase, StochasticOptions? options = null, CancellationToken cancellationToken = default)
        {
            var runs = factory.ModelId == "subject" ? subject : new[] { Priced(0.01m), Priced(0.01m), Priced(0.01m) };
            var measured = runs.Count(r => r.Error is null && !r.JudgeFailed);
            var stats = new StochasticStatistics(1, 80, 80, 0, 80, 80, 80, 80, 80, null, measured);
            return Task.FromResult(new StochasticResult(testCase, runs, stats, options ?? new StochasticOptions(), Passed: true));
        }
    }
}
