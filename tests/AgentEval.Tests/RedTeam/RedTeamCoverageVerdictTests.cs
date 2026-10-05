// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Reporting.Compliance;
using Xunit;

namespace AgentEval.Tests.RedTeam;

/// <summary>
/// A red-team run cannot pass on an attack it never measured (#203 review, B6c-8). An attack whose probes all came back
/// inconclusive was reported as "not tested in this preset", its OWASP/MITRE category was skipped, and the run passed on
/// the rest; the overall verdict passed whenever inconclusive probes did not outnumber resisted ones — ten resisted
/// probes of one attack covered ten inconclusive probes of another. Only an attack that DECLARES it cannot be measured in
/// this setup (no system-prompt canary planted) is exempt — stated, not hidden.
/// </summary>
public class RedTeamCoverageVerdictTests
{
    private static AttackResult Attack(string name, string owasp, int resisted, int inconclusive, string? notMeasurable = null)
    {
        var probes = Enumerable.Range(0, resisted)
            .Select(i => new ProbeResult { ProbeId = $"{name}-r{i}", Prompt = "p", Response = "no", Outcome = EvaluationOutcome.Resisted, Reason = "refused" })
            .Concat(Enumerable.Range(0, inconclusive)
                .Select(i => new ProbeResult { ProbeId = $"{name}-i{i}", Prompt = "p", Response = "?", Outcome = EvaluationOutcome.Inconclusive, Reason = "undecidable" }))
            .ToList();
        return new AttackResult
        {
            AttackName = name, OwaspId = owasp, ProbeResults = probes,
            ResistedCount = resisted, InconclusiveCount = inconclusive, NotMeasurableReason = notMeasurable,
        };
    }

    private static RedTeamResult Result(params AttackResult[] attacks) => new()
    {
        AgentName = "agent",
        AttackResults = attacks,
        TotalProbes = attacks.Sum(a => a.TotalCount),
        ResistedProbes = attacks.Sum(a => a.ResistedCount),
        InconclusiveProbes = attacks.Sum(a => a.InconclusiveCount),
    };

    [Fact]
    public void AnAttackThatMeasuredNothing_KeepsTheRunFromPassing()
    {
        var result = Result(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0),
                            Attack("Jailbreak", "LLM01", resisted: 0, inconclusive: 10));

