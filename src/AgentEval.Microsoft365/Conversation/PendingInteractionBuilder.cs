// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// Builds the host-facing pause of a suspended task. <see cref="InteractionDecoder.TryDecode"/> reads the interaction
/// data part of the status message: a confirmation card with every proposed call and the exact
/// <c>confirmationOption</c> strings, an <c>auth-required</c> payload, or an unrecognised payload kept raw. Proposed
/// calls keep their multiplicity; only byte-identical <c>action</c>/<c>actionId</c> twins merge. The content policy
/// applies here: <see cref="EvidenceContentPolicy.MetadataOnly"/> drops the prompt text and the raw payload. A
/// text-only pause becomes a plain <see cref="WorkIqInteractionKind.InputRequired"/> or
/// <see cref="WorkIqInteractionKind.AuthenticationRequired"/> pause, by task state. Nothing is answered.
/// </summary>
internal sealed class PendingInteractionBuilder
{
    private readonly WorkIqOptions _options;
    private readonly WorkIqDiagnostics _diagnostics;

    public PendingInteractionBuilder(WorkIqOptions options, WorkIqDiagnostics diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
    }

    /// <summary>The pause of a suspended task.</summary>
    /// <param name="snapshot">The suspended task.</param>
    /// <param name="captureId">The capture the snapshot came from (for source pointers).</param>
    public WorkIqPendingInteraction Build(WorkIqTaskSnapshot snapshot, string captureId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var retainContent = _options.ContentPolicy != EvidenceContentPolicy.MetadataOnly;
        var decoded = snapshot.StatusMessage is { } message ? InteractionDecoder.TryDecode(message, snapshot.Handle, captureId) : null;
        if (decoded is null)
        {
            return TextOnly(snapshot, retainContent);
        }

        if (decoded.Kind == WorkIqInteractionKind.Unknown)
        {
            _diagnostics.Trace(
                WorkIqTraceStage.Reduce,
                "interaction.unrecognised",
                $"the pause payload is not recognised by {decoded.ProfileId}; retained raw, never answered",
                WorkIqTraceLevel.Warning,
                taskId: snapshot.Handle.TaskId,
                contextId: snapshot.Handle.ContextId,
                a2aState: snapshot.RawState);
        }
        else
        {
            _diagnostics.Trace(
                WorkIqTraceStage.Reduce,
                "interaction.decoded",
                $"pause {decoded.Kind}: {decoded.ProposedCalls.Length} proposed call(s), options {(decoded.RecognizedOptions.IsEmpty ? "none" : string.Join("/", decoded.RecognizedOptions))}, " +
                $"sourceRequestId {(decoded.SourceRequestId is null ? "absent" : "present")}",
                taskId: snapshot.Handle.TaskId,
                contextId: snapshot.Handle.ContextId,
                a2aState: snapshot.RawState);
        }

        return retainContent ? decoded : decoded with { PromptText = null, RawInteractionData = null };
    }

    /// <summary>A pause without an interaction data part: its kind comes from the task state, with no options and no proposed calls.</summary>
    private WorkIqPendingInteraction TextOnly(WorkIqTaskSnapshot snapshot, bool retainContent)
    {
        var kind = snapshot.State == WorkIqTaskState.AuthRequired ? WorkIqInteractionKind.AuthenticationRequired : WorkIqInteractionKind.InputRequired;
        string? prompt = null;
        if (retainContent && snapshot.StatusMessage is { } textMessage)
        {
            var text = string.Concat(textMessage.Parts.Where(p => p.Kind == WorkIqPartKind.Text && p.Text is not null).Select(p => p.Text));
            prompt = text.Length == 0 ? null : text;
        }

        _diagnostics.Trace(
            WorkIqTraceStage.Reduce,
            "interaction.text_only",
            $"the {snapshot.RawState ?? "(absent)"} pause carries no interaction data part; surfaced as {kind} with no options and no proposed calls",
            WorkIqTraceLevel.Warning,
            taskId: snapshot.Handle.TaskId,
            contextId: snapshot.Handle.ContextId,
            a2aState: snapshot.RawState);
        return new WorkIqPendingInteraction(
            snapshot.Handle,
            kind,
            null,
            prompt,
            ImmutableArray<WorkIqProposedCall>.Empty,
            ImmutableArray<string>.Empty,
            InteractionDecoder.ProfileId,
            null,
            ImmutableArray<SourceRef>.Empty);
    }
}
