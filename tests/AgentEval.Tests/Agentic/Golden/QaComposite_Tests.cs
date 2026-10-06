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
        // Every dimension passes, F1 included: the reference answer is the response, word for word.
        var eval = AgenticEvaluatorFixture.BuildEvaluator("qa_composite", new FixedScoreEvaluator(100));
        const string answer = "Climate change is primarily caused by increased CO2 from fossil fuels, deforestation, and industrial emissions.";
        var input = new EvalInput(
            Query: "What are the main causes of climate change?",
            Response: answer,
            Context: "IPCC reports identify fossil fuel burning, deforestation, and industrial emissions as primary climate change drivers.",
            GroundTruth: answer);

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"qa_composite: expected Passed==true with stub score=100, got score={result.Score.Value} {result.Score.Label}: {result.Details.Summary}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_HighJudgeScore_ButF1Failing_Warns_AndNamesIt()
    {
        // This fixture used to be the "high score → pass" case: F1 against the reference is 0.48 (below its bar), and
        // the 0.95 average hid it. A failing quality dimension is "usable, not optimal" (#203 review, B6b/B6e).
        var eval = AgenticEvaluatorFixture.BuildEvaluator("qa_composite", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What are the main causes of climate change?",
            Response: "Climate change is primarily caused by increased CO2 from fossil fuels, deforestation, and industrial emissions.",
            Context: "IPCC reports identify fossil fuel burning, deforestation, and industrial emissions as primary climate change drivers.",
            GroundTruth: "Fossil fuel combustion, deforestation, and industrial emissions are the primary causes of climate change.");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("warn", result.Score.Label);
        Assert.Contains("f1_score", result.Details.Summary!, StringComparison.Ordinal);
        Assert.True(result.Score.Value > 0.9);   // the score is unchanged; only the verdict stops hiding the failure
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
