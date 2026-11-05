// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Capture;

/// <summary>
/// <see cref="RawCaptureSink"/>: byte round trip and request/response pairing, redaction of headers, query strings
/// and bodies before retention, the byte and frame bounds (state Truncated, one issue, first bytes kept), the
/// policy matrix (MetadataOnly / SelectedContent keep counts and hashes only, FullEvidence writes files with a
/// manifest), interruption, stream recording, completion idempotence and the write-failure path.
/// </summary>
public sealed class RawCaptureSinkTests : IDisposable
{
    private const string Jws = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
    private const string RequestJson = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"SendMessage\",\"params\":{\"message\":{\"role\":\"ROLE_USER\"}}}";
    private const string ResponseJson = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"task\":{\"id\":\"task-1\",\"status\":{\"state\":\"TASK_STATE_COMPLETED\"}}}}";
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-m365-capture-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp folder.
        }
    }

    private static (WorkIqDiagnostics Diagnostics, ListTraceSink Trace, ListIssueSink Issues) Diag()
    {
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        return (new WorkIqDiagnostics("run-1", "sha-1", [trace], [issues], new FakeTimeProvider(Start)), trace, issues);
    }

    private static HttpRequestMessage Request(string body = RequestJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://agent.example.test/a2a/v1?code=secret123&x=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jws);
        request.Headers.TryAddWithoutValidation("A2A-Version", "1.0");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpResponseMessage Response(HttpRequestMessage? request, string body = ResponseJson, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        response.Headers.TryAddWithoutValidation("request-id", "req-1");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=" + Jws + "; HttpOnly");
        return response;
    }

    private static WorkIqStreamFrame Frame(long sequence, string? dataJson = "{\"a\":1}", string? sseId = "id-1", string? sseEvent = "message") =>
        new(sequence, 0, Start.AddMilliseconds(sequence), null, dataJson, sseId, sseEvent, null);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Sha(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            hash.AppendData(part);
        }

        return Hex(hash.GetHashAndReset());
    }

    private static string AllText(IEnumerable<string> paths) => string.Join("\n", paths.Select(p => File.ReadAllText(p)));

    /// <summary>Every record as text (bodies decoded as UTF-8, never base64: the base64 of <c>{"</c> is <c>eyJ</c>).</summary>
    private static string Dump(IEnumerable<RawCaptureRecord> records) =>
        string.Join(
            "\n",
            records.Select(r =>
                $"{r.Method} {r.Uri} {r.ContentType} {string.Join(";", r.Headers.Select(h => h.Key + "=" + h.Value))} " +
                (r.Body is null ? "" : Encoding.UTF8.GetString(r.Body))));

    // ---------------------------------------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_BlankIdOrNegativeBound_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RawCaptureSink(" ", EvidenceContentPolicy.FullEvidence, 1, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawCaptureSink("c", EvidenceContentPolicy.FullEvidence, -1, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawCaptureSink("c", EvidenceContentPolicy.FullEvidence, 1, -1, null));
    }

    [Fact]
    public void Constructor_Defaults_AreEmptyAndComplete()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 100, 10, null);

        Assert.Equal("c1", sink.CaptureId);
        Assert.True(sink.RetainsBodies);
        Assert.Null(sink.CaptureDirectory);
        Assert.False(sink.IsTruncated);
        Assert.False(sink.IsInterrupted);
        Assert.False(sink.IsCompleted);
        Assert.Null(sink.Reference);
        Assert.Equal(CaptureState.Complete, sink.State);
        Assert.Empty(sink.Records);
        Assert.Empty(sink.Frames);
        Assert.Equal(0, sink.BytesObserved);
        Assert.Equal(0, sink.FrameCount);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Round trip, pairing, hashing
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void RecordRequestAndResponse_FullEvidence_RoundTripsBytesAndPairsTheSequence()
    {
        var (diag, _, _) = Diag();
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null, diag);
        var request = Request();
        var requestBytes = Encoding.UTF8.GetBytes(RequestJson);
        var responseBytes = Encoding.UTF8.GetBytes(ResponseJson);

        sink.RecordRequest(request, requestBytes);
        sink.RecordResponse(Response(request), responseBytes);
        var reference = sink.Complete();

        var records = sink.Records;
        Assert.Equal(2, records.Count);
        Assert.Equal(RawCaptureRecordKind.Request, records[0].Kind);
        Assert.Equal(1, records[0].Sequence);
        Assert.Equal("POST", records[0].Method);
        Assert.Equal(requestBytes, records[0].Body);
        Assert.Equal(requestBytes.Length, records[0].BodyBytesObserved);
        Assert.Equal(Hex(SHA256.HashData(requestBytes)), records[0].Sha256);
        Assert.Equal(Start, records[0].RecordedAt);

        Assert.Equal(RawCaptureRecordKind.Response, records[1].Kind);
        Assert.Equal(1, records[1].Sequence);
        Assert.Equal(200, records[1].HttpStatus);
        Assert.Equal(responseBytes, records[1].Body);
        Assert.Null(records[1].ReachedEnd);

        Assert.Equal("c1", reference.CaptureId);
        Assert.Equal(CaptureState.Complete, reference.State);
        Assert.Equal(RawCaptureSink.PolicyId, reference.PolicyId);
        Assert.Null(reference.ProtectedLocation);
        Assert.Equal(Sha(requestBytes, responseBytes), reference.Sha256);
        Assert.Equal(requestBytes.Length + responseBytes.Length, reference.ByteCount);
    }

    [Fact]
    public void RecordResponse_WithoutMatchingRequest_GetsItsOwnSequence()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null);

        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));
        sink.RecordResponse(Response(null), Encoding.UTF8.GetBytes(ResponseJson));
        sink.RecordResponse(Response(new HttpRequestMessage(HttpMethod.Get, "https://agent.example.test/.well-known/agent-card.json")), Encoding.UTF8.GetBytes("{}"));

        var sequences = sink.Records.Select(r => r.Sequence).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, sequences);
    }

    [Fact]
    public void Complete_NothingRecorded_NullHashAndCount()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null);

        var reference = sink.Complete();

        Assert.Null(reference.Sha256);
        Assert.Null(reference.ByteCount);
        Assert.Equal(CaptureState.Complete, reference.State);
    }

    [Fact]
    public void Complete_Twice_ReturnsTheSameReference()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null);
        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));

        var first = sink.Complete();
        var second = sink.Complete();

        Assert.Same(first, second);
        Assert.True(sink.IsCompleted);
        Assert.Same(first, sink.Reference);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Redaction
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Records_Always_RedactHeadersAndQueryStrings()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null);
        var request = Request();

        sink.RecordRequest(request, Encoding.UTF8.GetBytes(RequestJson));
        sink.RecordResponse(Response(request), Encoding.UTF8.GetBytes(ResponseJson));

        var records = sink.Records;
        Assert.Equal(WorkIqRedaction.RedactedMarker, records[0].Headers["Authorization"]);
        Assert.Equal("1.0", records[0].Headers["A2A-Version"]);
        Assert.Equal("https://agent.example.test/a2a/v1" + RawCaptureSink.RedactedQueryMarker, records[0].Uri);
        Assert.Equal(records[0].Uri, records[1].Uri);
        Assert.Equal(WorkIqRedaction.RedactedMarker, records[1].Headers["Set-Cookie"]);
        Assert.Equal("req-1", records[1].Headers["request-id"]);

        var dump = Dump(records);
        Assert.DoesNotContain("secret123", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer " + Jws, dump, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordRequest_BodyWithToken_RedactedBeforeRetentionAndHashDescribesRedactedBytes()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 4096, 10, null);
        var body = "{\"params\":{\"text\":\"my token is " + Jws + " and header Bearer " + Jws + "\"}}";

        sink.RecordRequest(Request(body), Encoding.UTF8.GetBytes(body));
        var reference = sink.Complete();

        var record = Assert.Single(sink.Records);
        var retained = Encoding.UTF8.GetString(record.Body!);
        Assert.DoesNotContain("eyJ", retained, StringComparison.Ordinal);
        Assert.Contains(WorkIqRedaction.JwtRedactedMarker, retained, StringComparison.Ordinal);
        Assert.Equal(body.Length, record.BodyBytesObserved);
        Assert.Equal(record.Body!.Length, record.BodyBytesRetained);
        Assert.Equal(Hex(SHA256.HashData(record.Body!)), record.Sha256);
        Assert.Equal(record.Sha256, reference.Sha256);
        Assert.Equal(record.Body!.Length, reference.ByteCount);
    }

    [Fact]
    public void RecordFrame_FullEvidence_RedactsDataJson()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 4096, 10, null);

        sink.RecordFrame(Frame(1, "{\"access_token\":\"" + Jws + "\"}"));

        var frame = Assert.Single(sink.Frames);
        Assert.NotNull(frame.DataJson);
        Assert.DoesNotContain("eyJ", frame.DataJson, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount("{\"access_token\":\"" + Jws + "\"}"), frame.ByteLength);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Bounds
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void RecordRequest_ExceedsMaxBytes_TruncatesKeepsTheFirstBytesAndRegistersOneIssue()
    {
        var (diag, trace, issues) = Diag();
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 10, 10, null, diag);
        var body = Encoding.UTF8.GetBytes(new string('a', 50) + new string('b', 50));

        sink.RecordRequest(Request(), body);
        sink.RecordResponse(Response(null), Encoding.UTF8.GetBytes(ResponseJson));
        var reference = sink.Complete();

        Assert.True(sink.IsTruncated);
        Assert.Equal(CaptureState.Truncated, reference.State);
        var records = sink.Records;
        Assert.Equal(body[..10], records[0].Body);
        Assert.True(records[0].Truncated);
        Assert.Equal(100, records[0].BodyBytesObserved);
        Assert.Equal(10, records[0].BodyBytesRetained);
        Assert.True(records[1].Truncated);
        Assert.Empty(records[1].Body!);
        Assert.Equal(100 + ResponseJson.Length, sink.BytesObserved);
        Assert.Equal(10, sink.BytesRetained);
        Assert.Equal(10, reference.ByteCount);
        Assert.Equal(Hex(SHA256.HashData(body[..10])), reference.Sha256);

        var issue = Assert.Single(issues.Issues);
        Assert.Equal(WorkIqIssueCategory.Capture, issue.Category);
        Assert.Equal("capture.truncated", issue.Code);
        Assert.Equal(WorkIqTraceStage.Capture, issue.Stage);
        Assert.Contains("MaxCaptureBytes", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains(trace.Events, e => e.Code == "capture.truncated" && e.Level == WorkIqTraceLevel.Warning);
        Assert.Equal(1, diag.IssueCount);
    }

    [Fact]
    public void RecordRequest_MaxBytesZero_RetainsNothingAndIsTruncated()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 0, 10, null);

        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));
        var reference = sink.Complete();

        Assert.Equal(CaptureState.Truncated, reference.State);
        Assert.Null(reference.Sha256);
        Assert.Null(reference.ByteCount);
        Assert.Equal(RequestJson.Length, sink.BytesObserved);
    }

    [Fact]
    public void RecordRequest_CutInsideAToken_DropsTheFragment()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 2 + 30, 10, null);
        var body = Encoding.UTF8.GetBytes("x " + Jws + " y");

        sink.RecordRequest(Request(), body);

        var retained = Encoding.UTF8.GetString(Assert.Single(sink.Records).Body!);
        Assert.Equal("x ", retained);
        Assert.DoesNotContain("eyJ", retained, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordFrame_ExceedsMaxFrames_CountsButDoesNotRetainAndTruncates()
    {
        var (diag, _, issues) = Diag();
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 4096, 2, null, diag);

        sink.RecordFrame(Frame(1));
        sink.RecordFrame(Frame(2, sseId: "id-2", sseEvent: null));
        sink.RecordFrame(Frame(3));
        sink.RecordFrame(Frame(4));

        Assert.Equal(4, sink.FrameCount);
        Assert.Equal(2, sink.FramesRetained);
        Assert.True(sink.IsTruncated);
        Assert.Equal(CaptureState.Truncated, sink.State);
        var frames = sink.Frames;
        Assert.Equal(1, frames[0].ClientSequence);
        Assert.Equal("id-1", frames[0].SseId);
        Assert.Equal("message", frames[0].SseEvent);
        Assert.Equal(Start.AddMilliseconds(1), frames[0].ReceivedAt);
        Assert.Equal(7, frames[0].ByteLength);
        Assert.Null(frames[0].PayloadCase);
        Assert.Null(frames[1].SseEvent);
        var issue = Assert.Single(issues.Issues);
        Assert.Equal("capture.truncated", issue.Code);
        Assert.Contains("MaxFrames", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordFrame_WithFailure_KeepsTheFailureFacts()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.SelectedContent, 4096, 10, null);
        var failure = new WorkIqFailure(WorkIqFailureCategory.RemoteError, "rpc error Bearer " + Jws, OutcomeCertainty.Known, WorkIqOperation.Stream, RpcErrorCode: -32601);
        var frame = new WorkIqStreamFrame(5, 1, Start, null, "{\"error\":{}}", null, null, failure);

        sink.RecordFrame(frame);

        var record = Assert.Single(sink.Frames);
        Assert.Equal(5, record.ClientSequence);
        Assert.Equal(1, record.ConnectionEpoch);
        Assert.Equal("RemoteError", record.FailureCategory);
        Assert.Equal(-32601, record.RpcErrorCode);
        Assert.DoesNotContain("eyJ", record.FailureMessage, StringComparison.Ordinal);
        Assert.Null(record.DataJson);
        Assert.Equal(12, record.ByteLength);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Interruption
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void MarkInterrupted_Always_SetsInterruptedStateAndRegistersOneIssue()
    {
        var (diag, trace, issues) = Diag();
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null, diag);
        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));

        sink.MarkInterrupted();
        sink.MarkInterrupted();
        var reference = sink.Complete();

        Assert.True(sink.IsInterrupted);
        Assert.Equal(CaptureState.Interrupted, reference.State);
        Assert.NotNull(reference.Sha256);
        var issue = Assert.Single(issues.Issues);
        Assert.Equal("capture.interrupted", issue.Code);
        Assert.Equal(WorkIqIssueCategory.Capture, issue.Category);
        Assert.Single(trace.Events, e => e.Code == "capture.interrupted");
    }

    [Fact]
    public void State_InterruptedAndTruncated_InterruptedWins()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1, 10, null);

        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));
        sink.MarkInterrupted();

        Assert.True(sink.IsTruncated);
        Assert.Equal(CaptureState.Interrupted, sink.State);
        Assert.Equal(CaptureState.Interrupted, sink.Complete().State);
    }

    [Fact]
    public void RecordAfterComplete_Always_IgnoredWithOneIssue()
    {
        var (diag, _, issues) = Diag();
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null, diag);
        var reference = sink.Complete();

        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));
        sink.RecordResponse(Response(null), Encoding.UTF8.GetBytes(ResponseJson));
        sink.RecordFrame(Frame(1));
        var recorder = sink.BeginStreamResponse(Response(null, contentType: "text/event-stream"));
        recorder.Append("data: x\n\n"u8);
        recorder.End(reachedEnd: false);

        Assert.Empty(sink.Records);
        Assert.Empty(sink.Frames);
        Assert.Equal(0, sink.BytesObserved);
        Assert.False(sink.IsInterrupted);
        Assert.Same(reference, sink.Complete());
        var issue = Assert.Single(issues.Issues);
        Assert.Equal("capture.after_complete", issue.Code);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Streams
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void StreamRecorder_ReadToEnd_RecordsTheBytesAsAStreamEntry()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null);
        var request = Request();
        sink.RecordRequest(request, Encoding.UTF8.GetBytes(RequestJson));
        var response = Response(request, "", contentType: "text/event-stream");

        var recorder = sink.BeginStreamResponse(response);
        Assert.True(recorder.IsOpen);
        Assert.Equal(1, recorder.Sequence);
        recorder.Append("data: {\"a\":1}\n\n"u8);
        recorder.Append("data: {\"b\":2}\n\n"u8);
        Assert.Equal(30, recorder.BytesObserved);
        recorder.End(reachedEnd: true);
        recorder.End(reachedEnd: false);

        Assert.False(recorder.IsOpen);
        var record = sink.Records[1];
        Assert.Equal(RawCaptureRecordKind.Stream, record.Kind);
        Assert.Equal(1, record.Sequence);
        Assert.Equal(200, record.HttpStatus);
        Assert.True(record.ReachedEnd);
        Assert.Equal(Encoding.UTF8.GetBytes("data: {\"a\":1}\n\ndata: {\"b\":2}\n\n"), record.Body);
        Assert.False(sink.IsInterrupted);
        Assert.Equal(CaptureState.Complete, sink.Complete().State);
    }

    [Fact]
    public void StreamRecorder_EndBeforeEof_MarksInterrupted()
    {
        var (diag, _, issues) = Diag();
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null, diag);
        var recorder = sink.BeginStreamResponse(Response(null, "", contentType: "text/event-stream"));

        recorder.Append("data: partial"u8);
        recorder.End(reachedEnd: false);

        var record = Assert.Single(sink.Records);
        Assert.False(record.ReachedEnd);
        Assert.Equal("data: partial"u8.ToArray(), record.Body);
        Assert.True(sink.IsInterrupted);
        Assert.Equal(CaptureState.Interrupted, sink.State);
        Assert.Equal("capture.interrupted", Assert.Single(issues.Issues).Code);
    }

    [Fact]
    public void StreamRecorder_ExceedsMaxBytes_KeepsThePrefixAndTruncates()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 8, 10, null);
        var recorder = sink.BeginStreamResponse(Response(null, "", contentType: "text/event-stream"));

        recorder.Append("0123456"u8);
        recorder.Append("789abcdef"u8);
        recorder.End(reachedEnd: true);

        var record = Assert.Single(sink.Records);
        Assert.True(record.Truncated);
        Assert.Equal("01234567"u8.ToArray(), record.Body);
        Assert.Equal(16, record.BodyBytesObserved);
        Assert.Equal(CaptureState.Truncated, sink.State);
    }

    [Fact]
    public void Complete_WithAnOpenStream_ClosesItAsInterrupted()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1024, 10, null);
        var recorder = sink.BeginStreamResponse(Response(null, "", contentType: "text/event-stream"));
        recorder.Append("data: x\n\n"u8);

        var reference = sink.Complete();

        Assert.False(recorder.IsOpen);
        Assert.Equal(CaptureState.Interrupted, reference.State);
        Assert.False(Assert.Single(sink.Records).ReachedEnd);
        Assert.Equal(9, reference.ByteCount);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Policy matrix
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(EvidenceContentPolicy.MetadataOnly)]
    [InlineData(EvidenceContentPolicy.SelectedContent)]
    public void Complete_NotRetainingPolicies_WriteNothingKeepNoBodyButKeepCountsAndHash(EvidenceContentPolicy policy)
    {
        var directory = new DirectoryInfo(Path.Combine(_root, policy.ToString()));
        var sink = new RawCaptureSink("c1", policy, 4096, 10, directory);
        var request = Request();
        var requestBytes = Encoding.UTF8.GetBytes(RequestJson);
        var responseBytes = Encoding.UTF8.GetBytes(ResponseJson);

        sink.RecordRequest(request, requestBytes);
        sink.RecordResponse(Response(request), responseBytes);
        sink.RecordFrame(Frame(1));
        var reference = sink.Complete();

        Assert.False(sink.RetainsBodies);
        Assert.False(directory.Exists);
        Assert.False(Directory.Exists(sink.CaptureDirectory!));
        Assert.Equal(CaptureState.NotRetained, reference.State);
        Assert.Null(reference.ProtectedLocation);
        Assert.Equal(Sha(requestBytes, responseBytes), reference.Sha256);
        Assert.Equal(requestBytes.Length + responseBytes.Length, reference.ByteCount);
        Assert.All(sink.Records, r => Assert.Null(r.Body));
        Assert.All(sink.Records, r => Assert.NotNull(r.Sha256));
        Assert.Equal(requestBytes.Length, sink.Records[0].BodyBytesRetained);
        Assert.Null(Assert.Single(sink.Frames).DataJson);
        Assert.Equal(7, Assert.Single(sink.Frames).ByteLength);
    }

    [Fact]
    public void Complete_FullEvidenceWithDirectory_WritesFilesAndManifestWithHashes()
    {
        var (diag, trace, issues) = Diag();
        var directory = new DirectoryInfo(Path.Combine(_root, "full"));
        var sink = new RawCaptureSink("turn-1/cap:1", EvidenceContentPolicy.FullEvidence, 4096, 10, directory, diag);
        var request = Request();
        var requestBytes = Encoding.UTF8.GetBytes(RequestJson);
        var responseBytes = Encoding.UTF8.GetBytes(ResponseJson);
        sink.RecordRequest(request, requestBytes);
        sink.RecordResponse(Response(request), responseBytes);
        var card = new HttpRequestMessage(HttpMethod.Get, "https://agent.example.test/.well-known/agent-card.json");
        sink.RecordRequest(card, ReadOnlyMemory<byte>.Empty);
        sink.RecordResponse(Response(card, "{\"name\":\"card\"}"), Encoding.UTF8.GetBytes("{\"name\":\"card\"}"));
        var streamResponse = Response(null, "", contentType: "text/event-stream");
        var recorder = sink.BeginStreamResponse(streamResponse);
        recorder.Append("data: {\"x\":1}\n\n"u8);
        recorder.End(reachedEnd: true);
        sink.RecordFrame(Frame(1));
        sink.RecordFrame(Frame(2, "{\"token\":\"" + Jws + "\"}"));

        var reference = sink.Complete();

        var captureDirectory = sink.CaptureDirectory!;
        Assert.Equal(captureDirectory, reference.ProtectedLocation);
        Assert.Equal(Path.Combine(directory.FullName, "turn-1_cap_1"), captureDirectory);
        Assert.True(Directory.Exists(captureDirectory));
        var files = Directory.GetFiles(captureDirectory).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "frames.jsonl", "manifest.json", "request-1.json", "response-1.json", "response-2.json", "response-3.sse" }, files);
        Assert.Equal(requestBytes, File.ReadAllBytes(Path.Combine(captureDirectory, "request-1.json")));
        Assert.Equal(responseBytes, File.ReadAllBytes(Path.Combine(captureDirectory, "response-1.json")));
        Assert.Equal("data: {\"x\":1}\n\n"u8.ToArray(), File.ReadAllBytes(Path.Combine(captureDirectory, "response-3.sse")));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(captureDirectory, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("turn-1/cap:1", root.GetProperty("captureId").GetString());
        Assert.Equal(RawCaptureSink.PolicyId, root.GetProperty("policyId").GetString());
        Assert.Equal("FullEvidence", root.GetProperty("contentPolicy").GetString());
        Assert.Equal("Complete", root.GetProperty("state").GetString());
        Assert.Equal(reference.Sha256, root.GetProperty("sha256").GetString());
        Assert.Equal(2, root.GetProperty("frameCount").GetInt32());
        Assert.Equal(Start, root.GetProperty("completedAt").GetDateTimeOffset());
        var manifestFiles = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(5, manifestFiles.Length);
        foreach (var file in manifestFiles)
        {
            if (file.TryGetProperty("fileName", out var fileName))
            {
                var bytes = File.ReadAllBytes(Path.Combine(captureDirectory, fileName.GetString()!));
                Assert.Equal(Hex(SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
            }
        }

        Assert.Equal(WorkIqRedaction.RedactedMarker, manifestFiles[0].GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("GET", manifestFiles[2].GetProperty("method").GetString());
        Assert.False(manifestFiles[2].TryGetProperty("fileName", out _));
        Assert.True(manifestFiles[4].GetProperty("reachedEnd").GetBoolean());
        var frames = root.GetProperty("frames");
        Assert.Equal(2, frames.GetProperty("count").GetInt32());
        Assert.Equal(Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(captureDirectory, "frames.jsonl")))), frames.GetProperty("sha256").GetString());

        var frameLines = File.ReadAllLines(Path.Combine(captureDirectory, "frames.jsonl"));
        Assert.Equal(2, frameLines.Length);
        Assert.All(frameLines, line => JsonDocument.Parse(line).Dispose());

        var everything = AllText(Directory.GetFiles(captureDirectory));
        Assert.DoesNotContain("eyJ", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer " + Jws, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", everything, StringComparison.Ordinal);
        Assert.Empty(issues.Issues);
        Assert.Contains(trace.Events, e => e.Code == "capture.written");
        Assert.Contains(trace.Events, e => e.Code == "capture.completed");
    }

    [Fact]
    public void Complete_FullEvidenceWithoutDirectory_KeepsBytesInMemoryWithNoLocation()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 4096, 10, null);
        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));

        var reference = sink.Complete();

        Assert.Equal(CaptureState.Complete, reference.State);
        Assert.Null(reference.ProtectedLocation);
        Assert.NotNull(Assert.Single(sink.Records).Body);
        Assert.Null(Assert.Single(sink.Records).FileName);
    }

    [Fact]
    public void Complete_UnwritableDirectory_RegistersWriteFailureAndReturnsNoLocation()
    {
        var (diag, trace, issues) = Diag();
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where the directory should be");
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 4096, 10, new DirectoryInfo(blocker), diag);
        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));

        var reference = sink.Complete();

        Assert.Null(reference.ProtectedLocation);
        Assert.Equal(CaptureState.Complete, reference.State);
        Assert.NotNull(reference.Sha256);
        var issue = Assert.Single(issues.Issues);
        Assert.Equal("capture.write_failed", issue.Code);
        Assert.Equal(WorkIqIssueCategory.Capture, issue.Category);
        Assert.Contains("RawCaptureDirectory", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains(trace.Events, e => e.Code == "capture.write_failed" && e.Level == WorkIqTraceLevel.Error);
        Assert.Null(Assert.Single(sink.Records).FileName);
    }

    [Fact]
    public void Complete_NoneDiagnostics_StillWorks()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.FullEvidence, 1, 0, null, WorkIqDiagnostics.None);

        sink.RecordRequest(Request(), Encoding.UTF8.GetBytes(RequestJson));
        sink.RecordFrame(Frame(1));
        sink.MarkInterrupted();

        Assert.Equal(CaptureState.Interrupted, sink.Complete().State);
        Assert.Equal(0, WorkIqDiagnostics.None.IssueCount);
    }

    [Fact]
    public void ConcurrentRecording_Always_CountsEveryByteAndFrame()
    {
        var sink = new RawCaptureSink("c1", EvidenceContentPolicy.SelectedContent, long.MaxValue, int.MaxValue, null);
        var body = Encoding.UTF8.GetBytes(ResponseJson);

        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0)
            {
                sink.RecordResponse(Response(null), body);
            }
            else
            {
                sink.RecordFrame(Frame(i));
            }
        });

        Assert.Equal(100, sink.Records.Count);
        Assert.Equal(100, sink.FrameCount);
        Assert.Equal(100L * body.Length, sink.BytesObserved);
        Assert.Equal(100L * body.Length, sink.Complete().ByteCount);
    }
}
