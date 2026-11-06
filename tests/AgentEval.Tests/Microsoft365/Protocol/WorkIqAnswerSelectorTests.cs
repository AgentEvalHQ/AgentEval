// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Protocol;

/// <summary><see cref="WorkIqAnswerSelector"/>: the deterministic answer policy over hand-built snapshots and messages.</summary>
public sealed class WorkIqAnswerSelectorTests
{
    private const string CaptureId = "send-0001";

    private static readonly WorkIqTaskHandle Handle = new(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, "task-1", "ctx-1", null, "principal-1");

    private static WorkIqArtifact Artifact(string id, string? name, params WorkIqPart[] parts) =>
        new(id, name, null, parts.ToImmutableArray(), 0, false);

    private static WorkIqPart Text(string text) => new(WorkIqPartKind.Text, text: text);

    private static WorkIqPart Data(string mediaType) =>
        new(WorkIqPartKind.Data, data: JsonSerializer.SerializeToElement(new { plugins = new { } }), mediaType: mediaType);

    private static WorkIqTaskSnapshot Snapshot(WorkIqSnapshotSource source = WorkIqSnapshotSource.Send, params WorkIqArtifact[] artifacts) =>
        new(Handle, WorkIqTaskState.Completed, "TASK_STATE_COMPLETED", null, null, artifacts.ToImmutableArray(), ImmutableArray<WorkIqMessage>.Empty, source, DateTimeOffset.UnixEpoch, ImmutableArray<string>.Empty, null);

    [Fact]
    public void SelectFromMessage_TextParts_ConcatenatedWithoutInsertedNewline()
    {
        var message = new WorkIqMessage("msg-1", "ROLE_AGENT", [Text("Project Atlas is on schedule."), Text(" Two risks stay open.")]);

        var selection = WorkIqAnswerSelector.SelectFromMessage(message, CaptureId);

        Assert.Equal("Project Atlas is on schedule. Two risks stay open.", selection.RawText);
        Assert.Equal(OutputCompleteness.Complete, selection.Completeness);
        Assert.Empty(selection.CandidateArtifactIds);
        Assert.Equal(2, selection.Sources.Length);
        Assert.Equal("/parts/0/text", selection.Sources[0].JsonPointer);
        Assert.Equal("/parts/1/text", selection.Sources[1].JsonPointer);
        Assert.All(selection.Sources, s => Assert.Equal(EvidenceOrigin.A2AMessage, s.Origin));
        Assert.Equal(1, selection.Sources[1].PartIndex);
    }

    [Fact]
    public void SelectFromMessage_NoTextParts_Empty()
    {
        var message = new WorkIqMessage("msg-1", "ROLE_AGENT", [Data("application/json")]);

        var selection = WorkIqAnswerSelector.SelectFromMessage(message, CaptureId);

        Assert.Equal(OutputCompleteness.Empty, selection.Completeness);
        Assert.Equal("", selection.RawText);
        Assert.Empty(selection.Sources);
    }

    [Fact]
    public void SelectFromTask_AnswerPlusDiagnostics_OnlyAnswerText()
    {
        var snapshot = Snapshot(
            WorkIqSnapshotSource.Send,
            Artifact("a-answer", "Answer", Text("The pilot starts on 17 November."), Text(" Bring the plan.")),
            Artifact("a-dev", "Developer Info", Data(WorkIqA2AClient.DeveloperInfoMediaType)),
            Artifact("a-ret", "Retrieval Info", Data(WorkIqA2AClient.RetrievalInfoMediaType)));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal(OutputCompleteness.Complete, selection.Completeness);
        Assert.Equal("The pilot starts on 17 November. Bring the plan.", selection.RawText);
        Assert.Equal(new[] { "a-answer" }, selection.CandidateArtifactIds);
        Assert.Equal(2, selection.Sources.Length);
        Assert.Equal("/artifacts/0/parts/1/text", selection.Sources[1].JsonPointer);
        Assert.Equal("a-answer", selection.Sources[1].ArtifactId);
        Assert.Equal(EvidenceOrigin.A2AArtifact, selection.Sources[1].Origin);
    }

    [Fact]
    public void SelectFromTask_DiagnosticByMediaTypeWithoutName_Excluded()
    {
        var snapshot = Snapshot(
            WorkIqSnapshotSource.Send,
            Artifact("a-dev", null, Data(WorkIqA2AClient.DeveloperInfoMediaType)),
            Artifact("a-text", null, Text("only text")));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal("only text", selection.RawText);
        Assert.Equal(new[] { "a-text" }, selection.CandidateArtifactIds);
    }

