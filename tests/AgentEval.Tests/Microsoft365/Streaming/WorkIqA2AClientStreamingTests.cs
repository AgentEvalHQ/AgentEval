// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.Reducer;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Streaming;

/// <summary>
/// Contract tests of the streaming half of <see cref="WorkIqA2AClient"/> (M13) over the scripted
/// <see cref="FakeWorkIqHandler"/> behind the production pipeline (<c>WorkIqAuthHandler → RawCaptureHandler → fake</c>):
/// frames are identical whatever the chunk boundaries, a mid-stream failure becomes one final failure frame
/// (never an exception after frames were yielded), a malformed frame is reported and the stream continues, ids are
/// checked, the request configuration follows the profile, a subscription increments the connection epoch, and
/// every step is traced. No network is touched.
/// </summary>
public sealed class WorkIqA2AClientStreamingTests
{
    private const string HappyPath = "sse/stream-happy-path.txt";
    private const string StreamMethod = "SendStreamingMessage";
    private const string SubscribeMethod = "SubscribeToTask";

    private static readonly DateTimeOffset Start = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);

    private static readonly Microsoft365Config DefaultConfig = new()
    {
        TenantId = "00000000-0000-0000-0000-000000000001",
        AppClientId = "00000000-0000-0000-0000-000000000002",
    };

    private sealed record Harness(
        WorkIqA2AClient Client,
        FakeWorkIqHandler Fake,
        ListTraceSink Trace,
        ListIssueSink Issues,
        FakeTimeProvider Clock,
        WorkIqDiagnostics Diagnostics);

    private static Harness Build(WorkIqOptions? options = null, Microsoft365Config? config = null)
    {
        var fake = new FakeWorkIqHandler().OnCard("cards/default-card-two-interfaces.json");
        var clock = new FakeTimeProvider(Start);
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        var diagnostics = new WorkIqDiagnostics("run-stream", "sha-stream", [trace], [issues], clock);
        var capture = new RawCaptureHandler { InnerHandler = fake, Diagnostics = diagnostics };
        var auth = new WorkIqAuthHandler(new StaticTokenProvider()) { InnerHandler = capture, Diagnostics = diagnostics };
        var http = new HttpClient(auth);
        var client = new WorkIqA2AClient(config ?? DefaultConfig, options ?? new WorkIqOptions(), http, capture, clock, diagnostics);
        return new Harness(client, fake, trace, issues, clock, diagnostics);
    }

    private static SendMessageRequest Request(string messageId = "msg-user-ab12cd34-0001") => new()
    {
        Message = new Message
        {
            MessageId = messageId,
            Role = Role.User,
            Parts = [Part.FromText("Summarize the status of Project Atlas and cite the sources.")],
        },
    };

    private static async Task<List<WorkIqStreamFrame>> CollectAsync(IAsyncEnumerable<WorkIqStreamFrame> frames, CancellationToken cancellationToken = default)
    {
        var list = new List<WorkIqStreamFrame>();
        await foreach (var frame in frames.WithCancellation(cancellationToken))
        {
            list.Add(frame);
        }

        return list;
    }

    private static string Projection(WorkIqStreamFrame frame) =>
        $"{frame.ClientSequence}|{frame.ConnectionEpoch}|{frame.Event?.PayloadCase}|{frame.DataJson}|{frame.Failure?.Category}|{frame.SseId}|{frame.SseEvent}";

    private static JsonElement StreamParams(FakeWorkIqHandler fake, string method)
    {
        var request = fake.Requests.Last(r => r.JsonRpcMethod == method);
        Assert.NotNull(request.BodyJson);
        using var document = JsonDocument.Parse(request.BodyJson);
        return document.RootElement.GetProperty("params").Clone();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Frames
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task StreamAsync_HappyPathChunked_YieldsTheSameFramesAsUnchunked(int chunkSize)
    {
        var whole = Build();
        whole.Fake.OnSse(StreamMethod, HappyPath);
        var expected = (await CollectAsync(whole.Client.StreamAsync(Request(), CancellationToken.None))).Select(Projection).ToArray();

        var chunked = Build();
        chunked.Fake.OnSse(StreamMethod, HappyPath, chunkSize);
        var actual = (await CollectAsync(chunked.Client.StreamAsync(Request(), CancellationToken.None))).Select(Projection).ToArray();

        Assert.Equal(5, expected.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task StreamAsync_HappyPath_FramesAreSequencedOnEpochOneWithTypedEventsAndProjectedData()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, HappyPath, chunkSize: 3);

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(5, frames.Count);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, frames.Select(f => f.ClientSequence).ToArray());
        Assert.All(frames, f => Assert.Equal(1, f.ConnectionEpoch));
        Assert.All(frames, f => Assert.Equal(Start, f.ReceivedAt));
        Assert.All(frames, f => Assert.Null(f.Failure));
        Assert.All(frames, f => Assert.Null(f.SseId));
        Assert.All(frames, f => Assert.Null(f.SseEvent));
        Assert.Equal(
            new[] { StreamResponseCase.Task, StreamResponseCase.ArtifactUpdate, StreamResponseCase.ArtifactUpdate, StreamResponseCase.StatusUpdate, StreamResponseCase.StatusUpdate },
            frames.Select(f => f.Event!.PayloadCase).ToArray());
        Assert.Equal(TaskState.Completed, frames[^1].Event!.StatusUpdate!.Status.State);
        Assert.Equal(1, h.Client.StreamConnectionEpoch);

        foreach (var frame in frames)
        {
            Assert.NotNull(frame.DataJson);
            var reparsed = JsonSerializer.Deserialize<StreamResponse>(frame.DataJson, A2AJsonUtilities.DefaultOptions);
            Assert.NotNull(reparsed);
            Assert.Equal(frame.Event!.PayloadCase, reparsed.PayloadCase);
        }
    }

    [Fact]
    public async Task StreamAsync_HappyPath_ReducesToACompletedTaskThroughTheReducer()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, HappyPath, chunkSize: 1);
        var reducer = new WorkIqTaskReducer(SseFixtureFrames.Template(), h.Clock, h.Diagnostics);

        await foreach (var frame in h.Client.StreamAsync(Request(), CancellationToken.None))
        {
            reducer.Apply(frame);
        }

        Assert.False(reducer.MarkStreamEnded());
        var snapshot = reducer.Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(Assert.Single(snapshot.Artifacts)));
    }

    [Fact]
    public async Task StreamAsync_MalformedFrame_YieldsAMalformedFailureFrameAndContinues()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, "sse/stream-malformed-event.txt", chunkSize: 5);

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(4, frames.Count);
        var malformed = frames[1];
        Assert.NotNull(malformed.Failure);
        Assert.Equal(WorkIqFailureCategory.Malformed, malformed.Failure.Category);
        Assert.Equal(WorkIqOperation.Stream, malformed.Failure.Operation);
        Assert.Equal(OutcomeCertainty.Indeterminate, malformed.Failure.Certainty);
        Assert.Equal(SseFixtureFrames.TaskId, malformed.Failure.TaskId);
        Assert.Null(malformed.Event);
        Assert.NotNull(malformed.DataJson);
        Assert.All(frames.Where(f => f != malformed), f => Assert.Null(f.Failure));
        Assert.Equal(TaskState.Completed, frames[^1].Event!.StatusUpdate!.Status.State);
        Assert.Contains(h.Trace.Events, e => e.Code == "stream.malformed" && e.Level == WorkIqTraceLevel.Warning);
        Assert.Contains(h.Issues.Issues, i => i.Code == "stream.one_of_violation" && i.Category == WorkIqIssueCategory.Protocol);
    }

    [Fact]
    public async Task StreamAsync_RpcErrorFrame_YieldsAFinalFailureFrameAndStopsWithoutThrowing()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, "sse/stream-rpc-error-frame.txt", chunkSize: 2);

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(3, frames.Count);
        Assert.Null(frames[0].Failure);
        Assert.Null(frames[1].Failure);
        var last = frames[2];
        Assert.Equal(3, last.ClientSequence);
        Assert.Null(last.Event);
        Assert.NotNull(last.Failure);
        Assert.Equal(WorkIqFailureCategory.RemoteError, last.Failure.Category);
        Assert.Equal(-32603, last.Failure.RpcErrorCode);
        Assert.Equal(WorkIqOperation.Stream, last.Failure.Operation);
        Assert.Equal(SseFixtureFrames.TaskId, last.Failure.TaskId);
        Assert.Equal(SseFixtureFrames.ContextId, last.Failure.ContextId);
        Assert.Contains(h.Trace.Events, e => e.Code == "stream.failed" && e.Level == WorkIqTraceLevel.Error && e.RpcErrorCode == -32603);
        Assert.Contains(h.Issues.Issues, i => i.Code == "stream.failed" && i.Category == WorkIqIssueCategory.Service);
    }

    [Fact]
    public async Task StreamAsync_TransportThrow_YieldsOnlyAFailureFrame()
    {
        var h = Build();
        h.Fake.Throw(StreamMethod, new HttpRequestException("connection reset by peer"));

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        var only = Assert.Single(frames);
        Assert.Equal(1, only.ClientSequence);
        Assert.Equal(1, only.ConnectionEpoch);
        Assert.Null(only.Event);
        Assert.Null(only.DataJson);
        Assert.NotNull(only.Failure);
        Assert.Equal(WorkIqFailureCategory.Transport, only.Failure.Category);
        Assert.Contains(h.Issues.Issues, i => i.Code == "stream.failed" && i.Category == WorkIqIssueCategory.Network);
        Assert.Contains(h.Trace.Events, e => e.Code == "stream.ended" && e.Level == WorkIqTraceLevel.Warning);
    }

    [Fact]
    public async Task StreamAsync_Http500_YieldsAFailureFrameWithTheStatus()
    {
        var h = Build();
        h.Fake.FailWith(StreamMethod, HttpStatusCode.InternalServerError, "{\"error\":\"upstream\"}");

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        var only = Assert.Single(frames);
        Assert.NotNull(only.Failure);
        Assert.Equal(500, only.Failure.HttpStatus);
        Assert.Null(only.Event);
    }

    [Fact]
    public async Task StreamAsync_EofWithoutTerminalState_EndsWithoutAFailureFrame()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, "sse/stream-eof-before-terminal.txt", chunkSize: 11);

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Null(f.Failure));
        var ended = Assert.Single(h.Trace.Events, e => e.Code == "stream.ended");
        Assert.Equal(WorkIqTraceLevel.Info, ended.Level);
        Assert.Contains("EOF is not completion", ended.Message, StringComparison.Ordinal);
        Assert.Equal("TASK_STATE_WORKING", ended.A2AState);
    }

    [Fact]
    public async Task StreamAsync_CancelledMidStream_YieldsACanceledFailureFrameInsteadOfThrowing()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, HappyPath, chunkSize: 1);
        using var cts = new CancellationTokenSource();
        var frames = new List<WorkIqStreamFrame>();

        await foreach (var frame in h.Client.StreamAsync(Request(), cts.Token))
        {
            frames.Add(frame);
            if (frames.Count == 1)
            {
                cts.Cancel();
            }
        }

        Assert.True(frames.Count >= 2);
        Assert.Null(frames[0].Failure);
        var last = frames[^1];
        Assert.NotNull(last.Failure);
        Assert.Equal(WorkIqFailureCategory.Canceled, last.Failure.Category);
        Assert.Null(last.Event);
    }

    [Fact]
    public async Task StreamAsync_IdMismatch_DeliversTheFrameAndTracesAWarningWithAnIssue()
    {
        const string stream =
            "data: {\"jsonrpc\":\"2.0\",\"id\":\"rpc-x\",\"result\":{\"task\":{\"id\":\"task-A\",\"contextId\":\"ctx-A\",\"status\":{\"state\":\"TASK_STATE_SUBMITTED\"}}}}\n\n" +
            "data: {\"jsonrpc\":\"2.0\",\"id\":\"rpc-x\",\"result\":{\"statusUpdate\":{\"taskId\":\"task-B\",\"contextId\":\"ctx-A\",\"status\":{\"state\":\"TASK_STATE_WORKING\"}}}}\n\n";
        var h = Build();
        h.Fake.OnSse(StreamMethod, stream);

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Null(f.Failure));
        Assert.Equal("task-B", frames[1].Event!.StatusUpdate!.TaskId);
        var mismatch = Assert.Single(h.Trace.Events, e => e.Code == "stream.id_mismatch");
        Assert.Equal(WorkIqTraceLevel.Warning, mismatch.Level);
        Assert.Contains("task-B", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("task-A", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(h.Issues.Issues, i => i.Code == "stream.id_mismatch" && i.Category == WorkIqIssueCategory.Protocol);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Request shape
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_DefaultProfile_PostsSendStreamingMessageWithTextPlainAndReturnImmediatelyFalse()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, HappyPath);

        await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(1, h.Fake.CountOf(StreamMethod));
        var parameters = StreamParams(h.Fake, StreamMethod);
        var configuration = parameters.GetProperty("configuration");
        Assert.Equal(new[] { "text/plain" }, configuration.GetProperty("acceptedOutputModes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(configuration.GetProperty("returnImmediately").GetBoolean());
        Assert.False(configuration.TryGetProperty("historyLength", out _));
        Assert.Equal("msg-user-ab12cd34-0001", parameters.GetProperty("message").GetProperty("messageId").GetString());
        Assert.Equal("ROLE_USER", parameters.GetProperty("message").GetProperty("role").GetString());
    }

    [Fact]
    public async Task StreamAsync_Native118ProfileAndHistoryLength_RequestsTheDiagnosticModes()
    {
        var h = Build(new WorkIqOptions { ProfileOverride = DiagnosticProfile.Native118Diagnostics, HistoryLength = 20 });
        h.Fake.OnSse(StreamMethod, HappyPath);

        await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        var configuration = StreamParams(h.Fake, StreamMethod).GetProperty("configuration");
        Assert.Equal(
            new[] { "text/plain", "application/vnd.ms-workiq-internal.retrieval", "application/vnd.ms-workiq.developer-info" },
            configuration.GetProperty("acceptedOutputModes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(20, configuration.GetProperty("historyLength").GetInt32());
    }

    [Fact]
    public void AcceptedOutputModesFor_EveryProfile_StartsWithTextPlainWithoutDuplicates()
    {
        foreach (var profile in Enum.GetValues<DiagnosticProfile>())
        {
            var modes = WorkIqA2AClient.AcceptedOutputModesFor(profile);
            Assert.Equal("text/plain", modes[0]);
            Assert.Equal(modes.Count, modes.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.Single(WorkIqA2AClient.AcceptedOutputModesFor(DiagnosticProfile.PublicAnswerOnly));
        Assert.Equal(3, WorkIqA2AClient.AcceptedOutputModesFor(DiagnosticProfile.Native118Diagnostics).Count);
    }

    [Fact]
    public void StreamAsync_NullRequest_ThrowsEagerly()
    {
        var h = Build();

        Assert.Throws<ArgumentNullException>(() => h.Client.StreamAsync(null!, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => h.Client.SubscribeAsync(" ", CancellationToken.None));
        h.Fake.AssertNoRequests();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Subscribe
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SubscribeAsync_AfterAStream_UsesTheNextEpochAndPostsSubscribeToTask()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, "sse/stream-eof-before-terminal.txt");
        h.Fake.OnSse(SubscribeMethod, "sse/subscribe-first-frame-snapshot.txt", chunkSize: 4);
        var first = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));
        Assert.Equal(3, first.Count);

        var frames = await CollectAsync(h.Client.SubscribeAsync(SseFixtureFrames.TaskId, CancellationToken.None));

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(2, f.ConnectionEpoch));
        Assert.Equal(new[] { 1L, 2L, 3L }, frames.Select(f => f.ClientSequence).ToArray());
        Assert.Equal(StreamResponseCase.Task, frames[0].Event!.PayloadCase);
        Assert.Equal(TaskState.Working, frames[0].Event!.Task!.Status.State);
        Assert.Equal(TaskState.Completed, frames[^1].Event!.StatusUpdate!.Status.State);
        Assert.Equal(2, h.Client.StreamConnectionEpoch);
        Assert.Equal(1, h.Fake.CountOf(SubscribeMethod));
        Assert.Equal(SseFixtureFrames.TaskId, StreamParams(h.Fake, SubscribeMethod).GetProperty("id").GetString());
        Assert.Contains(h.Trace.Events, e => e.Code == "subscribe.frame" && e.Stage == WorkIqTraceStage.Subscribe);
        Assert.Single(h.Trace.Events, e => e.Code == "subscribe.ended");
    }

    [Fact]
    public async Task SubscribeAsync_ReconcilesAnInterruptedStreamThroughTheReducer()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, "sse/stream-eof-before-terminal.txt", chunkSize: 1);
        h.Fake.OnSse(SubscribeMethod, "sse/subscribe-first-frame-snapshot.txt", chunkSize: 1);
        var reducer = new WorkIqTaskReducer(SseFixtureFrames.Template(), h.Clock, h.Diagnostics);

        await foreach (var frame in h.Client.StreamAsync(Request(), CancellationToken.None))
        {
            reducer.Apply(frame);
        }

        Assert.True(reducer.MarkStreamEnded());

        await foreach (var frame in h.Client.SubscribeAsync(reducer.Handle!.TaskId, CancellationToken.None))
        {
            reducer.Apply(frame, WorkIqSnapshotSource.Subscribe);
        }

        var snapshot = reducer.Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal(WorkIqSnapshotSource.Subscribe, snapshot.Source);
        Assert.Equal(SseFixtureFrames.FullAnswer, SseFixtureFrames.Text(Assert.Single(snapshot.Artifacts)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Diagnostics and capture
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_HappyPath_TracesOpenedEveryFrameTaskStatesAndEnded()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, HappyPath);

        await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        var events = h.Trace.Events.Where(e => e.Stage == WorkIqTraceStage.Stream).ToArray();
        Assert.Single(events, e => e.Code == "stream.opened");
        Assert.Equal(5, events.Count(e => e.Code == "stream.frame"));
        Assert.Equal(3, events.Count(e => e.Code == "task.state"));
        var ended = Assert.Single(events, e => e.Code == "stream.ended");
        Assert.Equal(WorkIqTraceLevel.Info, ended.Level);
        Assert.NotNull(ended.ElapsedMs);
        Assert.Equal("TASK_STATE_COMPLETED", ended.A2AState);
        Assert.Equal(SseFixtureFrames.TaskId, ended.TaskId);
        Assert.Equal(SseFixtureFrames.ContextId, ended.ContextId);

        var frameEvents = events.Where(e => e.Code == "stream.frame").ToArray();
        Assert.All(frameEvents, e => Assert.Equal(WorkIqTraceLevel.Info, e.Level));
        Assert.All(frameEvents, e => Assert.Equal(SseFixtureFrames.TaskId, e.TaskId));
        Assert.All(frameEvents, e => Assert.True(e.Data!.Value.GetProperty("bytes").GetInt32() > 0));
        Assert.Equal("artifactUpdate", frameEvents[1].Data!.Value.GetProperty("kind").GetString());
        Assert.Equal(SseFixtureFrames.AnswerArtifactId, frameEvents[1].Data!.Value.GetProperty("artifactId").GetString());
        Assert.Equal("TASK_STATE_COMPLETED", frameEvents[^1].A2AState);
        Assert.All(events, e => Assert.Equal("run-stream", e.RunId));
        Assert.DoesNotContain(h.Issues.Issues, i => i.Stage == WorkIqTraceStage.Stream);
    }

    [Fact]
    public async Task StreamAsync_AfterTheStreamEnds_ExposesTheRawCaptureReference()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, HappyPath);
        Assert.Null(h.Client.LastStreamCapture);

        await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        var capture = h.Client.LastStreamCapture;
        Assert.NotNull(capture);
        Assert.False(string.IsNullOrWhiteSpace(capture.CaptureId));
        Assert.Equal(RawCaptureSink.PolicyId, capture.PolicyId);
    }

    [Fact]
    public async Task StreamAsync_Always_NeverPutsATokenInAFrameOrATrace()
    {
        var h = Build();
        h.Fake.OnSse(StreamMethod, HappyPath);

        var frames = await CollectAsync(h.Client.StreamAsync(Request(), CancellationToken.None));

        Assert.All(frames, f => Assert.DoesNotContain(StaticTokenProvider.Token, f.DataJson ?? "", StringComparison.Ordinal));
        Assert.All(h.Trace.Events, e => Assert.DoesNotContain(StaticTokenProvider.Token, e.Message + (e.Data?.GetRawText() ?? ""), StringComparison.Ordinal));
        Assert.All(h.Fake.Requests, r => Assert.DoesNotContain(StaticTokenProvider.Token, string.Join("|", r.Headers.Values), StringComparison.Ordinal));
    }

    /// <summary>A token source for the pipeline; the value must never surface in frames, traces or recorded headers.</summary>
    private sealed class StaticTokenProvider : IWorkIqTokenProvider
    {
        public const string Token = "test-access-token-never-logged-5f1c";

        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) => new(Token);
    }
}
