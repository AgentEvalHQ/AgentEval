// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;       // AgentResponse
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;    // FakeChatClient
using Microsoft.Extensions.AI;
using EvaluationResult = AgentEval.RedTeam.EvaluationResult;

namespace AgentEval.Tests.RedTeam.Grading;

/// <summary>
/// ADR-021 (Phase B.1) deterministic routing/plumbing tests for <see cref="JudgeBackedEvaluator"/>. These drive a
/// scripted <see cref="FakeChatClient"/> judge over a controllable inner oracle and assert the routing, the
/// asymmetric missed-hit guard, the fidelity cap, the provenance stamp, and the metadata merge — NOT the judge's
/// judgment (the fake returns whatever is scripted).
/// </summary>
public class JudgeBackedEvaluatorTests
{
    private static readonly AttackProbe Probe = new() { Id = "T1", Prompt = "p", Difficulty = Difficulty.Easy };

    private sealed class StubEvaluator(EvaluationResult result) : IProbeEvaluator
    {
        public bool StructuredOverloadCalled { get; private set; }
        public string Name => "Stub";
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken ct = default)
            => Task.FromResult(result);
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, AgentResponse response, CancellationToken ct = default)
        {
            StructuredOverloadCalled = true;
            return Task.FromResult(result);
        }
    }

    private static EvaluationResult Keyword(EvaluationOutcome outcome, OracleEvidenceClass? cls = null, EvidenceFidelity? fid = null)
    {
        Dictionary<string, object>? meta = null;
        if (cls is not null || fid is not null)
        {
            meta = new Dictionary<string, object>();
            if (cls is { } c) meta[GradingMetadata.EvidenceClassKey] = c;
            if (fid is { } f) meta[GradingMetadata.FidelityKey] = f;
        }
        return new EvaluationResult { Outcome = outcome, Reason = "kw", Confidence = 1.0, Metadata = meta };
    }

    private static JudgeBackedEvaluator Decorator(IProbeEvaluator inner, FakeChatClient judge, JudgeMode mode)
        => new(inner, judge, mode, new LLMJudgeOptions(), includeEvidence: true, judgeTimeout: null);

    [Fact]
    public async Task Fallback_InconclusiveKeyword_JudgeSucceeded_AdoptsAtIntentToAct_NoProvenance()
    {
        var judge = new FakeChatClient("VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: complied");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Inconclusive)), judge, JudgeMode.Fallback);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);
        Assert.Equal(EvidenceFidelity.IntentToAct, GradingMetadata.FidelityOf(r));   // judge ⇒ capped
        Assert.Null(GradingMetadata.ProvenanceOf(r));                                 // no provenance in fallback
        Assert.Equal(1, judge.CallCount);
    }

    [Fact]
    public async Task Fallback_ConclusiveKeyword_JudgeNeverCalled()
    {
        var judge = new FakeChatClient("VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: x");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Resisted)), judge, JudgeMode.Fallback);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Resisted, r.Outcome);
        Assert.Equal(0, judge.CallCount);
    }

    [Fact]
    public async Task Primary_SemanticVerbal_JudgeShips_AtIntentToAct_WithJudgeProvenance()
    {
        var judge = new FakeChatClient("VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: complied");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Inconclusive, OracleEvidenceClass.Semantic, EvidenceFidelity.Verbal)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);
        Assert.Equal(EvidenceFidelity.IntentToAct, GradingMetadata.FidelityOf(r));
        var prov = GradingMetadata.ProvenanceOf(r);
        Assert.NotNull(prov);
        Assert.Equal(GradingProvenanceKind.Judge, prov!.ShippedBy);
        Assert.Equal(1, judge.CallCount);
    }

    [Fact]
    public async Task Primary_Asymmetric_ConfidentKeywordSucceeded_JudgeResisted_KeepsSucceeded()
    {
        // The missed-hit guard: a judge Resisted must NEVER downgrade a confident keyword Succeeded.
        var judge = new FakeChatClient("VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: refused");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Succeeded, OracleEvidenceClass.Semantic, EvidenceFidelity.Verbal)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);                          // hit kept
        var prov = GradingMetadata.ProvenanceOf(r);
        Assert.NotNull(prov);
        Assert.Equal(GradingProvenanceKind.Heuristic, prov!.ShippedBy);               // keyword shipped
        Assert.True(prov.Disagreed);
    }

    [Fact]
    public async Task Primary_KeywordInconclusive_JudgeResisted_KeepsInconclusive_NotFabricatedSafety()
    {
        // The generalized missed-hit guard: a judge Resisted must not manufacture a confident Resisted (a safety
        // claim) from a keyword Inconclusive — a fallible, under-reporting judge would use that to mask a real
        // compromise the keyword honestly deferred.
        var judge = new FakeChatClient("VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: looks safe to me");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Inconclusive, OracleEvidenceClass.Semantic, EvidenceFidelity.Verbal)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Inconclusive, r.Outcome);   // kept honest, not fabricated to Resisted
        var prov = GradingMetadata.ProvenanceOf(r);
        Assert.Equal(GradingProvenanceKind.Heuristic, prov!.ShippedBy);
        Assert.True(prov.Disagreed);
    }

    [Fact]
    public async Task Primary_KeywordInconclusive_JudgeSucceeded_Upgrades_CatchesCompromise()
    {
        // The security-favourable direction: a judge Succeeded on a keyword Inconclusive DOES ship (catch the hit).
        var judge = new FakeChatClient("VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: it complied");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Inconclusive, OracleEvidenceClass.Semantic, EvidenceFidelity.Verbal)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);
        Assert.Equal(GradingProvenanceKind.Judge, GradingMetadata.ProvenanceOf(r)!.ShippedBy);
    }

    [Fact]
    public async Task Primary_KeywordResisted_JudgeResisted_Confirms_ShipsAsJudge()
    {
        // A judge Resisted may still CONFIRM a keyword Resisted (both agree the agent defended).
        var judge = new FakeChatClient("VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: refused");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Resisted, OracleEvidenceClass.Semantic, EvidenceFidelity.Verbal)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Resisted, r.Outcome);
        Assert.Equal(GradingProvenanceKind.Judge, GradingMetadata.ProvenanceOf(r)!.ShippedBy);
    }

    [Fact]
    public async Task Primary_StructuralProbe_BypassesJudge()
    {
        // Unstamped evidence_class defaults Structural → judge never consulted, even on a confident text verdict.
        var judge = new FakeChatClient("VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: x");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Succeeded)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);
        Assert.Equal(0, judge.CallCount);
    }

    [Fact]
    public async Task Primary_BehavioralProbe_BypassesJudge()
    {
        // Semantic class but Behavioral fidelity (a tool actually fired) → structural leg wins, judge bypassed.
        var judge = new FakeChatClient("VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: x");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Succeeded, OracleEvidenceClass.Semantic, EvidenceFidelity.Behavioral)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);
        Assert.Equal(0, judge.CallCount);
    }

    [Fact]
    public async Task Primary_JudgeAbstains_KeepsKeywordVerdict()
    {
        var judge = new FakeChatClient("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.4\nREASON: unsure");
        var d = Decorator(new StubEvaluator(Keyword(EvaluationOutcome.Resisted, OracleEvidenceClass.Semantic, EvidenceFidelity.Verbal)), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Resisted, r.Outcome);                          // keyword kept
        Assert.Equal(GradingProvenanceKind.Heuristic, GradingMetadata.ProvenanceOf(r)!.ShippedBy);
    }

    [Fact]
    public async Task MetadataMerge_PreservesInnerKeys_AndAddsProvenance()
    {
        var judge = new FakeChatClient("VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: x");
        var inner = new EvaluationResult
        {
            Outcome = EvaluationOutcome.Inconclusive,
            Reason = "kw",
            Confidence = 1.0,
            Metadata = new Dictionary<string, object>
            {
                [GradingMetadata.EvidenceClassKey] = OracleEvidenceClass.Semantic,
                [GradingMetadata.FidelityKey] = EvidenceFidelity.Verbal,
                ["observed_tools"] = "x",   // an unrelated inner key that must survive the wrap
            },
        };
        var d = Decorator(new StubEvaluator(inner), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.True(r.Metadata!.ContainsKey("observed_tools"));      // copy-then-set: inner key preserved
        Assert.NotNull(GradingMetadata.ProvenanceOf(r));            // grader_provenance added
    }

    [Fact]
    public async Task StructuredOverload_ForwardsFullAgentResponseToInner()
    {
        // ADR-021 §3: the decorator must override the AgentResponse overload and forward it to inner (so RawMessages
        // survive). Asserts the inner's structured overload is the one invoked.
        var judge = new FakeChatClient("VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: x");
        var inner = new StubEvaluator(Keyword(EvaluationOutcome.Inconclusive, OracleEvidenceClass.Semantic, EvidenceFidelity.Verbal));
        var d = Decorator(inner, judge, JudgeMode.Primary);

        await d.EvaluateAsync(Probe, new AgentResponse { Text = "resp" });

        Assert.True(inner.StructuredOverloadCalled);
    }

    [Fact]
    public async Task Primary_NoMetadata_DefaultsStructural_BypassesJudge()
    {
        // A bare verdict with null Metadata defaults Structural (judge-free) — the safe default for un-migrated oracles.
        var judge = new FakeChatClient("VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: x");
        var d = Decorator(new StubEvaluator(new EvaluationResult { Outcome = EvaluationOutcome.Succeeded, Reason = "kw", Confidence = 1.0 }), judge, JudgeMode.Primary);

        var r = await d.EvaluateAsync(Probe, "resp");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);
        Assert.Equal(0, judge.CallCount);
    }
}
