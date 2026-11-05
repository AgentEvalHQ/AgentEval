// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Reducer;

/// <summary>
/// <see cref="WorkIqTaskReducer"/> over every <c>sse/*.txt</c> fixture, framed by the real SSE parser and the SDK's
/// serializer (see <see cref="SseFixtureFrames"/>), plus unit cases for snapshot semantics, identity checks and
/// diagnostics. The invariant under test everywhere: completion comes only from <c>TASK_STATE_COMPLETED</c> —
/// never from <c>lastChunk</c>, EOF, non-empty text or a message — and nothing is deduplicated or repaired by guess.
/// EOF is an interruption only while the task is still Submitted/Working (or before any state); after a terminal
/// or a suspended state (input-required, auth-required: the service waits for input) the stream ended normally.
/// </summary>
public sealed class WorkIqTaskReducerTests
{
    private const string InteractionMediaType = "application/vnd.ms-workiq.interaction+json";
    private const string HappyPath = "sse/stream-happy-path.txt";
    private const string Crlf = "sse/stream-crlf.txt";
    private const string InputRequired = "sse/stream-input-required.txt";
    private const string AuthRequiredEnvelope = "envelopes/send-task-auth-required.json";

    private static readonly DateTimeOffset ClockStart = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The framings of the happy path and the number of SSE events each one carries. Equal framing means equal
    /// <i>semantic</i> result (<see cref="Shape"/>), not equal frame count: <c>stream-crlf.txt</c> is the happy path
    /// byte for byte (5 events, see <see cref="Crlf_Always_CarriesTheHappyPathEventsByteForByte"/>); the other
    /// three were authored with the working status merged away (4 events), so their frame count is asserted
    /// against their own event count, as framed by the real <see cref="System.Net.ServerSentEvents.SseParser"/>.
    /// </summary>
    public static TheoryData<string, int> EquivalentFramings => new()
    {
        { "sse/stream-multiline-data.txt", 4 },
        { Crlf, 5 },
        { "sse/stream-with-comments-and-heartbeats.txt", 4 },
        { "sse/stream-with-id-and-event-fields.txt", 4 },
    };

    private static (WorkIqTaskReducer Reducer, ListTraceSink Trace, ListIssueSink Issues, FakeTimeProvider Clock) Build(
        WorkIqTaskHandle? template = null, string? caseId = "case-1", string? turnId = "turn-1")
    {
        var clock = new FakeTimeProvider(ClockStart);
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        var diagnostics = new WorkIqDiagnostics("run-reduce", "sha-1", [trace], [issues], clock);
        var reducer = new WorkIqTaskReducer(template ?? SseFixtureFrames.Template(), clock, diagnostics, caseId, turnId);
        return (reducer, trace, issues, clock);
    }

    private static WorkIqTaskReducer Replay(string fixture, WorkIqSnapshotSource source = WorkIqSnapshotSource.Stream, WorkIqTaskReducer? reducer = null, bool endStream = true, int epoch = 1)
    {
        reducer ??= Build().Reducer;
        foreach (var frame in SseFixtureFrames.Load(fixture, epoch))
        {
            reducer.Apply(frame, source);
        }

        if (endStream)
        {
            reducer.MarkStreamEnded();
        }

        return reducer;
    }

    private static WorkIqTaskSnapshot SnapshotOf(WorkIqTaskReducer reducer)
    {
        var snapshot = reducer.Snapshot();
        Assert.NotNull(snapshot);
        return snapshot;
    }

    private static WorkIqArtifact Answer(WorkIqTaskSnapshot snapshot) =>
        Assert.Single(snapshot.Artifacts, a => a.ArtifactId == SseFixtureFrames.AnswerArtifactId);

