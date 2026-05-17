// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Tests for the <see cref="MitreBenchmark"/> façade and its
/// <see cref="MitreBenchmarkRun.EvaluateAsync"/> adapter into the unified
/// <see cref="EvalResult"/> pipeline.
/// </summary>
/// <remarks>
/// Phase 6 (v0.10.0-beta). Mirrors <c>OwaspBenchmarkTests</c> in shape but
/// asserts MITRE ATLAS technique-IDs and the 12-leaf composite shape (= the
/// <see cref="MITREATLASReporter"/> roster), not OWASP categories.
/// </remarks>
public class MitreBenchmarkTests
{
    // ─── Preset shape ─────────────────────────────────────────────────────────

    [Fact]
    public void AtlasBaseline_ReturnsNonNullRun_WithAllNineAttacks()
    {
        var run = MitreBenchmark.AtlasBaseline();
        Assert.NotNull(run);
        Assert.Equal("AtlasBaseline", run.PresetName);

        // AtlasBaseline wires the full 9-attack roster from Attack.All.
        Assert.Equal(Attack.All.Count, run.Pipeline.GetProbePreview().Select(p => p.AttackName).Distinct().Count());

        // 6 ATLAS technique IDs are covered by the 9 attacks
        // (AML.T0024, T0037, T0043, T0045, T0051, T0054).
        var covered = run.CoveredAtlasIds;
        Assert.Contains("AML.T0024", covered);
        Assert.Contains("AML.T0037", covered);
        Assert.Contains("AML.T0043", covered);
        Assert.Contains("AML.T0045", covered);
        Assert.Contains("AML.T0051", covered);
        Assert.Contains("AML.T0054", covered);
        Assert.Equal(6, covered.Count);
    }

    [Fact]
    public void AtlasSmoke_ReturnsNonNullRun_WithExactlyThreeMvpAttacks()
    {
        var run = MitreBenchmark.AtlasSmoke();
        Assert.NotNull(run);
        Assert.Equal("AtlasSmoke", run.PresetName);

        var attackNames = run.Pipeline.GetProbePreview()
            .Select(p => p.AttackName).Distinct().OrderBy(n => n).ToList();
        Assert.Equal(3, attackNames.Count);
        Assert.Contains("Jailbreak", attackNames);
        Assert.Contains("PIILeakage", attackNames);
        Assert.Contains("PromptInjection", attackNames);

        // Smoke covers 4 ATLAS technique IDs:
        //   PromptInjection → T0051
        //   Jailbreak       → T0051, T0054
        //   PIILeakage      → T0024, T0037
        // Distinct: T0024, T0037, T0051, T0054.
        var covered = run.CoveredAtlasIds;
        Assert.Equal(
            new[] { "AML.T0024", "AML.T0037", "AML.T0051", "AML.T0054" }.OrderBy(x => x),
            covered.OrderBy(x => x));
    }

    [Fact]
    public void AtlasAuditGrade_ReturnsNonNullRun_WithAllAttacks()
    {
        var run = MitreBenchmark.AtlasAuditGrade();
        Assert.NotNull(run);
        Assert.Equal("AtlasAuditGrade", run.PresetName);
        Assert.Equal(Attack.All.Count, run.Pipeline.GetProbePreview().Select(p => p.AttackName).Distinct().Count());
    }

    [Fact]
    public void AllPresets_AcceptOptionalJudge()
    {
        var stub = new PassingStubEvaluator();
        Assert.NotNull(MitreBenchmark.AtlasBaseline(stub));
        Assert.NotNull(MitreBenchmark.AtlasSmoke(stub));
        Assert.NotNull(MitreBenchmark.AtlasAuditGrade(stub));
    }

    // ─── EvaluateAsync composite shape ────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_NoAgentInMetadata_ReturnsSkippedCompositeWithTwelveLeaves()
    {
        var run = MitreBenchmark.AtlasSmoke();
        var input = new EvalInput(Query: "any");          // no agent in Metadata

        var result = await run.EvaluateAsync(input);

        Assert.NotNull(result);
        Assert.Equal("skipped", result.Score.Label);
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(12, result.Details.SubResults!.Count);
        // All 12 leaves should be skipped when no agent is supplied.
        Assert.All(result.Details.SubResults, leaf => Assert.Equal("skipped", leaf.Score.Label));
    }

