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
/// Tests for the <see cref="OwaspBenchmark"/> façade and its
/// <see cref="OwaspBenchmarkRun.EvaluateAsync"/> adapter into the unified
/// <see cref="EvalResult"/> pipeline.
/// </summary>
public class OwaspBenchmarkTests
{
    // ─── Preset shape ─────────────────────────────────────────────────────────

    [Fact]
    public void Top10_ReturnsNonNullRun_WithAllNineAttacks()
    {
        var run = OwaspBenchmark.Top10();
        Assert.NotNull(run);
        Assert.Equal("Top10", run.PresetName);

        // Top10 wires the full 9-attack roster from Attack.All.
        Assert.Equal(Attack.All.Count, run.Pipeline.GetProbePreview().Select(p => p.AttackName).Distinct().Count());

        // 6 OWASP categories covered (LLM01, LLM02, LLM05, LLM06, LLM07, LLM10).
        var covered = run.CoveredOwaspIds;
        Assert.Contains("LLM01", covered);
        Assert.Contains("LLM02", covered);
        Assert.Contains("LLM05", covered);
        Assert.Contains("LLM06", covered);
        Assert.Contains("LLM07", covered);
        Assert.Contains("LLM10", covered);
        Assert.Equal(6, covered.Count);
    }

    [Fact]
    public void Smoke_ReturnsNonNullRun_WithExactlyThreeMvpAttacks()
    {
        var run = OwaspBenchmark.Smoke();
        Assert.NotNull(run);
        Assert.Equal("Smoke", run.PresetName);

        var attackNames = run.Pipeline.GetProbePreview()
            .Select(p => p.AttackName).Distinct().OrderBy(n => n).ToList();
        Assert.Equal(3, attackNames.Count);
        Assert.Contains("Jailbreak", attackNames);
        Assert.Contains("PIILeakage", attackNames);
        Assert.Contains("PromptInjection", attackNames);

        // Smoke covers only LLM01 + LLM02.
        var covered = run.CoveredOwaspIds;
        Assert.Equal(new[] { "LLM01", "LLM02" }.OrderBy(x => x), covered.OrderBy(x => x));
    }

    [Fact]
    public void AuditGrade_ReturnsNonNullRun_WithAllAttacks()
    {
        var run = OwaspBenchmark.AuditGrade();
        Assert.NotNull(run);
        Assert.Equal("AuditGrade", run.PresetName);
        Assert.Equal(Attack.All.Count, run.Pipeline.GetProbePreview().Select(p => p.AttackName).Distinct().Count());
    }

    [Fact]
    public void Top10ForRag_ReturnsNonNullRun_WithAllAttacks()
    {
        var run = OwaspBenchmark.Top10ForRag();
        Assert.NotNull(run);
        Assert.Equal("Top10ForRag", run.PresetName);
        Assert.Equal(Attack.All.Count, run.Pipeline.GetProbePreview().Select(p => p.AttackName).Distinct().Count());
    }

    [Fact]
    public void AllPresets_AcceptOptionalJudge()
    {
        var stub = new PassingStubEvaluator();
        Assert.NotNull(OwaspBenchmark.Top10(stub));
        Assert.NotNull(OwaspBenchmark.Smoke(stub));
        Assert.NotNull(OwaspBenchmark.AuditGrade(stub));
        Assert.NotNull(OwaspBenchmark.Top10ForRag(stub));
    }

    // ─── EvaluateAsync composite shape ────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_NoAgentInMetadata_ReturnsSkippedCompositeWithTenLeaves()
    {
        var run = OwaspBenchmark.Smoke();
        var input = new EvalInput(Query: "any");          // no agent in Metadata

        var result = await run.EvaluateAsync(input);

        Assert.NotNull(result);
        Assert.Equal("skipped", result.Score.Label);
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(10, result.Details.SubResults!.Count);
        // All 10 leaves should be skipped when no agent is supplied.
        Assert.All(result.Details.SubResults, leaf => Assert.Equal("skipped", leaf.Score.Label));
    }

    [Fact]
    public async Task EvaluateAsync_PassingAgent_ProducesTenLeafComposite_WithSixTestedFourSkipped()
    {
        var run = OwaspBenchmark.Top10();
        var agent = new PassingAgent("OwaspPassingAgent");
        var input = new EvalInput(
            Query: "evaluate this agent",
            Metadata: new Dictionary<string, object> { ["agent"] = agent });

        var result = await run.EvaluateAsync(input);

        // Top-level composite shape
        Assert.NotNull(result);
        Assert.Equal("owasp.top10", result.Metric.Key);
        Assert.Equal("compliance.owasp", result.Metric.Category);
        Assert.NotNull(result.Details.SubResults);

        var leaves = result.Details.SubResults!;
        Assert.Equal(10, leaves.Count);                                // canonical 10-leaf shape

        // 4 categories untested (LLM03/04/08/09) must appear as skipped leaves.
        var skipped = leaves.Where(l => l.Score.Label == "skipped").ToList();
        Assert.Equal(4, skipped.Count);
        var skippedKeys = skipped.Select(l => l.Metric.Key).OrderBy(x => x).ToList();
        Assert.Equal(
            new[] { "owasp.llm03", "owasp.llm04", "owasp.llm08", "owasp.llm09" },
            skippedKeys);

        // 6 tested categories must pass against an always-passing agent.
        var tested = leaves.Where(l => l.Score.Label != "skipped").ToList();
        Assert.Equal(6, tested.Count);
        Assert.All(tested, leaf =>
        {
            Assert.Equal("pass", leaf.Score.Label);
            Assert.True(leaf.Score.Passed);
            Assert.Equal("none", leaf.Score.Severity);
            Assert.Equal(1.0, leaf.Score.Value, precision: 6);
        });

        // Composite should reflect the passing tested leaves (min over non-skipped = 1.0).
        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal("Min", result.Details.AggregationStrategy);
    }

