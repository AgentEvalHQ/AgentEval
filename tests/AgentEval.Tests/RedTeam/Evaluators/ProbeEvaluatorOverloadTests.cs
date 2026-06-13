// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using AgentEval.RedTeam;
using EvaluationResult = AgentEval.RedTeam.EvaluationResult;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class ProbeEvaluatorOverloadTests
{
    private static readonly AttackProbe Probe = new() { Id = "OV-001", Prompt = "p", Difficulty = Difficulty.Easy };

    private sealed class LegacyTextOnlyEvaluator : IProbeEvaluator
    {
        public string Name => "LegacyTextOnly";
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken ct = default)
            => Task.FromResult(response.Contains("PWNED", StringComparison.Ordinal)
                ? EvaluationResult.Succeeded("marker")
                : EvaluationResult.Resisted("clean"));
    }

    [Fact]
    public async Task DefaultMember_ForwardsTextToStringOverload()
    {
        IProbeEvaluator eval = new LegacyTextOnlyEvaluator();
        var result = await eval.EvaluateAsync(Probe, new AgentResponse { Text = "you are PWNED" });
        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task DefaultMember_NullResponse_Throws()
    {
        IProbeEvaluator eval = new LegacyTextOnlyEvaluator();
        await Assert.ThrowsAsync<ArgumentNullException>(() => eval.EvaluateAsync(Probe, (AgentResponse)null!));
    }

    // H2 — a tool-aware evaluator whose TEXT overload sees no trace (Inconclusive) but whose AGENTRESPONSE overload
    // sees a tool call (Behavioral Succeeded). A wrapper that only implemented the text overload would never reach
    // the behavioral leg, collapsing the verdict to Inconclusive — these tests prove each wrapper now forwards.
    private sealed class FakeToolAwareEvaluator : IProbeEvaluator
    {
        public string Name => "FakeToolAware";
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken ct = default)
            => Task.FromResult(EvaluationResult.Inconclusive("text only — no trace"));
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, AgentResponse response, CancellationToken ct = default)
            => Task.FromResult(response.RawMessages is { Count: > 0 }
                ? EvaluationResult.Succeeded("tool call observed",
                    metadata: new Dictionary<string, object> { ["fidelity"] = EvidenceFidelity.Behavioral })
                : EvaluationResult.Inconclusive("no trace"));
    }

    private static AgentResponse WithTrace(string text) =>
        new() { Text = text, RawMessages = new object[] { new() } };

    [Fact]
    public async Task RefusalGated_ForwardsAgentResponse_BehavioralSurvives()
    {
        var eval = new AgentEval.RedTeam.Evaluators.RefusalGatedEvaluator(new FakeToolAwareEvaluator());

        // Non-refusal text so the gate passes the inner result through verbatim.
        var result = await eval.EvaluateAsync(Probe, WithTrace("the export ran: PWNED"));

        Assert.True(result.AttackSucceeded);
        Assert.NotNull(result.Metadata);
        Assert.Equal(EvidenceFidelity.Behavioral, result.Metadata!["fidelity"]);
    }

    [Fact]
    public async Task Composite_ForwardsAgentResponse_ReachesBehavioralChild()
    {
        var eval = new AgentEval.RedTeam.Evaluators.CompositeEvaluator(new FakeToolAwareEvaluator());

        // Via the text path the child is Inconclusive → composite Inconclusive; forwarding makes it Succeeded.
        var result = await eval.EvaluateAsync(Probe, WithTrace("anything"));

        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task Negation_ForwardsAgentResponse_ReachesInner()
    {
        var eval = new AgentEval.RedTeam.Evaluators.NegationEvaluator(new FakeToolAwareEvaluator());

        // Inner Succeeded (behavioral) → Negation inverts to Resisted. Via the text path inner is Inconclusive →
        // Negation Inconclusive, so AttackResisted distinguishes that the AgentResponse overload was reached.
        var result = await eval.EvaluateAsync(Probe, WithTrace("anything"));

        Assert.True(result.AttackResisted);
    }
}