    [Fact]
    public async Task EvaluateAsync_PassingAgent_ProducesTwelveLeafComposite_WithTaggedAtlasIds()
    {
        var run = MitreBenchmark.AtlasBaseline();
        var agent = new PassingAgent("MitrePassingAgent");
        var input = new EvalInput(
            Query: "evaluate this agent",
            Metadata: new Dictionary<string, object> { ["agent"] = agent });

        var result = await run.EvaluateAsync(input);

        // Top-level composite shape
        Assert.NotNull(result);
        Assert.Equal("mitre.atlasbaseline", result.Metric.Key);
        Assert.Equal("compliance.mitre", result.Metric.Category);
        Assert.NotNull(result.Details.SubResults);

        var leaves = result.Details.SubResults!;
        Assert.Equal(12, leaves.Count);                                // canonical 12-leaf shape

        // Every leaf's Metric.Key should be 'mitre.aml.t0xxx' — preserving the
        // ATLAS technique ID through the audit chain. This is the headline Phase-6
        // invariant: leaves are tagged with ATLAS technique IDs (not OWASP IDs).
        Assert.All(leaves, leaf =>
        {
            Assert.StartsWith("mitre.aml.t", leaf.Metric.Key);
            Assert.Equal("compliance.mitre", leaf.Metric.Category);
        });

        // 6 techniques covered by the 9 attacks should be tested-and-passed
        // against an always-refusing agent (AML.T0024, T0037, T0043, T0045, T0051, T0054).
        var expectedTested = new[]
        {
            "mitre.aml.t0024", "mitre.aml.t0037", "mitre.aml.t0043",
            "mitre.aml.t0045", "mitre.aml.t0051", "mitre.aml.t0054"
        };
        var testedLeaves = leaves.Where(l => l.Score.Label == "pass").ToList();
        var testedKeys = testedLeaves.Select(l => l.Metric.Key).OrderBy(x => x).ToList();
        Assert.Equal(expectedTested.OrderBy(x => x), testedKeys);
        Assert.All(testedLeaves, leaf =>
        {
            Assert.True(leaf.Score.Passed);
            Assert.Equal("none", leaf.Score.Severity);
            Assert.Equal(1.0, leaf.Score.Value, precision: 6);
        });

        // 6 techniques are skipped: 4 NotApplicable (T0044/T0046/T0052/T0053)
        // + 2 NotTested-applicable (T0047/T0048).
        var skipped = leaves.Where(l => l.Score.Label == "skipped").ToList();
        Assert.Equal(6, skipped.Count);
        var skippedKeys = skipped.Select(l => l.Metric.Key).OrderBy(x => x).ToList();
        Assert.Equal(
            new[]
            {
                "mitre.aml.t0044", "mitre.aml.t0046", "mitre.aml.t0047",
                "mitre.aml.t0048", "mitre.aml.t0052", "mitre.aml.t0053"
            },
            skippedKeys);

        // Composite reflects the passing tested leaves (min over non-skipped = 1.0).
        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal("Min", result.Details.AggregationStrategy);
    }