    [Fact]
    public void SelectFromTask_TwoAnswerArtifacts_AmbiguousKeepsAllCandidates()
    {
        var snapshot = Snapshot(
            WorkIqSnapshotSource.Send,
            Artifact("a-1", "Answer", Text("17 November")),
            Artifact("a-2", "Answer", Text("24 November")));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal(OutputCompleteness.Ambiguous, selection.Completeness);
        Assert.Null(selection.RawText);
        Assert.Equal(new[] { "a-1", "a-2" }, selection.CandidateArtifactIds);
        Assert.Empty(selection.Sources);
    }

    [Fact]
    public void SelectFromTask_AnswerAndFollowUps_PrefersAnswer()
    {
        var snapshot = Snapshot(
            WorkIqSnapshotSource.Send,
            Artifact("a-follow", "Follow-up questions", Text("Want the risks?")),
            Artifact("a-answer", "Answer", Text("On schedule.")));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal(OutputCompleteness.Complete, selection.Completeness);
        Assert.Equal("On schedule.", selection.RawText);
        Assert.Equal(new[] { "a-follow", "a-answer" }, selection.CandidateArtifactIds);
        Assert.Equal("/artifacts/1/parts/0/text", selection.Sources[0].JsonPointer);
    }

    [Fact]
    public void SelectFromTask_TwoUnnamedTextArtifacts_Ambiguous()
    {
        var snapshot = Snapshot(WorkIqSnapshotSource.Send, Artifact("a-1", null, Text("x")), Artifact("a-2", null, Text("y")));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal(OutputCompleteness.Ambiguous, selection.Completeness);
    }

    [Fact]
    public void SelectFromTask_EmptyAnswerParts_EmptyNotFailure()
    {
        var snapshot = Snapshot(WorkIqSnapshotSource.Send, Artifact("a-answer", "Answer"));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal(OutputCompleteness.Empty, selection.Completeness);
        Assert.Equal("", selection.RawText);
        Assert.Equal(new[] { "a-answer" }, selection.CandidateArtifactIds);
    }

    [Fact]
    public void SelectFromTask_NoCandidates_None()
    {
        var snapshot = Snapshot(WorkIqSnapshotSource.Send, Artifact("a-dev", "Developer Info", Data(WorkIqA2AClient.DeveloperInfoMediaType)));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal(OutputCompleteness.None, selection.Completeness);
        Assert.Null(selection.RawText);
        Assert.Empty(selection.CandidateArtifactIds);
    }

    [Fact]
    public void SelectFromTask_FilePartsAroundText_OnlyTextAssembled()
    {
        var snapshot = Snapshot(
            WorkIqSnapshotSource.Send,
            Artifact(
                "a-answer",
                "Answer",
                Text("Here is the plan."),
                new WorkIqPart(WorkIqPartKind.File, mediaType: "application/pdf", filename: "plan.pdf", url: "https://sharepoint.example.invalid/plan.pdf"),
                new WorkIqPart(WorkIqPartKind.File, mediaType: "text/plain", filename: "k.txt", rawBytes: [0x41])));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal("Here is the plan.", selection.RawText);
        Assert.Single(selection.Sources);
        Assert.Equal(0, selection.Sources[0].PartIndex);
    }

    [Fact]
    public void SelectFromTask_GetTaskSource_PointerIsTaskRelative()
    {
        var snapshot = Snapshot(WorkIqSnapshotSource.GetTask, Artifact("a-answer", "Answer", Text("x")));

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal("/artifacts/0/parts/0/text", selection.Sources[0].JsonPointer);
        Assert.Equal(CaptureId, selection.Sources[0].CaptureId);
    }

    [Fact]
    public void SelectFromTask_StatusMessageText_NeverMerged()
    {
        var status = new WorkIqMessage("msg-status", "ROLE_AGENT", [Text("Searching...")]);
        var snapshot = Snapshot(WorkIqSnapshotSource.Send, Artifact("a-answer", "Answer", Text("Done."))) with { StatusMessage = status };

        var selection = WorkIqAnswerSelector.SelectFromTask(snapshot, CaptureId);

        Assert.Equal("Done.", selection.RawText);
    }

    [Theory]
    [InlineData("Developer Info", true)]
    [InlineData(" retrieval info ", true)]
    [InlineData("Answer", false)]
    [InlineData(null, false)]
    public void IsDiagnosticName_CaseInsensitiveTrimmed(string? name, bool expected)
    {
        Assert.Equal(expected, WorkIqAnswerSelector.IsDiagnosticName(name));
    }

    [Fact]
    public void SelectFromTask_BlankCaptureId_Throws()
    {
        Assert.Throws<ArgumentException>(() => WorkIqAnswerSelector.SelectFromTask(Snapshot(), " "));
    }
}
