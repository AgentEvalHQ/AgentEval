// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public class NistAiRmfComplianceReporterTests
{
    private static AttackResult Resisted(string name, string owaspId, int n)
    {
        var probes = Enumerable.Range(0, n).Select(i => new ProbeResult
        {
            ProbeId = $"{name}-{i}", Prompt = "p", Response = "r",
            Outcome = EvaluationOutcome.Resisted, Reason = "blocked",
        }).ToList();
        return new AttackResult
        {
            AttackName = name, OwaspId = owaspId, ProbeResults = probes,
            ResistedCount = n, SucceededCount = 0, InconclusiveCount = 0,
        };
    }

    private static RedTeamResult Result(params AttackResult[] attacks)
        => new() { AgentName = "test-agent", AttackResults = attacks };

    private static readonly AttackResult[] AllSecurityResisted =
    [
        Resisted("PromptInjection", "LLM01", 4), Resisted("Jailbreak", "LLM01", 4),
        Resisted("IndirectInjection", "LLM01", 4), Resisted("EncodingEvasion", "LLM01", 4),
        Resisted("ExcessiveAgency", "LLM06", 4), Resisted("InsecureOutput", "LLM05", 4),
        Resisted("SupplyChain", "LLM03", 4), Resisted("DataPoisoning", "LLM04", 4),
        Resisted("VectorEmbedding", "LLM08", 4), Resisted("InferenceAPIAbuse", "LLM10", 4),
    ];

    [Fact]
    public void Reporter_ImplementsInterface_AndRegulationKey()
    {
        Assert.Equal("NIST-AI-RMF", NistAiRmfComplianceReporter.Regulation);
        Assert.IsAssignableFrom<IComplianceReporter<NistAiRmfComplianceReport>>(new NistAiRmfComplianceReporter());
    }

    [Fact]
    public void GovernanceControls_AreNotApplicable_NeverPass()
    {
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(Resisted("PromptInjection", "LLM01", 4)));
        var governance = report.Controls.Where(c => c.Control.Fidelity == ControlFidelity.Governance).ToList();

        Assert.NotEmpty(governance);
        Assert.All(governance, c => Assert.Equal(ControlEvaluationStatus.NotApplicable, c.Status));
        Assert.DoesNotContain(governance, c =>
            c.Status is ControlEvaluationStatus.Effective or ControlEvaluationStatus.PartiallyEffective);
    }

    [Fact]
    public void TestedControl_AllResisted_IsEffective()
    {
        // H13/H14: the adversarial-security attacks map to MEASURE.2.7 (Security and Resilience) — 2.6 is SAFETY
        // (no attacks). Verified vs NIST AI 100-1 AI RMF 1.0 Core 2026-06-13.
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(AllSecurityResisted));
        var ms27 = report.Controls.First(c => c.Control.ControlId == "MEASURE.2.7");
        Assert.Equal(ControlFidelity.Tested, ms27.Control.Fidelity);
        Assert.Equal(ControlEvaluationStatus.Effective, ms27.Status);
    }

    private static AttackResult Inconclusive(string name, string owaspId, int n)
    {
        var probes = Enumerable.Range(0, n).Select(i => new ProbeResult
        {
            ProbeId = $"{name}-{i}", Prompt = "p", Response = "[TIMEOUT]",
            Outcome = EvaluationOutcome.Inconclusive, Reason = "timed out",
        }).ToList();
        return new AttackResult
        {
            AttackName = name, OwaspId = owaspId, ProbeResults = probes,
            ResistedCount = 0, SucceededCount = 0, InconclusiveCount = n,
        };
    }

    [Fact]
    public void EmptyRun_ComplianceRate_IsZeroNotHundred_AndNoSuccessRecommendation()
    {
        // RC-6 honesty: a run that conclusively evaluated nothing must not render 100% compliant
        // nor claim "✅ all sub-actions meet thresholds".
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result());

        Assert.Equal(0.0, report.ComplianceRate);
        Assert.DoesNotContain(report.Recommendations, r => r.StartsWith("✅"));
        Assert.Contains(report.Recommendations, r => r.Contains("No controls were conclusively evaluated"));
    }

    [Fact]
    public void AllInconclusiveRun_ComplianceRate_IsZeroNotHundred()
    {
        // A timed-out SUT makes every probe Inconclusive -> every control NotEvaluated.
        var report = new NistAiRmfComplianceReporter().GenerateReport(
            Result(Inconclusive("PromptInjection", "LLM01", 4), Inconclusive("Jailbreak", "LLM01", 4)));

        Assert.Equal(0.0, report.ComplianceRate);
        // Section-2 review MEDIUM: the summary OverallPassRate must NOT be the 100-on-empty ConclusiveScore sentinel.
        Assert.Equal(0.0, report.Summary.OverallPassRate);
        Assert.DoesNotContain("\"overallPassRate\": 100", report.ToJson());
        Assert.DoesNotContain(report.Recommendations, r => r.StartsWith("✅"));
    }

    [Fact]
    public void SupportingControl_AllResisted_CappedAtPartiallyEffective()
    {
        // H14: confabulation/output-reliability maps to MEASURE.2.5 (Validity and Reliability), Supporting.
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(Resisted("Misinformation", "LLM09", 4)));
        var ms25 = report.Controls.First(c => c.Control.ControlId == "MEASURE.2.5");
        Assert.Equal(ControlFidelity.Supporting, ms25.Control.Fidelity);
        Assert.Equal(ControlEvaluationStatus.PartiallyEffective, ms25.Status);   // never Effective despite 100% pass
    }

    [Fact]
    public void UntestedControl_IsNotEvaluated_NotNotApplicable()
    {
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(Resisted("PromptInjection", "LLM01", 4)));
        var ms210 = report.Controls.First(c => c.Control.ControlId == "MEASURE.2.10");   // PII/SPE not in this run
        Assert.Equal(ControlEvaluationStatus.NotEvaluated, ms210.Status);
    }

    [Fact]
    public void Markdown_HasRmfScopeDisclaimer_NoSyntheticDisclaimerControl()
    {
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(AllSecurityResisted));
        var md = report.ToMarkdown();

        Assert.Contains("not RMF conformance", md);
        Assert.Contains("MEASURE", md);
        Assert.DoesNotContain(report.Controls, c => c.Control.ControlId.Contains("DISCLAIMER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Markdown_NotEvaluatedControl_OmitsPassRate_NotConflatedWithFailure()
    {
        // MEASURE.2.10 (PII/SystemPromptExtraction) was not run here → NotEvaluated. Its markdown section must NOT
        // print "Pass Rate: 0.0%" / "Tests Performed: 0", which would be indistinguishable from a control that was
        // tested and fully FAILED — conflating "not measured" with "measured and failed" (honesty discipline).
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(Resisted("PromptInjection", "LLM01", 4)));
        var section = Section(report.ToMarkdown(), "MEASURE.2.10");

        Assert.Contains("NotEvaluated", section);
        Assert.DoesNotContain("Pass Rate", section);
        Assert.DoesNotContain("Tests Performed", section);
    }

    // Extract a single control's markdown block: from its "### {controlId}" header to the trailing "---" separator.
    private static string Section(string md, string controlId)
    {
        var start = md.IndexOf($"### {controlId}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"control {controlId} not found in markdown");
        var rest = md[start..];
        var end = rest.IndexOf("\n---", StringComparison.Ordinal);
        return end >= 0 ? rest[..end] : rest;
    }
}
