// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Regression-gate hardening: item 4 (persisted ConclusiveScore + ConclusiveScoreDelta) and item 5 (per-probe
// evidence-fidelity persistence + escalation detection).
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Baseline;
using AgentEval.Testing;   // FakeChatClient (judge presence for the grading-mode-mismatch tests)

namespace AgentEval.Tests.RedTeam.Baseline;

public class BaselineConclusiveAndFidelityTests
{
    // One-attack result: `resisted` resisted probes + the listed failed probes (each with an explicit fidelity).
    private static RedTeamResult Result(int resisted, int inconclusive, params (string Id, EvidenceFidelity Fidelity)[] failed)
        => Result(resisted, inconclusive, null, failed);

    private static RedTeamResult Result(int resisted, int inconclusive, ScanOptions? options, params (string Id, EvidenceFidelity Fidelity)[] failed)
    {
        var probes = new List<ProbeResult>();
        for (var i = 0; i < resisted; i++)
            probes.Add(new ProbeResult { ProbeId = $"R-{i:D3}", Prompt = "p", Response = "I cannot.", Outcome = EvaluationOutcome.Resisted, Reason = "resisted", Difficulty = Difficulty.Easy, Severity = Severity.High });
        for (var i = 0; i < inconclusive; i++)
            probes.Add(new ProbeResult { ProbeId = $"I-{i:D3}", Prompt = "p", Response = "[TIMEOUT]", Outcome = EvaluationOutcome.Inconclusive, Reason = "inconclusive", Difficulty = Difficulty.Easy, Severity = Severity.High });
        foreach (var (id, fid) in failed)
            probes.Add(new ProbeResult { ProbeId = id, Prompt = "p", Response = "PWNED", Outcome = EvaluationOutcome.Succeeded, Reason = "compromised", Difficulty = Difficulty.Easy, Severity = Severity.High, Fidelity = fid });

        return new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = probes.Count,
            ResistedProbes = resisted,
            SucceededProbes = failed.Length,
            InconclusiveProbes = inconclusive,
            Options = options,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    MitreAtlasIds = ["AML.T0051"],
                    Severity = Severity.High,
                    ResistedCount = resisted,
                    SucceededCount = failed.Length,
                    InconclusiveCount = inconclusive,
                    ProbeResults = probes,
                }
            ]
        };
    }

    // ───────────────────────── Item 4: ConclusiveScore ─────────────────────────

    [Fact]
    public void FromResult_PersistsConclusiveScore()
    {
        var result = Result(resisted: 8, inconclusive: 4, ("PI-001", EvidenceFidelity.Verbal), ("PI-002", EvidenceFidelity.Verbal));
        var baseline = RedTeamBaseline.FromResult(result, "v1");

        // ConclusiveScore = resisted / (resisted + succeeded) * 100 = 8 / 10 * 100 = 80; NOT the inconclusive-diluted
        // OverallScore (8 / 14 ≈ 57.1).
        Assert.NotNull(baseline.ConclusiveScore);
        Assert.Equal(80.0, baseline.ConclusiveScore!.Value, 1);
        Assert.NotEqual(baseline.OverallScore, baseline.ConclusiveScore!.Value);
    }

    [Fact]
    public void ConclusiveScoreDelta_ComputesFromPersistedScores()
    {
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 10, inconclusive: 0), "v1"); // Conclusive = 100
        var current = Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal));  // Conclusive = 90

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Equal(-10.0, c.ConclusiveScoreDelta, 1);
    }

    [Fact]
    public void ConclusiveScoreDelta_FallsBackToScoreDelta_ForPreFieldBaseline()
    {
        // An older baseline that never persisted ConclusiveScore (null) must not read as a huge conclusive drop;
        // the delta falls back to the overall ScoreDelta.
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 10, inconclusive: 0), "v1") with { ConclusiveScore = null };
        var current = Result(resisted: 10, inconclusive: 0);

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Equal(c.ScoreDelta, c.ConclusiveScoreDelta, 5);
    }

    // ───────────────────────── Item 5: fidelity escalation ─────────────────────────

    [Fact]
    public void PersistentVuln_EvidenceStrengthened_IsFlaggedAndDegraded()
    {
        // Same probe fails in both runs, but the evidence went Verbal → Behavioral (described → actually executed).
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal)), "v1");
        var current = Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Behavioral));

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        var esc = Assert.Single(c.FidelityEscalations);
        Assert.Equal("PI-001", esc.ProbeId);
        Assert.Equal(EvidenceFidelity.Verbal, esc.From);
        Assert.Equal(EvidenceFidelity.Behavioral, esc.To);
        Assert.Equal(RegressionStatus.Degraded, c.Status);   // surfaced, not masked as Stable
        Assert.False(c.IsStable);
        Assert.False(c.IsRegression);                        // a strengthened persistent vuln is Degraded, not a hard regression
    }

    [Fact] // ADR-021 B.1: a baseline graded under a DIFFERENT JudgeMode is not fidelity-comparable — judge-primary
    // shifts the serialized fidelity, so a cross-mode "escalation" is a grading artefact, not a regression. Suppress + flag.
    // The mismatch only matters when the CURRENT run actually USED a judge (Mode is inert without one) — so this run
    // carries a judge in Primary mode against a Fallback baseline.
    public void GradingModeMismatch_SuppressesFidelityEscalation_AndFlags()
    {
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal)), "v1")
            with { GradingMode = JudgeMode.Fallback };
        var current = Result(9, 0, new ScanOptions { Mode = JudgeMode.Primary, JudgeClient = new FakeChatClient("x") },
            ("PI-001", EvidenceFidelity.Behavioral));   // would otherwise escalate

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.True(c.GradingModeMismatch);
        Assert.Empty(c.FidelityEscalations);                 // suppressed — a grading artefact, not a regression
    }

    [Fact] // Copilot/B.3 regression: two NO-JUDGE runs whose default modes differ (old Fallback baseline vs new Primary
    // default) must NOT be flagged — Mode is inert without a judge, so suppressing real escalations here would be a bug.
    public void GradingModeMismatch_NoJudge_NotFlagged_KeepsEscalation()
    {
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal)), "v1")
            with { GradingMode = JudgeMode.Fallback };
        var current = Result(9, 0, new ScanOptions { Mode = JudgeMode.Primary },   // Primary default, but NO judge → Mode inert
            ("PI-001", EvidenceFidelity.Behavioral));

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.False(c.GradingModeMismatch);
        Assert.Single(c.FidelityEscalations);                // a real escalation is kept, not masked by a phantom mismatch
    }

    [Fact] // The escalation still fires when the modes MATCH (baseline Fallback vs current Fallback).
    public void SameGradingMode_KeepsFidelityEscalation()
    {
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal)), "v1")
            with { GradingMode = JudgeMode.Fallback };
        var current = Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Behavioral));

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.False(c.GradingModeMismatch);
        Assert.Single(c.FidelityEscalations);
    }

    [Fact] // L28: a run that BOTH resolves one vuln AND escalates another's fidelity reports Degraded — the escalation
    // check is intentionally evaluated before the resolved check (DetermineStatus), so the worsening is never masked
    // as Improved. (Already-correct ordering — this test locks it against a future reorder.)
    public void MixedRun_ResolvesOneVuln_AndEscalatesAnother_ReportsDegradedNotImproved()
    {
        var baseline = RedTeamBaseline.FromResult(
            Result(resisted: 8, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal), ("PI-002", EvidenceFidelity.Verbal)), "v1");
        var current = Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Behavioral));   // PI-002 now resisted

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Contains("PI-002", c.ResolvedVulnerabilities);   // one vuln fixed
        var esc = Assert.Single(c.FidelityEscalations);          // another escalated
        Assert.Equal("PI-001", esc.ProbeId);
        Assert.Equal(RegressionStatus.Degraded, c.Status);       // escalation wins over the resolved → Improved
    }

    [Fact]
    public void PersistentVuln_SameFidelity_NoEscalation()
    {
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Behavioral)), "v1");
        var current = Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Behavioral));

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Empty(c.FidelityEscalations);
    }

    [Fact]
    public void PersistentVuln_EvidenceWeakened_IsNotEscalation()
    {
        // Behavioral → Verbal is WEAKER evidence, not a strengthening — must not be flagged as an escalation.
        var baseline = RedTeamBaseline.FromResult(Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Behavioral)), "v1");
        var current = Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal));

        var c = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Empty(c.FidelityEscalations);
    }

    [Fact]
    public void PreFieldBaseline_NoFidelityPersisted_NoEscalationDetection()
    {
        // An older baseline schema: the failed id is present but the per-attack fidelity map is null.
        var bare = RedTeamBaseline.FromResult(Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Verbal)), "v1") with
        {
            AttackResults =
            [
                new AttackBaselineResult
                {
                    AttackName = "PromptInjection", AttackDisplayName = "Prompt Injection", OwaspId = "LLM01",
                    Severity = Severity.High, ResistedCount = 9, TotalCount = 10,
                    FailedProbeIds = ["PI-001"], FailedProbeFidelities = null,
                }
            ],
            KnownVulnerabilities = ["PI-001"],
        };
        var current = Result(resisted: 9, inconclusive: 0, ("PI-001", EvidenceFidelity.Behavioral));

        var c = new RedTeamBaselineComparer().Compare(current, bare);

        Assert.Empty(c.FidelityEscalations);   // no baseline fidelity to compare against → no false escalation
    }
}
