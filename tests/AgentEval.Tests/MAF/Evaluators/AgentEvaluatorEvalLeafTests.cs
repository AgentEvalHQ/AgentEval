// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.MAF.Evaluators;
using Xunit;

using static AgentEval.Tests.MAF.Evaluators.HybridEvalTestHelpers;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// Tests <see cref="AgentEvaluatorEvalLeaf"/> / <c>AsEvalLeaf()</c> — a MAF <c>IAgentEvaluator</c> (e.g.
/// Foundry) wrapped as an AgentEval <c>IEval</c> so it can be a weighted leaf inside a <c>CompositeEval</c>.
/// </summary>
public class AgentEvaluatorEvalLeafTests
{
    [Fact]
    public async Task AsEvalLeaf_passing_evaluator_produces_a_passing_leaf()
    {
        IEval leaf = Fast("foundry").AsEvalLeaf("foundry.relevance", "Foundry Relevance", judgeModel: "gpt-4o-mini");

        var result = await leaf.EvaluateAsync(new EvalInput("What is the capital of France?", "Paris."));

        Assert.Equal("foundry.relevance", result.Metric.Key);
        Assert.Equal("Foundry Relevance", result.Metric.Name);
        Assert.True(result.Score.Passed);
        Assert.True(result.Score.Value > 0.0);
        Assert.Equal("gpt-4o-mini", result.Provenance.JudgeModel);
    }

    [Fact]
    public async Task AsEvalLeaf_failing_evaluator_is_isolated_into_a_failing_leaf()
    {
        IEval leaf = Throws("foundry").AsEvalLeaf("foundry.relevance", "Foundry Relevance");

        var result = await leaf.EvaluateAsync(new EvalInput("q", "r"));   // must NOT throw

        Assert.False(result.Score.Passed);
        Assert.Equal(0.0, result.Score.Value);
        Assert.Contains(result.Details.Evidence!, e => e.Message.Contains("boom"));
    }

    [Fact]
    public async Task AsEvalLeaf_composes_as_a_weighted_component_in_a_CompositeEval()
    {
        IEval foundryLeaf = Fast("foundry").AsEvalLeaf("foundry.relevance", "Foundry Relevance");
        IEval agentEvalLeaf = new StubComposite(Leaf("task_completion", 1.0));

        var benchmark = new CompositeEval(
            key: "hybrid.benchmark", name: "Hybrid Benchmark", category: "hybrid", version: "1.0.0",
            components: new[]
            {
                new EvalComponent(agentEvalLeaf, 0.5),   // AgentEval leaf
                new EvalComponent(foundryLeaf, 0.5),     // Foundry eval as a weighted leaf
            },
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.5);

        var result = await benchmark.EvaluateAsync(new EvalInput("q", "r"));

        Assert.Equal(2, result.Details.SubResults!.Count);
        Assert.Contains(result.Details.SubResults!, r => r.Metric.Key == "foundry.relevance");
        Assert.True(result.Score.Passed);          // both components pass -> weighted sum clears 0.5
        Assert.True(result.Score.Value > 0.0);
    }
}
