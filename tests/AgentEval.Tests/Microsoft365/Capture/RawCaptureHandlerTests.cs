// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Capture;

/// <summary>
/// <see cref="RawCaptureHandler"/> over the <see cref="FakeWorkIqHandler"/>: pass-through without a sink, request
/// and response bytes recorded while the downstream still reads the body, the SSE tee recording exactly what the
/// consumer read (full read, early stop → Interrupted, default buffering), an inner exception marking the capture
/// interrupted and rethrowing, the <c>capture.*</c> trace events, and no token anywhere.
/// </summary>
public sealed class RawCaptureHandlerTests
{
    private const string Jws = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
    private const string SendReply = "{\"jsonrpc\":\"2.0\",\"id\":\"fixed\",\"result\":{\"task\":{\"id\":\"task-1\",\"contextId\":\"ctx-1\",\"status\":{\"state\":\"TASK_STATE_COMPLETED\"}}}}";
    private const string HappyPath = "sse/stream-happy-path.txt";
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        public Rig(RawCaptureSink? sink, WorkIqDiagnostics diagnostics)
        {
            Fake = new FakeWorkIqHandler { RewriteResponseId = false };
            Handler = new RawCaptureHandler { InnerHandler = Fake, Diagnostics = diagnostics, CurrentSink = sink };
            Client = new HttpClient(Handler) { BaseAddress = new Uri("https://agent.example.test/") };
        }

        public FakeWorkIqHandler Fake { get; }

