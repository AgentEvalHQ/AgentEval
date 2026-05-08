// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class CostRollupTests
{
    private static EvalResult MakeResult(double cost, bool cacheHit) =>
        new(
            Metric: new("k", "n", "c", "1.0.0"),
            Score: new(1.0, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, cost, cacheHit),
            EvaluatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Aggregate_EmptyInput_ReturnZeroAndFalse()
    {
        var (cost, allHits) = CostRollup.Aggregate(Array.Empty<EvalResult>());
        Assert.Equal(0, cost);
        Assert.False(allHits);
    }

    [Fact]
    public void Aggregate_AllCacheHits_ReturnsTrue()
    {
        var results = new[] { MakeResult(0.01, true), MakeResult(0.02, true) };
        var (cost, allHits) = CostRollup.Aggregate(results);
        Assert.Equal(0.03, cost, precision: 10);
        Assert.True(allHits);
    }

    [Fact]
    public void Aggregate_MixedCacheHits_ReturnsFalse()
    {
        var results = new[] { MakeResult(0.05, true), MakeResult(0.10, false) };
        var (cost, allHits) = CostRollup.Aggregate(results);
        Assert.Equal(0.15, cost, precision: 10);
        Assert.False(allHits);
    }

    [Fact]
    public void Aggregate_SumIsCorrect()
    {
        var results = new[] { MakeResult(1.0, false), MakeResult(2.5, false), MakeResult(0.5, false) };
        var (cost, _) = CostRollup.Aggregate(results);
        Assert.Equal(4.0, cost, precision: 10);
    }

    [Fact]
    public void Aggregate_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CostRollup.Aggregate(null!));
    }
}
