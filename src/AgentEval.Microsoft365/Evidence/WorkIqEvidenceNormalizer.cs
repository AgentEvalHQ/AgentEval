// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence.Decoders;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// Turns one turn's reduced task state (or direct message), raw capture references, diagnostic profile and
/// identity into a <see cref="Microsoft365TurnEvidence"/> document. Deterministic: the same inputs and the same
/// <see cref="Microsoft365Evidence.NormalizerVersion"/> yield the same document (local observation ids are
/// ordinal, no clock, no random, culture-invariant). Owns the answer evidence (raw/display with source pointers)
/// through <see cref="WorkIqAnswerSelector"/>, citation sources via <see cref="AttributionDecoder"/> (the terminal
/// status message's metadata for a task, the message's metadata for a direct reply — never an artifact's or a
/// history message's), citation markers via <see cref="CitationMarkerParser"/>, marker-to-source links (all
/// <see cref="CitationBinding.Unresolved"/> unless a source carries an explicit provider key), and the diagnostic
/// channels (<c>NotRequested</c> under <see cref="DiagnosticProfile.PublicAnswerOnly"/>;
/// <c>NotProvided</c>/<c>Provided</c> via <see cref="Native118DiagnosticsDecoder"/> under
/// <see cref="DiagnosticProfile.Native118Diagnostics"/>). It never invents arguments or results, matches calls by
/// name, or turns missing telemetry into an empty success. JSON pointers in the document are relative to the
/// reduced task object (<c>/status/message/…</c>, <c>/artifacts/{i}/…</c>) or to the direct message object, not to
/// a JSON-RPC envelope, because a streamed task has no single envelope. Every run is traced on the
/// <see cref="WorkIqTraceStage.Normalize"/> stage of the <see cref="WorkIqDiagnostics"/> it was given.
/// </summary>
internal sealed class WorkIqEvidenceNormalizer
{
    /// <summary>The capture id used in source pointers when neither the snapshot nor the raw captures name one.</summary>
    public const string NoCaptureId = "none";

    /// <summary>The JSON pointer of a task's status message, relative to the task object.</summary>
    public const string TaskStatusMessagePointer = "/status/message";

    /// <summary>The JSON pointer of a task's artifact list, relative to the task object.</summary>
    public const string ArtifactsPointer = "/artifacts";

    /// <summary>The JSON pointer of a direct message's parts, relative to the message object.</summary>
    public const string MessagePartsPointer = "/parts";

    /// <summary>Issue (informational): the profile did not request the diagnostic channels.</summary>
    public const string PublicAnswerOnlyIssueCode = "profile.public_answer_only";

    /// <summary>Issue (informational): Work IQ A2A 1.0 has no supported skill-activation channel.</summary>
    public const string SkillsIssueCode = "skills.no_supported_channel";

    /// <summary>Issue: several artifacts qualify as the answer; none was chosen.</summary>
    public const string AnswerAmbiguousIssueCode = "answer.ambiguous";

    /// <summary>Issue (informational): answer text exists but is not final (the task did not complete).</summary>
    public const string AnswerNotFinalIssueCode = "answer.not_final";

    /// <summary>The trace code of the measured normalisation scope (<c>normalize.start</c> / <c>.end</c> / <c>.failed</c>).</summary>
    public const string TraceCode = "normalize";

    private static readonly Func<WorkIqTaskSnapshot, string, WorkIqAnswerSelection> DefaultSelectFromTask = WorkIqAnswerSelector.SelectFromTask;
    private static readonly Func<WorkIqMessage, string, WorkIqAnswerSelection> DefaultSelectFromMessage = WorkIqAnswerSelector.SelectFromMessage;

    /// <summary>Issue codes that describe a known limitation rather than an anomaly; traced at Info, never registered as a diagnostics issue.</summary>
    private static readonly HashSet<string> InformationalIssueCodes = new(StringComparer.Ordinal)
    {
        PublicAnswerOnlyIssueCode,
        SkillsIssueCode,
        AnswerNotFinalIssueCode,
        CitationMarkerParser.MarkdownIssueCode,
        Native118DiagnosticsDecoder.UnknownFieldsIssueCode,
    };

