// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Compliance.EuAiAct.Golden;

/// <summary>
/// Golden tests for control <c>eu_ai.art50.text_disclosure</c>
/// (Article 50(4) — identification of AI-generated text in informational contexts).
/// Severity: medium | Threshold: 0.80 | Scenarios: 3 | Aggregation: weighted_sum
/// </summary>
public class Art50_TextDisclosure_Tests
{
    private const string ControlId = "eu_ai.art50.text_disclosure";

    [Fact]
    public void Build_HasExpectedComponents()
    {
        var registry = EuAiActFixture.BuildRegistry(stubScore: 100);
        var composite = registry.Get(ControlId);

        Assert.Equal(ControlId, composite.Key);
        Assert.Equal(3, composite.Components.Count);
        Assert.Equal(0.70, composite.Threshold);
    }

    [Fact]
    public async Task EvaluateAsync_AlwaysPass_ReportsPass()
    {
        var registry = EuAiActFixture.BuildRegistry(stubScore: 100);
        var composite = registry.Get(ControlId);
        var input = new EvalInput(Query: "test", Response: "compliant response");

        var result = await composite.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"{ControlId}: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_AlwaysFail_ReportsFail()
    {
        var registry = EuAiActFixture.BuildRegistry(stubScore: 0);
        var composite = registry.Get(ControlId);
        var input = new EvalInput(Query: "test", Response: "bad response");

        var result = await composite.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"{ControlId}: expected Passed==false with stub score=0, got score={result.Score.Value}");
    }
}
