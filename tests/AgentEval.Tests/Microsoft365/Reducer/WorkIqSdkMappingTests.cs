// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Reducer;

/// <summary>
/// <see cref="WorkIqSdkMapping"/>: SDK → model, with the wire names taken from the SDK's own serializer, every
/// part kind preserved, every JSON element detached, and conformance deviations reported instead of repaired.
/// </summary>
public sealed class WorkIqSdkMappingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 14, 6, 13, TimeSpan.Zero);

    public static TheoryData<TaskState, WorkIqTaskState> StateMap => new()
    {
        { TaskState.Submitted, WorkIqTaskState.Submitted },
        { TaskState.Working, WorkIqTaskState.Working },
        { TaskState.Completed, WorkIqTaskState.Completed },
        { TaskState.Failed, WorkIqTaskState.Failed },
        { TaskState.Canceled, WorkIqTaskState.Canceled },
        { TaskState.Rejected, WorkIqTaskState.Rejected },
        { TaskState.InputRequired, WorkIqTaskState.InputRequired },
        { TaskState.AuthRequired, WorkIqTaskState.AuthRequired },
        { TaskState.Unspecified, WorkIqTaskState.Unknown },
        { (TaskState)99, WorkIqTaskState.Unknown },
    };

    // ---------------------------------------------------------------------------------------------------------
    // Enums
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(StateMap))]
    public void ToTaskState_EveryValue_ClassifiesAsDocumented(TaskState state, WorkIqTaskState expected)
    {
        Assert.Equal(expected, WorkIqSdkMapping.ToTaskState(state));
    }

    [Fact]
    public void ToRawState_DefinedValues_AreTheSdkWireNames()
    {
        Assert.Equal("TASK_STATE_COMPLETED", WorkIqSdkMapping.ToRawState(TaskState.Completed));
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", WorkIqSdkMapping.ToRawState(TaskState.InputRequired));
        Assert.Equal("TASK_STATE_UNSPECIFIED", WorkIqSdkMapping.ToRawState(TaskState.Unspecified));
        Assert.All(Enum.GetValues<TaskState>(), s => Assert.StartsWith("TASK_STATE_", WorkIqSdkMapping.ToRawState(s), StringComparison.Ordinal));
        Assert.Null(WorkIqSdkMapping.ToRawState((TaskState)99));
    }

    [Fact]
    public void ToRawState_Always_RoundTripsThroughTheSdkSerializer()
    {
        foreach (var state in Enum.GetValues<TaskState>())
        {
            var raw = WorkIqSdkMapping.ToRawState(state);
            var back = JsonSerializer.Deserialize<TaskState>($"\"{raw}\"", A2AJsonUtilities.DefaultOptions);
            Assert.Equal(state, back);
        }
    }

    [Fact]
    public void ToRawRole_Always_IsTheSdkWireNameOrTheNumber()
    {
        Assert.Equal("ROLE_USER", WorkIqSdkMapping.ToRawRole(Role.User));
        Assert.Equal("ROLE_AGENT", WorkIqSdkMapping.ToRawRole(Role.Agent));
        Assert.Equal("ROLE_UNSPECIFIED", WorkIqSdkMapping.ToRawRole(Role.Unspecified));
        Assert.Equal("7", WorkIqSdkMapping.ToRawRole((Role)7));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Parts
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToPart_Text_IsATextPart()
    {
        var part = WorkIqSdkMapping.ToPart(Part.FromText("hello"));

        Assert.Equal(WorkIqPartKind.Text, part.Kind);
        Assert.Equal("hello", part.Text);
        Assert.Null(part.Data);
        Assert.Null(part.Url);
        Assert.True(part.RawBytes.IsEmpty);
    }

    [Fact]
    public void ToPart_Url_IsAFilePartByReference()
    {
        var part = WorkIqSdkMapping.ToPart(Part.FromUrl("https://sharepoint.example.invalid/x.pdf", "application/pdf", "x.pdf"));

        Assert.Equal(WorkIqPartKind.File, part.Kind);
        Assert.Equal("https://sharepoint.example.invalid/x.pdf", part.Url);
        Assert.Equal("application/pdf", part.MediaType);
        Assert.Equal("x.pdf", part.Filename);
        Assert.True(part.RawBytes.IsEmpty);
        Assert.Null(part.Text);
    }

    [Fact]
    public void ToPart_Raw_IsAFilePartByValueWithCopiedBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var part = WorkIqSdkMapping.ToPart(Part.FromRaw(bytes, "application/octet-stream", "b.bin"));
        bytes[0] = 9;

        Assert.Equal(WorkIqPartKind.File, part.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, part.RawBytes.ToArray());
        Assert.Equal("application/octet-stream", part.MediaType);
        Assert.Equal("b.bin", part.Filename);
        Assert.Null(part.Url);
    }

    [Fact]
    public void ToPart_Data_IsADataPartDetachedFromItsSourceDocument()
    {
        WorkIqPart part;
        using (var document = JsonDocument.Parse("""{"data":{"k":"v","n":[1,2]},"mediaType":"application/json"}"""))
        {
            var sdk = document.RootElement.Deserialize<Part>(A2AJsonUtilities.DefaultOptions)!;
            part = WorkIqSdkMapping.ToPart(sdk);
        }

        Assert.Equal(WorkIqPartKind.Data, part.Kind);
        Assert.Equal("application/json", part.MediaType);
        Assert.Equal("v", part.Data!.Value.GetProperty("k").GetString());
        Assert.Equal(2, part.Data.Value.GetProperty("n").GetArrayLength());
    }

    [Fact]
    public void ToPart_NoContent_IsUnknownWithWarning()
    {
        var warnings = new List<string>();

        var part = WorkIqSdkMapping.ToPart(new Part { MediaType = "x/y" }, warnings);

        Assert.Equal(WorkIqPartKind.Unknown, part.Kind);
        Assert.Equal("x/y", part.MediaType);
        Assert.NotNull(part.Data);
        Assert.Equal(JsonValueKind.Object, part.Data.Value.ValueKind);
        Assert.Contains(warnings, w => w.StartsWith("part.no_content:", StringComparison.Ordinal));
    }

    [Fact]
    public void ToPart_TwoContentFields_UsesTheSdkCaseAndWarns()
    {
        var warnings = new List<string>();

        var part = WorkIqSdkMapping.ToPart(new Part { Text = "t", Url = "https://example.invalid/u" }, warnings);

        Assert.Equal(WorkIqPartKind.Text, part.Kind);
        Assert.Equal("t", part.Text);
        Assert.Contains(warnings, w => w.StartsWith("part.one_of_violation:", StringComparison.Ordinal));
    }

    [Fact]
    public void ToPart_Metadata_IsOneDetachedObject()
    {
        var sdk = Part.FromText("t");
        sdk.Metadata = new Dictionary<string, JsonElement>
        {
            ["origin"] = JsonDocument.Parse("\"turn0search1\"").RootElement.Clone(),
        };

        var part = WorkIqSdkMapping.ToPart(sdk);

        Assert.NotNull(part.Metadata);
        Assert.Equal("turn0search1", part.Metadata.Value.GetProperty("origin").GetString());
    }

    [Fact]
    public void ToPart_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WorkIqSdkMapping.ToPart(null!));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Messages and artifacts
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToMessage_FromTheDirectMessageEnvelope_KeepsPartsMetadataAndIds()
    {
        using var document = JsonDocument.Parse(FixtureLoader.Bytes("envelopes/send-direct-message.json"));
        var sdk = document.RootElement.GetProperty("result").GetProperty("message").Deserialize<Message>(A2AJsonUtilities.DefaultOptions)!;
        var warnings = new List<string>();

        var message = WorkIqSdkMapping.ToMessage(sdk, warnings);

        Assert.Equal(sdk.MessageId, message.MessageId);
        Assert.Equal("ROLE_AGENT", message.Role);
        Assert.Single(message.Parts);
        Assert.Equal(WorkIqPartKind.Text, message.Parts[0].Kind);
        Assert.Contains("cite", message.Parts[0].Text, StringComparison.Ordinal);
        Assert.NotNull(message.Metadata);
        Assert.Equal(JsonValueKind.Array, message.Metadata.Value.GetProperty("attributions").ValueKind);
        Assert.Equal(SseFixtureFrames.ContextId, message.ContextId);
        Assert.Null(message.TaskId);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ToMessage_UnspecifiedRoleAndBlankId_Warns()
    {
        var warnings = new List<string>();

        var message = WorkIqSdkMapping.ToMessage(new Message { MessageId = "", Role = Role.Unspecified }, warnings);

        Assert.Equal("", message.MessageId);
        Assert.Equal("ROLE_UNSPECIFIED", message.Role);
        Assert.Empty(message.Parts);
        Assert.Contains(warnings, w => w.StartsWith("message.id_missing:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("message.role_unspecified:", StringComparison.Ordinal));
    }

    [Fact]
    public void ToArtifact_Always_StampsRevisionAndIsNeverClosed()
    {
        var sdk = new Artifact { ArtifactId = "a", Name = "Answer", Description = "d", Parts = [Part.FromText("x"), Part.FromText("y")] };

        var artifact = WorkIqSdkMapping.ToArtifact(sdk, 3);

        Assert.Equal("a", artifact.ArtifactId);
        Assert.Equal("Answer", artifact.Name);
        Assert.Equal("d", artifact.Description);
        Assert.Equal(2, artifact.Parts.Length);
        Assert.Equal(3, artifact.Revision);
        Assert.False(artifact.IsClosed);
        Assert.Null(artifact.Metadata);
    }

    [Fact]
    public void ToArtifact_BlankId_Warns()
    {
        var warnings = new List<string>();

        var artifact = WorkIqSdkMapping.ToArtifact(new Artifact { ArtifactId = "" }, 0, warnings);

        Assert.Equal("", artifact.ArtifactId);
        Assert.Contains(warnings, w => w.StartsWith("artifact.id_missing:", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Snapshots
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToSnapshot_FromTheFilePartsEnvelope_PreservesTextUrlAndRawParts()
    {
        var task = ReadTask("envelopes/send-task-completed-with-file-parts.json");
        var handle = SseFixtureFrames.Template(task.Id, task.ContextId);

        var snapshot = WorkIqSdkMapping.ToSnapshot(task, handle, WorkIqSnapshotSource.Send, At, 0);

        Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
        Assert.Equal("TASK_STATE_COMPLETED", snapshot.RawState);
        Assert.Equal(WorkIqSnapshotSource.Send, snapshot.Source);
        Assert.Equal(At, snapshot.ObservedAt);
        Assert.Same(handle, snapshot.Handle);
        Assert.Null(snapshot.Capture);
        Assert.Empty(snapshot.SchemaWarnings);

        var artifact = Assert.Single(snapshot.Artifacts);
        Assert.Equal(0, artifact.Revision);
        Assert.False(artifact.IsClosed);
        Assert.Equal(3, artifact.Parts.Length);
        Assert.Equal(WorkIqPartKind.Text, artifact.Parts[0].Kind);
        Assert.Equal(WorkIqPartKind.File, artifact.Parts[1].Kind);
        Assert.Equal("https://sharepoint.example.invalid/sites/ProjectAtlas/Shared%20Documents/Pilot%20plan.pdf", artifact.Parts[1].Url);
        Assert.Equal("Pilot plan.pdf", artifact.Parts[1].Filename);
        Assert.Equal(WorkIqPartKind.File, artifact.Parts[2].Kind);
        Assert.Null(artifact.Parts[2].Url);
        Assert.Equal("text/plain", artifact.Parts[2].MediaType);
        Assert.Equal(Convert.FromBase64String("UGlsb3Qga2lja29mZjogMjAyNi0xMS0xNyAwOTowMCBFdXJvcGUvWnVyaWNoCg=="), artifact.Parts[2].RawBytes.ToArray());

        Assert.NotNull(snapshot.StatusMessage);
        Assert.Empty(snapshot.StatusMessage.Parts);
        Assert.Equal(1, SseFixtureFrames.AttributionCount(snapshot.StatusMessage));
        Assert.Single(snapshot.History);
    }

    [Fact]
    public void ToSnapshot_HandleWithoutIds_IsFilledFromTheTask()
    {
        var task = ReadTask("envelopes/gettask-completed.json");

        var snapshot = WorkIqSdkMapping.ToSnapshot(task, SseFixtureFrames.Template(), WorkIqSnapshotSource.GetTask, At, 0);

        Assert.Equal(task.Id, snapshot.Handle.TaskId);
        Assert.Equal(task.ContextId, snapshot.Handle.ContextId);
        Assert.Equal(SseFixtureFrames.Endpoint, snapshot.Handle.Endpoint);
        Assert.Equal("principal-test", snapshot.Handle.PrincipalKey);
        Assert.Empty(snapshot.SchemaWarnings);
    }

    [Fact]
    public void ToSnapshot_HandleIdMismatch_WarnsAndTheTaskIdWins()
    {
        var task = ReadTask("envelopes/gettask-completed.json");

        var snapshot = WorkIqSdkMapping.ToSnapshot(task, SseFixtureFrames.Template("task-expected", "ctx-expected"), WorkIqSnapshotSource.GetTask, At, 0);

        Assert.Equal(task.Id, snapshot.Handle.TaskId);
        Assert.Equal(task.ContextId, snapshot.Handle.ContextId);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("task.id_mismatch:", StringComparison.Ordinal));
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("task.context_id_mismatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void ToSnapshot_UpstreamWarningCount_IsRecordedOnceAndOnlyWhenPositive()
    {
        var task = ReadTask("envelopes/send-task-submitted.json");
        var handle = SseFixtureFrames.Template(task.Id, task.ContextId);

        var none = WorkIqSdkMapping.ToSnapshot(task, handle, WorkIqSnapshotSource.Send, At, 0);
        var three = WorkIqSdkMapping.ToSnapshot(task, handle, WorkIqSnapshotSource.Send, At, 3);

        Assert.Empty(none.SchemaWarnings);
        var entry = Assert.Single(three.SchemaWarnings);
        Assert.StartsWith("upstream.schema_warnings: 3 ", entry, StringComparison.Ordinal);
        Assert.Equal(WorkIqTaskState.Submitted, three.State);
        Assert.Empty(three.Artifacts);
    }

    [Fact]
    public void ToSnapshot_UnspecifiedState_IsUnknownWithWarningAndNeverTerminal()
    {
        var task = new AgentTask { Id = "t", ContextId = "c", Status = new A2A.TaskStatus { State = TaskState.Unspecified } };

        var snapshot = WorkIqSdkMapping.ToSnapshot(task, SseFixtureFrames.Template(), WorkIqSnapshotSource.Send, At, 0);

        Assert.Equal(WorkIqTaskState.Unknown, snapshot.State);
        Assert.Equal("TASK_STATE_UNSPECIFIED", snapshot.RawState);
        Assert.False(snapshot.IsTerminal);
        Assert.False(snapshot.IsSuspended);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("task.state_unspecified:", StringComparison.Ordinal));
    }

    [Fact]
    public void ToSnapshot_DuplicateArtifactId_KeepsEveryOccurrenceAndWarns()
    {
        var task = new AgentTask
        {
            Id = "t",
            ContextId = "c",
            Status = new A2A.TaskStatus { State = TaskState.Completed },
            Artifacts = [new Artifact { ArtifactId = "a", Parts = [Part.FromText("1")] }, new Artifact { ArtifactId = "a", Parts = [Part.FromText("2")] }],
        };

        var snapshot = WorkIqSdkMapping.ToSnapshot(task, SseFixtureFrames.Template(), WorkIqSnapshotSource.Send, At, 0);

        Assert.Equal(2, snapshot.Artifacts.Length);
        Assert.Contains(snapshot.SchemaWarnings, w => w.StartsWith("artifact.duplicate_id_in_snapshot:", StringComparison.Ordinal));
    }

    [Fact]
    public void ToSnapshot_StatusMessageAndHistory_AreDetachedFromTheSourceDocument()
    {
        WorkIqTaskSnapshot snapshot;
        using (var document = JsonDocument.Parse(FixtureLoader.Bytes("envelopes/send-task-completed-with-answer-and-attributions.json")))
        {
            var task = document.RootElement.GetProperty("result").GetProperty("task").Deserialize<AgentTask>(A2AJsonUtilities.DefaultOptions)!;
            snapshot = WorkIqSdkMapping.ToSnapshot(task, SseFixtureFrames.Template(), WorkIqSnapshotSource.Send, At, 0);
        }

        Assert.Equal(2, SseFixtureFrames.AttributionCount(snapshot.StatusMessage));
        var history = Assert.Single(snapshot.History);
        Assert.NotNull(history.Metadata);
        Assert.Equal("Europe/Zurich", history.Metadata.Value.GetProperty("Location").GetProperty("timeZone").GetString());
    }

    [Fact]
    public void ToSnapshot_NullArguments_Throw()
    {
        var task = new AgentTask { Id = "t", ContextId = "c" };

        Assert.Throws<ArgumentNullException>(() => WorkIqSdkMapping.ToSnapshot(null!, SseFixtureFrames.Template(), WorkIqSnapshotSource.Send, At, 0));
        Assert.Throws<ArgumentNullException>(() => WorkIqSdkMapping.ToSnapshot(task, null!, WorkIqSnapshotSource.Send, At, 0));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Metadata and handles
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToMetadata_Null_IsNull()
    {
        Assert.Null(WorkIqSdkMapping.ToMetadata(null));
    }

    [Fact]
    public void ToMetadata_Dictionary_IsOneObjectWithEveryKey()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["a"] = JsonDocument.Parse("1").RootElement.Clone(),
            ["b"] = JsonDocument.Parse("""{"x":true}""").RootElement.Clone(),
        };

        var element = WorkIqSdkMapping.ToMetadata(metadata);

        Assert.NotNull(element);
        Assert.Equal(1, element.Value.GetProperty("a").GetInt32());
        Assert.True(element.Value.GetProperty("b").GetProperty("x").GetBoolean());
    }

    [Fact]
    public void ToMetadata_UndefinedValue_IsDroppedWithWarning()
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, JsonElement> { ["ok"] = JsonDocument.Parse("1").RootElement.Clone(), ["bad"] = default };

        var element = WorkIqSdkMapping.ToMetadata(metadata, warnings);

        Assert.NotNull(element);
        Assert.True(element.Value.TryGetProperty("ok", out _));
        Assert.False(element.Value.TryGetProperty("bad", out _));
        Assert.Contains(warnings, w => w.StartsWith("metadata.undefined_value:", StringComparison.Ordinal));
    }

    [Fact]
    public void ReconcileHandle_BlankWireTaskId_WarnsAndKeepsTheHandle()
    {
        var warnings = new List<string>();
        var handle = SseFixtureFrames.Template("task-known", "ctx-known");

        var result = WorkIqSdkMapping.ReconcileHandle(handle, "", null, warnings, "status");

        Assert.Same(handle, result);
        Assert.Contains(warnings, w => w.StartsWith("status.id_missing:", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("context_id_missing", StringComparison.Ordinal));
    }

    private static AgentTask ReadTask(string envelope)
    {
        using var document = JsonDocument.Parse(FixtureLoader.Bytes(envelope));
        var result = document.RootElement.GetProperty("result");
        var element = result.TryGetProperty("task", out var nested) ? nested : result;
        return element.Deserialize<AgentTask>(A2AJsonUtilities.DefaultOptions)
               ?? throw new InvalidOperationException($"{envelope}: the task deserialized as null.");
    }
}
