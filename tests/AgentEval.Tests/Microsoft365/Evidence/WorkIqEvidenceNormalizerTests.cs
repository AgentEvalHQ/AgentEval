// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using AgentEval.Microsoft365.Protocol;
using Xunit;
using static AgentEval.Tests.Microsoft365.Evidence.EvidenceTestKit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// <see cref="WorkIqEvidenceNormalizer"/>: citations only from the terminal status message, compound markers with
/// both keys, raw text untouched while the display text drops unresolved markers, determinism, the profile
/// switch of the tool and retrieval channels (NotRequested vs NotProvided/Provided; approval intents are decoded
/// under both profiles), the answer states (present, empty, missing, ambiguous, not
/// final), the identity/version stamps, and the trace/issue events it emits.
/// </summary>
public sealed class WorkIqEvidenceNormalizerTests
{
    private const string AnswerFixture = "envelopes/send-task-completed-with-answer-and-attributions.json";
    private const string DevRetrievalFixture = "envelopes/send-task-completed-with-developer-and-retrieval-info.json";
    private const string DirectFixture = "envelopes/send-direct-message.json";
    private const string EmptyAnswerFixture = "envelopes/send-task-completed-empty-answer.json";
    private const string FailedFixture = "envelopes/send-task-failed-with-partial-artifact.json";
    private const string ConfirmationFixture = "envelopes/send-task-input-required-confirmation.json";
    private const string AmbiguousFixture = "envelopes/send-task-completed-two-text-artifacts-ambiguous.json";

    private const string StrippedAnswer =
        "Project Atlas is on schedule: the design review closed on 3 November 2026 and the pilot starts on 17 November 2026." +
        " Two risks stay open: the vendor contract and the data-residency sign-off.";

    // ---------------------------------------------------------------------------------------------------------
    // Citations
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Normalize_CompletedTaskWithAttributions_ReadsCitationsOnlyFromTheTerminalStatusMessage()
    {
        var evidence = Normalize(SnapshotFromEnvelope(AnswerFixture));

        var sources = evidence.CitationSources;
        Assert.Equal(EvidenceAvailability.Provided, sources.Availability);
        Assert.Equal(EvidenceValidation.Valid, sources.Validation);
        Assert.Equal(2, sources.Items.Length);
        Assert.Equal("/status/message/metadata/attributions", Assert.Single(sources.Sources).JsonPointer);
        Assert.Equal(CaptureId, Assert.Single(sources.Sources).CaptureId);
        Assert.Equal("Project Atlas - Status report 2026-11-03.docx", sources.Items[0].DisplayName.Value);
        Assert.Equal("RE: Atlas vendor contract - open items", sources.Items[1].DisplayName.Value);
        Assert.All(sources.Items, s => Assert.Equal(FieldState.Missing, s.ProviderReferenceKey.State));
    }

    [Fact]
    public void Normalize_AttributionsOutsideTheStatusMessage_AreNeverRead()
    {
        var attributions = "[{\"attributionType\":\"citation\",\"providerDisplayName\":\"Leak\",\"seeMoreWebUrl\":\"https://leak.example.invalid\"}]";
        var answer = new WorkIqArtifact("artifact-answer-1", "Answer", null, ImmutableArray.Create(Text("Text")), 0, false, ParseJson("{\"attributions\":" + attributions + "}"));
        var history = Message("msg-user-1", ImmutableArray.Create(Text("Question")), "{\"attributions\":" + attributions + "}");
        var task = Snapshot(WorkIqTaskState.Completed, Message("msg-status-1", ImmutableArray<WorkIqPart>.Empty), ImmutableArray.Create(answer)) with
        {
            History = ImmutableArray.Create(history),
        };

        var evidence = Normalize(task);

        Assert.Equal(EvidenceAvailability.NotProvided, evidence.CitationSources.Availability);
        Assert.Equal("/status/message", Assert.Single(evidence.CitationSources.Sources).JsonPointer);
    }