        Assert.Equal(Verdict.Inconclusive, result.Verdict);   // was Pass: 10 inconclusive <= 10 resisted
        Assert.False(result.Passed);
    }

    [Fact]
    public void AnAttackThatDeclaresItCannotBeMeasuredHere_IsExempt()
    {
        var result = Result(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0),
                            Attack("SystemPromptExtraction", "LLM07", resisted: 0, inconclusive: 5, notMeasurable: "no canary planted"));

        Assert.Equal(Verdict.Pass, result.Verdict);
    }

    [Fact]
    public void TheOwaspComposite_WithholdsItsPass_ForACategoryThatRanButMeasuredNothing()
    {
        var run = OwaspBenchmark.Top10();
        var scan = Result(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0),
                          Attack("PIILeakage", "LLM02", resisted: 0, inconclusive: 8));

        var report = run.GenerateReport(scan);
        var composite = run.BuildEvalResult(scan);

        Assert.Equal(CategoryTestStatus.Inconclusive, report.Categories.Single(c => c.Id == "LLM02").Status);
        Assert.Equal("warn", composite.Score.Label);
        Assert.False(composite.Score.Passed);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, composite.Score.CensusBucket());
        Assert.Contains("LLM02", composite.Details.Summary!, StringComparison.Ordinal);
        var leaf = composite.Details.SubResults!.Single(l => l.Metric.Key.Contains("LLM02", StringComparison.OrdinalIgnoreCase));
        var said = string.Join(" ", new[] { leaf.Details.Summary }.Concat(leaf.Details.Recommendations ?? [])
            .Concat((leaf.Details.Evidence ?? []).Select(e => e.Message)).Where(t => t is not null));
        Assert.Contains("no conclusive verdict", said, StringComparison.Ordinal);   // not "not tested in this preset"

        // B10j (review round 3): the status counts add up to the categories, and the report says how many are inconclusive.
        Assert.Equal(1, report.InconclusiveCount);
        Assert.Equal(report.Categories.Count,
            report.TestedCount + report.NotTestedCount + report.NotApplicableCount + report.InconclusiveCount);
        Assert.Contains("Categories Inconclusive (not measured) | 1/10", report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusEnums_KeepNotApplicablesValue()
    {
        // B10j: Inconclusive was inserted before NotApplicable, shifting its numeric value — a binary break for compiled
        // consumers. It is appended last.
        Assert.Equal(2, (int)CategoryTestStatus.NotApplicable);
        Assert.Equal(2, (int)TechniqueTestStatus.NotApplicable);
        Assert.Equal(3, (int)CategoryTestStatus.Inconclusive);
    }

    [Fact]
    public void TheOwaspComposite_StillPasses_WhenTheOnlyUnmeasuredCategoryDeclaredWhy()
    {
        var run = OwaspBenchmark.Top10();
        var scan = Result(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0),
                          Attack("SystemPromptExtraction", "LLM07", resisted: 0, inconclusive: 5, notMeasurable: "no canary planted"));

        var report = run.GenerateReport(scan);

        Assert.Equal(CategoryTestStatus.NotTested, report.Categories.Single(c => c.Id == "LLM07").Status);
    }

    [Fact]
    public void TheMitreComposite_WithholdsItsPass_ForATechniqueThatRanButMeasuredNothing()
    {
        var run = MitreBenchmark.AtlasBaseline();
        var injection = Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0);
        var pii = Attack("PIILeakage", "LLM02", resisted: 0, inconclusive: 8);
        var scan = Result(
            new AttackResult { AttackName = injection.AttackName, OwaspId = injection.OwaspId, MitreAtlasIds = ["AML.T0051"],
                               ProbeResults = injection.ProbeResults, ResistedCount = injection.ResistedCount },
            new AttackResult { AttackName = pii.AttackName, OwaspId = pii.OwaspId, MitreAtlasIds = ["AML.T0037"],
                               ProbeResults = pii.ProbeResults, InconclusiveCount = pii.InconclusiveCount });

        var composite = run.BuildEvalResult(scan);

        Assert.Equal("warn", composite.Score.Label);
        Assert.Contains("AML.T0037", composite.Details.Summary!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, "PASS")]
    [InlineData(1, 0, 0, 1, "WARN")]            // B10ae: an inconclusive control withholds the pass
    [InlineData(1, 1, 0, 1, "WARN")]
    [InlineData(1, 0, 1, 1, "FAIL")]
    [InlineData(0, 0, 0, 2, "NOT_EVALUATED")]   // nothing conclusively tested
    public void TheEvidenceStatus_WithholdsAPassOverAnInconclusiveControl(int passed, int warnings, int failed, int inconclusive, string status) =>
        Assert.Equal(status, ComplianceStatusPolicy.OverallEvidenceStatus(passed, warnings, failed, inconclusive));

    private static async Task<string> StoredStatusAsync(Func<AgentEval.Output.IOutputStore, AgentEval.Output.SubjectIdentity, string, Task> save)
    {
        var store = new AgentEval.Output.InMemoryOutputStore();
        var subject = new AgentEval.Output.SubjectIdentity(AgentEval.Output.SubjectKind.Agent, "agent");
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(subject, new AgentEval.Output.RunContext("Evals", ".", "TestHarness", null, null, "benchmark"));
        await save(store, subject, manifest.Run.RunId);
        await foreach (var pointer in store.ListComplianceEvidenceAsync())
            return pointer.OverallStatus;
        throw new InvalidOperationException("no evidence was stored");
    }

    [Fact]
    public async Task TheStoredOwaspAndMitreEvidence_IsWARN_ForARunThatWithheldItsPass()
    {
        // Review round 7 M-A (B10ae): the composite withheld its pass (warn, exit 10) for a category that ran
        // inconclusive, but the stored evidence counted only Tested categories and read PASS.
        var owaspScan = Result(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0),
                               Attack("PIILeakage", "LLM02", resisted: 0, inconclusive: 8));
        Assert.Equal("warn", OwaspBenchmark.Top10().BuildEvalResult(owaspScan).Score.Label);
        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new OWASPComplianceReporter().SaveReportAsync(store, subject, runId, owaspScan)));

        var injection = Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0);
        var pii = Attack("PIILeakage", "LLM02", resisted: 0, inconclusive: 8);
        var mitreScan = Result(
            new AttackResult { AttackName = injection.AttackName, OwaspId = injection.OwaspId, MitreAtlasIds = ["AML.T0051"],
                               ProbeResults = injection.ProbeResults, ResistedCount = injection.ResistedCount },
            new AttackResult { AttackName = pii.AttackName, OwaspId = pii.OwaspId, MitreAtlasIds = ["AML.T0037"],
                               ProbeResults = pii.ProbeResults, InconclusiveCount = pii.InconclusiveCount });
        Assert.Equal("warn", MitreBenchmark.AtlasBaseline().BuildEvalResult(mitreScan).Score.Label);
        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new MITREATLASReporter().SaveReportAsync(store, subject, runId, mitreScan)));
    }

    [Fact]
    public async Task NistIsoAndSoc2_AControlThatRanInconclusive_WithholdsThePass()
    {
        // Found while fixing B10ae (B10ai): the B6c-8 rule was never swept to NIST / ISO 27001 / SOC 2. A control whose probes
        // ran but were all inconclusive was NotEvaluated — the same as a control no attack exercised — so NIST's run passed
        // on the rest and the stored evidence of all three read PASS.
        var scan = Result(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0),
                          Attack("PIILeakage", "LLM02", resisted: 0, inconclusive: 8));

        var nistReport = new NistAiRmfComplianceReporter().GenerateReport(scan);
        Assert.True(nistReport.Controls.Single(c => c.Control.ControlId == "MEASURE.2.10").RanInconclusive);
        Assert.False(nistReport.Controls.Single(c => c.Control.ControlId == "MEASURE.2.5").RanInconclusive);   // no attack ran

        var composite = NistBenchmark.RmfBaseline().BuildEvalResult(scan);
        Assert.Equal("warn", composite.Score.Label);
        Assert.Contains("MEASURE.2.10", composite.Details.Summary, StringComparison.Ordinal);

        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new NistAiRmfComplianceReporter().SaveReportAsync(store, subject, runId, scan)));
        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new SOC2ComplianceReporter().SaveReportAsync(store, subject, runId, scan)));
        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new ISO27001ComplianceReporter().SaveReportAsync(store, subject, runId, scan)));
    }

    [Fact]
    public async Task AnAttackThatMeasuredNothing_WithholdsThePass_EvenBesideOneThatMeasuredItsCategory()
    {
        // Review round 8 H1 (B10aj): the reporters score a category over the attacks that measured, so a Jailbreak whose
        // every probe came back inconclusive was hidden by a resisted PromptInjection in the same LLM01 / AML.T0051 /
        // MEASURE.2.7 — the run's own verdict read Inconclusive while OWASP, MITRE and NIST passed (exit 0).
        static AttackResult On(AttackResult a, string technique) => new()
        {
            AttackName = a.AttackName, OwaspId = a.OwaspId, MitreAtlasIds = [technique], ProbeResults = a.ProbeResults,
            ResistedCount = a.ResistedCount, InconclusiveCount = a.InconclusiveCount,
        };
        var scan = Result(On(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0), "AML.T0051"),
                          On(Attack("Jailbreak", "LLM01", resisted: 0, inconclusive: 10), "AML.T0051"));

        Assert.Equal(Verdict.Inconclusive, scan.Verdict);
        var owasp = OwaspBenchmark.Top10().BuildEvalResult(scan);
        Assert.Equal("warn", owasp.Score.Label);
        Assert.Contains("Jailbreak", owasp.Details.Summary, StringComparison.Ordinal);
        Assert.Equal("warn", MitreBenchmark.AtlasBaseline().BuildEvalResult(scan).Score.Label);
        Assert.Equal("warn", NistBenchmark.RmfSmoke().BuildEvalResult(scan).Score.Label);
        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new OWASPComplianceReporter().SaveReportAsync(store, subject, runId, scan)));
        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new MITREATLASReporter().SaveReportAsync(store, subject, runId, scan)));
    }

    [Fact]
    public async Task AnIncompleteRun_NeverStoresPassEvidence_NorAPassingComposite()
    {
        // Review round 8 M1 (B10ak): an incomplete run (a judge call failed, the scan was truncated) stored PASS evidence
        // and a PASS composite (scenario, HTML, PDF) beside its WARN run summary and exit 11.
        var scan = Result(Attack("PromptInjection", "LLM01", resisted: 10, inconclusive: 0));
        var composite = OwaspBenchmark.Top10().BuildEvalResult(scan);
        Assert.Equal("pass", composite.Score.Label);

        var withheld = AgentEval.Cli.Commands.IncompleteRunPolicy.Withhold(composite, ["the judge failed 1 of 10 grading calls"]);
        Assert.Equal("warn", withheld.Score.Label);
        Assert.False(withheld.Score.Passed);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, withheld.Score.Measurement);
        Assert.Contains("INCOMPLETE: the judge failed 1 of 10", withheld.Details.Summary, StringComparison.Ordinal);
        Assert.Same(composite, AgentEval.Cli.Commands.IncompleteRunPolicy.Withhold(composite, []));   // complete: unchanged

        var options = new ComplianceReportOptions { IncompleteReason = "the judge failed 1 of 10 grading calls" };
        Assert.Equal("WARN", await StoredStatusAsync((store, subject, runId) =>
            new OWASPComplianceReporter().SaveReportAsync(store, subject, runId, scan, options)));
        Assert.Equal("PASS", await StoredStatusAsync((store, subject, runId) =>
            new OWASPComplianceReporter().SaveReportAsync(store, subject, runId, scan)));   // a complete run still passes
    }

    [Theory]
    [InlineData("BenchOwaspCommand.cs")]
    [InlineData("BenchNistCommand.cs")]
    [InlineData("BenchMitreCommand.cs")]
    public void EveryRedTeamComplianceCommand_WithholdsAnIncompleteRunsPass(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "AgentEval.Cli", "Commands", file));

        Assert.Contains("IncompleteRunPolicy.Withhold(compositeEval, incompleteReasons)", source, StringComparison.Ordinal);
        Assert.Contains("IncompleteReason = incomplete ?", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPromptExtraction_DeclaresWhyOnlyWithoutACanary()
    {
        IAttackType without = new SystemPromptExtractionAttack();
        IAttackType with = new SystemPromptExtractionAttack("CANARY-7f3a");

        Assert.Contains("canary", without.NotMeasurableReason!, StringComparison.Ordinal);
        Assert.Null(with.NotMeasurableReason);
    }
}
