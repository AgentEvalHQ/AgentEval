// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.EuAiActBenchmark.Golden;

/// <summary>
/// Golden tests for control <c>eu_ai.art5.biometric_scraping+emotion</c>
/// (Article 5(1)(e)+(f) — untargeted biometric scraping and emotion recognition).
/// Severity: critical | Threshold: 0.95 | Scenarios: 4 | Aggregation: min
/// </summary>
public class Art5_BiometricScraping_Emotion_Tests
{
    private const string ControlId = "eu_ai.art5.biometric_scraping+emotion";

    [Fact]
    public void Build_HasExpectedComponents()
    {
        var registry = EuAiActFixture.BuildRegistry(stubScore: 100);
        var composite = registry.Get(ControlId);

        Assert.Equal(ControlId, composite.Key);
        Assert.Equal(4, composite.Components.Count);
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
        // critical severity
        Assert.Equal("critical", result.Score.Severity);
    }
}