    [Fact]
    public void Normalize_StatusMessageAbsent_CitationsAreNotProvided()
    {
        var evidence = Normalize(SnapshotWithArtifacts(Artifact("artifact-answer-1", "Answer", Text("Text"))));

        Assert.Equal(EvidenceAvailability.NotProvided, evidence.CitationSources.Availability);
        Assert.Equal("/status/message", Assert.Single(evidence.CitationSources.Sources).JsonPointer);
    }

    [Fact]
    public void Normalize_CompoundMarkers_ArePreservedWithBothKeysAndOneUnresolvedLinkPerKey()
    {
        var evidence = Normalize(SnapshotFromEnvelope(AnswerFixture));

        var markers = evidence.CitationMarkers;
        Assert.Equal(EvidenceAvailability.Provided, markers.Availability);
        Assert.Equal(EvidenceValidation.Valid, markers.Validation);
        Assert.Equal(2, markers.Items.Length);
        Assert.Equal(new[] { "turn0search1" }, markers.Items[0].ReferenceKeys);
        Assert.Equal(new[] { "turn0search1", "turn0search2" }, markers.Items[1].ReferenceKeys);
        Assert.All(markers.Items, m => Assert.Equal(CitationMarkerParser.UnicodeCiteFormat, m.MarkerFormat));
        var markerSource = Assert.Single(markers.Items[0].Sources);
        Assert.Equal(EvidenceOrigin.TextMarker, markerSource.Origin);
        Assert.Equal("/artifacts/0/parts", markerSource.JsonPointer);
        Assert.Equal("artifact-answer-6b2d9f4a-0001", markerSource.ArtifactId);

        Assert.Equal(3, evidence.CitationLinks.Length);
        Assert.All(evidence.CitationLinks, l => Assert.Equal(CitationBinding.Unresolved, l.Binding));
        Assert.All(evidence.CitationLinks, l => Assert.Null(l.SourceObservationId));
        Assert.Equal(new[] { "citation-marker-0", "citation-marker-1", "citation-marker-1" }, evidence.CitationLinks.Select(l => l.MarkerObservationId).ToArray());
    }

    [Fact]
    public void Normalize_RawTextUnchanged_DisplayTextStripsUnresolvedMarkers()
    {
        var task = SnapshotFromEnvelope(AnswerFixture);
        var expectedRaw = string.Concat(task.Artifacts[0].Parts.Select(p => p.Text));

        var evidence = Normalize(task);

        var answer = Assert.IsType<AnswerEvidence>(evidence.Answer);
        Assert.Equal(FieldState.Present, answer.RawText.State);
        Assert.Equal(expectedRaw, answer.RawText.Value);
        Assert.Contains('', answer.RawText.Value!);
        Assert.Equal(EvidenceDerivation.ProviderValue, answer.RawText.Derivation);
        Assert.Equal(2, answer.RawText.Sources.Length);

        Assert.Equal(FieldState.Present, answer.DisplayText.State);
        Assert.Equal(StrippedAnswer, answer.DisplayText.Value);
        Assert.Equal(EvidenceDerivation.ParserProjection, answer.DisplayText.Derivation);
        Assert.Equal(CitationMarkerParser.DisplayRuleId, answer.DisplayText.DerivationRuleId);
        Assert.Equal(answer.RawText.Sources, answer.DisplayText.Sources);

        Assert.True(answer.IsFinal);
        Assert.Equal(WorkIqAnswerSelector.PolicyVersion, answer.SelectionPolicyVersion);
        Assert.Equal(2, answer.Sources.Length);
        Assert.Equal("/artifacts/0/parts/1/text", answer.Sources[1].JsonPointer);
    }

