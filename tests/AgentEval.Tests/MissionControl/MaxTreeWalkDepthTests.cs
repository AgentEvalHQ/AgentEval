// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using AgentEval.Evals;
using AgentEval.MissionControl.GraphQL;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Phase-5 Task 5.4 — regression test for the <c>MaxTreeWalkDepth=32</c> cap
/// that guards <see cref="Query"/>'s recursive walkers (<c>WalkAndAccumulate</c>
/// for cost-tier aggregation, <c>FindEvaluatorNode</c> for evaluator timelines)
/// against pathological / hostile composite trees. Without the cap a deep tree
/// would stack-overflow the resolver thread.
/// </summary>
public class MaxTreeWalkDepthTests
{
    [Fact]
    public void WalkAndAccumulate_DepthExceedsCap_DoesNotStackOverflow_AndCapsAccumulation()
    {
        // Build a 40-deep linear chain where each level adds 1.0 to the "unknown"
        // bucket (metric.Key not registered in EvaluatorCostMap, EstimatedCost=1.0).
        // With the cap at 32 the walker bails after 33 levels (depth 0..32 inclusive),
        // so the accumulated unknown bucket must be ≤ 33 and strictly less than 40.
        var deepTree = BuildLinearChain(depth: 40, costPerLevel: 1.0);

        double trivial = 0, low = 0, medium = 0, high = 0, unknown = 0;

        // The walker must return cleanly; an absent cap would stack-overflow at
        // the JIT-default ~16k frame depth before that, but 40 is also a smaller
        // depth that asserts the EARLY return logic engages.
        Query.WalkAndAccumulate(deepTree, ref trivial, ref low, ref medium, ref high, ref unknown);

        // The guard at `if (depth > MaxTreeWalkDepth) return;` visits depths 0..32
        // inclusive before returning, so the unknown bucket accumulates exactly
        // MaxTreeWalkDepth+1 = 33 levels' worth of cost (1.0 each).
        Assert.Equal((double)(Query.MaxTreeWalkDepth + 1), unknown);
        Assert.True(unknown < 40,
            $"Sanity: expected the depth cap to engage and prevent walking all 40 levels; got {unknown}.");
    }

    [Fact]
    public void FindEvaluatorNode_KeyOnlyAtLeafBeyondCap_ReturnsNull()
    {
        // Put the target key ONLY on the leaf at depth 40. The cap is 32 so the
        // walker must not reach the leaf — null is the correct answer.
        var deepTree = BuildLinearChain(depth: 40, costPerLevel: 0, deepestKey: "target.at.depth.40");

        var found = Query.FindEvaluatorNode(deepTree, "target.at.depth.40");

        Assert.Null(found);
    }

    [Fact]
    public void FindEvaluatorNode_KeyAtShallowDepth_StillReturnsMatch()
    {
        // Same 40-deep tree but a different key is also on the ROOT.
        // The walker must return the root match BEFORE the cap engages — pre-order traversal.
        var deepTree = BuildLinearChain(depth: 40, costPerLevel: 0, deepestKey: "buried");
        var rooted = deepTree with
        {
            Metric = new EvalMetadata("at-root", "AtRoot", "test", "1.0.0"),
        };

        var found = Query.FindEvaluatorNode(rooted, "at-root");

        Assert.NotNull(found);
        Assert.Equal("at-root", found!.Metric.Key);
    }

    private static EvalResult BuildLinearChain(int depth, double costPerLevel, string? deepestKey = null)
    {
        EvalResult Leaf(string key) => new(
            Metric: new EvalMetadata(key, "Leaf", "test", "1.0.0"),
            Score: new EvalScore(0.5, null, "warn", false, null, "medium", null),
            Details: new EvalDetails(null, null, null, null, null),
            Provenance: new EvalProvenance("atomic-llm", null, null, null, null, costPerLevel, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        EvalResult Build(int level)
        {
            if (level == 0)
                return Leaf(deepestKey ?? "leaf-at-bottom");
            var child = Build(level - 1);
            return new EvalResult(
                Metric: new EvalMetadata($"level-{level}", $"Level{level}", "composite", "1.0.0"),
                Score: new EvalScore(0.5, null, "warn", false, null, "medium", null),
                Details: new EvalDetails(null, null, null, new[] { child }, null),
                Provenance: new EvalProvenance("composite", null, null, null, null, costPerLevel, false),
                EvaluatedAt: DateTimeOffset.UtcNow);
        }

        return Build(depth);
    }
}

#endif
