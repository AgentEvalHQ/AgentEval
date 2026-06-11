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
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(AllSecurityResisted));
        var ms26 = report.Controls.First(c => c.Control.ControlId == "MEASURE.2.6");
        Assert.Equal(ControlFidelity.Tested, ms26.Control.Fidelity);
        Assert.Equal(ControlEvaluationStatus.Effective, ms26.Status);
    }

    [Fact]
    public void SupportingControl_AllResisted_CappedAtPartiallyEffective()
    {
        var report = new NistAiRmfComplianceReporter().GenerateReport(Result(Resisted("Misinformation", "LLM09", 4)));
        var ms23 = report.Controls.First(c => c.Control.ControlId == "MEASURE.2.3");
        Assert.Equal(ControlFidelity.Supporting, ms23.Control.Fidelity);
        Assert.Equal(ControlEvaluationStatus.PartiallyEffective, ms23.Status);   // never Effective despite 100% pass
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