    [Fact]
    public void Normalize_MarkdownAnchor_IsFlaggedAndKeptInTheDisplayText()
    {
        var evidence = Normalize(SnapshotWithArtifacts(Artifact("artifact-answer-1", "Answer", Text("See [1]."))));

        var marker = Assert.Single(evidence.CitationMarkers.Items);
        Assert.Equal(CitationMarkerParser.MarkdownNumericFormat, marker.MarkerFormat);
        Assert.Contains(evidence.CitationMarkers.Issues, i => i.Code == CitationMarkerParser.MarkdownIssueCode);
        Assert.Equal("See [1].", evidence.Answer!.DisplayText.Value);
        Assert.Equal(CitationBinding.Unresolved, Assert.Single(evidence.CitationLinks).Binding);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Determinism
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AnswerFixture)]
    [InlineData(DevRetrievalFixture)]
    [InlineData(ConfirmationFixture)]
    [InlineData(EmptyAnswerFixture)]
    public void Normalize_SameInputsTwice_YieldsIdenticalJson(string fixture)
    {
        var first = Microsoft365EvidenceJson.Serialize(Normalize(SnapshotFromEnvelope(fixture)));
        var second = Microsoft365EvidenceJson.Serialize(Normalize(SnapshotFromEnvelope(fixture)));

        Assert.Equal(first, second);
        Assert.Equal(first, Microsoft365EvidenceJson.Serialize(Microsoft365EvidenceJson.Deserialize(first)));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void Normalize_UnderAnotherCulture_YieldsTheSameJsonAsInvariant(string cultureName)
    {
        var task = SnapshotFromEnvelope(DevRetrievalFixture);
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        string invariant;
        string other;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            invariant = Microsoft365EvidenceJson.Serialize(Normalize(task));

            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            other = Microsoft365EvidenceJson.Serialize(Normalize(SnapshotFromEnvelope(DevRetrievalFixture)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }

        Assert.Equal(invariant, other);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Profiles
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Normalize_PublicAnswerOnly_ToolAndRetrievalChannelsAreNotRequestedAndCitationsStillDecoded()
    {
        var evidence = Normalize(SnapshotFromEnvelope(DevRetrievalFixture), profile: DiagnosticProfile.PublicAnswerOnly);

        foreach (var (availability, scope) in new[]
                 {
                     (evidence.ToolSelections.Availability, evidence.ToolSelections.Scope),
                     (evidence.ToolExecutions.Availability, evidence.ToolExecutions.Scope),
                     (evidence.RetrievalExecutions.Availability, evidence.RetrievalExecutions.Scope),
                     (evidence.SkillActivations.Availability, evidence.SkillActivations.Scope),
                 })
        {
            Assert.Equal(EvidenceAvailability.NotRequested, availability);
            Assert.Equal(EvidenceScope.Turn, scope);
        }

        // Approval intents are decoded under both profiles (the card arrives with the input-required status, not
        // through a requested mode): a completed task without an interaction part has none, so NotProvided, never
        // NotRequested.
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ApprovalIntents.Availability);
        Assert.Empty(evidence.ApprovalIntents.Items);
        Assert.Empty(evidence.ToolExecutions.Items);
        var publicIssue = Assert.Single(evidence.Issues, i => i.Code == WorkIqEvidenceNormalizer.PublicAnswerOnlyIssueCode);
        Assert.DoesNotContain("approval channels were not requested", publicIssue.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("Approval intents are still decoded", publicIssue.SafeMessage, StringComparison.Ordinal);
        Assert.Contains(evidence.Issues, i => i.Code == WorkIqEvidenceNormalizer.SkillsIssueCode);
        Assert.Equal(2, evidence.CitationSources.Items.Length);
        Assert.NotNull(evidence.Answer);
    }

    [Theory]
    [InlineData(DiagnosticProfile.PublicAnswerOnly)]
    [InlineData(DiagnosticProfile.Native118Diagnostics)]
    public void Normalize_ConfirmationFixture_ApprovalIntentsDecodedUnderBothProfiles(DiagnosticProfile profile)
    {
        var evidence = Normalize(SnapshotFromEnvelope(ConfirmationFixture), profile: profile);

        Assert.Equal(EvidenceAvailability.Provided, evidence.ApprovalIntents.Availability);
        var intent = Assert.Single(evidence.ApprovalIntents.Items);
        Assert.Equal("call-0a1b2c3d4e5f", intent.ProposedProviderInvocationId.Value);
        Assert.Equal("CreateDemoEvent", intent.Tool.FunctionDisplayName.Value);
        Assert.Equal("Pending", intent.Decision);
        Assert.Equal(
            profile == DiagnosticProfile.PublicAnswerOnly ? EvidenceAvailability.NotRequested : EvidenceAvailability.NotProvided,
            evidence.ToolExecutions.Availability);
    }

    [Fact]
    public void Normalize_ConfirmationFixture_ApprovalChannelIsIdenticalUnderBothProfiles()
    {
        var snapshot = SnapshotFromEnvelope(ConfirmationFixture);

        var underPublic = Normalize(snapshot, profile: DiagnosticProfile.PublicAnswerOnly).ApprovalIntents;
        var underNative = Normalize(snapshot, profile: DiagnosticProfile.Native118Diagnostics).ApprovalIntents;

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(underNative, Microsoft365EvidenceJson.Options),
            System.Text.Json.JsonSerializer.Serialize(underPublic, Microsoft365EvidenceJson.Options));
    }

    [Fact]
    public void Normalize_DirectMessageUnderPublicAnswerOnly_ApprovalIntentsAreNotProvidedNeverNotRequested()
    {
        var evidence = Normalize(null, MessageFromEnvelope(DirectFixture), profile: DiagnosticProfile.PublicAnswerOnly);

        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ApprovalIntents.Availability);
        Assert.Equal(EvidenceScope.Unknown, evidence.ApprovalIntents.Scope);
        Assert.Equal("", Assert.Single(evidence.ApprovalIntents.Sources).JsonPointer);
        Assert.Equal(EvidenceAvailability.NotRequested, evidence.ToolExecutions.Availability);
    }

    [Fact]
    public void Normalize_Native118WithoutDiagnosticArtifacts_ChannelsAreNotProvided()
    {
        var evidence = Normalize(SnapshotFromEnvelope(AnswerFixture), profile: DiagnosticProfile.Native118Diagnostics);

        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ToolSelections.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ToolExecutions.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.RetrievalExecutions.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ApprovalIntents.Availability);
        Assert.Equal(EvidenceAvailability.NotRequested, evidence.SkillActivations.Availability);
        Assert.Equal("/artifacts", Assert.Single(evidence.ToolSelections.Sources).JsonPointer);
        Assert.DoesNotContain(evidence.Issues, i => i.Code == WorkIqEvidenceNormalizer.PublicAnswerOnlyIssueCode);
        Assert.Contains(evidence.Issues, i => i.Code == WorkIqEvidenceNormalizer.SkillsIssueCode);
    }

    [Fact]
    public void Normalize_Native118WithDiagnosticArtifacts_ChannelsAreProvided()
    {
        var evidence = Normalize(SnapshotFromEnvelope(DevRetrievalFixture));

        Assert.Equal(2, evidence.ToolSelections.Items.Length);
        Assert.Single(evidence.ToolExecutions.Items);
        Assert.Single(evidence.RetrievalExecutions.Items);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ApprovalIntents.Availability);
        Assert.Equal(StrippedAnswer, evidence.Answer!.DisplayText.Value);
        Assert.Equal(EvidenceCoverage.Unknown, evidence.ToolExecutions.Coverage);
    }

    [Fact]
    public void Normalize_ConfirmationFixture_ByteIdenticalTwinsBecomeOneProposalWithRawArguments()
    {
        var evidence = Normalize(SnapshotFromEnvelope(ConfirmationFixture));

        Assert.Null(evidence.Answer);
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", evidence.TaskState);
        var intent = Assert.Single(evidence.ApprovalIntents.Items);
        Assert.Equal("call-0a1b2c3d4e5f", intent.ProposedProviderInvocationId.Value);
        Assert.Equal("CreateDemoEvent", intent.Tool.FunctionDisplayName.Value);
        Assert.StartsWith("{\"subject\":\"Atlas pilot kickoff\"", intent.ProposedArgumentsRawText.Value, StringComparison.Ordinal);
        Assert.Equal("Pending", intent.Decision);
        Assert.Equal(2, intent.Sources.Length);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ToolExecutions.Availability);
    }

    [Fact]
    public void Normalize_UnknownProfile_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Normalize(Snapshot(), profile: (DiagnosticProfile)42));

    // ---------------------------------------------------------------------------------------------------------
    // Answer states
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Normalize_DirectMessage_UsesTheMessageMetadataAndHasNoTaskState()
    {
        var message = MessageFromEnvelope(DirectFixture);

        var evidence = Normalize(null, message);

        Assert.Null(evidence.TaskState);
        var answer = Assert.IsType<AnswerEvidence>(evidence.Answer);
        Assert.True(answer.IsFinal);
        Assert.Equal("/parts/0/text", Assert.Single(answer.Sources).JsonPointer);
        Assert.Equal("Your next meeting is the Atlas design sync at 14:00 (Europe/Zurich) today.", answer.DisplayText.Value);
        Assert.Single(evidence.CitationMarkers.Items);
        Assert.Equal("/parts", Assert.Single(evidence.CitationMarkers.Items).Sources[0].JsonPointer);
        Assert.Equal(EvidenceAvailability.Provided, evidence.CitationSources.Availability);
        Assert.Equal("/metadata/attributions", Assert.Single(evidence.CitationSources.Sources).JsonPointer);
        Assert.Equal("Atlas design sync", Assert.Single(evidence.CitationSources.Items).DisplayName.Value);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ToolExecutions.Availability);
        Assert.Equal("", Assert.Single(evidence.ToolExecutions.Sources).JsonPointer);
    }

    [Fact]
    public void Normalize_EmptyAnswerArtifact_RawTextIsMissingAndFinal()
    {
        var evidence = Normalize(SnapshotFromEnvelope(EmptyAnswerFixture));

        var answer = Assert.IsType<AnswerEvidence>(evidence.Answer);
        Assert.Equal(FieldState.Missing, answer.RawText.State);
        Assert.Equal(FieldState.Missing, answer.DisplayText.State);
        Assert.True(answer.IsFinal);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.CitationMarkers.Availability);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.CitationSources.Availability);
        Assert.Empty(evidence.CitationLinks);
    }

    [Fact]
    public void Normalize_EmptyStringText_StaysPresentEmpty()
    {
        var evidence = Normalize(SnapshotWithArtifacts(Artifact("artifact-answer-1", "Answer", Text(""))));

        var answer = Assert.IsType<AnswerEvidence>(evidence.Answer);
        Assert.Equal(FieldState.Present, answer.RawText.State);
        Assert.Equal("", answer.RawText.Value);
        Assert.Equal("", answer.DisplayText.Value);
        Assert.True(answer.IsFinal);
        Assert.True(evidence.CitationMarkers.IsObservedEmpty);
    }

    [Fact]
    public void Normalize_AmbiguousAnswer_HasNoAnswerAndAnIssue()
    {
        var evidence = Normalize(SnapshotFromEnvelope(AmbiguousFixture));

        Assert.Null(evidence.Answer);
        var issue = Assert.Single(evidence.Issues, i => i.Code == WorkIqEvidenceNormalizer.AnswerAmbiguousIssueCode);
        Assert.Equal(2, issue.Sources.Length);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.CitationMarkers.Availability);
        Assert.Equal("/artifacts", Assert.Single(evidence.CitationMarkers.Sources).JsonPointer);
    }

    [Fact]
    public void Normalize_FailedTaskWithPartialArtifact_HasNoAnswer()
    {
        var evidence = Normalize(SnapshotFromEnvelope(FailedFixture));

        Assert.Null(evidence.Answer);
        Assert.Equal("TASK_STATE_FAILED", evidence.TaskState);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.CitationMarkers.Availability);
    }

    [Fact]
    public void Normalize_PartialTextFromASelector_IsNotFinalAndSaysSo()
    {
        var task = Snapshot(WorkIqTaskState.Failed, artifacts: ImmutableArray.Create(Artifact("artifact-answer-1", "Answer", Text("partial"))));
        var normalizer = new WorkIqEvidenceNormalizer(
            (snapshot, captureId) => new WorkIqAnswerSelection("partial", OutputCompleteness.Partial, ImmutableArray.Create("artifact-answer-1"), ImmutableArray.Create(new SourceRef(captureId, 0, EvidenceOrigin.A2AArtifact, "/artifacts/0/parts/0/text", "artifact-answer-1", 0, 0))),
            FakeAnswerSelector.SelectFromMessage);

        var evidence = normalizer.Normalize(task, null, [EvidenceTestKit.Capture()], CaptureState.Interrupted, DiagnosticProfile.PublicAnswerOnly, Identity());

        var answer = Assert.IsType<AnswerEvidence>(evidence.Answer);
        Assert.False(answer.IsFinal);
        Assert.Equal("partial", answer.RawText.Value);
        Assert.Equal(CaptureState.Interrupted, evidence.CaptureState);
        Assert.Contains(evidence.Issues, i => i.Code == WorkIqEvidenceNormalizer.AnswerNotFinalIssueCode);
    }

    [Fact]
    public void Normalize_NoTaskAndNoMessage_StillProducesADocument()
    {
        var evidence = Normalizer().Normalize(null, null, ImmutableArray<RawCaptureRef>.Empty, CaptureState.NotRetained, DiagnosticProfile.Native118Diagnostics, Identity(null));

        Assert.Null(evidence.Answer);
        Assert.Null(evidence.TaskState);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.CitationSources.Availability);
        Assert.Empty(evidence.CitationSources.Sources);
        Assert.Equal(EvidenceAvailability.NotProvided, evidence.ToolSelections.Availability);
        Assert.Empty(evidence.RawCaptures);
        Assert.Equal(CaptureState.NotRetained, evidence.CaptureState);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Stamps
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Normalize_Always_StampsVersionsIdentityAndCaptures()
    {
        var identity = new TurnIdentity("run-9", "case-9", "turn-9", 3, 1, "rpc-9", "msg-9", "msg-agent-9", "task-9", "ctx-9");
        var captures = ImmutableArray.Create(EvidenceTestKit.Capture("cap-a"), EvidenceTestKit.Capture("cap-b"));

        var evidence = Normalizer().Normalize(SnapshotFromEnvelope(AnswerFixture, "cap-a"), null, captures, CaptureState.Truncated, DiagnosticProfile.PublicAnswerOnly, identity);

        Assert.Equal(Microsoft365Evidence.SchemaVersion, evidence.SchemaVersion);
        Assert.Equal(Microsoft365Evidence.NormalizerVersion, evidence.NormalizerVersion);
        Assert.Equal(Microsoft365Evidence.ProviderProfileVersion, evidence.ProviderProfileVersion);
        Assert.Equal(Microsoft365Evidence.A2AVersion, evidence.A2AVersion);
        Assert.Same(identity, evidence.Identity);
        Assert.Equal("TASK_STATE_COMPLETED", evidence.TaskState);
        Assert.Equal(CaptureState.Truncated, evidence.CaptureState);
        Assert.Equal(captures, evidence.RawCaptures);
        Assert.Equal("cap-a", Assert.Single(evidence.CitationSources.Sources).CaptureId);
    }

    [Fact]
    public void Normalize_CaptureIdPrecedence_SnapshotThenFirstRawCaptureThenNone()
    {
        var withoutCapture = Snapshot(withCapture: false, artifacts: ImmutableArray.Create(Artifact("artifact-answer-1", "Answer", Text("x"))));

        var fromRaw = Normalizer().Normalize(withoutCapture, null, [EvidenceTestKit.Capture("cap-raw")], CaptureState.Complete, DiagnosticProfile.PublicAnswerOnly, Identity());
        var none = Normalizer().Normalize(withoutCapture, null, ImmutableArray<RawCaptureRef>.Empty, CaptureState.NotRetained, DiagnosticProfile.PublicAnswerOnly, Identity());

        Assert.Equal("cap-raw", Assert.Single(fromRaw.CitationSources.Sources).CaptureId);
        Assert.Equal(WorkIqEvidenceNormalizer.NoCaptureId, Assert.Single(none.CitationSources.Sources).CaptureId);
    }

    [Fact]
    public void Normalize_NullIdentity_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Normalizer().Normalize(Snapshot(), null, default, CaptureState.Complete, DiagnosticProfile.PublicAnswerOnly, null!));

    [Fact]
    public void Normalize_DefaultConstructor_DelegatesToTheRealAnswerSelector()
    {
        // Lane A owns WorkIqAnswerSelector. Until it lands the real selector throws NotImplementedException; the
        // contract tested here holds in both states: the normaliser never swallows the selector's exception, and
        // once the selector works its text is what the document carries.
        var task = SnapshotFromEnvelope(AnswerFixture);
        WorkIqAnswerSelection? real = null;
        Exception? realError = null;
        try
        {
            real = WorkIqAnswerSelector.SelectFromTask(task, CaptureId);
        }
        catch (Exception ex)
        {
            realError = ex;
        }

        var normalizer = new WorkIqEvidenceNormalizer();
        if (realError is not null)
        {
            var thrown = Assert.ThrowsAny<Exception>(() => normalizer.Normalize(task, null, [EvidenceTestKit.Capture()], CaptureState.Complete, DiagnosticProfile.PublicAnswerOnly, Identity()));
            Assert.IsType(realError.GetType(), thrown);
            return;
        }

        var evidence = normalizer.Normalize(task, null, [EvidenceTestKit.Capture()], CaptureState.Complete, DiagnosticProfile.PublicAnswerOnly, Identity());
        Assert.Equal(real!.RawText, evidence.Answer?.RawText.Value);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Normalize_WithDiagnostics_TracesTheScopeAndTheSummaries()
    {
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        using var diagnostics = new WorkIqDiagnostics("run-d", "sha-d", [trace], [issues]);

        var evidence = Normalize(SnapshotFromEnvelope(DevRetrievalFixture), diagnostics: diagnostics);

        var codes = trace.Events.Select(e => e.Code).ToArray();
        Assert.Equal("normalize.start", codes[0]);
        Assert.Equal("normalize.end", codes[^1]);
        Assert.Contains("normalize.answer", codes);
        Assert.Contains("normalize.citations", codes);
        Assert.Contains("normalize.channels", codes);
        Assert.Contains("normalize.issue", codes);
        Assert.DoesNotContain("normalize.failed", codes);
        Assert.All(trace.Events, e => Assert.Equal(WorkIqTraceStage.Normalize, e.Stage));
        Assert.All(trace.Events, e => Assert.Equal("case-1", e.CaseId));
        Assert.All(trace.Events, e => Assert.Equal("turn-1", e.TurnId));
        Assert.All(trace.Events, e => Assert.Equal(evidence.Identity.ProviderTaskId, e.TaskId));

        var end = trace.Events[^1];
        Assert.NotNull(end.ElapsedMs);
        Assert.Equal("TASK_STATE_COMPLETED", end.A2AState);

        var channels = trace.Events.Single(e => e.Code == "normalize.channels");
        Assert.NotNull(channels.Data);
        Assert.Equal("Provided", channels.Data.Value.GetProperty("toolExecutions").GetProperty("availability").GetString());
        Assert.Equal(1, channels.Data.Value.GetProperty("toolExecutions").GetProperty("count").GetInt32());

        var answer = trace.Events.Single(e => e.Code == "normalize.answer");
        Assert.True(answer.Data!.Value.GetProperty("hasAnswer").GetBoolean());
        Assert.Equal("Present", answer.Data.Value.GetProperty("rawTextState").GetString());
        Assert.DoesNotContain("Project Atlas", answer.Data.Value.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(0, issues.Count);
        Assert.Equal(0, diagnostics.IssueCount);
    }

    [Fact]
    public void Normalize_WithDiagnostics_AnomaliesBecomeIssuesWithACause()
    {
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        using var diagnostics = new WorkIqDiagnostics("run-d", null, [trace], [issues]);

        Normalize(SnapshotFromEnvelope(AmbiguousFixture), diagnostics: diagnostics, profile: DiagnosticProfile.PublicAnswerOnly);

        var issue = Assert.Single(issues.Issues);
        Assert.Equal(WorkIqEvidenceNormalizer.AnswerAmbiguousIssueCode, issue.Code);
        Assert.Equal(WorkIqTraceStage.Normalize, issue.Stage);
        Assert.Equal(WorkIqIssueCategory.Protocol, issue.Category);
        Assert.False(string.IsNullOrWhiteSpace(issue.SuggestedCause));
        Assert.Equal("case-1", issue.CaseId);
        Assert.Equal("turn-1", issue.TurnId);
        Assert.Equal("TASK_STATE_COMPLETED", issue.A2AState);
        Assert.NotNull(issue.TaskId);
        Assert.Contains(trace.Events, e => e.Code == "issue.protocol" && e.Level == WorkIqTraceLevel.Warning);
    }

    [Fact]
    public void Normalize_WithDiagnostics_DecoderAnomaliesAreRegistered()
    {
        var issues = new ListIssueSink();
        using var diagnostics = new WorkIqDiagnostics("run-d", null, [], [issues]);
        var task = SnapshotWithArtifacts(
            Artifact("artifact-dev-1", Native118DiagnosticsDecoder.DeveloperInfoName, Data("{\"plugins\":{\"executions\":[null]}}", Native118DiagnosticsDecoder.DeveloperInfoMediaType)));

        Normalize(task, diagnostics: diagnostics);

        Assert.Contains(issues.Issues, i => i.Code == Native118DiagnosticsDecoder.RowNotObjectIssueCode);
        Assert.Contains(issues.Issues, i => i.Code == Native118DiagnosticsDecoder.ContainerMalformedIssueCode || i.Code == Native118DiagnosticsDecoder.RowNotObjectIssueCode);
    }

    [Fact]
    public void Normalize_SelectorThrows_TracesTheFailureAndRethrows()
    {
        var trace = new ListTraceSink();
        using var diagnostics = new WorkIqDiagnostics("run-d", null, [trace], []);
        var normalizer = new WorkIqEvidenceNormalizer(
            (_, _) => throw new InvalidOperationException("selector exploded"),
            FakeAnswerSelector.SelectFromMessage,
            diagnostics);

        var thrown = Assert.Throws<InvalidOperationException>(() => normalizer.Normalize(Snapshot(), null, [EvidenceTestKit.Capture()], CaptureState.Complete, DiagnosticProfile.PublicAnswerOnly, Identity()));

        Assert.Equal("selector exploded", thrown.Message);
        var codes = trace.Events.Select(e => e.Code).ToArray();
        Assert.Equal(new[] { "normalize.start", "normalize.failed" }, codes);
        Assert.Equal(WorkIqTraceLevel.Error, trace.Events[^1].Level);
        Assert.Contains("InvalidOperationException", trace.Events[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_WithNoneDiagnostics_IsIdenticalToTracedOutput()
    {
        var trace = new ListTraceSink();
        using var diagnostics = new WorkIqDiagnostics("run-d", null, [trace], []);
        var task = SnapshotFromEnvelope(DevRetrievalFixture);

        var silent = Microsoft365EvidenceJson.Serialize(Normalize(task));
        var traced = Microsoft365EvidenceJson.Serialize(Normalize(SnapshotFromEnvelope(DevRetrievalFixture), diagnostics: diagnostics));

        Assert.Equal(silent, traced);
        Assert.True(trace.Count > 0);
    }
}
