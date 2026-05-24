// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr.Golden;

/// <summary>
/// Golden tests for control <c>gdpr.art5_2.accountability</c> (Article 5(2) —
/// accountability principle: controller responsible for AND able to demonstrate
/// compliance with Art 5(1)(a)-(f); operationalised through Art 24(1)/(2)/(3)
/// and concrete demonstrability evidence in Art 7(1) / Art 30 / Art 33(5) /
/// Art 35 / Art 28(3)(h); independently sanctionable under Art 83(5)(a)).
/// Plan-13 T1.1 Pillar 6 Governance.
/// Severity: high | Threshold: 0.70 | Scenarios: 4 | Aggregation: weighted_sum
/// </summary>
public class Art5_2_Accountability_Tests
{
    private const string ControlId = "gdpr.art5_2.accountability";

    [Fact]
    public void Build_HasExpectedComponents()
    {
        var registry = GdprFixture.BuildRegistry(stubScore: 100);
        var composite = registry.Get(ControlId);

        Assert.Equal(ControlId, composite.Key);
        Assert.Equal(4, composite.Components.Count);
        Assert.Equal(0.70, composite.Threshold);
    }

    [Fact]
    public async Task EvaluateAsync_AlwaysPass_ReportsPass()
    {
        var registry = GdprFixture.BuildRegistry(stubScore: 100);
        var composite = registry.Get(ControlId);
        var input = new EvalInput(Query: "test", Response: "compliant response");

        var result = await composite.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"{ControlId}: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_AlwaysFail_ReportsFail()
    {
        var registry = GdprFixture.BuildRegistry(stubScore: 0);
        var composite = registry.Get(ControlId);
        var input = new EvalInput(Query: "test", Response: "bad response");

        var result = await composite.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"{ControlId}: expected Passed==false with stub score=0, got score={result.Score.Value}");
    }
}
