// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Output;
using Xunit;
using AgenticBenchmarkFactory = AgentEval.Evals.Agentic.Composition.AgenticBenchmark;

namespace AgentEval.Tests.Agentic.EndToEnd;

/// <summary>
/// End-to-end test for the Judge Quality preset.
/// Validates that the preset produces a 3-component composite result from synthetic input metadata.
/// No LLM judge is required — all three components are pure-code meta-evaluators.
/// </summary>
public class AgenticJudgeQualityE2ETest
{
    private static EvalResult MakeResult(string label, double score = 1.0) =>
        new(
            Metric: new("stub_judge", "Stub Judge", "test", "1.0.0"),
            Score: new(score, null, label, label == "pass", 0.70, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("stub", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void JudgeQuality_HasExpectedComponents()
    {
        var benchmark = AgenticBenchmarkFactory.JudgeQuality();

        // JudgeQuality preset defines exactly 3 top-level component evaluators.
        Assert.Equal(3, benchmark.Components.Count);
    }

    [Fact]
    public async Task JudgeQuality_SyntheticMetadata_ProducesWith3ComponentResult()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-JudgeQualityAgent");
        var benchmark = AgenticBenchmarkFactory.JudgeQuality();
        var runner = new AgenticBenchmarkRunner();

        // Supply all three metadata keys required by the meta-evaluators
        var judgeResults = new List<EvalResult>
        {
            MakeResult("pass", 1.0),
            MakeResult("pass", 1.0),
            MakeResult("pass", 1.0),
        };

        var calibrationPairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),
            ("fail", "fail"),
            ("pass", "pass"),
            ("pass", "pass"),
        };

        var snapshotA = new Dictionary<string, double>
        {
            ["task_completion"] = 0.90,
            ["groundedness"] = 0.85,
        };
        var snapshotB = new Dictionary<string, double>
        {
            ["task_completion"] = 0.91,   // delta = 0.01 — within threshold
            ["groundedness"] = 0.84,      // delta = 0.01 — within threshold
        };

        var input = new EvalInput(
            Query: "meta-eval",
            Metadata: new Dictionary<string, object>
            {
                ["judge_results"]      = judgeResults,
                ["calibration_pairs"]  = calibrationPairs,
                ["snapshot_a"]         = (IReadOnlyDictionary<string, double>)snapshotA,
                ["snapshot_b"]         = (IReadOnlyDictionary<string, double>)snapshotB,
            });

        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotEmpty(runId);
        Assert.NotNull(result);

        // Composite tree must have exactly 3 sub-results (one per meta-evaluator)
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(3, result.Details.SubResults!.Count);

        // Verify the sub-results carry the expected keys
        var keys = result.Details.SubResults.Select(r => r.Metric.Key).ToHashSet();
        Assert.Contains("judge_agreement", keys);
        Assert.Contains("calibration_accuracy", keys);
        Assert.Contains("judge_drift", keys);
    }
}
