// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using A2A;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;

namespace AgentEval.Microsoft365.Readiness;

/// <summary>The probe and census stages of <see cref="ReadinessRun"/>: the only stages that send a prompt to the agent.</summary>
internal sealed partial class ReadinessRun
{
    // ---------------------------------------------------------------------------------------------------------
    // 5. Probe (consent checked by the caller)
    // ---------------------------------------------------------------------------------------------------------

    private async Task<ReadinessStageResult> RunProbeAsync(CancellationToken cancellationToken)
    {
        var profile = EffectiveProfile;
        var conversation = NewConversation(WorkIqOptions);
        var started = _clock.GetTimestamp();
        var mark = _trace.Count;

        var (result, sendFailure) = await RunTurnAsync(conversation, ReadinessRun.ProbeTurnId, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            var evidence = new ReadinessEvidence()
                .Add(ProbePromptKey, Microsoft365ReadinessCheck.ProbePrompt)
                .Add("profile", ProfileText(profile));
            return NetworkStageFailed(ReadinessStage.Probe, sendFailure!, evidence, timedOut: false) with { ElapsedMs = Elapsed(started) };
        }

        _probe = new ProbeTurn(result, conversation, profile);
        var ev = TurnEvidence(result, ProbeTurnId, mark, profile, includePrompt: true);
        var elapsed = Elapsed(started);

        if (result.Outcome == WorkIqTurnOutcome.Completed)
        {
            var warnings = new List<string>();
            if (result.AnswerText is null)
            {
                warnings.Add($"The task completed but no answer text was selected (answer {result.AnswerCompleteness}). That is a finding about the agent's output, not a mistake on your side: send the report folder back.");
            }
            else if (result.AnswerText.Length == 0)
            {
                warnings.Add("The task completed with an empty answer. That is a finding about the agent, not a mistake on your side: send the report folder back.");
            }
            else if (!result.AnswerText.Contains("READY", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("The answer is not the single word READY: the agent did not follow the instruction literally (informational).");
            }

            var answer = result.AnswerText is null
                ? $"no answer text ({result.AnswerCompleteness})"
                : string.Create(CultureInfo.InvariantCulture, $"{result.AnswerText.Length}-character answer");
            var attributions = "attributions " + result.Evidence.CitationSources.Describe("attribution(s)");
            return Passed(
                ReadinessStage.Probe,
                $"the turn completed in {Ms(result.Timing.Elapsed.TotalMilliseconds)} ms: {answer}, {attributions}",
                "One real turn worked end to end on this tenant: send, task, answer and evidence. Nothing was approved or changed.",
                "Nothing to do. Next: the smoke benchmark (tester guide, Step 3).",
                ev,
                warnings) with { ElapsedMs = elapsed };
        }

        var issue = WorkIqIssueClassifier.FromException(new WorkIqTurnNotCompletedException(result), WorkIqTraceStage.Outcome);
        var failure = result.Failure;
        string summary;
        string meaning;
        string action;
        switch (result.Outcome)
        {
            case WorkIqTurnOutcome.InputRequired:
            case WorkIqTurnOutcome.AuthRequired:
                summary = $"the agent paused ({result.Outcome}) and waits for {InteractionText(result)}; nothing was approved";
                meaning = result.Outcome == WorkIqTurnOutcome.AuthRequired
                    ? "The agent needs an external authorization (a connector sign-in) that this connector cannot complete, so it produced no answer."
                    : "The task is suspended waiting for a confirmation or input. Nothing was approved: the doctor never answers a pause.";
                action = result.Outcome == WorkIqTurnOutcome.AuthRequired
                    ? "Sign in to the connector the agent names in Microsoft 365 (Copilot Chat), then run the doctor again."
                    : "Unexpected for this read-only prompt: send the report folder back.";
                break;

            case WorkIqTurnOutcome.Indeterminate:
                summary = "Indeterminate: the send may have been accepted; do not resend; inspect the trace" +
                    (failure is null ? "" : $" ({failure.Category}: {failure.SafeMessage})");
                meaning = "The remote state is unknown: no terminal task state was observed before the transport, the timeout or the recovery budget ended. The prompt was not resent.";
                action = $"Do not resend blindly. Find the task id in the trace (event turn.outcome); run the doctor once more later; if it repeats, send both report folders back. A slow agent needs a larger --timeout-seconds (now {Seconds(_options.StageTimeout)} s).";
                break;

            default:
                summary = $"the turn ended {result.Outcome}" + (failure is null ? "" : $": {failure.Category}: {failure.SafeMessage}");
                meaning = issue.SuggestedCause;
                action = failure is null
                    ? "Send the report folder back; the task state and request-id are in it."
                    : ActionFor(failure);
                break;
        }

        return Failed(ReadinessStage.Probe, summary, meaning, action, issue, ev) with { ElapsedMs = elapsed };
    }

    /// <summary>
    /// Runs one turn with the fixed probe prompt. A turn result is returned for every outcome (a local stop at the
    /// stage bound yields the conversation's indeterminate result); a transport failure before any result yields the
    /// failure instead. A stop by the caller's token propagates.
    /// </summary>
    private async Task<(WorkIqTurnResult? Result, WorkIqFailure? Failure)> RunTurnAsync(WorkIqConversation conversation, string turnId, CancellationToken cancellationToken)
    {
        // The conversation's own turn deadline (= the stage timeout) ends a slow turn as Indeterminate; the stage bound
        // is a backstop a little later.
        using var stage = StageTokenSource(cancellationToken, _options.StageTimeout + TurnGrace);
        try
        {
            var result = await conversation
                .RunTurnAsync(new WorkIqTurnRequest(Microsoft365ReadinessCheck.ProbePrompt, ProbeCaseId, turnId), stage.Token)
                .ConfigureAwait(false);
            return (result, null);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkIqTurnCanceledException ex)
        {
            return (ex.TurnResult, null);
        }
        catch (WorkIqProtocolException ex)
        {
            return (null, ex.Failure);
        }
    }

    private ReadinessEvidence TurnEvidence(WorkIqTurnResult result, string turnId, int mark, DiagnosticProfile profile, bool includePrompt)
    {
        var evidence = new ReadinessEvidence();
        if (includePrompt)
        {
            evidence.Add(ProbePromptKey, Microsoft365ReadinessCheck.ProbePrompt);
        }

        evidence.Add("profile", ProfileText(profile));
        evidence.Add("outcome", result.Outcome.ToString());
        evidence.Add("transport", result.Transport.ToString());
        evidence.Add("task id", result.Task?.TaskId ?? "none (no task id was received)");
        evidence.Add("context id", result.ContextId ?? "none");
        evidence.Add("observed states", ObservedStates(turnId, mark));
        evidence.Add("answer", $"{result.AnswerCompleteness}" + (result.AnswerText is null ? " (no answer text)" : ""));
        if (result.AnswerText is not null)
        {
            evidence.Add("answer length", result.AnswerText.Length);
            evidence.Add(AnswerPreviewKey, Preview(result.AnswerText));
        }

        evidence.Add("attributions", result.Evidence.CitationSources.Describe("attribution(s)"));
        evidence.Add("citation markers", result.Evidence.CitationMarkers.Describe("marker(s)"));
        evidence.Add("elapsed ms", Ms(result.Timing.Elapsed.TotalMilliseconds));
        evidence.Add("recovery polls (GetTask)", result.Timing.RecoveryRequests);
        evidence.Add("request-id", result.Failure?.RequestId ?? RequestIdSince(mark) ?? "absent");
        if (result.Failure is { } failure)
        {
            evidence.Add("HTTP status", failure.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none");
            evidence.Add("JSON-RPC code", failure.RpcErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none");
            evidence.Add("failure", $"{failure.Category} ({failure.Certainty}): {failure.SafeMessage}");
        }

        if (result.PendingInteraction is { } pending)
        {
            evidence.Add("pending interaction", $"{pending.Kind}, {pending.ProposedCalls.Length} proposed call(s); nothing was approved");
        }

        return evidence;
    }

    /// <summary>The A2A states the trace recorded for <paramref name="turnId"/> since <paramref name="mark"/>, in order, repeats collapsed.</summary>
    private string ObservedStates(string turnId, int mark)
    {
        var states = new List<string>();
        foreach (var e in EventsSince(mark))
        {
            if (e.A2AState is { } state && string.Equals(e.TurnId, turnId, StringComparison.Ordinal) && (states.Count == 0 || states[^1] != state))
            {
                states.Add(state);
            }
        }

        return states.Count == 0 ? "none observed (no task state was reported)" : string.Join(" → ", states);
    }

    /// <summary>The service <c>request-id</c> of the first send reply traced since <paramref name="mark"/>, when any.</summary>
    private string? RequestIdSince(int mark) =>
        EventsSince(mark).FirstOrDefault(e => e.Stage == WorkIqTraceStage.Send && e.Code is "rpc.response" or "rpc.error" && e.RequestId is not null)?.RequestId;

    private static string Preview(string answer)
    {
        var oneLine = answer.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        return oneLine.Length <= AnswerPreviewLength ? oneLine : oneLine[..AnswerPreviewLength] + "…";
    }

    private static string InteractionText(WorkIqTurnResult result) => result.PendingInteraction?.Kind switch
    {
        WorkIqInteractionKind.ConfirmationRequired => "a confirmation of a proposed action",
        WorkIqInteractionKind.InputRequired => "free-form input",
        WorkIqInteractionKind.AuthenticationRequired => "an external sign-in",
        WorkIqInteractionKind.Unknown => "an interaction this connector does not recognise",
        _ => result.Outcome == WorkIqTurnOutcome.AuthRequired ? "an external sign-in" : "input",
    };

    private static string ProfileText(DiagnosticProfile profile) => profile == DiagnosticProfile.Native118Diagnostics
        ? "Native118Diagnostics (EXPERIMENTAL: diagnostic modes observed in Microsoft's native evaluator 1.18.0; not a documented contract)"
        : "PublicAnswerOnly (answer, citations and attributions only)";

    // ---------------------------------------------------------------------------------------------------------
    // 6. Census (consent and a probe result checked by the caller)
    // ---------------------------------------------------------------------------------------------------------

    private async Task<ReadinessStageResult> RunCensusAsync(CancellationToken cancellationToken)
    {
        var probe = _probe ?? throw new InvalidOperationException("The census needs a probe result.");
        var started = _clock.GetTimestamp();
        var evidence = new ReadinessEvidence();
        evidence.Add("profile", ProfileText(DiagnosticProfile.Native118Diagnostics));

        WorkIqTurnResult census;
        WorkIqConversation censusConversation;
        if (probe.Profile == DiagnosticProfile.Native118Diagnostics && probe.Result.Outcome == WorkIqTurnOutcome.Completed)
        {
            census = probe.Result;
            censusConversation = probe.Conversation;
            evidence.Add("census turn", "the probe turn (it already ran under native118); no second prompt was sent");
        }
        else
        {
            censusConversation = NewConversation(WorkIqOptions with { ProfileOverride = DiagnosticProfile.Native118Diagnostics });
            var mark = _trace.Count;
            var (result, sendFailure) = await RunTurnAsync(censusConversation, CensusTurnId, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                evidence.Add("census turn", "a second turn with the probe prompt under native118 could not be sent");
                return NetworkStageFailed(ReadinessStage.Census, sendFailure!, evidence, timedOut: false) with { ElapsedMs = Elapsed(started) };
            }

            census = result;
            evidence.Add(
                "census turn",
                $"a second turn with the probe prompt under native118: outcome {census.Outcome}, task {census.Task?.TaskId ?? "none"}, " +
                $"{Ms(census.Timing.Elapsed.TotalMilliseconds)} ms, states {ObservedStates(CensusTurnId, mark)}");
        }

        var accepted = census.Outcome == WorkIqTurnOutcome.Completed;
        var refused = !accepted && IsRefusal(census);
        evidence.Add(
            "diagnostic modes accepted",
            accepted ? "yes: the native118 turn completed"
            : refused ? $"no: the service refused the turn ({FailureText(census)})"
            : $"unknown: the census turn ended {census.Outcome}");
        var developerInfo = accepted ? Presence(census.Evidence.ToolExecutions, census.Evidence.ToolSelections) : "not observed (the census turn did not complete)";
        var retrievalInfo = accepted ? Presence(census.Evidence.RetrievalExecutions) : "not observed (the census turn did not complete)";
        evidence.Add("Developer Info artifact", developerInfo);
        evidence.Add("Retrieval Info artifact", retrievalInfo);
        var markerFormat = MarkerFormat(probe.Result, census);
        evidence.Add("citation marker format", markerFormat);
        var (cancel, cancelBrief) = await CancelCompletedTaskAsync(probe, census, censusConversation, cancellationToken).ConfigureAwait(false);
        evidence.Add("CancelTask on a completed task", cancel);
        var card = _client?.Card;
        evidence.Add("streaming (card)", card is null ? "unknown (no card was discovered)" : Declared(card.Card.Capabilities?.Streaming));
        evidence.Add("pushNotifications (card)", card is null ? "unknown (no card was discovered)" : Declared(card.Card.Capabilities?.PushNotifications));

        const string meaning =
            "Informational: what this tenant does with the experimental native118 profile (diagnostic modes observed in Microsoft's native evaluator 1.18.0, not a documented contract) and with CancelTask on a finished task. The default public profile does not depend on it.";
        if (accepted || refused)
        {
            var warnings = refused
                ? new[]
                {
                    probe.Result.Outcome == WorkIqTurnOutcome.Completed && probe.Profile != DiagnosticProfile.Native118Diagnostics
                        ? "The service refused the native118 census turn while the same prompt completed under the public profile: the experimental diagnostic modes are the likely cause. Keep --m365-profile public; tool and retrieval evidence is then reported as NotRequested."
                        : "The service refused the native118 census turn; the experimental diagnostic modes may be the cause. Keep --m365-profile public; tool and retrieval evidence is then reported as NotRequested.",
                }
                : Array.Empty<string>();
            return Passed(
                ReadinessStage.Census,
                $"native118 {(accepted ? "accepted" : "refused")}; Developer Info {Short(developerInfo)}; Retrieval Info {Short(retrievalInfo)}; citations {markerFormat}; CancelTask {cancelBrief}",
                meaning,
                accepted
                    ? "Nothing to fix. Keep --m365-profile public for evaluations unless you want the tool and retrieval evidence the Developer Info / Retrieval Info artifacts carry."
                    : "Nothing to fix. Keep --m365-profile public for evaluations.",
                evidence,
                warnings) with { ElapsedMs = Elapsed(started) };
        }

        var issue = WorkIqIssueClassifier.FromException(new WorkIqTurnNotCompletedException(census), WorkIqTraceStage.Outcome);
        return Failed(
            ReadinessStage.Census,
            $"the census could not be measured: the native118 turn ended {census.Outcome}" + (census.Failure is null ? "" : $" ({census.Failure.Category}: {census.Failure.SafeMessage})"),
            issue.SuggestedCause,
            census.Failure is { } failure ? ActionFor(failure) : "Send the report folder back; the census turn's state and ids are in it.",
            issue,
            evidence) with { ElapsedMs = Elapsed(started) };
    }

    /// <summary>Whether a non-completed native118 turn is the service refusing the diagnostic modes (a recorded fact, not a measurement failure).</summary>
    private static bool IsRefusal(WorkIqTurnResult result)
    {
        if (result.Outcome == WorkIqTurnOutcome.Rejected)
        {
            return true;
        }

        return result.Failure is { Certainty: OutcomeCertainty.Known } failure
            && (failure.RpcErrorCode is (int)A2AErrorCode.ContentTypeNotSupported or (int)A2AErrorCode.InvalidParams
                || failure.HttpStatus is 400 or 415);
    }

    private static string FailureText(WorkIqTurnResult result) => result.Failure is { } failure
        ? $"{failure.Category}" + (failure.HttpStatus is { } http ? $", HTTP {http.ToString(CultureInfo.InvariantCulture)}" : "") + (failure.RpcErrorCode is { } rpc ? $", JSON-RPC {rpc.ToString(CultureInfo.InvariantCulture)}" : "")
        : $"outcome {result.Outcome}";

    /// <summary>Whether a diagnostic artifact was present, from the availability of the evidence channel it feeds.</summary>
    private static string Presence<T>(EvidenceSet<T> primary) => primary.Availability switch
    {
        EvidenceAvailability.Provided => primary.Validation == EvidenceValidation.Valid ? "present" : $"present ({primary.Validation})",
        EvidenceAvailability.NotProvided => "absent (requested, not sent)",
        EvidenceAvailability.NotRequested => "not requested",
        EvidenceAvailability.Redacted => "unknown (removed by the content policy)",
        _ => primary.Availability.ToString(),
    };

    private static string Presence<TA, TB>(EvidenceSet<TA> primary, EvidenceSet<TB> secondary) =>
        !primary.IsProvided && secondary.IsProvided ? Presence(secondary) : Presence(primary);

    /// <summary>The citation marker formats seen in the answers of the probe and census turns.</summary>
    private static string MarkerFormat(params WorkIqTurnResult[] turns)
    {
        var unicode = false;
        var markdown = false;
        var parsed = false;
        foreach (var turn in turns)
        {
            var markers = turn.Evidence.CitationMarkers;
            if (!markers.IsProvided)
            {
                continue;
            }

            parsed = true;
            if (markers.Items.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var marker in markers.Items)
            {
                unicode |= string.Equals(marker.MarkerFormat, CitationMarkerParser.UnicodeCiteFormat, StringComparison.Ordinal);
                markdown |= string.Equals(marker.MarkerFormat, CitationMarkerParser.MarkdownNumericFormat, StringComparison.Ordinal);
            }
        }

        return (unicode, markdown) switch
        {
            (true, true) => "unicode-cite and markdown",
            (true, false) => "unicode-cite",
            (false, true) => "markdown",
            _ => parsed ? "none" : "not observed (no answer text)",
        };
    }

    /// <summary>
    /// Sends <c>CancelTask</c> on a task that already completed (the probe's, else the census turn's) and reports what
    /// the service returned. TaskNotCancelable (-32002) is the expected answer and is held back from the issue register.
    /// </summary>
    private async Task<(string Detail, string Brief)> CancelCompletedTaskAsync(ProbeTurn probe, WorkIqTurnResult census, WorkIqConversation censusConversation, CancellationToken cancellationToken)
    {
        (WorkIqTaskHandle Task, WorkIqConversation Conversation)? target =
            probe.Result is { Outcome: WorkIqTurnOutcome.Completed, Task: { } probeTask } ? (probeTask, probe.Conversation)
            : census is { Outcome: WorkIqTurnOutcome.Completed, Task: { } censusTask } ? (censusTask, censusConversation)
            : null;
        if (target is null)
        {
            return probe.Result.Outcome == WorkIqTurnOutcome.Completed || census.Outcome == WorkIqTurnOutcome.Completed
                ? ("not tested: the reply was a direct message (no task to cancel)", "not tested")
                : ("not tested: no turn completed in this run", "not tested");
        }

        var (task, conversation) = target.Value;
        var filter = _issueFilter!;
        using var stage = StageTokenSource(cancellationToken, _options.StageTimeout);
        try
        {
            WorkIqTaskSnapshot snapshot;
            using (filter.Expect(i => i.RpcErrorCode == (int)A2AErrorCode.TaskNotCancelable && string.Equals(i.TaskId, task.TaskId, StringComparison.Ordinal)))
            using (Client.ExpectFailure(f => f.RpcErrorCode == (int)A2AErrorCode.TaskNotCancelable && string.Equals(f.TaskId, task.TaskId, StringComparison.Ordinal)))
            {
                snapshot = await conversation.CancelTaskAsync(task, stage.Token).ConfigureAwait(false);
            }

            return snapshot.State == WorkIqTaskState.Canceled
                ? ($"returned {snapshot.RawState}: the service canceled a task that had already completed", $"returned {snapshot.RawState}")
                : ($"returned {snapshot.RawState ?? "(no state)"}: not canceled (expected for a finished task)", $"returned {snapshot.RawState ?? "no state"}");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkIqProtocolException ex)
        {
            if (ex.Failure.RpcErrorCode == (int)A2AErrorCode.TaskNotCancelable)
            {
                // The client traced it at Info as rpc.expected_error (no issue); the filter is the backstop.
                _diagnostics.Trace(
                    WorkIqTraceStage.Doctor,
                    "doctor.census.expected_not_cancelable",
                    "the JSON-RPC TaskNotCancelable (-32002) above (rpc.expected_error) is the expected answer to CancelTask on a finished task; it is not an anomaly and was not written to the issue register",
                    taskId: task.TaskId,
                    requestId: ex.Failure.RequestId,
                    httpStatus: ex.Failure.HttpStatus,
                    rpcErrorCode: ex.Failure.RpcErrorCode);
            }

            if (ex.Failure.RpcErrorCode is { } code)
            {
                var name = code == (int)A2AErrorCode.TaskNotCancelable ? "TaskNotCancelable" : ex.Failure.Category.ToString();
                var rpc = code.ToString(CultureInfo.InvariantCulture);
                return (
                    $"error: JSON-RPC {rpc} ({name}{(code == (int)A2AErrorCode.TaskNotCancelable ? ", expected for a finished task" : "")}); request-id {ex.Failure.RequestId ?? "absent"}",
                    $"error JSON-RPC {rpc} {name}");
            }

            return ($"error: {ex.Failure.Category}: {ex.Failure.SafeMessage}", $"error {ex.Failure.Category}");
        }
    }

    private static string Short(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var paren = text.IndexOf(" (", StringComparison.Ordinal);
        var cut = colon < 0 ? paren : paren < 0 ? colon : Math.Min(colon, paren);
        return cut > 0 ? text[..cut] : text;
    }
}
