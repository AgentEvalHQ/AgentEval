// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// The property-based honesty net (Jun14v2 retrospective — see OracleHonestyCorpus). One Theory over the whole corpus
// asserting the invariant that is ALWAYS a bug if violated: a clearly-safe response must never score Succeeded, and a
// clearly-vulnerable response must never score Resisted. Inconclusive satisfies both, so a failure is always a genuine
// honesty violation (a fabricated compromise or an under-reported hit), never a label nit.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;   // LLMJudgeOptions
using AgentEval.Testing;              // FakeChatClient
using AgentEval.Tests.RedTeam.Honesty;

namespace AgentEval.Tests.RedTeam.Honesty;

public class OracleHonestyInvariantTests
{
    public static IEnumerable<object[]> Cases =>
        Enumerable.Range(0, OracleHonestyCorpus.All.Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Oracle_NeverFabricatesAVerdict(int index)
    {
        var c = OracleHonestyCorpus.All[index];
        var result = await c.Evaluator.EvaluateAsync(c.Probe, c.Response);

        var snippet = c.Response.Length <= 90 ? c.Response : c.Response[..90] + "…";
        var where = $"[{c.Oracle}] {c.Origin} → {result.Outcome}\n  response: \"{snippet}\"";

        if (c.Expect == HonestyExpectation.SafeMustNotSucceed)
            Assert.False(result.Outcome == EvaluationOutcome.Succeeded,
                $"HONESTY VIOLATION — a SAFE response was scored Succeeded (fabricated compromise).\n  {where}");
        else
            Assert.False(result.Outcome == EvaluationOutcome.Resisted,
                $"HONESTY VIOLATION — a VULNERABLE response was scored Resisted (under-reported hit).\n  {where}");
    }

    // ADR-021 (B.1 §6) routing/plumbing invariant: under judge-primary with an ABSTAINING judge, a Semantic+Verbal
    // probe routes to the judge, the judge abstains (Inconclusive), and the advisory keyword verdict must ship
    // UNCORRUPTED — the routing, the IntentToAct/Verbal cap, and the asymmetric guard must not themselves manufacture
    // or mask a verdict. (This tests the plumbing, NOT the judge's judgment — the fake abstains on every case.)
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task JudgePrimary_AbstainingJudge_RoutingNeverFabricates(int index)
    {
        var c = OracleHonestyCorpus.All[index];
        var abstaining = new FakeChatClient("VERDICT: INCONCLUSIVE\nCONFIDENCE: 0.3\nREASON: cannot tell");
        var grader = new JudgeBackedEvaluator(
            c.Evaluator, abstaining, JudgeMode.Primary, new LLMJudgeOptions(), includeEvidence: true, judgeTimeout: null);

        var result = await grader.EvaluateAsync(c.Probe, c.Response);

        var snippet = c.Response.Length <= 90 ? c.Response : c.Response[..90] + "…";
        var where = $"[{c.Oracle}] {c.Origin} → {result.Outcome} (judge-primary, abstaining)\n  response: \"{snippet}\"";

        if (c.Expect == HonestyExpectation.SafeMustNotSucceed)
            Assert.False(result.Outcome == EvaluationOutcome.Succeeded,
                $"ROUTING HONESTY VIOLATION — judge-primary routing fabricated Succeeded on a SAFE response.\n  {where}");
        else
            Assert.False(result.Outcome == EvaluationOutcome.Resisted,
                $"ROUTING HONESTY VIOLATION — judge-primary routing masked a hit (Resisted) on a VULNERABLE response.\n  {where}");
    }

    [Fact]
    public void Corpus_IsNonTrivial_AndCoversBothDirections()
    {
        Assert.True(OracleHonestyCorpus.All.Count >= 40, "the honesty corpus should be substantial");
        Assert.Contains(OracleHonestyCorpus.All, c => c.Expect == HonestyExpectation.SafeMustNotSucceed);
        Assert.Contains(OracleHonestyCorpus.All, c => c.Expect == HonestyExpectation.VulnerableMustNotResist);
        // every wired lexical oracle is represented
        foreach (var oracle in new[] { "Misinformation", "DataPoisoning", "ExcessiveAgency", "SupplyChain", "Jailbreak", "InsecureOutput", "InferenceAPIAbuse" })
            Assert.Contains(OracleHonestyCorpus.All, c => c.Oracle == oracle);
    }
}
