// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.JudgeQuality;
using Xunit;

namespace AgentEval.Tests.Agentic.JudgeQuality;

/// <summary>
/// Unit tests for <see cref="JudgeDriftEval"/>.
/// </summary>
public class JudgeDriftEvalTests
{
    private static EvalInput BuildInput(
        IReadOnlyDictionary<string, double> snapshotA,
        IReadOnlyDictionary<string, double> snapshotB) =>
        new(Query: "test", Metadata: new Dictionary<string, object>
        {
            ["snapshot_a"] = snapshotA,
            ["snapshot_b"] = snapshotB,
        });

    [Fact]
    public async Task EvaluateAsync_ZeroDrift_Passes()
    {
        var eval = new JudgeDriftEval(passThreshold: 0.05);
        var snapshotA = new Dictionary<string, double>
        {
            ["task_completion"] = 0.90,
            ["groundedness"] = 0.85,
        };
        var snapshotB = new Dictionary<string, double>
        {
            ["task_completion"] = 0.90,
            ["groundedness"] = 0.85,
        };

        var result = await eval.EvaluateAsync(BuildInput(snapshotA, snapshotB));

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal(1.0, result.Score.Value, precision: 5);
        Assert.Equal(0.0, result.Details.Dimensions!["max_delta"], precision: 5);
    }

    [Fact]
    public async Task EvaluateAsync_MaxDrift_Fails()
    {
        var eval = new JudgeDriftEval(passThreshold: 0.05);
        // One evaluator drifted by 1.0 (max possible)
        var snapshotA = new Dictionary<string, double> { ["task_completion"] = 0.0 };
        var snapshotB = new Dictionary<string, double> { ["task_completion"] = 1.0 };

        var result = await eval.EvaluateAsync(BuildInput(snapshotA, snapshotB));

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
        // score = 1.0 - 1.0 = 0.0
        Assert.Equal(0.0, result.Score.Value, precision: 5);
        Assert.Equal(1.0, result.Details.Dimensions!["max_delta"], precision: 5);
    }

    [Fact]
    public async Task EvaluateAsync_IntermediateDrift_ReflectsMaxDelta()
    {
        var eval = new JudgeDriftEval(passThreshold: 0.05);
        // Two evaluators with different deltas: 0.03 and 0.12; max_delta = 0.12
        var snapshotA = new Dictionary<string, double>
        {
            ["task_completion"] = 0.90,
            ["groundedness"] = 0.80,
        };
        var snapshotB = new Dictionary<string, double>
        {
            ["task_completion"] = 0.87,  // delta = 0.03
            ["groundedness"] = 0.68,     // delta = 0.12
        };

        var result = await eval.EvaluateAsync(BuildInput(snapshotA, snapshotB));

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
        // score = 1.0 - 0.12 = 0.88
        Assert.Equal(0.88, result.Score.Value, precision: 5);
        Assert.Equal(0.12, result.Details.Dimensions!["max_delta"], precision: 5);
        // Both evaluator deltas should be present in dimensions
        Assert.True(result.Details.Dimensions!.ContainsKey("shared_keys"));
        Assert.Equal(2.0, result.Details.Dimensions!["shared_keys"], precision: 5);
    }
}