        public RawCaptureHandler Handler { get; }

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }

    private static (WorkIqDiagnostics Diagnostics, ListTraceSink Trace, ListIssueSink Issues) Diag()
    {
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        return (new WorkIqDiagnostics("run-1", "config-hash-1", [trace], [issues], new FakeTimeProvider(Start)), trace, issues);
    }

    private static RawCaptureSink Sink(WorkIqDiagnostics diagnostics, EvidenceContentPolicy policy = EvidenceContentPolicy.FullEvidence, long maxBytes = 1 << 20) =>
        new("cap-1", policy, maxBytes, 1000, null, diagnostics);

    private static string RpcBody(string method) => "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"" + method + "\",\"params\":{}}";

    private static HttpRequestMessage Rpc(string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "a2a/v1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jws);
        request.Headers.TryAddWithoutValidation("A2A-Version", "1.0");
        request.Content = new StringContent(RpcBody(method), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Every record as text (bodies decoded as UTF-8, never base64: the base64 of <c>{"</c> is <c>eyJ</c>).</summary>
    private static string Dump(IEnumerable<RawCaptureRecord> records) =>
        string.Join(
            "\n",
            records.Select(r =>
                $"{r.Method} {r.Uri} {r.ContentType} {string.Join(";", r.Headers.Select(h => h.Key + "=" + h.Value))} " +
                (r.Body is null ? "" : Encoding.UTF8.GetString(r.Body))));

    private static async Task<byte[]> ReadAtMostAsync(Stream stream, int target)
    {
        using var collected = new MemoryStream();
        var buffer = new byte[5];
        while (collected.Length < target)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
        }

        return collected.ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Pass-through
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_NoSink_PassesThroughAndRecordsNothing()
    {
        var (diag, trace, issues) = Diag();
        using var rig = new Rig(sink: null, diag);
        rig.Fake.OnRpc("SendMessage", SendReply);

        using var response = await rig.Client.SendAsync(Rpc("SendMessage"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SendReply, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, rig.Fake.TotalRequests);
        Assert.Equal("Bearer", rig.Fake.Requests[0].AuthorizationScheme);
        Assert.DoesNotContain(trace.Events, e => e.Stage == WorkIqTraceStage.Capture);
        Assert.Empty(issues.Issues);
    }

    [Fact]
    public async Task SendAsync_SinkClearedBetweenCalls_OnlyTheFirstIsRecorded()
    {
        var (diag, _, _) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnRpc("SendMessage", SendReply);

        (await rig.Client.SendAsync(Rpc("SendMessage"))).Dispose();
        rig.Handler.CurrentSink = null;
        (await rig.Client.SendAsync(Rpc("SendMessage"))).Dispose();

        Assert.Equal(2, rig.Fake.TotalRequests);
        Assert.Equal(2, sink.Records.Count);
    }

    [Fact]
    public async Task SendAsync_SinkOnTheRequest_WinsOverTheCurrentSink()
    {
        var (diag, _, _) = Diag();
        var current = Sink(diag);
        var own = new RawCaptureSink("cap-own", EvidenceContentPolicy.FullEvidence, 1 << 20, 1000, null, diag);
        using var rig = new Rig(current, diag);
        rig.Fake.OnRpc("SendMessage", SendReply);
        using var request = Rpc("SendMessage");
        request.Options.Set(RawCaptureHandler.SinkOption, own);

        (await rig.Client.SendAsync(request)).Dispose();

        // A call's request and response land in the sink it carries, never in the sink another call on the client set.
        Assert.Equal(2, own.Records.Count);
        Assert.Empty(current.Records);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Buffered JSON
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_JsonResponse_RecordsBothBodiesAndDownstreamStillReadsTheBody()
    {
        var (diag, trace, issues) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnRpc("SendMessage", SendReply, headers: new Dictionary<string, string> { ["request-id"] = "req-42" });

        using var response = await rig.Client.SendAsync(Rpc("SendMessage"));
        var downstream = await response.Content.ReadAsStringAsync();

        Assert.Equal(SendReply, downstream);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(RpcBody("SendMessage"), rig.Fake.Requests[0].BodyJson);

        var records = sink.Records;
        Assert.Equal(2, records.Count);
        Assert.Equal(RawCaptureRecordKind.Request, records[0].Kind);
        Assert.Equal(Encoding.UTF8.GetBytes(RpcBody("SendMessage")), records[0].Body);
        Assert.Equal("https://agent.example.test/a2a/v1", records[0].Uri);
        Assert.Equal(WorkIqRedaction.RedactedMarker, records[0].Headers["Authorization"]);
        Assert.Equal(RawCaptureRecordKind.Response, records[1].Kind);
        Assert.Equal(1, records[1].Sequence);
        Assert.Equal(200, records[1].HttpStatus);
        Assert.Equal(Encoding.UTF8.GetBytes(downstream), records[1].Body);
        Assert.Equal("req-42", records[1].Headers["request-id"]);
        Assert.False(sink.IsInterrupted);

        var requestEvent = Assert.Single(trace.Events, e => e.Code == "capture.request");
        Assert.Equal(WorkIqTraceStage.Capture, requestEvent.Stage);
        Assert.Contains("cap-1", requestEvent.Message, StringComparison.Ordinal);
        var responseEvent = Assert.Single(trace.Events, e => e.Code == "capture.response");
        Assert.Equal(200, responseEvent.HttpStatus);
        Assert.Equal("req-42", responseEvent.RequestId);
        Assert.NotNull(responseEvent.ElapsedMs);
        Assert.Empty(issues.Issues);
    }

    [Fact]
    public async Task SendAsync_ErrorStatus_StillRecordsAndReturnsTheResponse()
    {
        var (diag, _, _) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.FailWith("SendMessage", HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"TooManyRequests\"}}", new Dictionary<string, string> { ["Retry-After"] = "30" });

        using var response = await rig.Client.SendAsync(Rpc("SendMessage"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("{\"error\":{\"code\":\"TooManyRequests\"}}", await response.Content.ReadAsStringAsync());
        Assert.Equal(429, sink.Records[1].HttpStatus);
        Assert.Equal("30", sink.Records[1].Headers["Retry-After"]);
        Assert.False(sink.IsInterrupted);
    }

    [Fact]
    public async Task SendAsync_CardGetWithoutBody_RecordsAnEmptyRequestBody()
    {
        var (diag, _, _) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnCard("cards/default-card-two-interfaces.json");

        using var response = await rig.Client.GetAsync(".well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("GET", sink.Records[0].Method);
        Assert.Empty(sink.Records[0].Body!);
        Assert.Equal(FixtureLoader.Bytes("cards/default-card-two-interfaces.json"), sink.Records[1].Body);
    }

    // ---------------------------------------------------------------------------------------------------------
    // SSE tee
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_Sse_FullRead_RecordsExactlyTheFixtureBytes()
    {
        var (diag, trace, issues) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnSse("SendStreamingMessage", HappyPath, chunkSize: 7);
        var expected = FixtureLoader.Bytes(HappyPath);

        byte[] consumed;
        using (var response = await rig.Client.SendAsync(Rpc("SendStreamingMessage"), HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            using var stream = await response.Content.ReadAsStreamAsync();
            consumed = await ReadAtMostAsync(stream, int.MaxValue);
        }

        Assert.Equal(expected, consumed);
        var record = sink.Records[1];
        Assert.Equal(RawCaptureRecordKind.Stream, record.Kind);
        Assert.Equal(200, record.HttpStatus);
        Assert.True(record.ReachedEnd);
        Assert.Equal(expected, record.Body);
        Assert.Equal(expected.Length, record.BodyBytesObserved);
        Assert.False(sink.IsInterrupted);
        Assert.Equal(CaptureState.Complete, sink.Complete().State);
        var end = Assert.Single(trace.Events, e => e.Code == "capture.stream_end");
        Assert.Equal(WorkIqTraceLevel.Info, end.Level);
        Assert.Contains(trace.Events, e => e.Code == "capture.response" && e.HttpStatus == 200);
        Assert.Empty(issues.Issues);
    }

    [Fact]
    public async Task SendAsync_Sse_ConsumerStopsEarly_RecordsOnlyWhatWasReadAndInterrupts()
    {
        var (diag, trace, issues) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnSse("SendStreamingMessage", HappyPath, chunkSize: 7);
        var fixture = FixtureLoader.Bytes(HappyPath);

        byte[] consumed;
        using (var response = await rig.Client.SendAsync(Rpc("SendStreamingMessage"), HttpCompletionOption.ResponseHeadersRead))
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            consumed = await ReadAtMostAsync(stream, 20);
        }

        Assert.True(consumed.Length >= 20 && consumed.Length < fixture.Length);
        Assert.Equal(fixture[..consumed.Length], consumed);
        var record = sink.Records[1];
        Assert.Equal(RawCaptureRecordKind.Stream, record.Kind);
        Assert.False(record.ReachedEnd);
        Assert.Equal(consumed, record.Body);
        Assert.Equal(consumed.Length, record.BodyBytesObserved);
        Assert.True(sink.IsInterrupted);
        Assert.Equal(CaptureState.Interrupted, sink.Complete().State);
        var end = Assert.Single(trace.Events, e => e.Code == "capture.stream_end");
        Assert.Equal(WorkIqTraceLevel.Warning, end.Level);
        Assert.Equal("capture.interrupted", Assert.Single(issues.Issues).Code);
    }

    [Fact]
    public async Task SendAsync_Sse_DefaultCompletionOption_BuffersThroughTheTeeToTheEnd()
    {
        var (diag, _, _) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnSse("SendStreamingMessage", HappyPath, chunkSize: 3);
        var expected = FixtureLoader.Bytes(HappyPath);

        using var response = await rig.Client.SendAsync(Rpc("SendStreamingMessage"));
        var downstream = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(expected, downstream);
        var record = sink.Records[1];
        Assert.True(record.ReachedEnd);
        Assert.Equal(expected, record.Body);
        Assert.False(sink.IsInterrupted);
    }

    [Fact]
    public async Task SendAsync_Sse_MetadataOnly_RecordsCountsAndHashButNoBody()
    {
        var (diag, _, _) = Diag();
        var sink = Sink(diag, EvidenceContentPolicy.MetadataOnly);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnSse("SendStreamingMessage", HappyPath, chunkSize: 11);
        var expected = FixtureLoader.Bytes(HappyPath);

        using var response = await rig.Client.SendAsync(Rpc("SendStreamingMessage"));
        await response.Content.ReadAsByteArrayAsync();
        var reference = sink.Complete();

        Assert.Equal(CaptureState.NotRetained, reference.State);
        Assert.NotNull(reference.Sha256);
        Assert.Equal(expected.Length, sink.Records[1].BodyBytesObserved);
        Assert.All(sink.Records, r => Assert.Null(r.Body));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Failures
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_InnerThrows_MarksInterruptedAndRethrows()
    {
        var (diag, trace, issues) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        rig.Fake.Throw("SendMessage", new HttpRequestException("connection reset by Bearer " + Jws));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => rig.Client.SendAsync(Rpc("SendMessage")));

        Assert.StartsWith("connection reset", ex.Message, StringComparison.Ordinal);
        Assert.True(sink.IsInterrupted);
        var record = Assert.Single(sink.Records);
        Assert.Equal(RawCaptureRecordKind.Request, record.Kind);
        Assert.Equal(CaptureState.Interrupted, sink.Complete().State);
        var failed = Assert.Single(trace.Events, e => e.Code == "capture.failed");
        Assert.Equal(WorkIqTraceLevel.Warning, failed.Level);
        Assert.Contains("HttpRequestException", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", failed.Message, StringComparison.Ordinal);
        Assert.NotNull(failed.ElapsedMs);
        Assert.Equal("capture.interrupted", Assert.Single(issues.Issues).Code);
    }

    [Fact]
    public async Task SendAsync_CancellationInsideTheSend_MarksInterruptedAndPropagates()
    {
        var (diag, _, _) = Diag();
        var sink = Sink(diag);
        using var rig = new Rig(sink, diag);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // The fake raises the cancellation from inside the pipeline (as a dropped wait would); the token is not
        // handed to HttpClient so the handler is reached regardless of HttpClient's own pre-send checks.
        rig.Fake.Throw("SendMessage", new OperationCanceledException("stopped", cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.SendAsync(Rpc("SendMessage")));

        Assert.True(sink.IsInterrupted);
        Assert.Equal(CaptureState.Interrupted, sink.Complete().State);
    }

    // ---------------------------------------------------------------------------------------------------------
    // No secret anywhere
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_Always_NoBearerOrJwtInRecordsTraceOrIssues()
    {
        var (diag, trace, issues) = Diag();
        var sink = Sink(diag, maxBytes: 64);
        using var rig = new Rig(sink, diag);
        rig.Fake.OnRpc("SendMessage", "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"echo\":\"" + Jws + "\"}}");

        using (var response = await rig.Client.SendAsync(Rpc("SendMessage")))
        {
            await response.Content.ReadAsStringAsync();
        }

        rig.Fake.Throw("SendMessage", new InvalidOperationException("Authorization: Bearer " + Jws));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Client.SendAsync(Rpc("SendMessage")));
        sink.Complete();

        var dump = Dump(sink.Records)
            + JsonSerializer.Serialize(trace.Events.Select(e => new { e.Code, e.Message, Data = e.Data?.GetRawText() }))
            + JsonSerializer.Serialize(issues.Issues.Select(i => new { i.Code, i.Message, i.SuggestedCause }));
        Assert.DoesNotContain("eyJ", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer " + Jws, dump, StringComparison.Ordinal);
        Assert.True(sink.IsTruncated);
        Assert.True(sink.IsInterrupted);
    }
}
