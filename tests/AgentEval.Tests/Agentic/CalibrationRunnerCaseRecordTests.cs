// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Calibration;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// A calibration run can keep every evaluated case — the leaf verdict AND each criterion's verdict — so a later
/// analysis can recompute verdicts from the criteria without paying for the calls again. Before this, a run kept
/// only category aggregates.
/// </summary>
public class CalibrationRunnerCaseRecordTests
{
    private sealed class FixedEval : IEval
    {
        public string Key => "k";
        public string Name => "k";
        public string Category => "test";
        public string Version => "1.1.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version),
                new(0.8, null, "pass", true, 0.7, "none", null),
                new(new Dictionary<string, double> { ["Resists the attack."] = 1.0, ["Stays in role."] = 0.0 }, null, null, null, null),
                new("atomic-llm", "judge-x", "agenteval.judge.default-system.v1", "abcdef0123456789", null, 0, false),
                DateTimeOffset.UtcNow));
    }

    private static CalibrationDataset Dataset(int n) => new("adversarial",
        Enumerable.Range(0, n).Select(i => new CalibrationEntry($"cal-{i}", "k", "q", "r", "pass", 0.7, 1.0, "why")).ToList());

    [Fact]
    public async Task EveryCase_IsRecorded_WithItsCriteriaAndPromptIdentity()
    {
        var records = new List<CalibrationCaseRecord>();
        var runner = new CalibrationRunner(_ => new FixedEval());

        await runner.RunAsync([Dataset(3)], (r, _) => { records.Add(r); return Task.CompletedTask; }, limitPerCategory: null);

        Assert.Equal(3, records.Count);
        var leaf = Assert.Single(records[0].Leaves);
        Assert.Equal(0.0, leaf.Criteria!["Stays in role."]);
        Assert.Equal("abcdef0123456789", leaf.PromptHash);
        Assert.Equal("1.1.0", records[0].EvaluatorVersion);
        Assert.Null(leaf.AggregationStrategy);   // atomic: Criteria are criterion verdicts
    }

    private sealed class AggregatingEval : IEval
    {
        public string Key => "k";
        public string Name => "k";
        public string Category => "test";
        public string Version => "1.1.0";

        // The JailbreakResistanceEval shape: one score per matched pattern, no sub-results kept.
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version),
                new(0.5, null, "fail", false, 0.7, "none", null),
                new(new Dictionary<string, double> { ["pattern-dan"] = 1.0, ["pattern-aim"] = 0.0 }, null, null, null, "mean-of-2-pattern-scores"),
                new("atomic-llm", "judge-x", null, null, null, 0, false),
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AnAggregateLeaf_SaysItsCriteriaAreDimensions_NotCriterionVerdicts()
    {
        var records = new List<CalibrationCaseRecord>();
        await new CalibrationRunner(_ => new AggregatingEval())
            .RunAsync([Dataset(1)], (r, _) => { records.Add(r); return Task.CompletedTask; }, limitPerCategory: null);

        var leaf = Assert.Single(records[0].Leaves);
        Assert.Equal("mean-of-2-pattern-scores", leaf.AggregationStrategy);
        Assert.Contains("pattern-dan", leaf.Criteria!.Keys);
    }

    [Fact]
    public async Task TheLimit_IsPerCategory()
    {
        var records = new List<CalibrationCaseRecord>();
        var report = await new CalibrationRunner(_ => new FixedEval())
            .RunAsync([Dataset(5)], (r, _) => { records.Add(r); return Task.CompletedTask; }, limitPerCategory: 1);

        Assert.Single(records);
        Assert.Equal(1, report.PerCategory["adversarial"].EntryCount);
    }
}
