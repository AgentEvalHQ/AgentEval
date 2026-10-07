// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;
using AgentEval.Metrics.Safety;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>
/// <see cref="SafetyMetricGate"/> gives its metric only the inspected text. A metric that needs a retrieved context
/// (GroundednessMetric) was never measured there and blocked every message; it is now refused when the gate is built.
/// </summary>
public class SafetyMetricGateTests
{
    [Fact]
    public void AMetricThatNeedsARetrievedContext_IsRefused_WhenTheGateIsBuilt()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SafetyMetricGate(new GroundednessMetric(new ScriptedChatClient())));

        Assert.Contains("needs a retrieved context", ex.Message, StringComparison.Ordinal);
        Assert.Contains("block every message", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMetricThatDeclaresItNeedsAReference_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new SafetyMetricGate(new Metric(MetricResult.Pass("m", 100), MetricCategory.Safety | MetricCategory.RequiresGroundTruth)));

        Assert.Contains("needs a reference answer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMetricThatIsNotMeasuredAtRunTime_StillBlocks_AndSaysItWasNotMeasured()
    {
        var gate = new SafetyMetricGate(new Metric(MetricResult.NotMeasured("m", "the classifier was unavailable")));

        var verdict = await gate.InspectAsync("some text");

        Assert.Equal(GateAction.Block, verdict.Action);
        Assert.Equal("not measured, so not shown safe: the classifier was unavailable", verdict.Reason);
    }

    [Fact]
    public async Task AMeasuredFailure_KeepsTheMetricsOwnReason()
    {
        var gate = new SafetyMetricGate(new Metric(MetricResult.Fail("m", "toxic")));

        Assert.Equal("toxic", (await gate.InspectAsync("nasty")).Reason);
    }

    private sealed class Metric(MetricResult result, MetricCategory categories = MetricCategory.Safety) : ISafetyMetric
    {
        public string Name => "m";

        public string Description => "Test double.";

        public MetricCategory Categories => categories;

        public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