    // ─── EvaluateAsync failure path ───────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_SuccumbingAgent_ProducesCriticalAtlasLeaves()
    {
        // A "naive" agent that ECHOES whatever it receives — every prompt-injection
        // probe will succeed against it. Smoke includes PromptInjection + Jailbreak +
        // PIILeakage which together cover T0024, T0037, T0051, T0054.
        var run = MitreBenchmark.AtlasSmoke();
        var agent = new SuccumbingAgent("MitreSuccumbingAgent");
        var input = new EvalInput(
            Query: "evaluate this agent",
            Metadata: new Dictionary<string, object> { ["agent"] = agent });

        var result = await run.EvaluateAsync(input);

        Assert.NotNull(result);
        Assert.NotNull(result.Details.SubResults);
        var leaves = result.Details.SubResults!;
        Assert.Equal(12, leaves.Count);

        // The T0051 (Prompt Injection) leaf should reflect probe failures.
        var t0051 = leaves.FirstOrDefault(l => l.Metric.Key == "mitre.aml.t0051");
        Assert.NotNull(t0051);
        Assert.NotEqual("skipped", t0051!.Score.Label);
        // A succumbing agent on the Smoke preset reliably triggers at least medium
        // severity; we assert label != "pass" and severity != "none" without binding
        // to a specific severity tier (heuristic evaluators may shift across releases).
        Assert.NotEqual("pass", t0051.Score.Label);
        Assert.NotEqual("none", t0051.Score.Severity);
        Assert.False(t0051.Score.Passed);

        // Evidence must contain at least one probe entry (the failing prompts).
        Assert.NotNull(t0051.Details.Evidence);
        Assert.True(t0051.Details.Evidence!.Count >= 1,
            "T0051 leaf must carry at least the technique-description evidence row.");

        // Composite is not "pass".
        Assert.NotEqual("pass", result.Score.Label);
    }

    // ─── GenerateReport JSON round-trip ───────────────────────────────────────

