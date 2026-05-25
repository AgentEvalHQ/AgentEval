// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr.Golden;

/// <summary>
/// Golden tests for control <c>gdpr.art44_49.international_transfers</c>
/// (Articles 44-49 — international data transfers: Schrems II, SCCs (2021/914),
/// BCRs, Art 45 adequacy (incl. EU-US DPF), Art 48 third-country-order conflict,
/// Art 49 narrow derogations with EDPB Guidelines 2/2018 restrictive
/// interpretation, EDPB Recommendations 01/2020 TIA methodology).
/// Plan-13 T1.1 Pillar 6 Governance.
/// Severity: high | Threshold: 0.70 | Scenarios: 4 | Aggregation: weighted_sum
/// </summary>
public class Art44_49_InternationalTransfers_Tests
{
    private const string ControlId = "gdpr.art44_49.international_transfers";

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
