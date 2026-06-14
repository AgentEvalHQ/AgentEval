// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// NIST AI RMF benchmark family (parity with OWASP/MITRE). The composite has one leaf per NIST control; only the
// MEASURE security/privacy/validity sub-actions are scored — GOVERN/MAP/MANAGE (and MEASURE 2.6 Safety) are skipped.
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Compliance;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.Benchmarks;

public class NistBenchmarkTests
{
    // ─── Preset shape ─────────────────────────────────────────────────────────

    [Fact]
    public void RmfBaseline_ReturnsRun_WithFullRoster_AndMeasureControlsCovered()
    {
        var run = NistBenchmark.RmfBaseline();
        Assert.Equal("RmfBaseline", run.PresetName);
        Assert.Equal(Attack.All.Count, run.Pipeline.GetProbePreview().Select(p => p.AttackName).Distinct().Count());

        var covered = run.CoveredControlIds;
        Assert.Contains("MEASURE.2.7", covered);    // Security & Resilience (all adversarial attacks)
        Assert.Contains("MEASURE.2.10", covered);   // Privacy (PII + system-prompt)
        Assert.Contains("MEASURE.2.5", covered);    // Validity/Reliability (Misinformation)
        Assert.DoesNotContain("MEASURE.2.6", covered);   // Safety — governance, never covered
        Assert.DoesNotContain("GOVERN.6.1", covered);    // governance, never covered
    }

    [Fact]
    public void RmfSmoke_CoversSecurityAndPrivacy_NotValidity()
    {
        var run = NistBenchmark.RmfSmoke();
        Assert.Equal("RmfSmoke", run.PresetName);
        var covered = run.CoveredControlIds;
        Assert.Contains("MEASURE.2.7", covered);    // PromptInjection + Jailbreak
        Assert.Contains("MEASURE.2.10", covered);   // PIILeakage
        Assert.DoesNotContain("MEASURE.2.5", covered);   // Misinformation not in the MVP roster
    }

    [Fact]
    public void AllPresets_AcceptOptionalJudge()
    {
        var stub = new PassingStubEvaluator();
        Assert.NotNull(NistBenchmark.RmfBaseline(stub));
        Assert.NotNull(NistBenchmark.RmfSmoke(stub));
        Assert.NotNull(NistBenchmark.RmfAuditGrade(stub));
    }

    // ─── EvaluateAsync composite shape ────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_NoAgent_ReturnsSkippedComposite_WithAllControlLeaves()
    {
        var run = NistBenchmark.RmfSmoke();
        var result = await run.EvaluateAsync(new EvalInput(Query: "any"));

        Assert.Equal("skipped", result.Score.Label);
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(NistAiRmfControls.All.Length, result.Details.SubResults!.Count);
        Assert.All(result.Details.SubResults, leaf => Assert.Equal("skipped", leaf.Score.Label));
    }

    [Fact]
    public async Task EvaluateAsync_PassingAgent_TestsMeasureControls_SkipsGovernance()
    {
        var run = NistBenchmark.RmfBaseline();
        var input = new EvalInput(
            Query: "evaluate",
            Metadata: new Dictionary<string, object> { ["agent"] = new PassingAgent("NistPassingAgent") });

        var result = await run.EvaluateAsync(input);

        Assert.Equal("nist.rmfbaseline", result.Metric.Key);
        Assert.Equal("compliance.nist", result.Metric.Category);
        var leaves = result.Details.SubResults!;
        Assert.Equal(NistAiRmfControls.All.Length, leaves.Count);     // one leaf per control
        Assert.All(leaves, l => Assert.StartsWith("nist.", l.Metric.Key));

        // The 2 Tested MEASURE sub-actions pass against an always-refusing agent. MEASURE.2.5 (Misinformation) is a
        // Supporting control: H6 caps it at "warn" even at 100% pass so the audit-chain leaf matches the human
        // report's PartiallyEffective — it must NOT read "pass".
        var tested = leaves.Where(l => l.Score.Label == "pass").Select(l => l.Metric.Key).OrderBy(x => x).ToList();
        Assert.Equal(["nist.measure.2.10", "nist.measure.2.7"], tested);

        var supporting = leaves.Single(l => l.Metric.Key == "nist.measure.2.5");
        Assert.Equal("warn", supporting.Score.Label);
        Assert.False(supporting.Score.Passed);
        Assert.Equal(0.80, supporting.Score.Value, 3);   // Jun14-L6: Value capped to the PartiallyEffective floor, not 1.0

        // The 6 governance controls (incl. MEASURE 2.6 Safety) are skipped — never PASS.
        var skipped = leaves.Count(l => l.Score.Label == "skipped");
        Assert.Equal(6, skipped);

        // Composite is the min over leaves — a Supporting "warn" pulls the whole composite to "warn".
        Assert.Equal("warn", result.Score.Label);
    }