    [Fact]
    public async Task GenerateReport_PreservesTechniqueIds_AndRoundTripsJson()
    {
        var run = MitreBenchmark.AtlasBaseline();
        var agent = new PassingAgent("MitreReportAgent");

        // First produce a RedTeamResult by running the scan.
        var redTeamResult = await run.ScanAsync(agent);
        var report = run.GenerateReport(redTeamResult);

        Assert.NotNull(report);
        Assert.Equal(12, report.Techniques.Count);                            // all 12 canonical
        // Spot-check that the key technique IDs are present in the report.
        var techniqueIds = report.Techniques.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AML.T0051", techniqueIds);
        Assert.Contains("AML.T0054", techniqueIds);

        var json = report.ToJson();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var deserialized = JsonSerializer.Deserialize<MITREATLASReport>(
            json,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

        Assert.NotNull(deserialized);
        Assert.Equal(report.AgentName, deserialized!.AgentName);
        Assert.Equal(report.Techniques.Count, deserialized.Techniques.Count);
        // Structural equality on the technique IDs preserves the canonical ordering.
        Assert.Equal(
            report.Techniques.Select(t => t.Id).ToList(),
            deserialized.Techniques.Select(t => t.Id).ToList());
    }

    // ─── Output-store round-trip ──────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_RoundTripsThroughOutputStore_PreservesTwelveLeafShape_AndAtlasIds()
    {
        var run = MitreBenchmark.AtlasBaseline();
        var agent = new PassingAgent("MitreStoreAgent");
        var input = new EvalInput(
            Query: "evaluate me",
            Metadata: new Dictionary<string, object> { ["agent"] = agent });

        var result = await run.EvaluateAsync(input);
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(12, result.Details.SubResults!.Count);

        // Persist + read back through ScenarioResult.
        var scenarioResult = EvalResultPersistence.ToScenarioResult(
            result,
            scenarioId: "mitre-atlasbaseline",
            scenarioName: "MITRE ATLAS Baseline");
        Assert.NotNull(scenarioResult);
        Assert.False(string.IsNullOrEmpty(scenarioResult.Output));

        var restored = EvalResultPersistence.FromScenarioResult(scenarioResult);
        Assert.NotNull(restored);
        Assert.Equal(result.Metric.Key, restored!.Metric.Key);
        Assert.Equal(result.Score.Value, restored.Score.Value, precision: 6);
        Assert.Equal(result.Score.Label, restored.Score.Label);
        Assert.Equal(result.Score.Severity, restored.Score.Severity);
        Assert.NotNull(restored.Details.SubResults);
        Assert.Equal(12, restored.Details.SubResults!.Count);

        // Headline Phase-6 invariant: ATLAS technique IDs survive round-trip
        // through the output-store. This is the audit-chain tag preservation
        // promise of the Convention-2 adapter (see lastreview/10 §1156-1158).
        var restoredKeys = restored.Details.SubResults!.Select(l => l.Metric.Key).ToList();
        Assert.All(restoredKeys, k =>
            Assert.StartsWith("mitre.aml.t", k));

        // The 6 untested/non-applicable techniques must remain as skipped leaves.
        var restoredSkipped = restored.Details.SubResults
            .Where(l => l.Score.Label == "skipped")
            .Select(l => l.Metric.Key)
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(
            new[]
            {
                "mitre.aml.t0044", "mitre.aml.t0046", "mitre.aml.t0047",
                "mitre.aml.t0048", "mitre.aml.t0052", "mitre.aml.t0053"
            },
            restoredSkipped);
    }

    // ─── Convention-2 adapter equivalence ─────────────────────────────────────

    /// <summary>
    /// Phase-10 completeness audit (LR#21): pins the Convention-2 invariant that
    /// <see cref="MitreBenchmarkRun.EvaluateAsync"/> is functionally equivalent to
    /// <c>BuildEvalResult(await ScanAsync(agent))</c> — the two code paths must produce
    /// the same 12-leaf composite shape and scoring. Catches a regression where one
    /// path drifts from the other.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_EquivalentTo_ScanThenBuildEvalResult()
    {
        var agent = new PassingAgent("MitreEquivAgent");

        var runForEval  = MitreBenchmark.AtlasBaseline();
        var runForBuild = MitreBenchmark.AtlasBaseline();

        var evalInput = new EvalInput(
            Query: "drive both paths",
            Metadata: new Dictionary<string, object> { ["agent"] = agent });
        var viaAdapter = await runForEval.EvaluateAsync(evalInput);

        var rawResult = await runForBuild.ScanAsync(agent);
        var viaBuilder = runForBuild.BuildEvalResult(rawResult);

        // Composite shape parity
        Assert.Equal(viaAdapter.Metric.Key, viaBuilder.Metric.Key);
        Assert.Equal(viaAdapter.Metric.Category, viaBuilder.Metric.Category);
        Assert.Equal(viaAdapter.Details.AggregationStrategy, viaBuilder.Details.AggregationStrategy);

        // Sub-results parity (both 12-leaf composites)
        Assert.NotNull(viaAdapter.Details.SubResults);
        Assert.NotNull(viaBuilder.Details.SubResults);
        Assert.Equal(viaAdapter.Details.SubResults!.Count, viaBuilder.Details.SubResults!.Count);
        Assert.Equal(12, viaAdapter.Details.SubResults.Count);

        // ATLAS technique-ID parity preserved
        var adapterKeys = viaAdapter.Details.SubResults.Select(l => l.Metric.Key).ToList();
        var builderKeys = viaBuilder.Details.SubResults.Select(l => l.Metric.Key).ToList();
        Assert.Equal(adapterKeys, builderKeys);
        Assert.All(adapterKeys, k => Assert.StartsWith("mitre.aml.t", k));

        // Score label parity for a deterministic passing agent
        Assert.Equal(viaAdapter.Score.Label, viaBuilder.Score.Label);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An agent that always returns a safe, refusing response. Every red-team
    /// probe's heuristic evaluator should classify the response as "resisted".
    /// </summary>
    private sealed class PassingAgent : IEvaluableAgent
    {
        public string Name { get; }
        public PassingAgent(string name) { Name = name; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse
            {
                Text = "I cannot help with that request. Please ask something else."
            });
    }

    /// <summary>
    /// An agent that ECHOES whatever it receives. The red-team probe evaluators
    /// reliably classify echo-bot responses as compromised.
    /// </summary>
    private sealed class SuccumbingAgent : IEvaluableAgent
    {
        public string Name { get; }
        public SuccumbingAgent(string name) { Name = name; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = prompt });
    }

    /// <summary>Pass-through stub evaluator for preset-builder tests.</summary>
    private sealed class PassingStubEvaluator : IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = list
                    .Select(c => new AgentEval.Core.CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }
}
