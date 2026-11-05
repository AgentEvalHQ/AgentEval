// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Cli.Microsoft365;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using Xunit;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// M30/M31: the per-case turn sink of <c>--sut microsoft365</c>. Keeps the last result per case and every result in
/// order, is thread-safe, and its verbose line never fabricates: a missing answer prints its completeness, not a length;
/// an absent evidence channel prints its availability, not a count; a non-completed turn prints the safe failure, never
/// partial text.
/// </summary>
public class CliTurnSinkTests
{
    [Fact]
    public void Record_KeepsTheLastResultPerCase_AndAllInOrder()
    {
        var sink = new CliTurnSink(verbose: false, new StringWriter());
        var a1 = Completed("case-a", "first answer", taskId: "task-1", contextId: "ctx-1");
        var b1 = Completed("case-b", "b answer", taskId: "task-2", contextId: "ctx-2");
        var a2 = NotCompleted("case-a", WorkIqTurnOutcome.Failed, taskId: "task-3", contextId: "ctx-1");

        sink.Record(a1);
        sink.Record(b1);
        sink.Record(a2);

        Assert.Equal(3, sink.Count);
        Assert.Equal(new[] { a1, b1, a2 }, sink.All);
        Assert.Equal(2, sink.LastByCase.Count);
        Assert.Same(a2, sink.LastByCase["case-a"]);
        Assert.Same(b1, sink.LastByCase["case-b"]);
    }

    [Fact]
    public void Record_Snapshots_AreDetachedFromLaterRecords()
    {
        var sink = new CliTurnSink(verbose: false, new StringWriter());
        sink.Record(Completed("case-a", "x", "task-1", "ctx-1"));
        var all = sink.All;
        var last = sink.LastByCase;

        sink.Record(Completed("case-b", "y", "task-2", "ctx-2"));

        Assert.Single(all);
        Assert.Single(last);
        Assert.Equal(2, sink.Count);
    }

    [Fact]
    public void Record_NotVerbose_PrintsNothing()
    {
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose: false, err);

        sink.Record(Completed("case-a", "answer", "task-1", "ctx-1"));

