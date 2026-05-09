// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Reasoning;

/// <summary>
/// Per-evaluator tests for <c>reasoning_correctness</c>.
/// Key: reasoning_correctness | Category: reasoning | CostTier: MEDIUM
/// </summary>
public class ReasoningCorrectnessEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("reasoning_correctness", new FixedScoreEvaluator(100));

        Assert.Equal("reasoning_correctness", eval.Key);
        Assert.Equal("Reasoning Correctness", eval.Name);
        Assert.Equal("reasoning", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        // Supply explicit reasoning_trace to ensure the evaluator doesn't skip.
        var eval = AgenticEvaluatorFixture.BuildEvaluator("reasoning_correctness", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "If Alice has 5 apples and gives 2 to Bob, how many does Alice have?",
            Response: "Alice has 3 apples.",
            Metadata: new Dictionary<string, object>
            {
                ["reasoning_trace"] = "Step 1: Alice starts with 5 apples. Step 2: She gives 2 to Bob. Step 3: 5 - 2 = 3. Therefore Alice has 3 apples.",
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"reasoning_correctness: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        // Supply explicit reasoning_trace to ensure the evaluator doesn't skip.
        var eval = AgenticEvaluatorFixture.BuildEvaluator("reasoning_correctness", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "If Alice has 5 apples and gives 2 to Bob, how many does Alice have?",
            Response: "Alice has 7 apples.",
            Metadata: new Dictionary<string, object>
            {
                ["reasoning_trace"] = "Alice has 5 apples. She gives 2, so she gains 2, making 5 + 2 = 7.",
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"reasoning_correctness: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
