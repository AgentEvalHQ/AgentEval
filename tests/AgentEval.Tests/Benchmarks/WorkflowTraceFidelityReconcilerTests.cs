// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Models;
using AgentEval.Tracing;
using CoreTokenUsage = AgentEval.Core.TokenUsage;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Glass Box Part 2 — WF-SAMPLE-T4: <see cref="WorkflowTraceFidelityReconciler"/>. Confirms per-executor
/// reconciliation (grouping steps by executor and summing framework tokens), the four diff kinds, the
/// EvalResult tree shape, and the loop-workflow regression guard (fix C5).
/// </summary>
public class WorkflowTraceFidelityReconcilerTests
{
    private static ExecutorStep Step(string executorId, int stepIndex, int prompt, int completion, string? finish) =>
        new()
        {
            ExecutorId = executorId,
            Output = $"{executorId}#{stepIndex}",
            StepIndex = stepIndex,
            TokenUsage = new CoreTokenUsage { PromptTokens = prompt, CompletionTokens = completion },
            FinishReason = finish,
        };

    private static WorkflowExecutionResult Result(params ExecutorStep[] steps) =>
        new() { FinalOutput = "done", Steps = steps };

    private static AgentTrace ChatTrace(int totalTokens, string? finish)
    {
        var trace = new AgentTrace();
        trace.Entries.Add(AgentEval.Tracing.TraceEntry.ForChatResponse(
            index: 0, correlationId: null, text: "r", durationMs: 1,
            usage: new TraceTokenUsage { PromptTokens = totalTokens, CompletionTokens = 0 },
            toolCalls: null, finishReason: finish, providerMetadata: null));
        return trace;
    }

    [Fact]
    public void Reconcile_MatchingTokens_Agree()
    {
        var result = Result(Step("a", 0, 10, 5, "stop"));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(15, "stop") };

        var report = new WorkflowTraceFidelityReconciler().Reconcile(result, chat);

