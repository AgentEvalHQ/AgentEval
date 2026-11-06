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
/// The per-case turn sink of <c>--sut microsoft365</c>. Keeps every result in order, hands back a case's latest turn
/// since a cursor, is thread-safe, and its verbose line never fabricates: a missing answer prints its completeness, not
/// a length; an evidence channel prints <c>EvidenceSet.Describe</c> (availability, count, and validation when not valid),
/// never a bare count; a non-completed turn prints the safe failure, never partial text.
/// </summary>
public class CliTurnSinkTests
{
    [Fact]
    public void Record_KeepsEveryResult_LatestForIsTheCasesLastTurn()
    {
        var sink = new CliTurnSink(verbose: false, new StringWriter());
        var a1 = Completed("case-a", "first answer", taskId: "task-1", contextId: "ctx-1");
        var b1 = Completed("case-b", "b answer", taskId: "task-2", contextId: "ctx-2");
        var a2 = NotCompleted("case-a", WorkIqTurnOutcome.Failed, taskId: "task-3", contextId: "ctx-1");

        sink.Record(a1);
        sink.Record(b1);
        sink.Record(a2);

        Assert.Equal(3, sink.Count);
        Assert.Same(a2, sink.LatestFor("case-a", since: 0));
        Assert.Same(b1, sink.LatestFor("case-b", since: 0));
    }

