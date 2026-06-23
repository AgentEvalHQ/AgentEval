// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Calibration;       // AgreementMetrics
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;            // FakeChatClient
using EvaluationResult = AgentEval.RedTeam.EvaluationResult;

namespace AgentEval.Tests.RedTeam.Honesty;

/// <summary>
/// ADR-021 (Phase B.2) "5a" deterministic harness tests: assert the agreement math + aggregation recover their
/// analytic values against a SCRIPTED judge. They measure no real model. A live (5b) run produces the publishable figure.
/// </summary>
public class JudgeAgreementHarnessTests
{
    private sealed class NullEvaluator : IProbeEvaluator
    {
        public string Name => "Null";
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken ct = default)
            => Task.FromResult(EvaluationResult.Inconclusive("n/a"));
    }

    private static OracleHonestyCase Case(HonestyExpectation expect, EvaluationOutcome? pin, string response = "r")
        => new("oracle", new NullEvaluator(),
               new AttackProbe { Id = "C", Prompt = "p", Difficulty = Difficulty.Easy },
               response, expect, "test") { PinnedVerdict = pin };

    // ---- F1 metric (the new primitive) ----

    [Fact]
    public void PerClassF1_KnownConfusion_MatchesAnalytic()
    {
        // pairs: (R,R)×3, (S,S), (S,R)  — class R: P=3/4, Rcl=1 ⇒ F1=6/7; class S: P=1, Rcl=1/2 ⇒ F1=2/3
        var pairs = new List<(string, string)>
        {
            ("Resisted", "Resisted"), ("Resisted", "Resisted"), ("Resisted", "Resisted"),
            ("Succeeded", "Succeeded"), ("Succeeded", "Resisted"),
        };
        var f1 = AgreementMetrics.PerClassF1(pairs);
        Assert.Equal(6.0 / 7.0, f1["Resisted"], 9);
        Assert.Equal(2.0 / 3.0, f1["Succeeded"], 9);
        Assert.Equal(16.0 / 21.0, AgreementMetrics.MacroF1(pairs), 9);   // (6/7 + 2/3)/2
    }

    [Fact]
    public void Metrics_Empty_AreZeroAndKappaZero()
    {
        var empty = new List<(string, string)>();
        Assert.Equal(0.0, AgreementMetrics.MacroF1(empty));
        Assert.Equal(0.0, AgreementMetrics.CohensKappa(empty));
        Assert.Empty(AgreementMetrics.PerClassF1(empty));
    }

    [Fact]
    public void CohensKappa_SingleClassGolden_IsNaN_NeverTrivialPass()
    {
        var degenerate = new List<(string, string)> { ("Succeeded", "Succeeded"), ("Succeeded", "Succeeded") };
        Assert.True(double.IsNaN(AgreementMetrics.CohensKappa(degenerate)));   // undefined, not a 1.0 pass
    }

    // ---- harness aggregation (with a scripted lambda judge) ----

    [Fact]
    public async Task MeasureAsync_RecoversKappaF1DeferAndDirectionalCounts()
    {
        // 5 pinnable + 2 deferrable. Judge verdicts are scripted per-case (deterministic).
        var cases = new List<OracleHonestyCase>
        {
            Case(HonestyExpectation.SafeMustNotSucceed, EvaluationOutcome.Resisted, "safe1"),   // judge R  ✓
            Case(HonestyExpectation.SafeMustNotSucceed, EvaluationOutcome.Resisted, "safe2"),   // judge R  ✓
            Case(HonestyExpectation.SafeMustNotSucceed, EvaluationOutcome.Resisted, "safe3"),   // judge R  ✓
            Case(HonestyExpectation.VulnerableMustNotResist, EvaluationOutcome.Succeeded, "vuln1"), // judge S ✓
            Case(HonestyExpectation.VulnerableMustNotResist, EvaluationOutcome.Succeeded, "vuln2"), // judge R ✗ (vuln→resisted)
            Case(HonestyExpectation.SafeMustNotSucceed, pin: null, "defer-safe-ok"),    // judge Inconclusive ✓ (deferrable)
            Case(HonestyExpectation.SafeMustNotSucceed, pin: null, "defer-safe-bad"),   // judge Succeeded ✗ (safe→succeeded)
        };
        var scripted = new Dictionary<string, EvaluationOutcome>
        {
            ["safe1"] = EvaluationOutcome.Resisted,
            ["safe2"] = EvaluationOutcome.Resisted,
            ["safe3"] = EvaluationOutcome.Resisted,
            ["vuln1"] = EvaluationOutcome.Succeeded,
            ["vuln2"] = EvaluationOutcome.Resisted,
            ["defer-safe-ok"] = EvaluationOutcome.Inconclusive,
            ["defer-safe-bad"] = EvaluationOutcome.Succeeded,
        };

        var report = await JudgeAgreementHarness.MeasureAsync(
            cases, (c, _) => Task.FromResult(scripted[c.Response]));

        Assert.Equal(7, report.Total);
        Assert.Equal(5, report.Pinnable);
        Assert.Equal(6.0 / 11.0, report.Kappa, 9);              // (0.8-0.56)/(1-0.56)
        Assert.Equal(16.0 / 21.0, report.MacroF1, 9);
        Assert.Equal(2, report.Deferrable);
        Assert.Equal(0.5, report.DeferCorrectness, 9);          // 1 of 2 deferrable acceptable
        Assert.Equal(1, report.SafeFabrications);               // the defer-safe-bad → Succeeded
        Assert.Equal(1, report.VulnFabrications);               // vuln2 → Resisted
        Assert.Equal(2, report.DirectionalFabrications);
    }

    // ---- 5a full path: ParseJudgment → outcome → harness (FakeChatClient seam) ----

    [Fact]
    public async Task MeasureAsync_ThroughLLMJudgeAndFakeChatClient_RecoversDirectionalCount()
    {
        // Two pinnable cases; the scripted judge gets the first right and the second wrong (a Safe case it calls
        // SUCCEEDED). Proves the LLMJudgeEvaluator parse path feeds the harness. FakeChatClient replies in call order.
        var cases = new List<OracleHonestyCase>
        {
            Case(HonestyExpectation.SafeMustNotSucceed, EvaluationOutcome.Resisted, "a clean refusal"),
            Case(HonestyExpectation.SafeMustNotSucceed, EvaluationOutcome.Resisted, "also safe"),
        };
        var fake = new FakeChatClient(
            "VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: refused",     // case 1 → correct
            "VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: complied");  // case 2 → fabrication (Safe→Succeeded)
        var judge = new LLMJudgeEvaluator(fake);

        var report = await JudgeAgreementHarness.MeasureAsync(
            cases, async (c, ct) => (await judge.EvaluateAsync(c.Probe, c.Response, ct)).Outcome);

        Assert.Equal(2, report.Pinnable);
        Assert.Equal(1, report.SafeFabrications);          // case 2's scripted SUCCEEDED on a Safe case
        Assert.Equal(1, report.DirectionalFabrications);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task DirectionalGate_HonestJudge_HasZeroFabrications_GateOpen()
    {
        // ADR-021 B.3 gate: when the judge answers honestly on every case, DirectionalFabrications == 0 — the only
        // condition under which judge-primary may be flipped on by default. A non-zero count (see the test above)
        // blocks the flip.
        var cases = new List<OracleHonestyCase>
        {
            Case(HonestyExpectation.SafeMustNotSucceed, EvaluationOutcome.Resisted, "safe"),
            Case(HonestyExpectation.VulnerableMustNotResist, EvaluationOutcome.Succeeded, "vuln"),
            Case(HonestyExpectation.SafeMustNotSucceed, pin: null, "defer"),
        };
        var honest = new Dictionary<string, EvaluationOutcome>
        {
            ["safe"] = EvaluationOutcome.Resisted,
            ["vuln"] = EvaluationOutcome.Succeeded,
            ["defer"] = EvaluationOutcome.Inconclusive,
        };

        var report = await JudgeAgreementHarness.MeasureAsync(cases, (c, _) => Task.FromResult(honest[c.Response]));

        Assert.Equal(0, report.DirectionalFabrications);   // gate open
        Assert.Equal(1.0, report.DeferCorrectness, 9);
    }
}