    private readonly Func<WorkIqTaskSnapshot, string, WorkIqAnswerSelection> _selectFromTask;
    private readonly Func<WorkIqMessage, string, WorkIqAnswerSelection> _selectFromMessage;
    private readonly WorkIqDiagnostics _diagnostics;

    /// <summary>Creates a normaliser that uses <see cref="WorkIqAnswerSelector"/> and traces nothing.</summary>
    public WorkIqEvidenceNormalizer()
        : this(null, null, null)
    {
    }

    /// <summary>Creates a normaliser that uses <see cref="WorkIqAnswerSelector"/> and traces on <paramref name="diagnostics"/>.</summary>
    /// <param name="diagnostics">The run's diagnostics; null traces nothing.</param>
    internal WorkIqEvidenceNormalizer(WorkIqDiagnostics? diagnostics)
        : this(null, null, diagnostics)
    {
    }

    /// <summary>Creates a normaliser with explicit answer-selection seams (tests) and optional diagnostics.</summary>
    /// <param name="selectFromTask">Replaces <see cref="WorkIqAnswerSelector.SelectFromTask"/>; null keeps the real selector.</param>
    /// <param name="selectFromMessage">Replaces <see cref="WorkIqAnswerSelector.SelectFromMessage"/>; null keeps the real selector.</param>
    /// <param name="diagnostics">The run's diagnostics; null traces nothing.</param>
    internal WorkIqEvidenceNormalizer(
        Func<WorkIqTaskSnapshot, string, WorkIqAnswerSelection>? selectFromTask,
        Func<WorkIqMessage, string, WorkIqAnswerSelection>? selectFromMessage,
        WorkIqDiagnostics? diagnostics = null)
    {
        _selectFromTask = selectFromTask ?? DefaultSelectFromTask;
        _selectFromMessage = selectFromMessage ?? DefaultSelectFromMessage;
        _diagnostics = diagnostics ?? WorkIqDiagnostics.None;
    }

    /// <summary>Normalises a completed, failed, suspended or interrupted turn.</summary>
    /// <param name="task">The final task snapshot, or null for a direct-message reply.</param>
    /// <param name="directMessage">The direct reply message when the provider answered without a task.</param>
    /// <param name="rawCaptures">The raw captures that fed the turn.</param>
    /// <param name="captureState">The overall capture state of the turn.</param>
    /// <param name="profile">The diagnostic profile that was requested.</param>
    /// <param name="identity">The turn's identities.</param>
    public Microsoft365TurnEvidence Normalize(
        WorkIqTaskSnapshot? task,
        WorkIqMessage? directMessage,
        ImmutableArray<RawCaptureRef> rawCaptures,
        CaptureState captureState,
        DiagnosticProfile profile,
        TurnIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown diagnostic profile.");
        }

        var captures = rawCaptures.IsDefault ? ImmutableArray<RawCaptureRef>.Empty : rawCaptures;
        var captureId = task?.Capture?.CaptureId ?? (captures.Length > 0 ? captures[0].CaptureId : NoCaptureId);

        using var scope = _diagnostics.Measure(
            WorkIqTraceStage.Normalize,
            TraceCode,
            "Normalise the turn into the evidence document",
            caseId: identity.CaseId,
            turnId: identity.TurnId,
            taskId: task?.Handle.TaskId ?? identity.ProviderTaskId,
            contextId: task?.Handle.ContextId ?? identity.ProviderContextId);
        scope.Observe(a2aState: task?.RawState);

