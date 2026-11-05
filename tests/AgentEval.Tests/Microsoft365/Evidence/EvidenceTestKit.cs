// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// Builders for the evidence tests: task snapshots, messages, artifacts and parts from literals or from the
/// synthetic envelope fixtures (a test-side stand-in for the lane-C reducer, which is not merged yet), the turn
/// identity and capture refs, and a normaliser wired to <see cref="FakeAnswerSelector"/> (lane A's selector is a
/// stub until it lands). All data is synthetic.
/// </summary>
internal static class EvidenceTestKit
{
    /// <summary>The capture id every snapshot built here reports.</summary>
    public const string CaptureId = "cap-send-0001";

    /// <summary>The default task id.</summary>
    public const string TaskId = "task-1";

    /// <summary>The default context id.</summary>
    public const string ContextId = "ctx-1";

    public static readonly Uri Endpoint = new("https://workiq.svc.cloud.microsoft/a2a/");
    public static readonly DateTimeOffset ObservedAt = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    public static WorkIqTaskHandle Handle(string taskId = TaskId, string? contextId = ContextId) =>
        new(Endpoint, "1.0", null, taskId, contextId, null, "principal-key");

    public static WorkIqPart Text(string text) => new(WorkIqPartKind.Text, text: text);

    public static WorkIqPart Data(string json, string? mediaType = null) =>
        new(WorkIqPartKind.Data, data: ParseJson(json), mediaType: mediaType);

    public static WorkIqPart Data(JsonElement data, string? mediaType = null) =>
        new(WorkIqPartKind.Data, data: data, mediaType: mediaType);

    /// <summary>Parses JSON into a detached element.</summary>
    public static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static WorkIqMessage Message(
        string messageId = "msg-agent-1",
        ImmutableArray<WorkIqPart> parts = default,
        string? metadataJson = null,
        string? taskId = TaskId,
        string? contextId = ContextId) =>
        new(messageId, "ROLE_AGENT", parts, metadataJson is null ? null : ParseJson(metadataJson), contextId, taskId);

    public static WorkIqMessage MessageWithParts(params WorkIqPart[] parts) => Message(parts: parts.ToImmutableArray());

    /// <summary>A status message whose metadata carries the given attribution list JSON (<c>[...]</c>).</summary>
    public static WorkIqMessage StatusWithAttributions(string attributionsArrayJson) =>
        Message("msg-status-1", ImmutableArray<WorkIqPart>.Empty, "{\"attributions\":" + attributionsArrayJson + "}");

    public static WorkIqArtifact Artifact(string artifactId, string? name, params WorkIqPart[] parts) =>
        new(artifactId, name, null, parts.ToImmutableArray(), 0, false);

    public static WorkIqTaskSnapshot Snapshot(
        WorkIqTaskState state = WorkIqTaskState.Completed,
        WorkIqMessage? statusMessage = null,
        ImmutableArray<WorkIqArtifact> artifacts = default,
        string captureId = CaptureId,
        string taskId = TaskId,
        bool withCapture = true) =>
        new(
            Handle(taskId),
            state,
            RawStateOf(state),
            ObservedAt,
            statusMessage,
            artifacts.IsDefault ? ImmutableArray<WorkIqArtifact>.Empty : artifacts,
            ImmutableArray<WorkIqMessage>.Empty,
            WorkIqSnapshotSource.Send,
            ObservedAt,
            ImmutableArray<string>.Empty,
            withCapture ? Capture(captureId) : null);

    public static WorkIqTaskSnapshot SnapshotWithArtifacts(params WorkIqArtifact[] artifacts) =>
        Snapshot(artifacts: artifacts.ToImmutableArray());

    public static RawCaptureRef Capture(string captureId = CaptureId) =>
        new(captureId, CaptureState.Complete, "evidence-content/selected/v1", null, new string('0', 64), 512);

