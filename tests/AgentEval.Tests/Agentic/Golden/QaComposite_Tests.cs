// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>qa_composite</c> evaluator.
/// Key: qa_composite | Category: rag | Threshold: 0.70
/// </summary>
public class QaComposite_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("qa_composite", new FixedScoreEvaluator(100));

        Assert.Equal("qa_composite", eval.Key);
        Assert.Equal("QA Composite", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("qa_composite", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What are the main causes of climate change?",
            Response: "Climate change is primarily caused by increased CO2 from fossil fuels, deforestation, and industrial emissions.",
            Context: "IPCC reports identify fossil fuel burning, deforestation, and industrial emissions as primary climate change drivers.",
            GroundTruth: "Fossil fuel combustion, deforestation, and industrial emissions are the primary causes of climate change.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"qa_composite: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("qa_composite", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "Describe the system architecture.",
            Response: "Architecture it is good thing yes. Very technical much wow.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"qa_composite: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