        try
        {
            var evidence = Build(task, directMessage, captures, captureState, profile, identity, captureId);
            Report(evidence, task, identity);
            return evidence;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Building
    // ---------------------------------------------------------------------------------------------------------

    private Microsoft365TurnEvidence Build(
        WorkIqTaskSnapshot? task,
        WorkIqMessage? directMessage,
        ImmutableArray<RawCaptureRef> captures,
        CaptureState captureState,
        DiagnosticProfile profile,
        TurnIdentity identity,
        string captureId)
    {
        var issues = new List<EvidenceIssue>();

        // 1. Answer selection (the task has precedence over a direct message).
        WorkIqAnswerSelection? selection = null;
        SourceRef? answerSource = null;
        SourceRef? messageSource = null;
        if (task is not null)
        {
            selection = NormalizeSelection(_selectFromTask(task, captureId));
            answerSource = AnswerSourceOf(task, selection, captureId);
            messageSource = new SourceRef(captureId, 0, EvidenceOrigin.A2AMessage, TaskStatusMessagePointer);
        }
        else if (directMessage is not null)
        {
            selection = NormalizeSelection(_selectFromMessage(directMessage, captureId));
            answerSource = new SourceRef(captureId, 0, EvidenceOrigin.A2AMessage, MessagePartsPointer);
            messageSource = new SourceRef(captureId, 0, EvidenceOrigin.A2AMessage, "");
        }

        var rawText = RawTextOf(selection, issues);

        // 2. Citation markers: a parser projection of the raw text.
        var markers = rawText is { IsPresent: true } && answerSource is not null
            ? CitationMarkerParser.Parse(rawText.Value!, answerSource)
            : EvidenceSet<CitationMarker>.NotProvided(EvidenceScope.Response, answerSource is null ? default : ImmutableArray.Create(answerSource));

        // 3. Citation sources: only the terminal status message (task) or the message itself (direct reply).
        var sources = task is not null
            ? AttributionDecoder.Decode(task.StatusMessage?.Metadata, messageSource!)
            : directMessage is not null
                ? AttributionDecoder.Decode(directMessage.Metadata, messageSource!)
                : EvidenceSet<CitationSource>.NotProvided(EvidenceScope.Response, default);

        // 4. Links: one per marker occurrence × reference key; bound only by an explicit provider key.
        var links = BuildLinks(markers, sources);

        // 5. Answer evidence with the display projection.
        AnswerEvidence? answer = null;
        if (rawText is not null && selection is not null)
        {
            var display = rawText.IsPresent
                ? Observed<string>.Present(
                    CitationMarkerParser.RenderDisplayText(rawText.Value!, markers.Items, marker => ResolvedOrdinals(marker, links, sources)),
                    rawText.Sources,
                    EvidenceDerivation.ParserProjection,
                    CitationMarkerParser.DisplayRuleId)
                : Observed<string>.Missing();
            var assembled = selection.Completeness is OutputCompleteness.Complete or OutputCompleteness.Empty;
            var isFinal = assembled && (task is null ? directMessage is not null : task.State == WorkIqTaskState.Completed);
            answer = new AnswerEvidence(rawText, display, isFinal, WorkIqAnswerSelector.PolicyVersion, selection.Sources);
            if (!isFinal)
            {
                issues.Add(new EvidenceIssue(
                    AnswerNotFinalIssueCode,
                    "Answer text exists but is not final: the task did not complete, so the text is partial and never an answer.",
                    selection.Sources));
            }
        }

        // 6. Diagnostic channels by profile.
        EvidenceSet<ToolSelectionObservation> toolSelections;
        EvidenceSet<ToolExecutionObservation> toolExecutions;
        EvidenceSet<RetrievalExecutionObservation> retrievalExecutions;
        EvidenceSet<ApprovalIntentObservation> approvalIntents;
        switch (profile)
        {
            case DiagnosticProfile.Native118Diagnostics when task is not null:
                var decoded = Native118DiagnosticsDecoder.Decode(task, captureId);
                toolSelections = decoded.ToolSelections;
                toolExecutions = decoded.ToolExecutions;
                retrievalExecutions = decoded.RetrievalExecutions;
                approvalIntents = decoded.ApprovalIntents;
                issues.AddRange(decoded.Issues);
                break;
            case DiagnosticProfile.Native118Diagnostics:
                var absence = messageSource is null ? default : ImmutableArray.Create(messageSource);
                toolSelections = EvidenceSet<ToolSelectionObservation>.NotProvided(EvidenceScope.Unknown, absence);
                toolExecutions = EvidenceSet<ToolExecutionObservation>.NotProvided(EvidenceScope.Unknown, absence);
                retrievalExecutions = EvidenceSet<RetrievalExecutionObservation>.NotProvided(EvidenceScope.Unknown, absence);
                approvalIntents = EvidenceSet<ApprovalIntentObservation>.NotProvided(EvidenceScope.Unknown, absence);
                break;
            default:
                toolSelections = EvidenceSet<ToolSelectionObservation>.NotRequested(EvidenceScope.Turn);
                toolExecutions = EvidenceSet<ToolExecutionObservation>.NotRequested(EvidenceScope.Turn);
                retrievalExecutions = EvidenceSet<RetrievalExecutionObservation>.NotRequested(EvidenceScope.Turn);
                approvalIntents = EvidenceSet<ApprovalIntentObservation>.NotRequested(EvidenceScope.Turn);
                issues.Add(new EvidenceIssue(
                    PublicAnswerOnlyIssueCode,
                    "Diagnostic profile PublicAnswerOnly: the tool, retrieval and approval channels were not requested (acceptedOutputModes text/plain only); their absence is not evidence of absence.",
                    ImmutableArray<SourceRef>.Empty));
                break;
        }

        var skillActivations = EvidenceSet<SkillActivationObservation>.NotRequested(EvidenceScope.Turn);
        issues.Add(new EvidenceIssue(
            SkillsIssueCode,
            "Work IQ A2A 1.0 exposes no skill-activation channel; advertised AgentCard skills are capabilities, not activations.",
            ImmutableArray<SourceRef>.Empty));

        return new Microsoft365TurnEvidence(
            Microsoft365Evidence.SchemaVersion,
            Microsoft365Evidence.NormalizerVersion,
            Microsoft365Evidence.ProviderProfileVersion,
            Microsoft365Evidence.A2AVersion,
            identity,
            task?.RawState,
            captureState,
            answer,
            toolSelections,
            toolExecutions,
            approvalIntents,
            retrievalExecutions,
            markers,
            sources,
            links,
            skillActivations,
            captures,
            issues.ToImmutableArray());
    }

    private static WorkIqAnswerSelection NormalizeSelection(WorkIqAnswerSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection with
        {
            CandidateArtifactIds = selection.CandidateArtifactIds.IsDefault ? ImmutableArray<string>.Empty : selection.CandidateArtifactIds,
            Sources = selection.Sources.IsDefault ? ImmutableArray<SourceRef>.Empty : selection.Sources,
        };
    }

    /// <summary>
    /// The raw answer text: Present when the selector assembled text (an empty string stays Present), Missing when
    /// the selected answer has no text part, null when there is no answer (nothing selected, or several candidates —
    /// recorded as <see cref="AnswerAmbiguousIssueCode"/>).
    /// </summary>
    private static Observed<string>? RawTextOf(WorkIqAnswerSelection? selection, List<EvidenceIssue> issues)
    {
        if (selection is null)
        {
            return null;
        }

        switch (selection.Completeness)
        {
            case OutputCompleteness.Ambiguous:
                issues.Add(new EvidenceIssue(
                    AnswerAmbiguousIssueCode,
                    FormattableString.Invariant($"{selection.CandidateArtifactIds.Length} artifacts qualify as the answer; none was chosen and all are retained in the capture."),
                    selection.Sources));
                return null;
            case OutputCompleteness.None:
                return null;
            default:
                return selection.RawText is null
                    ? Observed<string>.Missing()
                    : Observed<string>.Present(selection.RawText, selection.Sources);
        }
    }

    /// <summary>The pointer the markers are parsed against: the single answer artifact's parts, or the artifact list when no single artifact was chosen.</summary>
    private static SourceRef AnswerSourceOf(WorkIqTaskSnapshot task, WorkIqAnswerSelection selection, string captureId)
    {
        if (selection.CandidateArtifactIds.Length == 1)
        {
            var artifactId = selection.CandidateArtifactIds[0];
            for (var i = 0; i < task.Artifacts.Length; i++)
            {
                var artifact = task.Artifacts[i];
                if (string.Equals(artifact.ArtifactId, artifactId, StringComparison.Ordinal))
                {
                    return new SourceRef(
                        captureId,
                        0,
                        EvidenceOrigin.A2AArtifact,
                        EvidenceJsonReader.Pointer(EvidenceJsonReader.Pointer(ArtifactsPointer, i), "parts"),
                        artifact.ArtifactId,
                        artifact.Revision);
                }
            }
        }

        return new SourceRef(captureId, 0, EvidenceOrigin.A2AArtifact, ArtifactsPointer);
    }

    private static ImmutableArray<CitationLink> BuildLinks(EvidenceSet<CitationMarker> markers, EvidenceSet<CitationSource> sources)
    {
        var links = ImmutableArray.CreateBuilder<CitationLink>();
        foreach (var marker in markers.Items)
        {
            foreach (var key in marker.ReferenceKeys)
            {
                CitationSource? bound = null;
                foreach (var source in sources.Items)
                {
                    if (source.ProviderReferenceKey.IsPresent && string.Equals(source.ProviderReferenceKey.Value, key, StringComparison.Ordinal))
                    {
                        bound = source;
                        break;
                    }
                }

                links.Add(bound is null
                    ? new CitationLink(marker.ObservationId, null, CitationBinding.Unresolved, marker.Sources)
                    : new CitationLink(marker.ObservationId, bound.ObservationId, CitationBinding.ExplicitProviderKey, marker.Sources.AddRange(bound.Sources)));
            }
        }

        return links.ToImmutable();
    }

    /// <summary>The 1-based ordinals (position in <see cref="Microsoft365TurnEvidence.CitationSources"/>) of the sources a marker is explicitly bound to; empty when unresolved.</summary>
    private static IReadOnlyList<int> ResolvedOrdinals(CitationMarker marker, ImmutableArray<CitationLink> links, EvidenceSet<CitationSource> sources)
    {
        var ordinals = new List<int>();
        foreach (var link in links)
        {
            if (link.SourceObservationId is null || !string.Equals(link.MarkerObservationId, marker.ObservationId, StringComparison.Ordinal))
            {
                continue;
            }

            for (var i = 0; i < sources.Items.Length; i++)
            {
                if (string.Equals(sources.Items[i].ObservationId, link.SourceObservationId, StringComparison.Ordinal))
                {
                    var ordinal = i + 1;
                    if (!ordinals.Contains(ordinal))
                    {
                        ordinals.Add(ordinal);
                    }

                    break;
                }
            }
        }

        return ordinals;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------------------------------------------

    private void Report(Microsoft365TurnEvidence evidence, WorkIqTaskSnapshot? task, TurnIdentity identity)
    {
        if (_diagnostics.IsNone)
        {
            return;
        }

        var taskId = task?.Handle.TaskId ?? identity.ProviderTaskId;
        var contextId = task?.Handle.ContextId ?? identity.ProviderContextId;

        Trace("normalize.answer", evidence.Answer is null ? "No answer evidence (nothing selected)" : "Answer evidence built", new
        {
            hasAnswer = evidence.Answer is not null,
            rawTextState = evidence.Answer?.RawText.State.ToString(),
            displayTextState = evidence.Answer?.DisplayText.State.ToString(),
            isFinal = evidence.Answer?.IsFinal,
            selectionPolicy = evidence.Answer?.SelectionPolicyVersion,
            sourceParts = evidence.Answer?.Sources.Length,
            taskState = evidence.TaskState,
            captureState = evidence.CaptureState.ToString(),
        });

        Trace("normalize.citations", "Citation sources, markers and links decoded", new
        {
            sources = Summary(evidence.CitationSources),
            markers = Summary(evidence.CitationMarkers),
            links = evidence.CitationLinks.Length,
            unresolvedLinks = evidence.CitationLinks.Count(l => l.Binding == CitationBinding.Unresolved),
        });

        Trace("normalize.channels", "Diagnostic channels decoded", new
        {
            toolSelections = Summary(evidence.ToolSelections),
            toolExecutions = Summary(evidence.ToolExecutions),
            retrievalExecutions = Summary(evidence.RetrievalExecutions),
            approvalIntents = Summary(evidence.ApprovalIntents),
            skillActivations = Summary(evidence.SkillActivations),
        });

        foreach (var issue in AllIssues(evidence))
        {
            if (InformationalIssueCodes.Contains(issue.Code))
            {
                Trace("normalize.issue", issue.SafeMessage, new { code = issue.Code, sources = issue.Sources.Length });
                continue;
            }

            _diagnostics.Issue(
                WorkIqIssue.Create(WorkIqTraceStage.Normalize, WorkIqIssueCategory.Protocol, issue.Code, issue.SafeMessage, SuggestedCauseOf(issue.Code)) with
                {
                    CaseId = identity.CaseId,
                    TurnId = identity.TurnId,
                    TaskId = taskId,
                    ContextId = contextId,
                    A2AState = evidence.TaskState,
                },
                WorkIqTraceLevel.Warning);
        }

        void Trace(string code, string message, object data) =>
            _diagnostics.Trace(new WorkIqTraceEvent(default, WorkIqTraceStage.Normalize, WorkIqTraceLevel.Info, code, message)
            {
                CaseId = identity.CaseId,
                TurnId = identity.TurnId,
                TaskId = taskId,
                ContextId = contextId,
                A2AState = evidence.TaskState,
                Data = JsonSerializer.SerializeToElement(data, WorkIqDiagnosticsJson.Options),
            });
    }

    private static object Summary<T>(EvidenceSet<T> set) => new
    {
        availability = set.Availability.ToString(),
        validation = set.Validation.ToString(),
        coverage = set.Coverage.ToString(),
        count = set.Items.Length,
        issues = set.Issues.Length,
    };

    private static IEnumerable<EvidenceIssue> AllIssues(Microsoft365TurnEvidence evidence) =>
        evidence.Issues
            .Concat(evidence.CitationSources.Issues)
            .Concat(evidence.CitationMarkers.Issues)
            .Concat(evidence.ToolSelections.Issues)
            .Concat(evidence.ToolExecutions.Issues)
            .Concat(evidence.RetrievalExecutions.Issues)
            .Concat(evidence.ApprovalIntents.Issues)
            .Concat(evidence.SkillActivations.Issues);

    /// <summary>A plain-language cause an external tester can act on, keyed by the issue code's prefix.</summary>
    private static string SuggestedCauseOf(string code)
    {
        if (string.Equals(code, AnswerAmbiguousIssueCode, StringComparison.Ordinal))
        {
            return "The service returned several answer candidates and the selection policy chose none; inspect the artifacts in the capture and report the shape.";
        }

        if (string.Equals(code, Native118DiagnosticsDecoder.AmbiguousArtifactIssueCode, StringComparison.Ordinal))
        {
            return "Two distinct artifacts play the same diagnostic role; the decoder never picks the last one. Report the capture so the artifact relationship can be decoded.";
        }

        if (string.Equals(code, InteractionDecoder.ConflictingTwinsIssueCode, StringComparison.Ordinal))
        {
            return "The confirmation card's action and actionId payloads differ; both proposals are kept and nothing was approved.";
        }

        if (code.StartsWith("diagnostics.", StringComparison.Ordinal))
        {
            return "The diagnostic artifact does not match the native 1.18 shapes this decoder knows (an experimental, undocumented format); the service may have changed it. The raw data stays in the capture.";
        }

        if (code.StartsWith("citations.", StringComparison.Ordinal))
        {
            return "The attribution metadata or the citation markers do not match the sample-observed shapes; the raw data stays in the capture.";
        }

        if (code.StartsWith("approval.", StringComparison.Ordinal))
        {
            return "The confirmation card does not match the interaction profile; the pause is retained raw and is never auto-answered.";
        }

        return "See the capture at the issue's source pointers.";
    }
}