    // ─── EvaluateAsync failure path ───────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_SuccumbingAgent_ProducesCriticalLLM01Leaf()
    {
        // A "naive" agent that ECHOES whatever it receives — every prompt-injection
        // probe will succeed against it because the probes specifically test for
        // instruction-override compliance, and an echo-bot complies with everything.
        var run = OwaspBenchmark.Smoke();
        var agent = new SuccumbingAgent("OwaspSuccumbingAgent");
        var input = new EvalInput(
            Query: "evaluate this agent",
            Metadata: new Dictionary<string, object> { ["agent"] = agent });

        var result = await run.EvaluateAsync(input);

        Assert.NotNull(result);
        Assert.NotNull(result.Details.SubResults);
        var leaves = result.Details.SubResults!;
        Assert.Equal(10, leaves.Count);

        // LLM01 leaf should reflect prompt-injection failures.
        var llm01 = leaves.FirstOrDefault(l => l.Metric.Key == "owasp.llm01");
        Assert.NotNull(llm01);
        Assert.NotEqual("skipped", llm01!.Score.Label);
        // Severity must escalate to fail or warn (depending on per-probe severity);
        // a succumbing agent on the Smoke preset (which includes PromptInjection +
        // Jailbreak) reliably triggers at least medium severity, so we assert
        // label != "pass" and severity != "none". This avoids over-binding to
        // any specific high/medium classification (heuristic evaluators may shift
        // between releases).
        Assert.NotEqual("pass", llm01.Score.Label);
        Assert.NotEqual("none", llm01.Score.Severity);
        Assert.False(llm01.Score.Passed);

        // Evidence must contain at least one probe entry (the failing prompts).
        Assert.NotNull(llm01.Details.Evidence);
        Assert.True(llm01.Details.Evidence!.Count >= 1,
            "LLM01 leaf must carry at least the category-description evidence row.");

        // Composite is at least "warn", and not "pass".
        Assert.NotEqual("pass", result.Score.Label);
    }

    // ─── GenerateReport JSON round-trip ───────────────────────────────────────

    [Fact]
    public async Task GenerateReport_ProducesAllTenCategories_AndRoundTripsJson()
    {
        var run = OwaspBenchmark.Top10();
        var agent = new PassingAgent("OwaspReportAgent");

        // First produce a RedTeamResult by running the scan.
        var redTeamResult = await run.ScanAsync(agent);
        var report = run.GenerateReport(redTeamResult);

        Assert.NotNull(report);
        Assert.Equal(10, report.Categories.Count);                            // all 10 covered

        var json = report.ToJson();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var deserialized = JsonSerializer.Deserialize<OWASPComplianceReport>(
            json,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

        Assert.NotNull(deserialized);
        Assert.Equal(report.AgentName, deserialized!.AgentName);
        Assert.Equal(report.Categories.Count, deserialized.Categories.Count);
        // Structural equality on the category IDs preserves the LLM01..LLM10 ordering.
        Assert.Equal(
            report.Categories.Select(c => c.Id).ToList(),
            deserialized.Categories.Select(c => c.Id).ToList());
    }

    // ─── Output-store round-trip ──────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_RoundTripsThroughOutputStore_PreservesTenLeafShape()
    {
        var run = OwaspBenchmark.Top10();
        var agent = new PassingAgent("OwaspStoreAgent");
        var input = new EvalInput(
            Query: "evaluate me",
            Metadata: new Dictionary<string, object> { ["agent"] = agent });

        var result = await run.EvaluateAsync(input);
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(10, result.Details.SubResults!.Count);

        // Persist + read back through ScenarioResult.
        var scenarioResult = EvalResultPersistence.ToScenarioResult(
            result,
            scenarioId: "owasp-top10",
            scenarioName: "OWASP Top 10");
        Assert.NotNull(scenarioResult);
        Assert.False(string.IsNullOrEmpty(scenarioResult.Output));

        var restored = EvalResultPersistence.FromScenarioResult(scenarioResult);
        Assert.NotNull(restored);
        Assert.Equal(result.Metric.Key, restored!.Metric.Key);
        Assert.Equal(result.Score.Value, restored.Score.Value, precision: 6);
        Assert.Equal(result.Score.Label, restored.Score.Label);
        Assert.Equal(result.Score.Severity, restored.Score.Severity);
        Assert.NotNull(restored.Details.SubResults);
        Assert.Equal(10, restored.Details.SubResults!.Count);

        // The 4 untested categories must remain as skipped leaves after round-trip.
        var restoredSkipped = restored.Details.SubResults
            .Where(l => l.Score.Label == "skipped")
            .Select(l => l.Metric.Key)
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(
            new[] { "owasp.llm03", "owasp.llm04", "owasp.llm08", "owasp.llm09" },
            restoredSkipped);
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
    /// (which look for compliance with injection / jailbreak instructions in the
    /// response) will reliably classify echo-bot responses as compromised, since
    /// the probe text typically embeds the trigger phrase the evaluator looks for.
    /// </summary>
    private sealed class SuccumbingAgent : IEvaluableAgent
    {
        public string Name { get; }
        public SuccumbingAgent(string name) { Name = name; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = prompt });
    }

    /// <summary>Pass-through stub evaluator for Top10/Smoke/AuditGrade preset-builder tests.</summary>
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
