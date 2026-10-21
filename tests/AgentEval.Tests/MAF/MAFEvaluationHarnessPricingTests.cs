// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Which model a run is priced at. The harness priced every run at <see cref="EvaluationOptions.ModelName"/> only, and
/// <see cref="StochasticRunner"/> shares one options object across every model of a comparison: a comparison either
/// priced every model at one model's rate (comparing token counts, not prices) or priced nothing. With no ModelName the
/// harness now prices a run at the model the agent names (<see cref="IModelIdentifiable"/>).
/// </summary>
public sealed class MAFEvaluationHarnessPricingTests
{
    private static readonly TestCase Case = new() { Name = "priced run", Input = "Say hello." };

    private static readonly EvaluationOptions SharedOptions = new()
    {
        TrackPerformance = true,
        EvaluateResponse = false,
        Verbose = false,
    };

    // 1,000 input and 1,000 output tokens: the cost is the model's per-1K input rate plus its per-1K output rate.
    private static IEvaluableAgent Agent(string? modelId)
    {
        var inner = new FakeAIAgent("agent", "Hello.", new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 1000 });
        return modelId is null ? new MAFAgentAdapter(inner) : new MAFIdentifiableAgentAdapter(inner, modelId, modelId);
    }

    [Fact]
    public async Task WithoutAModelName_ARunIsPricedAtTheModelTheAgentNames()
    {
        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(Agent("gpt-4o-mini"), Case, SharedOptions);

        Assert.Equal("gpt-4o-mini", result.Performance!.ModelUsed);
        Assert.Equal(0.00015m + 0.0006m, result.Performance.EstimatedCost);
    }

    [Fact]
    public async Task AModelNameInTheOptions_StillWins()
    {
        var options = new EvaluationOptions { TrackPerformance = true, EvaluateResponse = false, Verbose = false, ModelName = "gpt-4o" };

        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(Agent("gpt-4o-mini"), Case, options);

        Assert.Equal("gpt-4o", result.Performance!.ModelUsed);
        Assert.Equal(0.0025m + 0.01m, result.Performance.EstimatedCost);
    }

    [Fact]
    public async Task AnAgentThatNamesNoModel_IsNotPriced()
    {
        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(Agent(modelId: null), Case, SharedOptions);

        Assert.Null(result.Performance!.ModelUsed);
        Assert.Null(result.Performance.EstimatedCost);
    }

    [Fact]
    public async Task AComparisonWithSharedOptions_PricesEachModelAtItsOwnRate()
    {
        var runner = new StochasticRunner(new MAFEvaluationHarness(verbose: false), statisticsCalculator: null, SharedOptions);
        var factories = new IAgentFactory[]
        {
            new DelegateAgentFactory("gpt-4o", "GPT-4o", () => Agent("gpt-4o")),
            new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => Agent("gpt-4o-mini")),
        };

        var result = await new ModelComparer(runner).CompareModelsAsync(factories, Case,
            new ModelComparisonOptions(RunsPerModel: 3, EnableCostAnalysis: true, EnableStatistics: false));

        Assert.Null(result.CostNotMeasuredReason);
        Assert.Equal(0.0025m + 0.01m, result.ModelResults.Single(m => m.ModelId == "gpt-4o").AverageCost);
        Assert.Equal(0.00015m + 0.0006m, result.ModelResults.Single(m => m.ModelId == "gpt-4o-mini").AverageCost);
        Assert.Equal(100.0, result.Ranking.Single(r => r.ModelId == "gpt-4o-mini").CostScore);
        Assert.Equal(0.0, result.Ranking.Single(r => r.ModelId == "gpt-4o").CostScore);
    }
}