    // ---------------------------------------------------------------------------------------------------------
    // Fixture streams
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void HappyPath_Always_CompletesWithTwoChunksConcatenatedAndAttributionsOnTerminalStatus()
    {
        var reducer = Replay(HappyPath);
        var snapshot = SnapshotOf(reducer);

        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal("TASK_STATE_COMPLETED", snapshot.RawState);
        Assert.True(snapshot.IsTerminal);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 14, 20, 2, 900, TimeSpan.Zero), snapshot.StatusTimestamp);
        Assert.Equal(SseFixtureFrames.TaskId, snapshot.Handle.TaskId);
        Assert.Equal(SseFixtureFrames.ContextId, snapshot.Handle.ContextId);
        Assert.Equal(WorkIqSnapshotSource.Stream, snapshot.Source);

        var answer = Answer(snapshot);
        Assert.Equal("Answer", answer.Name);
        Assert.Equal(2, answer.Parts.Length);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(answer));
        Assert.DoesNotContain("\n", SseFixtureFrames.Text(answer), StringComparison.Ordinal);
        Assert.Equal(2, answer.Revision);
        Assert.True(answer.IsClosed);

        Assert.NotNull(snapshot.StatusMessage);
        Assert.Empty(snapshot.StatusMessage.Parts);
        Assert.Equal(2, SseFixtureFrames.AttributionCount(snapshot.StatusMessage));
        Assert.Single(snapshot.History);
        Assert.Equal("ROLE_USER", snapshot.History[0].Role);

        Assert.False(reducer.IsInterrupted);
        Assert.Null(reducer.Failure);
        Assert.Null(reducer.DirectMessage());
        Assert.Empty(snapshot.SchemaWarnings);
        Assert.Equal(5, reducer.FramesSeen);
        Assert.Equal(5, reducer.FramesApplied);
    }

    [Fact]
    public void HappyPath_Always_ObservedAtIsTheLastAppliedFrameReceipt()
    {
        var frames = SseFixtureFrames.Load(HappyPath);
        var reducer = Replay(HappyPath);

        Assert.Equal(frames[^1].ReceivedAt, SnapshotOf(reducer).ObservedAt);
    }

    [Fact]
    public void ReplaceThenAppend_Always_DraftVanishesAndFinalTextIsReplacementPlusAppended()
    {
        var snapshot = SnapshotOf(Replay("sse/stream-replace-then-append.txt"));

        var answer = Answer(snapshot);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(answer));
        Assert.DoesNotContain("Draft", SseFixtureFrames.Text(answer), StringComparison.Ordinal);
        Assert.Equal(3, answer.Revision);
        Assert.Equal(2, answer.Parts.Length);
        Assert.True(answer.IsClosed);
    }

    [Fact]
    public void TwoArtifactsInterleaved_Always_KeepsBothKeyedByIdInFirstSeenOrder()
    {
        var snapshot = SnapshotOf(Replay("sse/stream-two-artifacts-interleaved.txt"));

        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal(2, snapshot.Artifacts.Length);
        Assert.Equal(SseFixtureFrames.AnswerArtifactId, snapshot.Artifacts[0].ArtifactId);
        Assert.Equal(SseFixtureFrames.NotesArtifactId, snapshot.Artifacts[1].ArtifactId);

        var answer = snapshot.Artifacts[0];
        Assert.Equal("Answer", answer.Name);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(answer));
        Assert.Equal(3, answer.Parts.Length);
        Assert.Equal(3, answer.Revision);
        Assert.True(answer.IsClosed);

        var notes = snapshot.Artifacts[1];
        Assert.Equal("Follow-up questions", notes.Name);
        Assert.Equal("Follow-up questions: Who owns the data-residency sign-off?", SseFixtureFrames.Text(notes));
        Assert.Equal(2, notes.Revision);
        Assert.True(notes.IsClosed);
    }

    [Fact]
    public void LastChunkBeforeTerminalThenEof_Always_IsInterruptedNotCompleted()
    {
        var reducer = Replay("sse/stream-lastchunk-before-terminal-then-eof.txt");
        var snapshot = SnapshotOf(reducer);

        Assert.Equal(WorkIqTaskState.Submitted, snapshot.State);
        Assert.False(snapshot.IsTerminal);
        var answer = Answer(snapshot);
        Assert.True(answer.IsClosed);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(answer));
        Assert.True(reducer.IsInterrupted);
        Assert.Equal(1, reducer.Interruptions);
    }

    [Fact]
    public void EofBeforeTerminal_Always_IsInterruptedWithPartialArtifactRetained()
    {
        var reducer = Replay("sse/stream-eof-before-terminal.txt");
        var snapshot = SnapshotOf(reducer);

        Assert.Equal(WorkIqTaskState.Working, snapshot.State);
        Assert.Equal("TASK_STATE_WORKING", snapshot.RawState);
        var answer = Answer(snapshot);
        Assert.Equal(SseFixtureFrames.FirstChunk, SseFixtureFrames.Text(answer));
        Assert.False(answer.IsClosed);
        Assert.Equal(1, answer.Revision);
        Assert.True(reducer.IsInterrupted);
        Assert.NotNull(snapshot.StatusMessage);
        Assert.Equal("Searching SharePoint and Outlook for Project Atlas updates...", SseFixtureFrames.Text(snapshot.StatusMessage));
    }

    [Fact]
    public void DuplicateFrame_Always_IsAppliedTwiceNeverDeduplicated()
    {
        var snapshot = SnapshotOf(Replay("sse/stream-duplicate-frame.txt"));

        var answer = Answer(snapshot);
        Assert.Equal(
            "Today's Atlas meetings: The Atlas design sync is at 14:00. The Atlas design sync is at 14:00. Both are in Zurich.citeturn0search1",
            SseFixtureFrames.Text(answer));
        Assert.Equal(4, answer.Parts.Length);
        Assert.Equal(4, answer.Revision);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
    }

    [Theory]
    [MemberData(nameof(EquivalentFramings))]
    public void EquivalentFraming_Always_ReducesToTheHappyPathShape(string fixture, int events)
    {
        var expected = Shape(Replay(HappyPath));

        var reducer = Replay(fixture);

        Assert.Equal(expected, Shape(reducer));
        Assert.Equal(events, SseFixtureFrames.Items(fixture).Count);
        Assert.Equal(events, reducer.FramesSeen);
        Assert.Equal(events, reducer.FramesApplied);
        Assert.False(reducer.IsInterrupted);
    }

    /// <summary>
    /// <c>stream-crlf.txt</c> is the happy path with CRLF line ends and its own JSON-RPC id: the real parser must
    /// yield the same five <c>data</c> payloads, so it is a byte-level framing variant, not a shorter sequence.
    /// </summary>
    [Fact]
    public void Crlf_Always_CarriesTheHappyPathEventsByteForByte()
    {
        var happy = SseFixtureFrames.Items(HappyPath).Select(i => i.Data).ToArray();
        var crlf = SseFixtureFrames.Items(Crlf).Select(i => i.Data.Replace("rpc-0108", "rpc-0101", StringComparison.Ordinal)).ToArray();

        Assert.Equal(5, happy.Length);
        Assert.Equal(happy, crlf);
        Assert.All(crlf, data => Assert.DoesNotContain("\r", data, StringComparison.Ordinal));
    }

    [Fact]
    public void IdAndEventFraming_Always_PreservesSseIdsOnFramesWithoutTreatingThemAsReplay()
    {
        var frames = SseFixtureFrames.Load("sse/stream-with-id-and-event-fields.txt");

        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.StartsWith("evt-0110-", f.SseId, StringComparison.Ordinal));
        Assert.All(frames, f => Assert.Equal("message", f.SseEvent));
        Assert.Equal(4, frames.Select(f => f.SseId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Documented choice: a malformed frame (two of the one-of fields set) is recorded as a Malformed failure and
    /// marks the reduction interrupted; the frames after it are still applied, so the state is Completed <b>and</b>
    /// interrupted. The conversation can never report a clean pass over a stream with a rejected frame, and the
    /// valid completed state is never discarded either (the malformed frame's content is not merged).
    /// </summary>
    [Fact]
    public void MalformedEvent_Always_RecordsMalformedFailureAndAppliesLaterValidFramesOnly()
    {
        var reducer = Replay("sse/stream-malformed-event.txt");
        var snapshot = SnapshotOf(reducer);

        var failure = Assert.Single(reducer.Failures);
        Assert.Equal(WorkIqFailureCategory.Malformed, failure.Category);
        Assert.True(reducer.IsInterrupted);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(Answer(snapshot)));
        Assert.Equal(2, SseFixtureFrames.AttributionCount(snapshot.StatusMessage));
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("frame.malformed:", StringComparison.Ordinal));
        Assert.Null(reducer.DirectMessage());
        Assert.Equal(4, reducer.FramesSeen);
        Assert.Equal(3, reducer.FramesApplied);
    }

    [Fact]
    public void DirectMessageOnly_Always_HasDirectMessageAndNoSnapshotAndIsNotInterrupted()
    {
        var reducer = Replay("sse/stream-direct-message-only.txt");

        var message = reducer.DirectMessage();
        Assert.NotNull(message);
        Assert.Null(reducer.Snapshot());
        Assert.False(reducer.HasTask);
        Assert.Equal("msg-agent-ab12cd34-0112", message.MessageId);
        Assert.Equal("ROLE_AGENT", message.Role);
        Assert.Equal("Your next meeting is the Atlas design sync at 14:00 (Europe/Zurich) today.citeturn0search1", SseFixtureFrames.Text(message));
        Assert.Equal(SseFixtureFrames.ContextId, message.ContextId);
        Assert.Null(message.TaskId);
        Assert.Equal(1, SseFixtureFrames.AttributionCount(message));
        Assert.Equal(1, reducer.DirectMessageCount);
        Assert.False(reducer.IsInterrupted);
        Assert.Empty(reducer.SchemaWarnings);
    }

    [Fact]
    public void FailedAfterPartial_Always_IsFailedWithPartialArtifactRetainedAndNeverCompleted()
    {
        var reducer = Replay("sse/stream-failed-after-partial.txt");
        var snapshot = SnapshotOf(reducer);

        Assert.Equal(WorkIqTaskState.Failed, snapshot.State);
        Assert.Equal("TASK_STATE_FAILED", snapshot.RawState);
        Assert.True(snapshot.IsTerminal);
        Assert.Equal(SseFixtureFrames.FirstChunk, SseFixtureFrames.Text(Answer(snapshot)));
        Assert.NotNull(snapshot.StatusMessage);
        Assert.Contains("HTTP 503", SseFixtureFrames.Text(snapshot.StatusMessage), StringComparison.Ordinal);
        Assert.False(reducer.IsInterrupted);
    }

    [Fact]
    public void InputRequired_Always_IsSuspendedWithTheConfirmationDataPartPreserved()
    {
        var reducer = Replay(InputRequired);
        var snapshot = SnapshotOf(reducer);

        Assert.Equal(WorkIqTaskState.InputRequired, snapshot.State);
        Assert.True(snapshot.IsSuspended);
        Assert.False(snapshot.IsTerminal);
        Assert.Empty(snapshot.Artifacts);
        Assert.NotNull(snapshot.StatusMessage);
        Assert.Equal(2, snapshot.StatusMessage.Parts.Length);
        Assert.Equal(WorkIqPartKind.Text, snapshot.StatusMessage.Parts[0].Kind);
        var data = snapshot.StatusMessage.Parts[1];
        Assert.Equal(WorkIqPartKind.Data, data.Kind);
        Assert.Equal(InteractionMediaType, data.MediaType);
        Assert.NotNull(data.Data);
        var interaction = data.Data.Value.GetProperty("input-required");
        Assert.Equal("confirmation_required", interaction.GetProperty("type").GetString());
        Assert.Equal("Action.Submit", interaction.GetProperty("adaptiveCard").GetProperty("actions")[0].GetProperty("type").GetString());
        Assert.False(reducer.IsInterrupted);
        Assert.Equal(0, reducer.Interruptions);
    }

    /// <summary>
    /// HLD D6 / research item 9: input-required is a suspended state. The service closes the stream and waits for
    /// input on the same task, so EOF after it is a normal end (Info trace, no interruption issue) — and still not
    /// completion: the snapshot is suspended, never terminal, and carries no answer artifact.
    /// </summary>
    [Fact]
    public void MarkStreamEnded_AfterInputRequired_IsANormalEndNotAnInterruption()
    {
        var (reducer, trace, issues, _) = Build();
        Replay(InputRequired, reducer: reducer, endStream: false);

        Assert.False(reducer.MarkStreamEnded());

        Assert.False(reducer.IsInterrupted);
        Assert.Equal(0, reducer.Interruptions);
        Assert.DoesNotContain(issues.Issues, i => i.Code == "reduce.interrupted");
        Assert.DoesNotContain(trace.Events, e => e.Code == "reduce.interrupted");
        var ended = Assert.Single(trace.Events, e => e.Code == "reduce.stream_ended");
        Assert.Equal(WorkIqTraceLevel.Info, ended.Level);
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", ended.A2AState);
        Assert.Contains("suspended", ended.Message, StringComparison.Ordinal);
        Assert.NotNull(ended.Data);
        Assert.True(ended.Data.Value.GetProperty("suspended").GetBoolean());
        Assert.False(ended.Data.Value.GetProperty("terminal").GetBoolean());
        Assert.Equal(3, ended.Data.Value.GetProperty("framesSeen").GetInt32());
        var snapshot = SnapshotOf(reducer);
        Assert.True(snapshot.IsSuspended);
        Assert.False(snapshot.IsTerminal);
        Assert.Empty(snapshot.Artifacts);
    }

    /// <summary>
    /// Auth-required is the other suspended state. No SSE fixture carries it (only the <c>SendMessage</c> envelope
    /// does), so the envelope's task is applied as the stream's snapshot frame; EOF after it is a normal end and the
    /// <c>auth-required</c> payload stays on the status message, never promoted to an answer.
    /// </summary>
    [Fact]
    public void MarkStreamEnded_AfterAuthRequired_IsANormalEndNotAnInterruption()
    {
        var (reducer, trace, issues, _) = Build();
        using var document = JsonDocument.Parse(FixtureLoader.Text(AuthRequiredEnvelope));
        var task = document.RootElement.GetProperty("result").GetProperty("task").Deserialize<AgentTask>(A2AJsonUtilities.DefaultOptions);
        Assert.NotNull(task);
        reducer.Apply(SseFixtureFrames.Frame(new StreamResponse { Task = task }));

        Assert.False(reducer.MarkStreamEnded());

        Assert.False(reducer.IsInterrupted);
        Assert.Equal(0, reducer.Interruptions);
        Assert.DoesNotContain(issues.Issues, i => i.Code == "reduce.interrupted");
        var ended = Assert.Single(trace.Events, e => e.Code == "reduce.stream_ended");
        Assert.Equal(WorkIqTraceLevel.Info, ended.Level);
        Assert.Equal("TASK_STATE_AUTH_REQUIRED", ended.A2AState);
        Assert.NotNull(ended.Data);
        Assert.True(ended.Data.Value.GetProperty("suspended").GetBoolean());
        Assert.False(ended.Data.Value.GetProperty("terminal").GetBoolean());
        var snapshot = SnapshotOf(reducer);
        Assert.Equal(WorkIqTaskState.AuthRequired, snapshot.State);
        Assert.Equal("TASK_STATE_AUTH_REQUIRED", snapshot.RawState);
        Assert.True(snapshot.IsSuspended);
        Assert.False(snapshot.IsTerminal);
        Assert.Equal(task.Id, snapshot.Handle.TaskId);
        Assert.Empty(snapshot.Artifacts);
        Assert.NotNull(snapshot.StatusMessage);
        Assert.Contains(snapshot.StatusMessage.Parts, p => p.Kind == WorkIqPartKind.Data && p.Data is { } data && data.TryGetProperty("auth-required", out _));
    }

    /// <summary>A real drop after a pause is still an interruption: the suspended rule covers EOF only, and the issue says the pause was observed.</summary>
    [Fact]
    public void MarkInterrupted_AfterASuspendedState_IsStillAnInterruptionThatNamesThePause()
    {
        var (reducer, trace, issues, _) = Build();
        Replay(InputRequired, reducer: reducer, endStream: false);

        reducer.MarkInterrupted();

        Assert.True(reducer.IsInterrupted);
        Assert.Equal(1, reducer.Interruptions);
        var issue = Assert.Single(issues.Issues, i => i.Code == "reduce.interrupted");
        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", issue.A2AState);
        Assert.Contains("after a suspended state (TASK_STATE_INPUT_REQUIRED)", issue.Message, StringComparison.Ordinal);
        var interrupted = Assert.Single(trace.Events, e => e.Code == "reduce.interrupted");
        Assert.True(interrupted.Data!.Value.GetProperty("suspended").GetBoolean());
        Assert.False(interrupted.Data.Value.GetProperty("terminal").GetBoolean());
        Assert.True(SnapshotOf(reducer).IsSuspended);
    }

    [Fact]
    public void SubscribeFirstFrameSnapshot_AfterAnInterruptedStream_ReplacesStateAndCompletes()
    {
        var reducer = Replay("sse/stream-eof-before-terminal.txt");
        Assert.True(reducer.IsInterrupted);
        Assert.Equal(WorkIqTaskState.Working, SnapshotOf(reducer).State);

        Replay("sse/subscribe-first-frame-snapshot.txt", WorkIqSnapshotSource.Subscribe, reducer, epoch: 2);
        var snapshot = SnapshotOf(reducer);

        Assert.Equal(WorkIqSnapshotSource.Subscribe, snapshot.Source);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        var answer = Answer(snapshot);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(answer));
        Assert.Equal(2, answer.Parts.Length);
        Assert.Equal(2, answer.Revision);
        Assert.True(answer.IsClosed);
        Assert.Equal(2, SseFixtureFrames.AttributionCount(snapshot.StatusMessage));
        Assert.True(reducer.IsInterrupted);
        Assert.Equal(1, reducer.Interruptions);
    }

    [Fact]
    public void SubscribeFirstFrameSnapshot_WithADifferentDraft_ReplacesTheDraftAndBumpsRevision()
    {
        var (reducer, _, _, _) = Build();
        foreach (var frame in SseFixtureFrames.Load("sse/stream-replace-then-append.txt").Take(2))
        {
            reducer.Apply(frame);
        }

        Assert.Equal("Draft: the Atlas pilot is delayed to December.", SseFixtureFrames.Text(Answer(SnapshotOf(reducer))));
        reducer.MarkInterrupted();

        Replay("sse/subscribe-first-frame-snapshot.txt", WorkIqSnapshotSource.Subscribe, reducer, epoch: 2);
        var snapshot = SnapshotOf(reducer);

        var answer = Answer(snapshot);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(answer));
        Assert.DoesNotContain("Draft", SseFixtureFrames.Text(answer), StringComparison.Ordinal);
        Assert.Equal(3, answer.Revision);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
    }

    [Fact]
    public void SubscribeFirstFrameSnapshot_Alone_LoadsTheSnapshotWithSubscribeSource()
    {
        var snapshot = SnapshotOf(Replay("sse/subscribe-first-frame-snapshot.txt", WorkIqSnapshotSource.Subscribe));

        Assert.Equal(WorkIqSnapshotSource.Subscribe, snapshot.Source);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(Answer(snapshot)));
        Assert.Equal(1, Answer(snapshot).Revision);
    }

    [Fact]
    public void RpcErrorFrame_Always_RecordsTheFailureAndInterruptsWithoutChangingState()
    {
        var reducer = Replay("sse/stream-rpc-error-frame.txt");
        var snapshot = SnapshotOf(reducer);

        var failure = Assert.Single(reducer.Failures);
        Assert.Equal(WorkIqFailureCategory.RemoteError, failure.Category);
        Assert.Equal(-32603, failure.RpcErrorCode);
        Assert.True(reducer.IsInterrupted);
        Assert.Equal(WorkIqTaskState.Working, snapshot.State);
        Assert.False(snapshot.IsTerminal);
        Assert.Empty(snapshot.Artifacts);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Snapshot (Load) semantics
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Load_SecondSnapshot_ReplacesTheArtifactSetAndBumpsOnlyChangedRevisions()
    {
        var (reducer, _, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working, MakeArtifact("a", "A", "one"), MakeArtifact("b", "B", "two")), WorkIqSnapshotSource.Send, null);
        var first = SnapshotOf(reducer);
        Assert.Equal(WorkIqSnapshotSource.Send, first.Source);
        Assert.All(first.Artifacts, a => Assert.Equal(0, a.Revision));

        reducer.Load(MakeTask(TaskState.Completed, MakeArtifact("a", "A", "one changed"), MakeArtifact("c", "C", "three")), WorkIqSnapshotSource.GetTask, null);
        var second = SnapshotOf(reducer);

        Assert.Equal(WorkIqSnapshotSource.GetTask, second.Source);
        Assert.Equal(WorkIqTaskState.Completed, second.State);
        Assert.Equal(new[] { "a", "c" }, second.Artifacts.Select(a => a.ArtifactId).ToArray());
        Assert.Equal(1, second.Artifacts[0].Revision);
        Assert.Equal("one changed", SseFixtureFrames.Text(second.Artifacts[0]));
        Assert.Equal(0, second.Artifacts[1].Revision);
    }

    [Fact]
    public void Load_IdenticalArtifactInSnapshot_KeepsRevisionAndClosedFlag()
    {
        var (reducer, _, _, _) = Build();
        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(MakeArtifact("a", "A", "same"), append: false, lastChunk: true)));
        Assert.Equal(1, Answer("a", reducer).Revision);
        Assert.True(Answer("a", reducer).IsClosed);

        reducer.Load(MakeTask(TaskState.Working, MakeArtifact("a", "A", "same")), WorkIqSnapshotSource.Subscribe, null);

        var artifact = Answer("a", reducer);
        Assert.Equal(1, artifact.Revision);
        Assert.True(artifact.IsClosed);
    }

    [Fact]
    public void Load_SnapshotWithoutFrame_StampsObservedAtFromTheClockAndKeepsCapture()
    {
        var (reducer, _, _, clock) = Build();
        var capture = new RawCaptureRef("cap-1", CaptureState.Complete, "workiq-raw-capture/v1", null, "abc", 10);
        clock.Advance(TimeSpan.FromSeconds(5));

        reducer.Load(MakeTask(TaskState.Submitted), WorkIqSnapshotSource.Send, null, capture);

        var snapshot = SnapshotOf(reducer);
        Assert.Equal(ClockStart.AddSeconds(5), snapshot.ObservedAt);
        Assert.Same(capture, snapshot.Capture);
        Assert.Equal(WorkIqTaskState.Submitted, snapshot.State);
    }

    [Fact]
    public void Load_TemplateWithAnotherTaskId_WarnsAndUsesTheTaskId()
    {
        var (reducer, _, issues, _) = Build(SseFixtureFrames.Template("task-expected", SseFixtureFrames.ContextId));

        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);

        var snapshot = SnapshotOf(reducer);
        Assert.Equal(SseFixtureFrames.TaskId, snapshot.Handle.TaskId);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("task.id_mismatch:", StringComparison.Ordinal));
        Assert.Contains(issues.Issues, i => i.Code == "reduce.task.id_mismatch" && i.Category == WorkIqIssueCategory.Protocol);
    }

    [Fact]
    public void Load_UnspecifiedState_IsUnknownAndWarnsAndNeverTerminal()
    {
        var (reducer, _, _, _) = Build();

        reducer.Load(MakeTask(TaskState.Unspecified), WorkIqSnapshotSource.Send, null);

        var snapshot = SnapshotOf(reducer);
        Assert.Equal(WorkIqTaskState.Unknown, snapshot.State);
        Assert.Equal("TASK_STATE_UNSPECIFIED", snapshot.RawState);
        Assert.False(snapshot.IsTerminal);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("task.state_unspecified:", StringComparison.Ordinal));
        Assert.True(reducer.MarkStreamEnded());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Identity and conformance
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_StatusUpdateForAnotherTask_IsNotAppliedAndWarns()
    {
        var (reducer, trace, issues, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);

        reducer.Apply(SseFixtureFrames.Frame(MakeStatusUpdate(TaskState.Completed, taskId: "task-other")));

        var snapshot = SnapshotOf(reducer);
        Assert.Equal(WorkIqTaskState.Working, snapshot.State);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("status.task_id_mismatch:", StringComparison.Ordinal));
        Assert.Contains(trace.Events, e => e.Code == "reduce.status_skipped" && e.Level == WorkIqTraceLevel.Warning && e.TaskId == "task-other");
        Assert.Contains(issues.Issues, i => i.Code == "reduce.status.task_id_mismatch");
        Assert.Equal(1, reducer.FramesApplied);
        Assert.False(reducer.IsInterrupted);
    }

    [Fact]
    public void Apply_ArtifactUpdateForAnotherTask_IsNotAppliedAndWarns()
    {
        var (reducer, _, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);

        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(MakeArtifact("x", "X", "foreign"), append: false, lastChunk: false, taskId: "task-other")));

        var snapshot = SnapshotOf(reducer);
        Assert.Empty(snapshot.Artifacts);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("artifact.task_id_mismatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_ContextIdMismatch_AppliesByTaskIdAndWarns()
    {
        var (reducer, _, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);

        reducer.Apply(SseFixtureFrames.Frame(MakeStatusUpdate(TaskState.Completed, contextId: "ctx-other")));

        var snapshot = SnapshotOf(reducer);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal(SseFixtureFrames.ContextId, snapshot.Handle.ContextId);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("status.context_id_mismatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_StatusUpdateBeforeAnySnapshot_EstablishesIdentityWithWarning()
    {
        var (reducer, _, _, _) = Build();

        reducer.Apply(SseFixtureFrames.Frame(MakeStatusUpdate(TaskState.Working)));

        var snapshot = SnapshotOf(reducer);
        Assert.Equal(SseFixtureFrames.TaskId, snapshot.Handle.TaskId);
        Assert.Equal(SseFixtureFrames.ContextId, snapshot.Handle.ContextId);
        Assert.Equal(WorkIqTaskState.Working, snapshot.State);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("status.before_task_snapshot:", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_AppendAfterLastChunk_AppliesAndWarns()
    {
        var (reducer, _, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);
        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(MakeArtifact("a", "A", "first"), append: false, lastChunk: true), 2));

        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(MakeArtifact("a", null, " second"), append: true, lastChunk: false), 3));

        var artifact = Answer("a", reducer);
        Assert.Equal("first second", SseFixtureFrames.Text(artifact));
        Assert.Equal("A", artifact.Name);
        Assert.Equal(2, artifact.Revision);
        Assert.True(artifact.IsClosed);
        Assert.Contains(reducer.SchemaWarnings, w => w.StartsWith("artifact.append_after_last_chunk:", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_ReplaceWithoutName_KeepsTheKnownNameAndReopensTheArtifact()
    {
        var (reducer, _, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);
        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(MakeArtifact("a", "Answer", "draft"), append: false, lastChunk: true), 2));

        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(MakeArtifact("a", null, "final"), append: false, lastChunk: false), 3));

        var artifact = Answer("a", reducer);
        Assert.Equal("final", SseFixtureFrames.Text(artifact));
        Assert.Equal("Answer", artifact.Name);
        Assert.False(artifact.IsClosed);
        Assert.Equal(2, artifact.Revision);
    }

    [Fact]
    public void Apply_ArtifactUpdateWithoutId_IsSkippedAndWarns()
    {
        var (reducer, trace, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);

        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(new Artifact { ArtifactId = "", Parts = [Part.FromText("orphan")] }, append: false, lastChunk: false)));

        Assert.Empty(SnapshotOf(reducer).Artifacts);
        Assert.Contains(reducer.SchemaWarnings, w => w.StartsWith("artifact.update_without_id:", StringComparison.Ordinal));
        Assert.Contains(trace.Events, e => e.Code == "reduce.artifact_skipped");
    }

    [Fact]
    public void Apply_NonTextParts_ArePreservedInOrder()
    {
        var (reducer, _, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);
        var artifact = new Artifact
        {
            ArtifactId = "a",
            Name = "Answer",
            Parts =
            [
                Part.FromText("see attachment"),
                Part.FromUrl("https://sharepoint.example.invalid/x.pdf", "application/pdf", "x.pdf"),
                Part.FromRaw([1, 2, 3], "application/octet-stream", "bytes.bin"),
                Part.FromData(JsonDocument.Parse("""{"k":"v"}""").RootElement.Clone()),
            ],
        };

        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(artifact, append: false, lastChunk: true)));

        var parts = Answer("a", reducer).Parts;
        Assert.Equal(new[] { WorkIqPartKind.Text, WorkIqPartKind.File, WorkIqPartKind.File, WorkIqPartKind.Data }, parts.Select(p => p.Kind).ToArray());
        Assert.Equal("https://sharepoint.example.invalid/x.pdf", parts[1].Url);
        Assert.Equal("application/pdf", parts[1].MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, parts[2].RawBytes.ToArray());
        Assert.Equal("bytes.bin", parts[2].Filename);
        Assert.Equal("v", parts[3].Data!.Value.GetProperty("k").GetString());
    }

    [Fact]
    public void Apply_SecondDirectMessage_KeepsTheFirstWarnsAndRetainsTheSecond()
    {
        var (reducer, _, issues, _) = Build();
        reducer.Apply(SseFixtureFrames.Frame(new StreamResponse { Message = MakeDirectMessage("m-1", "first") }, 1));

        reducer.Apply(SseFixtureFrames.Frame(new StreamResponse { Message = MakeDirectMessage("m-2", "second") }, 2));

        Assert.Equal("m-1", reducer.DirectMessage()!.MessageId);
        Assert.Equal(2, reducer.DirectMessageCount);
        Assert.Equal("m-2", Assert.Single(reducer.AdditionalDirectMessages).MessageId);
        Assert.Contains(reducer.SchemaWarnings, w => w.StartsWith("message.duplicate_direct_message:", StringComparison.Ordinal));
        Assert.Contains(issues.Issues, i => i.Code == "reduce.message.duplicate_direct_message");
        Assert.Null(reducer.Snapshot());
    }

    [Fact]
    public void Apply_DirectMessageInsideATaskStream_IsKeptApartAndWarns()
    {
        var (reducer, _, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);

        reducer.Apply(SseFixtureFrames.Frame(new StreamResponse { Message = MakeDirectMessage("m-1", "aside") }, 2));

        Assert.NotNull(reducer.DirectMessage());
        Assert.Empty(SnapshotOf(reducer).Artifacts);
        Assert.Contains(reducer.SchemaWarnings, w => w.StartsWith("message.in_task_stream:", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_FrameWithTwoPayloads_IsMalformedNotAppliedAndInterrupts()
    {
        var (reducer, _, issues, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);
        var twoPayloads = new StreamResponse { Task = MakeTask(TaskState.Completed), Message = MakeDirectMessage("m-1", "x") };

        reducer.Apply(SseFixtureFrames.Frame(twoPayloads, 2));

        Assert.Equal(WorkIqTaskState.Working, SnapshotOf(reducer).State);
        Assert.Null(reducer.DirectMessage());
        Assert.Equal(WorkIqFailureCategory.Malformed, Assert.Single(reducer.Failures).Category);
        Assert.True(reducer.IsInterrupted);
        Assert.Contains(reducer.SchemaWarnings, w => w.StartsWith("frame.one_of_violation:", StringComparison.Ordinal));
        Assert.Contains(issues.Issues, i => i.Code == "reduce.frame_malformed" && i.Category == WorkIqIssueCategory.Protocol);
    }

    [Fact]
    public void Apply_FrameWithFailure_RecordsItInterruptsAndDoesNotRegisterASecondIssueForIt()
    {
        var (reducer, trace, issues, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);
        var failure = new WorkIqFailure(WorkIqFailureCategory.Transport, "connection reset", OutcomeCertainty.Indeterminate, WorkIqOperation.Stream, HttpStatus: null);

        reducer.Apply(new WorkIqStreamFrame(2, 1, SseFixtureFrames.Start, null, null, null, null, failure));

        Assert.Same(failure, reducer.Failure);
        Assert.True(reducer.IsInterrupted);
        Assert.Equal(WorkIqTaskState.Working, SnapshotOf(reducer).State);
        Assert.Contains(trace.Events, e => e.Code == "reduce.frame_failure" && e.Level == WorkIqTraceLevel.Warning);
        Assert.Single(issues.Issues, i => i.Code == "reduce.interrupted");
        Assert.DoesNotContain(issues.Issues, i => i.Code == "reduce.frame_malformed");
    }

    [Fact]
    public void Apply_FrameWithNeitherEventNorFailure_IsMalformed()
    {
        var (reducer, _, _, _) = Build();

        reducer.Apply(new WorkIqStreamFrame(1, 1, SseFixtureFrames.Start, null, null, null, null, null));

        Assert.Equal(WorkIqFailureCategory.Malformed, Assert.Single(reducer.Failures).Category);
        Assert.Equal(WorkIqOperation.Local, reducer.Failure!.Operation);
        Assert.True(reducer.IsInterrupted);
    }

    [Fact]
    public void Apply_WithANonStreamSource_Throws()
    {
        var (reducer, _, _, _) = Build();
        var frame = SseFixtureFrames.Frame(MakeStatusUpdate(TaskState.Working));

        Assert.Throws<ArgumentOutOfRangeException>(() => reducer.Apply(frame, WorkIqSnapshotSource.GetTask));
        Assert.Throws<ArgumentNullException>(() => reducer.Apply(null!));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Interruption, capture, empty reducer
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Snapshot_BeforeAnyTask_IsNullAndDirectMessageIsNull()
    {
        var (reducer, _, _, _) = Build();

        Assert.Null(reducer.Snapshot());
        Assert.Null(reducer.DirectMessage());
        Assert.False(reducer.HasTask);
        Assert.Null(reducer.Handle);
        Assert.False(reducer.IsInterrupted);
        Assert.Equal(0, reducer.FramesSeen);
    }

    [Fact]
    public void MarkInterrupted_Twice_IsIdempotentAndRegistersOneIssue()
    {
        var (reducer, trace, issues, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);

        reducer.MarkInterrupted();
        reducer.MarkInterrupted();

        Assert.True(reducer.IsInterrupted);
        Assert.Equal(1, reducer.Interruptions);
        var issue = Assert.Single(issues.Issues, i => i.Code == "reduce.interrupted");
        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal(SseFixtureFrames.TaskId, issue.TaskId);
        Assert.Equal("TASK_STATE_WORKING", issue.A2AState);
        Assert.Equal(2, trace.Events.Count(e => e.Code == "reduce.interrupted"));
        Assert.Equal(WorkIqTaskState.Working, SnapshotOf(reducer).State);
    }

    [Fact]
    public void MarkStreamEnded_AfterTerminalState_IsNotInterrupted()
    {
        var (reducer, trace, _, _) = Build();
        reducer.Load(MakeTask(TaskState.Completed), WorkIqSnapshotSource.Send, null);

        Assert.False(reducer.MarkStreamEnded());

        Assert.False(reducer.IsInterrupted);
        var ended = Assert.Single(trace.Events, e => e.Code == "reduce.stream_ended");
        Assert.Equal(WorkIqTraceLevel.Info, ended.Level);
    }

    [Fact]
    public void MarkStreamEnded_WithNothingObserved_IsInterrupted()
    {
        var (reducer, trace, issues, _) = Build();

        Assert.True(reducer.MarkStreamEnded());

        Assert.True(reducer.IsInterrupted);
        var issue = Assert.Single(issues.Issues, i => i.Code == "reduce.interrupted");
        Assert.Contains("(last state: none)", issue.Message, StringComparison.Ordinal);
        var ended = Assert.Single(trace.Events, e => e.Code == "reduce.stream_ended");
        Assert.Equal(WorkIqTraceLevel.Warning, ended.Level);
        Assert.False(ended.Data!.Value.GetProperty("terminal").GetBoolean());
        Assert.False(ended.Data.Value.GetProperty("suspended").GetBoolean());
    }

    [Theory]
    [InlineData(TaskState.Submitted, "TASK_STATE_SUBMITTED")]
    [InlineData(TaskState.Working, "TASK_STATE_WORKING")]
    public void MarkStreamEnded_WhileSubmittedOrWorking_IsInterrupted(TaskState state, string rawState)
    {
        var (reducer, trace, issues, _) = Build();
        reducer.Apply(SseFixtureFrames.Frame(new StreamResponse { Task = MakeTask(state) }));

        Assert.True(reducer.MarkStreamEnded());

        Assert.True(reducer.IsInterrupted);
        Assert.Equal(1, reducer.Interruptions);
        var issue = Assert.Single(issues.Issues, i => i.Code == "reduce.interrupted");
        Assert.Equal(rawState, issue.A2AState);
        Assert.Contains($"before a terminal or suspended task state (last state: {rawState})", issue.Message, StringComparison.Ordinal);
        var ended = Assert.Single(trace.Events, e => e.Code == "reduce.stream_ended");
        Assert.Equal(WorkIqTraceLevel.Warning, ended.Level);
    }

    [Fact]
    public void AttachCapture_Always_IsCarriedByLaterSnapshots()
    {
        var reducer = Replay(HappyPath);
        var capture = new RawCaptureRef("cap-stream", CaptureState.Complete, "workiq-raw-capture/v1", null, "deadbeef", 2838);

        reducer.AttachCapture(capture);

        Assert.Same(capture, SnapshotOf(reducer).Capture);
        Assert.Throws<ArgumentNullException>(() => reducer.AttachCapture(null!));
    }

    [Fact]
    public void Constructor_NullTemplate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkIqTaskReducer(null!));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Diagnostics_HappyPath_TracesEveryReduceStepWithIdsAndNoIssue()
    {
        var (reducer, trace, issues, _) = Build();

        Replay(HappyPath, reducer: reducer);

        var codes = trace.Events.Select(e => e.Code).ToArray();
        Assert.Equal(1, codes.Count(c => c == "reduce.snapshot_loaded"));
        Assert.Equal(2, codes.Count(c => c == "reduce.artifact_applied"));
        Assert.Equal(2, codes.Count(c => c == "reduce.status_applied"));
        Assert.Equal(3, codes.Count(c => c == "reduce.state_changed"));
        Assert.Equal(1, codes.Count(c => c == "reduce.stream_ended"));
        Assert.All(trace.Events, e => Assert.Equal(WorkIqTraceStage.Reduce, e.Stage));
        Assert.All(trace.Events, e => Assert.Equal("case-1", e.CaseId));
        Assert.All(trace.Events, e => Assert.Equal("turn-1", e.TurnId));
        Assert.All(trace.Events, e => Assert.Equal(SseFixtureFrames.TaskId, e.TaskId));
        Assert.All(trace.Events, e => Assert.Equal("run-reduce", e.RunId));
        Assert.All(trace.Events, e => Assert.Equal(WorkIqTraceLevel.Info, e.Level));
        Assert.Empty(issues.Issues);

        var completed = trace.Events.Last(e => e.Code == "reduce.status_applied");
        Assert.Equal("TASK_STATE_COMPLETED", completed.A2AState);
        Assert.NotNull(completed.Data);
        Assert.Equal("Working", completed.Data.Value.GetProperty("previousState").GetString());
        Assert.Equal("Completed", completed.Data.Value.GetProperty("state").GetString());

        var artifact = trace.Events.Last(e => e.Code == "reduce.artifact_applied");
        Assert.Equal(SseFixtureFrames.AnswerArtifactId, artifact.Data!.Value.GetProperty("artifactId").GetString());
        Assert.True(artifact.Data.Value.GetProperty("append").GetBoolean());
        Assert.True(artifact.Data.Value.GetProperty("lastChunk").GetBoolean());
        Assert.Equal(2, artifact.Data.Value.GetProperty("revision").GetInt32());
    }

    [Fact]
    public void Diagnostics_None_StillReduces()
    {
        var reducer = new WorkIqTaskReducer(SseFixtureFrames.Template());

        Replay(HappyPath, reducer: reducer);

        Assert.Equal(WorkIqTaskState.Completed, SnapshotOf(reducer).State);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(Answer(SnapshotOf(reducer))));
    }

    [Fact]
    public void Diagnostics_Warning_NeverCarriesPartText()
    {
        var (reducer, trace, issues, _) = Build();
        reducer.Load(MakeTask(TaskState.Working), WorkIqSnapshotSource.Send, null);
        const string secret = "TENANT-SECRET-CONTENT-9f3a";

        reducer.Apply(SseFixtureFrames.Frame(MakeArtifactUpdate(MakeArtifact("a", "A", secret), append: false, lastChunk: false, taskId: "task-other"), 2));
        reducer.Apply(SseFixtureFrames.Frame(new StreamResponse { Message = MakeDirectMessage("m-1", secret) }, 3));

        Assert.All(trace.Events, e => Assert.DoesNotContain(secret, e.Message, StringComparison.Ordinal));
        Assert.All(trace.Events, e => Assert.DoesNotContain(secret, e.Data?.GetRawText() ?? "", StringComparison.Ordinal));
        Assert.All(issues.Issues, i => Assert.DoesNotContain(secret, i.Message + i.SuggestedCause, StringComparison.Ordinal));
        Assert.All(reducer.SchemaWarnings, w => Assert.DoesNotContain(secret, w, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static WorkIqArtifact Answer(string id, WorkIqTaskReducer reducer) =>
        Assert.Single(SnapshotOf(reducer).Artifacts, a => a.ArtifactId == id);

    private static AgentTask MakeTask(TaskState state, params Artifact[] artifacts) => new()
    {
        Id = SseFixtureFrames.TaskId,
        ContextId = SseFixtureFrames.ContextId,
        Status = new A2A.TaskStatus { State = state },
        Artifacts = artifacts.Length == 0 ? null : artifacts.ToList(),
    };

    private static Artifact MakeArtifact(string id, string? name, params string[] texts) => new()
    {
        ArtifactId = id,
        Name = name,
        Parts = texts.Select(Part.FromText).ToList(),
    };

    private static StreamResponse MakeArtifactUpdate(Artifact artifact, bool append, bool lastChunk, string? taskId = null, string? contextId = null) => new()
    {
        ArtifactUpdate = new TaskArtifactUpdateEvent
        {
            TaskId = taskId ?? SseFixtureFrames.TaskId,
            ContextId = contextId ?? SseFixtureFrames.ContextId,
            Artifact = artifact,
            Append = append,
            LastChunk = lastChunk,
        },
    };

    private static StreamResponse MakeStatusUpdate(TaskState state, string? taskId = null, string? contextId = null, Message? message = null) => new()
    {
        StatusUpdate = new TaskStatusUpdateEvent
        {
            TaskId = taskId ?? SseFixtureFrames.TaskId,
            ContextId = contextId ?? SseFixtureFrames.ContextId,
            Status = new A2A.TaskStatus { State = state, Message = message },
        },
    };

    private static Message MakeDirectMessage(string id, string text) => new()
    {
        MessageId = id,
        Role = Role.Agent,
        Parts = [Part.FromText(text)],
        ContextId = SseFixtureFrames.ContextId,
    };

    /// <summary>
    /// The semantic shape of a reduction: final state, every artifact (id, name, revision, closed flag, part count,
    /// concatenated text), the status message's part count and metadata (the attributions), history length, warning
    /// count, the interrupted flag and whether a direct message arrived. Frame counts are <b>not</b> part of it — a
    /// framing may merge or split events, and comments or heartbeats are not events — so
    /// <see cref="EquivalentFraming_Always_ReducesToTheHappyPathShape"/> asserts them against each fixture's own
    /// event count. Ids and timestamps that legitimately differ per fixture are excluded.
    /// </summary>
    private static string Shape(WorkIqTaskReducer reducer)
    {
        var snapshot = SnapshotOf(reducer);
        var artifacts = snapshot.Artifacts.Select(a => $"{a.ArtifactId}|{a.Name}|{a.Revision}|{a.IsClosed}|{a.Parts.Length}|{SseFixtureFrames.Text(a)}");
        return string.Join("\n",
            snapshot.State,
            snapshot.RawState,
            string.Join(";", artifacts),
            snapshot.StatusMessage?.Parts.Length,
            snapshot.StatusMessage?.Metadata?.GetRawText(),
            snapshot.History.Length,
            snapshot.SchemaWarnings.Length,
            reducer.IsInterrupted,
            reducer.DirectMessage() is null);
    }
}
