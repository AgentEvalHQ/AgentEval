// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using AgentEval.Evals;
using AgentEval.MissionControl.GraphQL;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// T63: the run cost breakdown counts the leaves whose cost was not priced instead of summing them as $0. A
/// composite whose cost is null because a part was not priced is not counted itself; its unpriced leaves are.
/// </summary>
public sealed class CostBreakdownNotPricedTests
{
    private static EvalResult Leaf(string key, double? cost) => new(
        Metric: new EvalMetadata(key, key, "test", "1.0.0"),
        Score: new EvalScore(0.9, null, "pass", true, null, "none", null),
        Details: new EvalDetails(null, null, null, null, null),
        Provenance: new EvalProvenance("atomic-llm", "judge", null, null, null, cost, false),
        EvaluatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void UnpricedLeaves_AreCounted_PricedLeavesAreSummed()
    {
        var tree = new EvalResult(
            Metric: new EvalMetadata("root", "Root", "composite", "1.0.0"),
            Score: new EvalScore(0.9, null, "pass", true, null, "none", null),
            Details: new EvalDetails(null, null, null, [Leaf("priced-a", 0.001), Leaf("unpriced", null), Leaf("priced-b", 0.002)], null),
            Provenance: new EvalProvenance("composite", null, null, null, null, null, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
        double trivial = 0, low = 0, medium = 0, high = 0, unknown = 0;
        int unpriced = 0;

        Query.WalkAndAccumulate(tree, ref trivial, ref low, ref medium, ref high, ref unknown, ref unpriced);

        Assert.Equal(1, unpriced);                     // the unpriced leaf, not also its composite
        Assert.Equal(0.003, unknown, precision: 10);   // the keys are not in EvaluatorCostMap
    }
}

#endif
