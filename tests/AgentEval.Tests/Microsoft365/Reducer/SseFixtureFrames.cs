// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net.ServerSentEvents;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;

namespace AgentEval.Tests.Microsoft365.Reducer;

/// <summary>
/// Splits an embedded <c>sse/*.txt</c> fixture into <see cref="WorkIqStreamFrame"/>s the way the client does, but
/// from the bytes: the real <see cref="SseParser"/> frames the stream (blank-line framing, multi-line <c>data</c>,
/// CRLF, comments, <c>id</c>/<c>event</c>/<c>retry</c>), each <c>data</c> payload is read as a JSON-RPC envelope
/// with the SDK's <see cref="A2AJsonUtilities.DefaultOptions"/>, an <c>error</c> envelope becomes a
/// <see cref="WorkIqFailureCategory.RemoteError"/> frame, a <c>result</c> that does not hold exactly one of
/// <c>task | message | statusUpdate | artifactUpdate</c> becomes a <see cref="WorkIqFailureCategory.Malformed"/>
/// frame (event null), and everything else a typed frame. Shared by the reducer and streaming tests.
/// </summary>
internal static class SseFixtureFrames
{
    /// <summary>The local receipt time of the first frame; each later frame is 100 ms later.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 5, 14, 20, 0, TimeSpan.Zero);

    /// <summary>The task every SSE fixture uses.</summary>
    public const string TaskId = "task-ab12cd34-ef56-4a78-9b01-23c45d67e89f";

    /// <summary>The context every fixture uses.</summary>
    public const string ContextId = "ctx-5e0c7a2b-91d4-4f8e-b6a3-0d2f8c1e7b55";

    /// <summary>The answer artifact every SSE fixture uses.</summary>
    public const string AnswerArtifactId = "artifact-answer-ab12cd34-0001";

    /// <summary>The second artifact of the interleaved fixture.</summary>
    public const string NotesArtifactId = "artifact-notes-ab12cd34-0002";

    /// <summary>The complete answer text of the happy path and its equivalent framings (two chunks, no inserted newline, markers kept).</summary>
    public const string FullAnswer =
        "Project Atlas is on schedule: the design review closed on 3 November 2026 and the pilot starts on 17 November 2026.citeturn0search1";

    /// <summary>The first chunk of the answer (what a partial/interrupted stream retains).</summary>
    public const string FirstChunk = "Project Atlas is on schedule: the design review closed on 3 November 2026 and ";

    /// <summary>The selected JSON-RPC endpoint of the fixture card.</summary>
    public static readonly Uri Endpoint = new("https://workiq.svc.cloud.microsoft/a2a/");

    /// <summary>A handle template the way a conversation builds it before the service assigns the task id.</summary>
    public static WorkIqTaskHandle Template(string taskId = "", string? contextId = null) =>
        new(Endpoint, "1.0", null, taskId, contextId, null, "principal-test");

    /// <summary>The SSE items of a fixture, framed by the SDK's parser.</summary>
    public static IReadOnlyList<SseItem<string>> Items(string fixture)
    {
        using var stream = new MemoryStream(FixtureLoader.Bytes(fixture));
        return SseParser.Create(stream).Enumerate().ToList();
    }

    /// <summary>The frames of a fixture, sequenced from <paramref name="firstSequence"/> on connection <paramref name="connectionEpoch"/>.</summary>
    public static IReadOnlyList<WorkIqStreamFrame> Load(string fixture, int connectionEpoch = 1, long firstSequence = 1)
    {
        var frames = new List<WorkIqStreamFrame>();
        var sequence = firstSequence - 1;
        foreach (var item in Items(fixture))
        {
            sequence++;
            frames.Add(ToFrame(item.Data, sequence, connectionEpoch, Start.AddMilliseconds(100 * sequence), item.EventId, item.EventType));
        }

        return frames;
    }

    /// <summary>Builds one frame from a JSON-RPC envelope text.</summary>
    public static WorkIqStreamFrame ToFrame(string data, long sequence, int epoch, DateTimeOffset receivedAt, string? sseId = null, string? sseEvent = null)
    {
        var envelope = JsonSerializer.Deserialize<JsonRpcResponse>(data, A2AJsonUtilities.DefaultOptions)
                       ?? throw new InvalidOperationException("The envelope deserialized as null.");
        if (envelope.Error is { } error)
        {
            var remote = new WorkIqFailure(
                WorkIqFailureCategory.RemoteError,
                $"JSON-RPC error {error.Code} inside the stream",
                OutcomeCertainty.Indeterminate,
                WorkIqOperation.Stream,
                RpcErrorCode: error.Code,
                RpcRequestId: envelope.Id.ToString());
            return new WorkIqStreamFrame(sequence, epoch, receivedAt, null, data, sseId, sseEvent, remote);
        }

        var response = envelope.Result.Deserialize<StreamResponse>(A2AJsonUtilities.DefaultOptions)
                       ?? throw new InvalidOperationException("The result deserialized as null.");
        var populated = (response.Task is null ? 0 : 1) + (response.Message is null ? 0 : 1) + (response.StatusUpdate is null ? 0 : 1) + (response.ArtifactUpdate is null ? 0 : 1);
        if (populated != 1)
        {
            var malformed = new WorkIqFailure(
                WorkIqFailureCategory.Malformed,
                $"stream frame {sequence} has {populated} of task|message|statusUpdate|artifactUpdate populated; exactly one is required",
                OutcomeCertainty.Indeterminate,
                WorkIqOperation.Stream);
            return new WorkIqStreamFrame(sequence, epoch, receivedAt, null, data, sseId, sseEvent, malformed);
        }

        return new WorkIqStreamFrame(sequence, epoch, receivedAt, response, data, sseId, sseEvent, null);
    }

    /// <summary>A frame around an SDK stream response built in code.</summary>
    public static WorkIqStreamFrame Frame(StreamResponse response, long sequence = 1, int epoch = 1) =>
        new(sequence, epoch, Start.AddMilliseconds(100 * sequence), response, null, null, null, null);

    /// <summary>The text parts of an artifact concatenated with nothing inserted between chunks (newlines are content).</summary>
    public static string Text(WorkIqArtifact artifact) =>
        string.Concat(artifact.Parts.Where(p => p.Kind == WorkIqPartKind.Text).Select(p => p.Text));

    /// <summary>The text parts of a message concatenated with nothing inserted.</summary>
    public static string Text(WorkIqMessage message) =>
        string.Concat(message.Parts.Where(p => p.Kind == WorkIqPartKind.Text).Select(p => p.Text));

    /// <summary>How many <c>metadata.attributions</c> a message carries; -1 when the array is absent (absent is not empty).</summary>
    public static int AttributionCount(WorkIqMessage? message)
    {
        if (message?.Metadata is not { } metadata
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("attributions", out var attributions)
            || attributions.ValueKind != JsonValueKind.Array)
        {
            return -1;
        }

        return attributions.GetArrayLength();
    }
}