    public static TurnIdentity Identity(string? taskId = TaskId) =>
        new("run-1", "case-1", "turn-1", 0, 0, "rpc-1", "msg-user-1", null, taskId, ContextId);

    public static string RawStateOf(WorkIqTaskState state) => state switch
    {
        WorkIqTaskState.Submitted => "TASK_STATE_SUBMITTED",
        WorkIqTaskState.Working => "TASK_STATE_WORKING",
        WorkIqTaskState.Completed => "TASK_STATE_COMPLETED",
        WorkIqTaskState.Failed => "TASK_STATE_FAILED",
        WorkIqTaskState.Canceled => "TASK_STATE_CANCELED",
        WorkIqTaskState.Rejected => "TASK_STATE_REJECTED",
        WorkIqTaskState.InputRequired => "TASK_STATE_INPUT_REQUIRED",
        WorkIqTaskState.AuthRequired => "TASK_STATE_AUTH_REQUIRED",
        _ => "TASK_STATE_UNSPECIFIED",
    };

    public static WorkIqTaskState StateOf(string? rawState) => rawState switch
    {
        "TASK_STATE_SUBMITTED" => WorkIqTaskState.Submitted,
        "TASK_STATE_WORKING" => WorkIqTaskState.Working,
        "TASK_STATE_COMPLETED" => WorkIqTaskState.Completed,
        "TASK_STATE_FAILED" => WorkIqTaskState.Failed,
        "TASK_STATE_CANCELED" => WorkIqTaskState.Canceled,
        "TASK_STATE_REJECTED" => WorkIqTaskState.Rejected,
        "TASK_STATE_INPUT_REQUIRED" => WorkIqTaskState.InputRequired,
        "TASK_STATE_AUTH_REQUIRED" => WorkIqTaskState.AuthRequired,
        _ => WorkIqTaskState.Unknown,
    };

    /// <summary>A normaliser wired to <see cref="FakeAnswerSelector"/> (lane A's real selector is a stub until it lands).</summary>
    public static WorkIqEvidenceNormalizer Normalizer(WorkIqDiagnostics? diagnostics = null) =>
        new(FakeAnswerSelector.SelectFromTask, FakeAnswerSelector.SelectFromMessage, diagnostics);

    /// <summary>Normalises with one complete raw capture and the default identity.</summary>
    public static Microsoft365TurnEvidence Normalize(
        WorkIqTaskSnapshot? task,
        WorkIqMessage? directMessage = null,
        DiagnosticProfile profile = DiagnosticProfile.Native118Diagnostics,
        WorkIqDiagnostics? diagnostics = null,
        string captureId = CaptureId) =>
        Normalizer(diagnostics).Normalize(
            task,
            directMessage,
            ImmutableArray.Create(Capture(captureId)),
            CaptureState.Complete,
            profile,
            Identity(task?.Handle.TaskId ?? TaskId));

    // ---------------------------------------------------------------------------------------------------------
    // Envelope fixtures → snapshots (test-side stand-in for the reducer)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Builds the snapshot of a <c>SendMessage</c> (<c>result.task</c>) or <c>GetTask</c> (<c>result</c>) envelope fixture.</summary>
    public static WorkIqTaskSnapshot SnapshotFromEnvelope(string fixture, string captureId = CaptureId)
    {
        using var document = JsonDocument.Parse(FixtureLoader.Text(fixture));
        var result = document.RootElement.GetProperty("result");
        var task = result.TryGetProperty("task", out var nested) ? nested : result;
        return SnapshotFromTaskElement(task, captureId);
    }

    /// <summary>Builds the direct reply message of a <c>result.message</c> envelope fixture.</summary>
    public static WorkIqMessage MessageFromEnvelope(string fixture)
    {
        using var document = JsonDocument.Parse(FixtureLoader.Text(fixture));
        return MessageOf(document.RootElement.GetProperty("result").GetProperty("message"));
    }

