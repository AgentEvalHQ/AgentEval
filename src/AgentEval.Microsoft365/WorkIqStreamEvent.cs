// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>
/// The rich event stream of <see cref="IWorkIqConversation.StreamTurnAsync"/> and
/// <see cref="IWorkIqConversation.SubscribeAsync"/>: a closed set of variants (nested types) plus
/// <see cref="UnknownProviderEvent"/> for anything else, each carrying the turn id, a local sequence number and
/// the local receipt time. Artifact revisions keep their structure; nothing is flattened to text prematurely.
/// The stream always ends with <see cref="TurnFinished"/>, whose result states the local outcome — it does not
/// imply that the remote task completed.
/// </summary>
/// <param name="TurnId">The client turn id the event belongs to.</param>
/// <param name="Sequence">Local, monotonically increasing sequence inside the stream (client order, not server order).</param>
/// <param name="ObservedAt">Local receipt time.</param>
public abstract record WorkIqStreamEvent(string TurnId, long Sequence, DateTimeOffset ObservedAt)
{
    /// <summary>A full task snapshot was observed (initial reply, or the first frame of a subscription, which replaces state).</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Snapshot">The snapshot.</param>
    public sealed record TaskSnapshotObserved(string TurnId, long Sequence, DateTimeOffset ObservedAt, WorkIqTaskSnapshot Snapshot)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);

    /// <summary>A <c>statusUpdate</c> was observed; only <see cref="WorkIqTaskState.Completed"/> establishes completion.</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Task">The task the status belongs to.</param>
    /// <param name="State">The classified state.</param>
    /// <param name="RawState">The raw state string.</param>
    /// <param name="StatusTimestamp">The server timestamp, when sent.</param>
    /// <param name="StatusMessage">The status message, when sent (progress text and, on terminal states, attribution metadata).</param>
    public sealed record TaskStatusObserved(
        string TurnId,
        long Sequence,
        DateTimeOffset ObservedAt,
        WorkIqTaskHandle Task,
        WorkIqTaskState State,
        string? RawState,
        DateTimeOffset? StatusTimestamp,
        WorkIqMessage? StatusMessage)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);

    /// <summary>An <c>artifactUpdate</c> was applied; <paramref name="LastChunk"/> closes this artifact's chunk sequence, not the task.</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Task">The task the artifact belongs to.</param>
    /// <param name="Artifact">The artifact after the update was applied.</param>
    /// <param name="Append">Whether the update appended (<see langword="true"/>) or replaced (<see langword="false"/> / omitted) the content.</param>
    /// <param name="LastChunk">Whether the update carried <c>lastChunk: true</c>.</param>
    public sealed record ArtifactRevisionObserved(
        string TurnId,
        long Sequence,
        DateTimeOffset ObservedAt,
        WorkIqTaskHandle Task,
        WorkIqArtifact Artifact,
        bool Append,
        bool LastChunk)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);

    /// <summary>Provider progress/status chatter (a non-terminal status message); never answer text and never a reasoning trace.</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Task">The task, when known.</param>
    /// <param name="Message">The status message.</param>
    public sealed record ProgressObserved(
        string TurnId,
        long Sequence,
        DateTimeOffset ObservedAt,
        WorkIqTaskHandle? Task,
        WorkIqMessage Message)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);

    /// <summary>The evidence document was normalised (once, after the authoritative final state).</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Evidence">The evidence document.</param>
    public sealed record EvidenceObserved(string TurnId, long Sequence, DateTimeOffset ObservedAt, Microsoft365TurnEvidence Evidence)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);

    /// <summary>The task paused for input, approval or authentication; nothing is auto-answered.</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Interaction">The pending interaction.</param>
    public sealed record InteractionRequired(string TurnId, long Sequence, DateTimeOffset ObservedAt, WorkIqPendingInteraction Interaction)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);

    /// <summary>A conformance deviation that did not stop the turn (unknown field, empty status-message parts, id mismatch).</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Code">Stable code.</param>
    /// <param name="SafeMessage">Summary without tenant content.</param>
    /// <param name="Sources">Where it was observed.</param>
    public sealed record ProtocolWarning(
        string TurnId,
        long Sequence,
        DateTimeOffset ObservedAt,
        string Code,
        string SafeMessage,
        ImmutableArray<SourceRef> Sources)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);

    /// <summary>A frame that is none of the known variants; its JSON is retained (cloned) so a future decoder can read it.</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Raw">The frame payload, cloned.</param>
    /// <param name="SseEvent">The SSE <c>event</c> field, when any.</param>
    /// <param name="SseId">The SSE <c>id</c> field, when any.</param>
    public sealed record UnknownProviderEvent(
        string TurnId,
        long Sequence,
        DateTimeOffset ObservedAt,
        JsonElement Raw,
        string? SseEvent,
        string? SseId)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt)
    {
        private readonly JsonElement _raw = JsonElementOwnership.Detach(Raw, nameof(Raw)).GetValueOrDefault();

        /// <summary>The frame payload, always a detached clone.</summary>
        public JsonElement Raw
        {
            get => _raw;
            init => _raw = JsonElementOwnership.Detach(value, nameof(Raw)).GetValueOrDefault();
        }
    }

    /// <summary>The local operation reached its defined outcome; <see cref="Result"/> says which. Always the last event.</summary>
    /// <param name="TurnId">The client turn id.</param>
    /// <param name="Sequence">Local sequence.</param>
    /// <param name="ObservedAt">Local receipt time.</param>
    /// <param name="Result">The turn result (completed, suspended, failed, canceled or indeterminate).</param>
    public sealed record TurnFinished(string TurnId, long Sequence, DateTimeOffset ObservedAt, WorkIqTurnResult Result)
        : WorkIqStreamEvent(TurnId, Sequence, ObservedAt);
}
