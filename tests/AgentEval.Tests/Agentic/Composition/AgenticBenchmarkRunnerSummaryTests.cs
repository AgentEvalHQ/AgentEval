// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Composition;
using Xunit;

namespace AgentEval.Tests.Agentic.Composition;

/// <summary>
/// Regression coverage for BUG-04: AgenticBenchmarkRunner.BuildSummary counted skipped
/// leaves (Label "skipped") as failures, so RunStats disagreed with the composite verdict.
/// </summary>
public class AgenticBenchmarkRunnerSummaryTests
{
    private static EvalResult Leaf(string key, double score, string label, bool passed) =>
        new(
            Metric: new(key, key, "system-outcome", "1.0.0"),
            Score: new(score, null, label, passed, 0.85, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("test", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void BuildSummary_SkippedLeaves_NotCountedAsFailed_AndBucketsReconcile()
    {
        var root = new EvalResult(
            Metric: new("agentic.standard", "Agentic", "agentic", "1.0.0"),
            Score: new(0.5, null, "fail", false, 0.85, "none", null),
            Details: new(null, null, null, new[]
            {
                Leaf("task_completion", 0.9, "pass", passed: true),
                Leaf("task_adherence",  0.2, "fail", passed: false),
                Leaf("tool_call",       0.7, "warn", passed: false),
                Leaf("telemetry",       0.0, "skipped", passed: false),
            }, "weighted-sum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UnixEpoch);

        var summary = AgenticBenchmarkRunner.BuildSummary(root, "run-1");

        Assert.Equal(4, summary.Stats.Total);
        Assert.Equal(1, summary.Stats.Passed);
        Assert.Equal(1, summary.Stats.Failed);    // skipped is NOT lumped in here (was 2 pre-fix)
        Assert.Equal(1, summary.Stats.Warnings);
        Assert.Equal(1, summary.Stats.Skipped);

        // The four buckets must reconcile against Total.
        Assert.Equal(
            summary.Stats.Total,
            summary.Stats.Passed + summary.Stats.Failed + summary.Stats.Warnings + summary.Stats.Skipped);
    }
}