    public static WorkIqTaskSnapshot SnapshotFromTaskElement(JsonElement task, string captureId = CaptureId)
    {
        var status = task.GetProperty("status");
        var rawState = status.GetProperty("state").GetString();
        DateTimeOffset? timestamp = status.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            ? ts.GetDateTimeOffset()
            : null;
        var statusMessage = status.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? MessageOf(m) : null;
        var artifacts = task.TryGetProperty("artifacts", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(ArtifactOf).ToImmutableArray()
            : ImmutableArray<WorkIqArtifact>.Empty;
        var history = task.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Select(MessageOf).ToImmutableArray()
            : ImmutableArray<WorkIqMessage>.Empty;
        var taskId = task.GetProperty("id").GetString()!;
        var contextId = task.TryGetProperty("contextId", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return new WorkIqTaskSnapshot(
            Handle(taskId, contextId),
            StateOf(rawState),
            rawState,
            timestamp,
            statusMessage,
            artifacts,
            history,
            WorkIqSnapshotSource.Send,
            ObservedAt,
            ImmutableArray<string>.Empty,
            Capture(captureId));
    }

    public static WorkIqMessage MessageOf(JsonElement message)
    {
        JsonElement? metadata = message.TryGetProperty("metadata", out var md) ? md : null;
        return new WorkIqMessage(
            message.GetProperty("messageId").GetString()!,
            message.GetProperty("role").GetString()!,
            PartsOf(message),
            metadata,
            OptionalString(message, "contextId"),
            OptionalString(message, "taskId"));
    }

    public static WorkIqArtifact ArtifactOf(JsonElement artifact)
    {
        JsonElement? metadata = artifact.TryGetProperty("metadata", out var md) ? md : null;
        return new WorkIqArtifact(
            artifact.GetProperty("artifactId").GetString()!,
            OptionalString(artifact, "name"),
            OptionalString(artifact, "description"),
            PartsOf(artifact),
            0,
            false,
            metadata);
    }

    public static WorkIqPart PartOf(JsonElement part)
    {
        var mediaType = OptionalString(part, "mediaType");
        var filename = OptionalString(part, "filename");
        JsonElement? metadata = part.TryGetProperty("metadata", out var md) ? md : null;
        if (part.TryGetProperty("text", out var text))
        {
            return new WorkIqPart(WorkIqPartKind.Text, text: text.GetString(), mediaType: mediaType, filename: filename, metadata: metadata);
        }

        if (part.TryGetProperty("data", out var data))
        {
            return new WorkIqPart(WorkIqPartKind.Data, data: data, mediaType: mediaType, filename: filename, metadata: metadata);
        }

        if (part.TryGetProperty("url", out var url))
        {
            return new WorkIqPart(WorkIqPartKind.File, url: url.GetString(), mediaType: mediaType, filename: filename, metadata: metadata);
        }

        if (part.TryGetProperty("raw", out var raw))
        {
            return new WorkIqPart(WorkIqPartKind.File, rawBytes: raw.GetBytesFromBase64().ToImmutableArray(), mediaType: mediaType, filename: filename, metadata: metadata);
        }

        return new WorkIqPart(WorkIqPartKind.Unknown, data: part, mediaType: mediaType, filename: filename, metadata: metadata);
    }

    private static ImmutableArray<WorkIqPart> PartsOf(JsonElement container) =>
        container.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? parts.EnumerateArray().Select(PartOf).ToImmutableArray()
            : ImmutableArray<WorkIqPart>.Empty;

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>An invariant-culture pointer segment helper for expectations.</summary>
    public static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}

/// <summary>
/// A test stand-in for lane A's <see cref="WorkIqAnswerSelector"/> implementing the documented M21 policy: a
/// direct message's text parts; for a completed task the artifact named <c>Answer</c> (else the single text
/// artifact), diagnostic artifacts excluded by media type and name; several candidates → Ambiguous with every
/// candidate retained; text chunks concatenated without inserted newlines; no text part → Empty with a null
/// text; an empty concatenation → Empty with <c>""</c>; a task that is not completed → None.
/// </summary>
internal static class FakeAnswerSelector
{
    public static WorkIqAnswerSelection SelectFromTask(WorkIqTaskSnapshot task, string captureId)
    {
        if (task.State != WorkIqTaskState.Completed)
        {
            return new WorkIqAnswerSelection(null, OutputCompleteness.None, ImmutableArray<string>.Empty, ImmutableArray<SourceRef>.Empty);
        }

        var candidates = new List<(int Index, WorkIqArtifact Artifact)>();
        for (var i = 0; i < task.Artifacts.Length; i++)
        {
            var artifact = task.Artifacts[i];
            if (IsDiagnostic(artifact))
            {
                continue;
            }

            if (string.Equals(artifact.Name, "Answer", StringComparison.Ordinal) || artifact.Parts.Any(p => p.Kind == WorkIqPartKind.Text))
            {
                candidates.Add((i, artifact));
            }
        }

        var named = candidates.Where(c => string.Equals(c.Artifact.Name, "Answer", StringComparison.Ordinal)).ToList();
        var chosen = named.Count > 0 ? named : candidates;
        if (chosen.Count == 0)
        {
            return new WorkIqAnswerSelection(null, OutputCompleteness.None, ImmutableArray<string>.Empty, ImmutableArray<SourceRef>.Empty);
        }

        if (chosen.Count > 1)
        {
            return new WorkIqAnswerSelection(
                null,
                OutputCompleteness.Ambiguous,
                chosen.Select(c => c.Artifact.ArtifactId).ToImmutableArray(),
                chosen.Select(c => new SourceRef(captureId, 0, EvidenceOrigin.A2AArtifact, Pointer("/artifacts", c.Index), c.Artifact.ArtifactId, c.Artifact.Revision)).ToImmutableArray());
        }

        var (index, answer) = chosen[0];
        var sources = ImmutableArray.CreateBuilder<SourceRef>();
        var text = new StringBuilder();
        var textParts = 0;
        for (var j = 0; j < answer.Parts.Length; j++)
        {
            var part = answer.Parts[j];
            if (part.Kind != WorkIqPartKind.Text)
            {
                continue;
            }

            textParts++;
            text.Append(part.Text);
            sources.Add(new SourceRef(captureId, 0, EvidenceOrigin.A2AArtifact, Pointer(Pointer(Pointer("/artifacts", index), "parts"), j) + "/text", answer.ArtifactId, answer.Revision, j));
        }

        var ids = ImmutableArray.Create(answer.ArtifactId);
        if (textParts == 0)
        {
            var partsSource = new SourceRef(captureId, 0, EvidenceOrigin.A2AArtifact, Pointer(Pointer("/artifacts", index), "parts"), answer.ArtifactId, answer.Revision);
            return new WorkIqAnswerSelection(null, OutputCompleteness.Empty, ids, ImmutableArray.Create(partsSource));
        }

        var raw = text.ToString();
        return new WorkIqAnswerSelection(raw, raw.Length == 0 ? OutputCompleteness.Empty : OutputCompleteness.Complete, ids, sources.ToImmutable());
    }

    public static WorkIqAnswerSelection SelectFromMessage(WorkIqMessage message, string captureId)
    {
        var sources = ImmutableArray.CreateBuilder<SourceRef>();
        var text = new StringBuilder();
        var textParts = 0;
        for (var j = 0; j < message.Parts.Length; j++)
        {
            var part = message.Parts[j];
            if (part.Kind != WorkIqPartKind.Text)
            {
                continue;
            }

            textParts++;
            text.Append(part.Text);
            sources.Add(new SourceRef(captureId, 0, EvidenceOrigin.A2AMessage, Pointer("/parts", j) + "/text", null, null, j));
        }

        if (textParts == 0)
        {
            return new WorkIqAnswerSelection(null, OutputCompleteness.Empty, ImmutableArray<string>.Empty, ImmutableArray.Create(new SourceRef(captureId, 0, EvidenceOrigin.A2AMessage, "/parts")));
        }

        var raw = text.ToString();
        return new WorkIqAnswerSelection(raw, raw.Length == 0 ? OutputCompleteness.Empty : OutputCompleteness.Complete, ImmutableArray<string>.Empty, sources.ToImmutable());
    }

    private static bool IsDiagnostic(WorkIqArtifact artifact) =>
        string.Equals(artifact.Name, Native118DiagnosticsDecoder.DeveloperInfoName, StringComparison.Ordinal)
        || string.Equals(artifact.Name, Native118DiagnosticsDecoder.RetrievalInfoName, StringComparison.Ordinal)
        || artifact.Parts.Any(p => p.MediaType is not null
            && (string.Equals(p.MediaType, Native118DiagnosticsDecoder.DeveloperInfoMediaType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.MediaType, Native118DiagnosticsDecoder.RetrievalInfoMediaType, StringComparison.OrdinalIgnoreCase)));

    private static string Pointer(string parent, string token) => parent + "/" + token;

    private static string Pointer(string parent, int index) => parent + "/" + index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The JSON subset matcher of the fixture host (the research's <c>assert_subset</c>): objects by key (every
/// expected key must exist and match; extra actual keys are fine), arrays by length and position, scalars by
/// kind and value (enums compare as their member-name strings; <c>null</c> matches only <c>null</c>).
/// </summary>
internal static class JsonSubset
{
    public static void AssertSubset(JsonElement expected, JsonElement actual, string path = "$")
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.True(actual.ValueKind == JsonValueKind.Object, $"{path}: expected an object, found {actual.ValueKind} ({actual.GetRawText()})");
                foreach (var property in expected.EnumerateObject())
                {
                    Assert.True(actual.TryGetProperty(property.Name, out var child), $"{path}.{property.Name}: missing in the actual projection {actual.GetRawText()}");
                    AssertSubset(property.Value, child, $"{path}.{property.Name}");
                }

                break;
            case JsonValueKind.Array:
                Assert.True(actual.ValueKind == JsonValueKind.Array, $"{path}: expected an array, found {actual.ValueKind} ({actual.GetRawText()})");
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                Assert.True(expectedItems.Length == actualItems.Length, $"{path}: expected {expectedItems.Length} element(s), found {actualItems.Length}: {actual.GetRawText()}");
                for (var i = 0; i < expectedItems.Length; i++)
                {
                    AssertSubset(expectedItems[i], actualItems[i], $"{path}[{i}]");
                }

                break;
            case JsonValueKind.String:
                Assert.True(actual.ValueKind == JsonValueKind.String, $"{path}: expected the string {expected.GetRawText()}, found {actual.ValueKind} ({actual.GetRawText()})");
                Assert.True(string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal), $"{path}: expected {expected.GetRawText()}, found {actual.GetRawText()}");
                break;
            case JsonValueKind.Number:
                Assert.True(actual.ValueKind == JsonValueKind.Number, $"{path}: expected the number {expected.GetRawText()}, found {actual.ValueKind} ({actual.GetRawText()})");
                Assert.True(expected.GetDouble() == actual.GetDouble(), $"{path}: expected {expected.GetRawText()}, found {actual.GetRawText()}");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.True(actual.ValueKind == expected.ValueKind, $"{path}: expected {expected.GetRawText()}, found {actual.ValueKind} ({actual.GetRawText()})");
                break;
            case JsonValueKind.Null:
                Assert.True(actual.ValueKind == JsonValueKind.Null, $"{path}: expected null, found {actual.ValueKind} ({actual.GetRawText()})");
                break;
            default:
                Assert.Fail($"{path}: unsupported expected kind {expected.ValueKind}");
                break;
        }
    }
}
