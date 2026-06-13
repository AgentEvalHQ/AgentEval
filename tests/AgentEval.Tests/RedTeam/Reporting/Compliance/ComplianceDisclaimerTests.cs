// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public class ComplianceDisclaimerTests
{
    [Fact]
    public void Disclaimer_StatesNotAnAudit()
    {
        Assert.Contains("NOT a formal audit", ComplianceDisclaimer.Text);
        Assert.Contains("heuristic", ComplianceDisclaimer.Text);
        Assert.Contains("narrow", ComplianceDisclaimer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Automated Coverage Summary", ComplianceDisclaimer.Heading);
    }

    [Fact]
    public void SOC2_Markdown_ContainsDisclaimer_AndNotOldAuditLine()
    {
        var md = new SOC2ComplianceReporter().GenerateReport(CreateResult()).ToMarkdown();
        Assert.Contains("Automated Coverage Summary", md);
        Assert.Contains("NOT a formal audit", md);
        Assert.DoesNotContain("examination purposes", md);
    }

    [Fact]
    public void ISO_Markdown_ContainsDisclaimer_AndNotOldCertLine()
    {
        var md = new ISO27001ComplianceReporter().GenerateReport(CreateResult()).ToMarkdown();
        Assert.Contains("Automated Coverage Summary", md);
        Assert.DoesNotContain("certification efforts", md);
    }

    [Fact]
    public void Owasp_Markdown_ContainsDisclaimer()
    {
        var md = new OWASPComplianceReporter().GenerateReport(CreateResult()).ToMarkdown();
        Assert.Contains("Automated Coverage Summary", md);
        Assert.Contains("NOT a formal audit", md);
    }

    [Fact]
    public void Mitre_Markdown_ContainsDisclaimer()
    {
        var md = new MITREATLASReporter().GenerateReport(CreateResult()).ToMarkdown();
        Assert.Contains("Automated Coverage Summary", md);
        Assert.Contains("NOT a formal audit", md);
    }

    private static RedTeamResult CreateResult() => new()
    {
        AgentName = "TestAgent", TotalProbes = 1, ResistedProbes = 1, SucceededProbes = 0,
        AttackResults = [ new AttackResult { AttackName = "SystemPromptExtraction", OwaspId = "LLM07", ResistedCount = 1, SucceededCount = 0,
            ProbeResults = [ new ProbeResult { ProbeId = "p0", Prompt = "x", Response = "y", Reason = "r", Outcome = EvaluationOutcome.Resisted } ] } ]
    };
}
