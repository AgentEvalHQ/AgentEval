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
/// that guards <see cref="Query"/>'s recursive cost-tier walker (<c>WalkAndAccumulate</c>).
/// Canonical evaluator timelines use flat recorded results rather than recursive legacy traversal
/// against pathological / hostile composite trees. Without the cap a deep tree
/// would stack-overflow the resolver thread.
/// </summary>
public class MaxTreeWalkDepthTests
{
    [Fact]
    public void WalkAndAccumulate_DepthExceedsCap_DoesNotStackOverflow_AndCapsAccumulation()
    {
        // Build a 40-deep linear chain where each level carries EstimatedCost 1.0 (key not
        // registered in EvaluatorCostMap). With the cap at 32 the walker stops at depth 32
        // and only that composite's 1.0 is counted.
        var deepTree = BuildLinearChain(depth: 40, costPerLevel: 1.0);

        double trivial = 0, low = 0, medium = 0, high = 0, unknown = 0;

        // The walker must return cleanly; an absent cap would stack-overflow at
        // the JIT-default ~16k frame depth before that, but 40 is also a smaller
        // depth that asserts the EARLY return logic engages.
        Query.WalkAndAccumulate(deepTree, ref trivial, ref low, ref medium, ref high, ref unknown);

        // The walk descends through depths 0..32 and stops at the composite on the cap (depth 32): its own cost stands
        // for the subtree it does not walk, counted once (T83). Composites above it are walked, not added, so the
        // unknown bucket holds that one level's cost (1.0), not 33 levels' worth.
        Assert.Equal(1.0, unknown);
        Assert.True(unknown < 40,
            $"Sanity: expected the depth cap to engage and prevent walking all 40 levels; got {unknown}.");
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
