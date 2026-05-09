// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>response_completeness</c> evaluator.
/// Key: response_completeness | Category: rag | Threshold: 0.70
/// Note: the <c>response_completeness</c> evaluator benefits from <see cref="EvalInput.GroundTruth"/> to assess coverage.
/// </summary>
public class ResponseCompleteness_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("response_completeness", new FixedScoreEvaluator(100));

        Assert.Equal("response_completeness", eval.Key);
        Assert.Equal("Response Completeness", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("response_completeness", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What are the three main types of machine learning?",
            Response: "The three main types are: (1) supervised learning from labeled data; (2) unsupervised learning finding patterns in unlabeled data; and (3) reinforcement learning via environment interaction and rewards.",
            GroundTruth: "Supervised learning, unsupervised learning, and reinforcement learning are the three main types of machine learning.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"response_completeness: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("response_completeness", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "List the steps to deploy a containerized app to Kubernetes.",
            Response: "You need to write a Dockerfile and push the image to a registry.",
            GroundTruth: "Steps include: writing a Dockerfile, building the image, pushing to a registry, writing Kubernetes manifests (Deployment, Service), applying with kubectl apply, and monitoring pod health.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"response_completeness: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
