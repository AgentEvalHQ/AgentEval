// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Cli.Commands;
using AgentEval.Evals.Meta;
using AgentEval.Memory.External.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// A bench run that scored nothing is written to AEF as not measured: never as a failed or scored 0 beside an
/// INCONCLUSIVE exit (T13-6), and an auto-audit fidelity with no chat-boundary truth is not a measured 1.0 (T13-11).
/// </summary>
public sealed class BenchAefNotMeasuredTests
{
    private static QuestionResult Question(string id, bool? correct, TypedMemEvalOutcome? outcome = null) => new()
    {
        QuestionId = id,
        QuestionType = "temporal-reasoning",
        Question = "q",
        GoldAnswer = "g",
        AgentResponse = "a",
        Correct = correct,
        RawScore = null,
        TypedOutcome = outcome is { } o
            ? new TypedMemEvalQuestionDetail { Shape = "recency", Outcome = o, Attribution = TypedMemEvalEvidenceAttribution.Unobserved }
            : null,
    };

    private static ExternalBenchmarkResult Result(double? accuracy, params QuestionResult[] questions) => new()
    {
        BenchmarkId = "longmemeval",
        BenchmarkName = "LongMemEval",
        OverallAccuracy = accuracy,
        TaskAveragedAccuracy = null,
        PerTypeResults = [],
        Duration = TimeSpan.FromSeconds(1),
        Options = new ExternalBenchmarkOptions(),
        QuestionResults = questions,
    };

    [Fact]
    public void LongMemEvalTree_WithNothingScored_IsNotMeasured_NotFailedAtZero()
    {
        var tree = BenchAef.QuestionTree(Result(null, Question("q1", null)), "longmemeval", "Quick", "INCONCLUSIVE", null);

        Assert.Equal(AefState.NotMeasured, AefCompositeRunWriter.StateOf(tree.Score));
        Assert.False(tree.Score.Passed);
    }

    [Fact]
    public void LongMemEvalTree_WithAnAccuracy_IsStillMeasured()
    {
        // Guard: a scored run keeps its verdict.
        var tree = BenchAef.QuestionTree(Result(100, Question("q1", true)), "longmemeval", "Quick", "PASS", null);

        Assert.Equal(AefState.Passed, AefCompositeRunWriter.StateOf(tree.Score));
    }

    [Fact]
    public void TypedMemEvalTree_WithNothingScored_IsNotMeasured_NotScoredZero()
    {
        var result = Result(null, Question("q1", null, TypedMemEvalOutcome.Inconclusive));

        var (tree, _) = BenchAef.TypedTree(result, "forgetting", null);

        Assert.Equal(MeasurementState.NotMeasured, tree.Score.Measurement);
        Assert.Equal(AefState.NotMeasured, AefCompositeRunWriter.StateOf(tree.Score));
    }

    [Fact]
    public void AutoAuditTree_WithNoChatBoundaryTruth_WritesTheFidelityAsNotMeasured()
    {
        var endpoint = new AutoAuditEndpointResult("ep", 1.0, 0, 0, 0, 0, true, []) { FidelityMeasured = false };

        var (tree, _) = BenchAef.AutoAuditTree(endpoint);

        Assert.Equal(AefState.NotMeasured, AefCompositeRunWriter.StateOf(tree.Score));
        var fidelity = tree.Details.SubResults!.Single(r => r.Metric.Key == "fidelity");
        Assert.Equal(AefState.NotMeasured, AefCompositeRunWriter.StateOf(fidelity.Score));
        var safety = tree.Details.SubResults!.Single(r => r.Metric.Key == "safety");
        Assert.Equal(AefState.Scored, AefCompositeRunWriter.StateOf(safety.Score));
    }

    [Fact]
    public void AutoAuditTree_WithAMeasuredFidelity_IsScored()
    {
        var (tree, _) = BenchAef.AutoAuditTree(new AutoAuditEndpointResult("ep", 0.9, 0, 10, 5, 3, true, []));

        Assert.Equal(AefState.Scored, AefCompositeRunWriter.StateOf(tree.Score));
    }
}