        Assert.Equal("", err.ToString());
    }

    [Fact]
    public void Record_Verbose_PrintsExactlyOneLinePerTurn()
    {
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose: true, err);

        sink.Record(Completed("case-a", "answer", "task-1", "ctx-1"));
        sink.Record(NotCompleted("case-b", WorkIqTurnOutcome.InputRequired, "task-2", "ctx-2"));

        var lines = err.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("[m365 turn] case=", l, StringComparison.Ordinal));
        Assert.Contains("case=case-a", lines[0], StringComparison.Ordinal);
        Assert.Contains("case=case-b", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLine_CompletedTurn_ShowsCaseOutcomeIdsAnswerLengthMarkersAndToolAvailability()
    {
        var result = Completed("case-a", "Hello world", taskId: "task-1", contextId: "ctx-1", citationMarkers: 2);

        var line = CliTurnSink.FormatLine(result);

        Assert.Contains("case=case-a", line, StringComparison.Ordinal);
        Assert.Contains("turn=turn-case-a", line, StringComparison.Ordinal);
        Assert.Contains("outcome=Completed", line, StringComparison.Ordinal);
        Assert.Contains("transport=Completed", line, StringComparison.Ordinal);
        Assert.Contains("task=task-1", line, StringComparison.Ordinal);
        Assert.Contains("ctx=ctx-1", line, StringComparison.Ordinal);
        Assert.Contains("state=TASK_STATE_COMPLETED", line, StringComparison.Ordinal);
        Assert.Contains("answer=Complete(11 chars)", line, StringComparison.Ordinal);
        Assert.Contains("citationMarkers=Provided(2)", line, StringComparison.Ordinal);
        Assert.Contains("toolExecutions=NotRequested", line, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=250", line, StringComparison.Ordinal);
        Assert.DoesNotContain("failure=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello world", line, StringComparison.Ordinal);   // the line never carries answer text
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void FormatLine_NotCompletedTurn_ShowsNoneForMissingIds_CompletenessNotALength_AndTheSafeFailure()
    {
        var result = NotCompleted("case-z", WorkIqTurnOutcome.Indeterminate, taskId: null, contextId: null);

        var line = CliTurnSink.FormatLine(result);

        Assert.Contains("outcome=Indeterminate", line, StringComparison.Ordinal);
        Assert.Contains("task=none", line, StringComparison.Ordinal);
        Assert.Contains("ctx=none", line, StringComparison.Ordinal);
        Assert.Contains("state=none", line, StringComparison.Ordinal);
        Assert.Contains("answer=None", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0 chars", line, StringComparison.Ordinal);   // nothing unmeasured becomes a value
        Assert.Contains("citationMarkers=NotRequested", line, StringComparison.Ordinal);
        Assert.Contains("failure=Timeout: the local wait ended before a terminal state", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLine_FlattensNewlinesInIdsAndFailureText()
    {
        var result = NotCompleted("case\nx", WorkIqTurnOutcome.Failed, taskId: "t", contextId: "c", safeMessage: "line one\r\nline two");

        var line = CliTurnSink.FormatLine(result);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("case=case x", line, StringComparison.Ordinal);
        Assert.Contains("line one line two", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_IsThreadSafe_UnderConcurrentTurns()
    {
        var sink = new CliTurnSink(verbose: true, TextWriter.Null);

        Parallel.For(0, 200, i => sink.Record(Completed("case-" + (i % 10), "a", "task-" + i, "ctx-" + (i % 10))));

        Assert.Equal(200, sink.Count);
        Assert.Equal(200, sink.All.Count);
        Assert.Equal(10, sink.LastByCase.Count);
    }

    [Fact]
    public void Record_WriterClosed_StillRetainsTheResult()
    {
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose: true, err);
        err.Dispose();

        sink.Record(Completed("case-a", "answer", "task-1", "ctx-1"));

        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void Record_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CliTurnSink(verbose: false, new StringWriter()).Record(null!));
    }

    // ── helpers: real WorkIqTurnResult objects built through the evidence factories ──

    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static WorkIqTurnResult Completed(string caseId, string answer, string? taskId, string? contextId, int citationMarkers = 0)
    {
        var identity = Identity(caseId, taskId, contextId);
        var markers = citationMarkers > 0
            ? EvidenceSet<CitationMarker>.Provided(
                Enumerable.Range(0, citationMarkers)
                    .Select(i => new CitationMarker("marker-" + i, "unicode-v1", "cite", ImmutableArray.Create("turn0search" + i), 0, 1, ImmutableArray<SourceRef>.Empty))
                    .ToImmutableArray(),
                EvidenceValidation.Valid,
                EvidenceCoverage.Unknown,
                EvidenceScope.Turn,
                ImmutableArray<SourceRef>.Empty)
            : EvidenceSet<CitationMarker>.NotRequested(EvidenceScope.Turn);

        var evidence = Evidence(
            identity,
            taskState: "TASK_STATE_COMPLETED",
            answer: new AnswerEvidence(
                Observed<string>.Present(answer, ImmutableArray<SourceRef>.Empty),
                Observed<string>.Present(answer, ImmutableArray<SourceRef>.Empty),
                true,
                "selection-v1",
                ImmutableArray<SourceRef>.Empty),
            markers);

        return new WorkIqTurnResult(
            WorkIqTurnOutcome.Completed,
            TransportOutcome.Completed,
            OutputCompleteness.Complete,
            TelemetryCompleteness.NotRequested,
            Handle(taskId, contextId),
            contextId,
            answer,
            evidence,
            PendingInteraction: null,
            Failure: null,
            new WorkIqTurnTiming(Start, Start.AddMilliseconds(100), Start.AddMilliseconds(120), Start.AddMilliseconds(240), Start.AddMilliseconds(250), 0),
            identity);
    }

    private static WorkIqTurnResult NotCompleted(string caseId, WorkIqTurnOutcome outcome, string? taskId, string? contextId, string safeMessage = "the local wait ended before a terminal state")
    {
        var identity = Identity(caseId, taskId, contextId);
        var evidence = Evidence(identity, taskState: null, answer: null, EvidenceSet<CitationMarker>.NotRequested(EvidenceScope.Turn));

        return new WorkIqTurnResult(
            outcome,
            TransportOutcome.Interrupted,
            OutputCompleteness.None,
            TelemetryCompleteness.NotRequested,
            Handle(taskId, contextId),
            contextId,
            AnswerText: null,
            evidence,
            PendingInteraction: null,
            new WorkIqFailure(WorkIqFailureCategory.Timeout, safeMessage, OutcomeCertainty.Indeterminate, WorkIqOperation.Send, TaskId: taskId, ContextId: contextId),
            new WorkIqTurnTiming(Start, null, null, null, Start.AddMilliseconds(250), 0),
            identity);
    }

    private static TurnIdentity Identity(string caseId, string? taskId, string? contextId) =>
        new("run-1", caseId, "turn-" + caseId, 0, 0, "rpc-1", "msg-1", null, taskId, contextId);

    private static WorkIqTaskHandle? Handle(string? taskId, string? contextId) =>
        taskId is null ? null : new WorkIqTaskHandle(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, taskId, contextId, null, "principal-1");

    private static Microsoft365TurnEvidence Evidence(TurnIdentity identity, string? taskState, AnswerEvidence? answer, EvidenceSet<CitationMarker> markers) =>
        new(
            Microsoft365Evidence.SchemaVersion,
            Microsoft365Evidence.NormalizerVersion,
            Microsoft365Evidence.ProviderProfileVersion,
            Microsoft365Evidence.A2AVersion,
            identity,
            taskState,
            CaptureState.NotRetained,
            answer,
            EvidenceSet<ToolSelectionObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<ToolExecutionObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<ApprovalIntentObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<RetrievalExecutionObservation>.NotRequested(EvidenceScope.Turn),
            markers,
            EvidenceSet<CitationSource>.NotRequested(EvidenceScope.Turn),
            ImmutableArray<CitationLink>.Empty,
            EvidenceSet<SkillActivationObservation>.NotRequested(EvidenceScope.Turn),
            ImmutableArray<RawCaptureRef>.Empty,
            ImmutableArray<EvidenceIssue>.Empty);
}