        var rec = Assert.Single(report.Executors);
        Assert.Equal(WorkflowFidelityDiff.Agree, rec.DiffKind);
        Assert.Equal(1.0, rec.Score);
        Assert.Equal(1.0, report.OverallScore);
    }

    [Fact]
    public void Reconcile_TokenDivergence_TokenMismatch()
    {
        var result = Result(Step("a", 0, 10, 5, "stop"));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(99, "stop") };

        var rec = Assert.Single(new WorkflowTraceFidelityReconciler().Reconcile(result, chat).Executors);

        Assert.Equal(WorkflowFidelityDiff.TokenMismatch, rec.DiffKind);
        Assert.Equal(0.5, rec.Score);
        Assert.Equal(15, rec.TokensFramework);
        Assert.Equal(99, rec.TokensChatTruth);
    }

    [Fact]
    public void Reconcile_FrameworkOverReportsOrRoundingDiff_IsNotTokenMismatch()
    {
        // Over-reporting (framework > chat truth) and a sub-tolerance (<2%) difference are NOT deceptions —
        // only meaningful UNDER-reporting is flagged (mirrors TraceFidelityRunner).
        var overReport = new WorkflowTraceFidelityReconciler().Reconcile(
            Result(Step("a", 0, 60, 40, "stop")),                       // framework 100
            new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(99, "stop") }); // chat 99
        Assert.Equal(WorkflowFidelityDiff.Agree, Assert.Single(overReport.Executors).DiffKind);

        var rounding = new WorkflowTraceFidelityReconciler().Reconcile(
            Result(Step("a", 0, 600, 400, "stop")),                     // framework 1000
            new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(1005, "stop") }); // chat 1005 (0.5% short)
        Assert.Equal(WorkflowFidelityDiff.Agree, Assert.Single(rounding.Executors).DiffKind);
    }

    [Fact]
    public void Reconcile_FrameworkUnderReportsBeyondTolerance_IsTokenMismatch()
    {
        // Framework hides ~13% of the tokens the model actually consumed → a real fidelity discrepancy.
        var rec = Assert.Single(new WorkflowTraceFidelityReconciler().Reconcile(
            Result(Step("a", 0, 520, 350, "stop")),                      // framework 870
            new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(1000, "stop") }).Executors); // chat 1000
        Assert.Equal(WorkflowFidelityDiff.TokenMismatch, rec.DiffKind);
    }

    [Fact]
    public void Reconcile_TokenUnderReportAndSuppressedFinish_IsBoth_ScoresZero()
    {
        // Both axes diverge at once: framework hides ~13% of tokens AND suppresses the finish reason.
        var result = Result(Step("a", 0, 520, 350, finish: null));       // framework 870, no finish
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(1000, "content_filter") };

        var rec = Assert.Single(new WorkflowTraceFidelityReconciler().Reconcile(result, chat).Executors);

        Assert.Equal(WorkflowFidelityDiff.Both, rec.DiffKind);
        Assert.Equal(0.0, rec.Score);
    }

    [Fact]
    public void OverallScore_ExcludesNoTruthExecutors_SoDeceptionIsNotDilutedAway()
    {
        // One traced executor is a full deception (Both, 0.0); three others are untraced (NoTruth, 1.0).
        // A plain mean would give 0.75 (Passed). Excluding NoTruth, the deception must dominate → 0.0.
        var result = Result(
            Step("a", 0, 520, 350, finish: null),
            Step("b", 1, 10, 5, "stop"),
            Step("c", 2, 10, 5, "stop"),
            Step("d", 3, 10, 5, "stop"));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(1000, "content_filter") };

        var report = new WorkflowTraceFidelityReconciler().Reconcile(result, chat);

        Assert.Equal(0.0, report.OverallScore);
        Assert.Equal(1, report.VerifiedCount);

        var root = new WorkflowTraceFidelityReconciler().ReconcileToEvalResult(result, chat);
        Assert.False(root.Score.Passed); // the deception is NOT averaged past the 0.8 gate
    }

    [Fact]
    public void Reconcile_SuppressedFinishReason_IsFinishMismatch()
    {
        // The headline deception: chat truth hit a content filter, but the framework ledger reports no
        // finish reason (null). This MUST be flagged, not scored as Agree.
        var result = Result(Step("a", 0, 10, 5, finish: null));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(15, "content_filter") };

        var rec = Assert.Single(new WorkflowTraceFidelityReconciler().Reconcile(result, chat).Executors);

        Assert.Equal(WorkflowFidelityDiff.FinishMismatch, rec.DiffKind);
        Assert.Equal(0.5, rec.Score);
        Assert.Null(rec.FinishFramework);
        Assert.Equal("content_filter", rec.FinishChatTruth);
    }

    [Fact]
    public void Reconcile_ChatTraceWithNoResponses_IsNoTruth_NotTokenMismatch()
    {
        // A supplied trace with zero ChatTurn responses is not "truth" — comparing framework tokens against a
        // zero total would raise a spurious TokenMismatch.
        var result = Result(Step("a", 0, 10, 5, "stop"));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = new AgentTrace() };

        var rec = Assert.Single(new WorkflowTraceFidelityReconciler().Reconcile(result, chat).Executors);

        Assert.Equal(WorkflowFidelityDiff.NoTruth, rec.DiffKind);
        Assert.Equal(1.0, rec.Score);
    }

    [Fact]
    public void Reconcile_NoChatTrace_NoTruth_ScoresOne()
    {
        var report = new WorkflowTraceFidelityReconciler().Reconcile(Result(Step("a", 0, 10, 5, "stop")), chatTraces: null);

        var rec = Assert.Single(report.Executors);
        Assert.Equal(WorkflowFidelityDiff.NoTruth, rec.DiffKind);
        Assert.Equal(1.0, rec.Score);
        Assert.Null(rec.TokensChatTruth);
    }

    [Fact]
    public void Reconcile_LoopWorkflow_GroupsByExecutor_SumsFrameworkTokens_Agree()
    {
        // A runs twice (A -> B -> A): two steps for A totaling 10+5 + 1+1 = 17 tokens, against A's single
        // whole-run chat trace of 17. WITHOUT per-executor grouping this would be a false TokenMismatch (C5).
        var result = Result(
            Step("a", 0, 10, 5, "stop"),
            Step("b", 1, 20, 8, "stop"),
            Step("a", 2, 1, 1, "stop"));
        var chat = new Dictionary<string, AgentTrace>
        {
            ["a"] = ChatTrace(17, "stop"),
            ["b"] = ChatTrace(28, "stop"),
        };

        var report = new WorkflowTraceFidelityReconciler().Reconcile(result, chat);

        Assert.Equal(2, report.Executors.Count); // one record per EXECUTOR, not per step
        var a = report.Executors.Single(e => e.ExecutorId == "a");
        Assert.Equal(17, a.TokensFramework);
        Assert.Equal(WorkflowFidelityDiff.Agree, a.DiffKind);
    }

    [Fact]
    public void ReconcileToEvalResult_ProducesRootWithPerExecutorLeaves()
    {
        var result = Result(Step("a", 0, 10, 5, "stop"), Step("b", 1, 20, 8, "stop"));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(15, "stop"), ["b"] = ChatTrace(99, "stop") };

        var root = new WorkflowTraceFidelityReconciler().ReconcileToEvalResult(result, chat);

        Assert.Equal("workflow_trace_fidelity", root.Metric.Key);
        Assert.NotNull(root.Details.SubResults);
        Assert.Equal(2, root.Details.SubResults!.Count);
        // a agrees (1.0), b token-mismatches (0.5) => mean 0.75
        Assert.Equal(0.75, root.Score.Value, precision: 5);
        Assert.All(root.Details.SubResults, leaf => Assert.StartsWith("workflow_trace_fidelity.executor.", leaf.Metric.Key));

        // Severity must use the normalized lowercase vocabulary SeverityRollup recognizes, or a real
        // mismatch (b, score 0.5) would roll up as "none" and hide the warning.
        var recognized = new[] { "none", "low", "medium", "high", "critical" };
        Assert.Contains(root.Score.Severity, recognized);
        Assert.All(root.Details.SubResults, leaf => Assert.Contains(leaf.Score.Severity, recognized));
        var bLeaf = root.Details.SubResults.Single(l => l.Metric.Key.EndsWith(".b"));
        Assert.Equal("medium", bLeaf.Score.Severity); // score 0.5 => medium, not "Medium"
    }

    [Fact]
    public void ReconcileToEvalResult_NullFinishReason_RendersAsAngleBracketNull_NotEmptyString()
    {
        // A suppressed/null finish reason is a key fidelity signal.  The evidence message must render null
        // explicitly as "<null>" so consumers can distinguish "framework reported nothing" from an empty string.
        var result = Result(Step("a", 0, 10, 5, finish: null));
        var chat = new Dictionary<string, AgentTrace> { ["a"] = ChatTrace(15, finish: null) };

        var root = new WorkflowTraceFidelityReconciler().ReconcileToEvalResult(result, chat);

        var leaf = Assert.Single(root.Details.SubResults!);
        var evidenceMsg = Assert.Single(leaf.Details.Evidence!).Message;
        Assert.Contains("<null>", evidenceMsg);
        Assert.DoesNotContain("/ ''", evidenceMsg); // must not render as empty-quoted string
    }
}