    [Fact]
    public void LatestFor_NothingRecordedForTheCaseSinceTheCursor_IsNull()
    {
        var sink = new CliTurnSink(verbose: false, new StringWriter());
        var a1 = Completed("case-a", "x", "task-1", "ctx-1");
        sink.Record(a1);
        var cursor = sink.Count;

        sink.Record(Completed("case-b", "y", "task-2", "ctx-2"));

        Assert.Null(sink.LatestFor("case-a", cursor));   // a later trial never borrows an earlier trial's turn
        Assert.Same(a1, sink.LatestFor("case-a", since: 0));
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
        Assert.Contains("citationMarkers=Provided (2 marker(s))", line, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(EvidenceValidation.Conflicting)]
    [InlineData(EvidenceValidation.Malformed)]
    public void FormatLine_ProvidedButNotValidChannel_ShowsItsValidation_NeverReadsAsObservedEmpty(EvidenceValidation validation)
    {
        // A conflicting or malformed channel holds no items; before the shared Describe it printed "Provided(0)", the same
        // text as a channel that was observed empty, so "no tools ran" and "the tool evidence disagreed" looked alike.
        var result = Completed("case-a", "answer", "task-1", "ctx-1", toolExecutions: ProvidedToolExecutions(validation));

        var line = CliTurnSink.FormatLine(result);

        Assert.Contains($"toolExecutions=Provided (0 execution(s), {validation})", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Provided(0)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("observed empty", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLine_ObservedEmptyChannel_SaysObservedEmpty()
    {
        var result = Completed("case-a", "answer", "task-1", "ctx-1", toolExecutions: ProvidedToolExecutions(EvidenceValidation.Valid));

        var line = CliTurnSink.FormatLine(result);

        Assert.Contains("toolExecutions=Provided (observed empty: 0 execution(s) in this snapshot)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_IsThreadSafe_UnderConcurrentTurns()
    {
        var sink = new CliTurnSink(verbose: true, TextWriter.Null);

        Parallel.For(0, 200, i => sink.Record(Completed("case-" + (i % 10), "a", "task-" + i, "ctx-" + (i % 10))));

        Assert.Equal(200, sink.Count);
        Assert.All(Enumerable.Range(0, 10), i => Assert.NotNull(sink.LatestFor("case-" + i, since: 0)));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_CompletedWithNoSelectedAnswer_WarnsEvenWhenNotVerbose(bool verbose)
    {
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose, err);
        var noAnswer = Completed("case-amb", "unused", "task-9", "ctx-9") with { AnswerText = null, AnswerCompleteness = OutputCompleteness.Ambiguous };

        sink.Record(noAnswer);

        var lines = err.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(verbose ? 2 : 1, lines.Length);
        var warning = lines[^1];
        Assert.StartsWith("[m365] WARNING case=case-amb task=task-9", warning, StringComparison.Ordinal);
        Assert.Contains("no answer was selected (answer Ambiguous)", warning, StringComparison.Ordinal);
        Assert.Contains(WorkIqEvaluableAgent.CompletedNoAnswerFinishReason, warning, StringComparison.Ordinal);
        Assert.Equal(1, sink.Count);
        Assert.Same(noAnswer, sink.LatestFor("case-amb", since: 0));
    }

    [Fact]
    public void Record_AmbiguousAnswer_WhenTheCommandRecordsItAsNotMeasured_TheWarningSaysNotMeasured()
    {
        // eval records a turn with several candidates and none selected as not measured; its warning must say so, and
        // must not say the empty text is graded.
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose: false, err) { UnselectedAnswerIsNotMeasured = true };

        sink.Record(Completed("case-amb", "unused", "task-9", "ctx-9") with { AnswerText = null, AnswerCompleteness = OutputCompleteness.Ambiguous });

        var warning = err.ToString();
        Assert.Contains("recorded as NOT MEASURED", warning, StringComparison.Ordinal);
        Assert.Contains("not the agent's answer", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("graded", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_AmbiguousAnswer_WhenTheCommandGradesIt_TheWarningSaysTheEmptyTextIsGraded()
    {
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose: false, err);

        sink.Record(Completed("case-amb", "unused", "task-9", "ctx-9") with { AnswerText = null, AnswerCompleteness = OutputCompleteness.Ambiguous });

        var warning = err.ToString();
        Assert.Contains("graded on an empty text that is not the agent's answer", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT MEASURED", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_CompletedWithNoAnswerAtAll_IsGraded_NeverCalledNotMeasured(bool unselectedAnswerIsNotMeasured)
    {
        // A completed turn with no answer at all (None) stays a failure: the warning never calls it not measured.
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose: false, err) { UnselectedAnswerIsNotMeasured = unselectedAnswerIsNotMeasured };

        sink.Record(Completed("case-none", "unused", "task-8", "ctx-8") with { AnswerText = null, AnswerCompleteness = OutputCompleteness.None });

        var warning = err.ToString();
        Assert.Contains("no answer was selected (answer None)", warning, StringComparison.Ordinal);
        Assert.Contains("the agent gave no answer, so the case is graded on an empty text", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT MEASURED", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_CompletedEmptyAnswer_IsNoWarning()
    {
        var err = new StringWriter();
        var sink = new CliTurnSink(verbose: false, err);

        sink.Record(Completed("case-empty", "", "task-1", "ctx-1") with { AnswerCompleteness = OutputCompleteness.Empty });

        Assert.Equal("", err.ToString());
    }

    [Fact]
    public void Record_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CliTurnSink(verbose: false, new StringWriter()).Record(null!));
    }

    // ── helpers: real WorkIqTurnResult objects built through the evidence factories ──

    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static EvidenceSet<ToolExecutionObservation> ProvidedToolExecutions(EvidenceValidation validation) =>
        EvidenceSet<ToolExecutionObservation>.Provided(
            ImmutableArray<ToolExecutionObservation>.Empty,
            validation,
            EvidenceCoverage.Unknown,
            EvidenceScope.Turn,
            ImmutableArray<SourceRef>.Empty);

    private static WorkIqTurnResult Completed(
        string caseId, string answer, string? taskId, string? contextId, int citationMarkers = 0,
        EvidenceSet<ToolExecutionObservation>? toolExecutions = null)
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
            markers,
            toolExecutions);

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

    private static Microsoft365TurnEvidence Evidence(
        TurnIdentity identity, string? taskState, AnswerEvidence? answer, EvidenceSet<CitationMarker> markers,
        EvidenceSet<ToolExecutionObservation>? toolExecutions = null) =>
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
            toolExecutions ?? EvidenceSet<ToolExecutionObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<ApprovalIntentObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<RetrievalExecutionObservation>.NotRequested(EvidenceScope.Turn),
            markers,
            EvidenceSet<CitationSource>.NotRequested(EvidenceScope.Turn),
            ImmutableArray<CitationLink>.Empty,
            EvidenceSet<SkillActivationObservation>.NotRequested(EvidenceScope.Turn),
            ImmutableArray<RawCaptureRef>.Empty,
            ImmutableArray<EvidenceIssue>.Empty);
}
