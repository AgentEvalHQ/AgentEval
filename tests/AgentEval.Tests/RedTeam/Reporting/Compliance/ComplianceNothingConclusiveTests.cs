// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

/// <summary>
/// T81: the five compliance reports of a scan in which no probe reached a verdict evaluated nothing. Each has no
/// compliance rate, no risk level and no overall pass rate (null, where they read 0.0% and Low or Moderate), and its
/// Markdown and JSON say so. The OWASP, NIST and MITRE composites leave the pass-rate dimension out. A scan that measured
/// keeps every one of these numbers.
/// </summary>
public class ComplianceNothingConclusiveTests
{
    private static RedTeamResult ZeroProbeScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 0,
        AttackResults = [],
    };

    private static RedTeamResult AllInconclusiveScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 5,
        InconclusiveProbes = 5,
        AttackResults = [PromptInjection(EvaluationOutcome.Inconclusive, 5)],
    };

    private static RedTeamResult ResistedScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 5,
        ResistedProbes = 5,
        AttackResults = [PromptInjection(EvaluationOutcome.Resisted, 5)],
    };

    private static RedTeamResult Scan(string kind) => kind switch
    {
        "zero-probe" => ZeroProbeScan(),
        "all-inconclusive" => AllInconclusiveScan(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>One attack every framework maps: OWASP LLM01, MITRE AML.T0051, and a NIST, SOC2 and ISO 27001 control.</summary>
    private static AttackResult PromptInjection(EvaluationOutcome outcome, int n) => new()
    {
        AttackName = "PromptInjection",
        AttackDisplayName = "Prompt Injection",
        OwaspId = "LLM01",
        MitreAtlasIds = ["AML.T0051"],
        Severity = Severity.High,
        ResistedCount = outcome == EvaluationOutcome.Resisted ? n : 0,
        InconclusiveCount = outcome == EvaluationOutcome.Inconclusive ? n : 0,
        ProbeResults = Enumerable.Range(1, n).Select(i => new ProbeResult
        {
            ProbeId = $"PI-{i:D3}",
            Prompt = "p",
            Response = outcome == EvaluationOutcome.Inconclusive ? "[TIMEOUT]" : "I cannot help with that.",
            Outcome = outcome,
            Reason = outcome.ToString(),
            Severity = Severity.High,
        }).ToList(),
    };

    private static IComplianceReport Report(string framework, RedTeamResult result) => framework switch
    {
        "owasp" => new OWASPComplianceReporter().GenerateReport(result),
        "mitre" => new MITREATLASReporter().GenerateReport(result),
        "nist" => new NistAiRmfComplianceReporter().GenerateReport(result),
        "soc2" => new SOC2ComplianceReporter().GenerateReport(result),
        "iso27001" => new ISO27001ComplianceReporter().GenerateReport(result),
        _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, null),
    };

    private static ComplianceSummary SummaryOf(IComplianceReport report) => report switch
    {
        OWASPComplianceReport r => r.Summary,
        MITREATLASReport r => r.Summary,
        NistAiRmfComplianceReport r => r.Summary,
        SOC2ComplianceReport r => r.Summary,
        ISO27001ComplianceReport r => r.Summary,
        _ => throw new ArgumentOutOfRangeException(nameof(report), report.GetType().Name, null),
    };

    [Theory]
    [InlineData("owasp", "zero-probe")]
    [InlineData("owasp", "all-inconclusive")]
    [InlineData("mitre", "zero-probe")]
    [InlineData("mitre", "all-inconclusive")]
    [InlineData("nist", "zero-probe")]
    [InlineData("nist", "all-inconclusive")]
    [InlineData("soc2", "zero-probe")]
    [InlineData("soc2", "all-inconclusive")]
    [InlineData("iso27001", "zero-probe")]
    [InlineData("iso27001", "all-inconclusive")]
    public void NothingConclusive_HasNoComplianceRate_NoRiskLevel_AndNoPassRate(string framework, string kind)
    {
        var report = Report(framework, Scan(kind));

        Assert.Null(report.ComplianceRate);   // it read 0.0
        Assert.Null(report.RiskLevel);        // it read Low (NIST, SOC2, ISO 27001) or Moderate (OWASP, MITRE)
        Assert.Null(SummaryOf(report).OverallPassRate);
    }

    [Theory]
    [InlineData("owasp")]
    [InlineData("mitre")]
    [InlineData("nist")]
    [InlineData("soc2")]
    [InlineData("iso27001")]
    public void AMeasuredScan_KeepsItsComplianceRate_RiskLevel_AndPassRate(string framework)
    {
        var report = Report(framework, ResistedScan());

        Assert.NotNull(report.ComplianceRate);
        Assert.NotNull(report.RiskLevel);
        Assert.Equal(100.0, SummaryOf(report).OverallPassRate);
    }

    [Theory]
    [InlineData("owasp", "| Overall Pass Rate | not measured (no probe reached a verdict) |")]
    [InlineData("owasp", "| Risk Level | not assessed (nothing was conclusively tested) |")]
    [InlineData("mitre", "| Overall Pass Rate | not measured (no probe reached a verdict) |")]
    [InlineData("mitre", "| Risk Level | not assessed (nothing was conclusively tested) |")]
    [InlineData("nist", "| Compliance Rate | not measured (no control was conclusively evaluated) |")]
    [InlineData("soc2", "| Compliance Rate | not measured (no control was conclusively evaluated) |")]
    [InlineData("iso27001", "| Compliance Rate | not measured (no control was conclusively evaluated) |")]
    public void NothingConclusive_Markdown_SaysNotMeasured(string framework, string expectedLine)
    {
        var markdown = Report(framework, AllInconclusiveScan()).ToMarkdown();

        Assert.Contains(expectedLine, markdown);
        Assert.DoesNotContain("| Overall Pass Rate | 0.0% |", markdown);
        Assert.DoesNotContain("| Compliance Rate | 0.0% |", markdown);
        Assert.DoesNotContain("| Risk Level | Low |", markdown);
        Assert.DoesNotContain("| Risk Level | Moderate |", markdown);
    }

    [Theory]
    [InlineData("owasp")]
    [InlineData("mitre")]
    [InlineData("nist")]
    [InlineData("soc2")]
    [InlineData("iso27001")]
    public void NothingConclusive_Json_WritesNullRateAndRiskLevel(string framework)
    {
        var root = JsonDocument.Parse(Report(framework, ZeroProbeScan()).ToJson()).RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("complianceRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("riskLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("summary").GetProperty("overallPassRate").ValueKind);
    }

    // ───────────────────────── the OWASP, NIST and MITRE composites ─────────────────────────

    [Theory]
    [InlineData("owasp", "owasp_overall_pass_rate")]
    [InlineData("nist", "nist_overall_pass_rate")]
    [InlineData("mitre", "mitre_overall_pass_rate")]
    public void BenchmarkComposite_OfANothingConclusiveScan_HasNoPassRateDimension(string framework, string key)
    {
        var composite = BuildComposite(framework, AllInconclusiveScan());

        Assert.False(composite.Details.Dimensions!.ContainsKey(key));   // it held 0
        var evidence = Assert.Single(composite.Details.Evidence!);
        Assert.Contains("not measured (no probe reached a verdict)", evidence.Message);
        Assert.DoesNotContain("0.0%", evidence.Message);
    }

    [Theory]
    [InlineData("owasp", "owasp_overall_pass_rate")]
    [InlineData("nist", "nist_overall_pass_rate")]
    [InlineData("mitre", "mitre_overall_pass_rate")]
    public void BenchmarkComposite_OfAMeasuredScan_KeepsItsPassRateDimension(string framework, string key)
    {
        var composite = BuildComposite(framework, ResistedScan());

        Assert.Equal(1.0, composite.Details.Dimensions![key], 6);
        Assert.DoesNotContain("not measured", Assert.Single(composite.Details.Evidence!).Message);
    }

    private static AgentEval.Evals.EvalResult BuildComposite(string framework, RedTeamResult result) => framework switch
    {
        "owasp" => OwaspBenchmark.Smoke().BuildEvalResult(result),
        "nist" => NistBenchmark.RmfSmoke().BuildEvalResult(result),
        "mitre" => MitreBenchmark.AtlasSmoke().BuildEvalResult(result),
        _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, null),
    };
}
