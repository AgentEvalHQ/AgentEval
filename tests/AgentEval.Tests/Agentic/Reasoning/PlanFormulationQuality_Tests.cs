// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Reasoning;

/// <summary>
/// Per-evaluator tests for <c>plan_formulation_quality</c>.
/// Key: plan_formulation_quality | Category: reasoning | CostTier: MEDIUM
/// </summary>
public class PlanFormulationQualityEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("plan_formulation_quality", new FixedScoreEvaluator(100));

        Assert.Equal("plan_formulation_quality", eval.Key);
        Assert.Equal("Plan Formulation Quality", eval.Name);
        Assert.Equal("reasoning", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        // PlanFormulationQuality requires either a "plan" metadata key or plan-like structure in the response.
        var eval = AgenticEvaluatorFixture.BuildEvaluator("plan_formulation_quality", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "How should I deploy this microservice?",
            Response: "1. Build the Docker image. 2. Push to container registry. 3. Deploy to Kubernetes. 4. Verify health checks.",
            Metadata: new Dictionary<string, object>
            {
                ["plan"] = "1. Build the Docker image. 2. Push to registry. 3. Deploy to K8s. 4. Verify.",
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"plan_formulation_quality: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("plan_formulation_quality", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "How should I deploy this microservice?",
            Response: "1. Just do it. 2. Hope it works.",
            Metadata: new Dictionary<string, object>
            {
                ["plan"] = "1. Just do it. 2. Hope it works.",
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"plan_formulation_quality: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingRequiredInput_ReturnsSkipped()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("plan_formulation_quality", new FixedScoreEvaluator(100));
        // No plan metadata and no plan-like structure in response
        var input = new EvalInput(Query: "How should I deploy?", Response: "Use a cloud provider.");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }
}