    [Fact]
    public async Task GenerateReport_ProducesNistReport_WithGovernanceNotApplicable()
    {
        var run = NistBenchmark.RmfBaseline();
        var redTeam = await run.ScanAsync(new PassingAgent("NistReportAgent"));
        var report = run.GenerateReport(redTeam);

        Assert.Equal("NIST AI RMF", report.FrameworkName);
        Assert.Equal(NistAiRmfControls.All.Length, report.Controls.Count);
        // Governance controls are Not Applicable (never PASS).
        Assert.Contains(report.Controls, c =>
            c.Control.ControlId == "GOVERN.6.1" && c.Status == ControlEvaluationStatus.NotApplicable);
    }

    // ─── Registration + CLI preset resolution ─────────────────────────────────

    [Theory]
    [InlineData("rmf-baseline", "RmfBaseline")]
    [InlineData("rmf-smoke", "RmfSmoke")]
    [InlineData("rmf-audit-grade", "RmfAuditGrade")]
    [InlineData("audit", "RmfAuditGrade")]
    public void Registration_ResolvesPresets(string spec, string expectedName)
        => Assert.Equal(expectedName, NistBenchmarkRegistration.ResolvePresetRun(spec, judge: null).PresetName);

    [Fact]
    public void Registration_UnknownPreset_Throws()
        => Assert.Throws<ArgumentException>(() => NistBenchmarkRegistration.ResolvePresetRun("nope", null));

    [Fact]
    public void CliCommand_ResolvePreset_MapsAliases()
    {
        var stub = new PassingStubEvaluator();
        Assert.Equal("RmfBaseline", AgentEval.Cli.Commands.BenchNistCommand.ResolvePreset("baseline", stub).PresetName);
        Assert.Equal("RmfSmoke", AgentEval.Cli.Commands.BenchNistCommand.ResolvePreset("smoke", stub).PresetName);
    }

    [Fact]
    public async Task EvaluateAsync_VulnerableAgent_CompositeFails_OnSecurityControl()
    {
        // An echo agent gets compromised by injection/jailbreak → MEASURE.2.7 (Security & Resilience) fails →
        // the MinAggregation composite fails. This exercises the warn/fail label path (not just all-pass/all-skip).
        var run = NistBenchmark.RmfBaseline();
        var input = new EvalInput(
            Query: "x", Metadata: new Dictionary<string, object> { ["agent"] = new EchoAgent("VulnAgent") });

        var result = await run.EvaluateAsync(input);

        Assert.Equal("fail", result.Score.Label);
        var security = result.Details.SubResults!.Single(l => l.Metric.Key == "nist.measure.2.7");
        Assert.Equal("fail", security.Score.Label);
    }

    // ─── fakes ────────────────────────────────────────────────────────────────

    private sealed class PassingAgent(string name) : IEvaluableAgent
    {
        public string Name { get; } = name;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = "I cannot help with that request. Please ask something else." });
    }

    // Echoes the prompt — the red-team marker probes (e.g. "say PWNED") are then reflected back ⇒ compromised.
    private sealed class EchoAgent(string name) : IEvaluableAgent
    {
        public string Name { get; } = name;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = prompt });
    }

    private sealed class PassingStubEvaluator : IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = criteria.Select(c => new AgentEval.Core.CriterionResult { Criterion = c, Met = true, Explanation = "stub" }).ToList()
            });
    }
}
