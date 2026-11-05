// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>
/// The A2A 1.0 task lifecycle state as this provider classifies it. <see cref="Unknown"/> is any state the
/// provider did not recognise; it never implies completion.
/// </summary>
public enum WorkIqTaskState
{
    /// <summary>The state was absent or not recognised; the raw string is in <see cref="WorkIqTaskSnapshot.RawState"/>.</summary>
    Unknown,

    /// <summary><c>TASK_STATE_SUBMITTED</c>: accepted, not started.</summary>
    Submitted,

    /// <summary><c>TASK_STATE_WORKING</c>: in progress.</summary>
    Working,

    /// <summary><c>TASK_STATE_COMPLETED</c>: the only state that can yield a final answer.</summary>
    Completed,

    /// <summary><c>TASK_STATE_FAILED</c>: terminal failure; partial artifacts are retained but never an answer.</summary>
    Failed,

    /// <summary><c>TASK_STATE_CANCELED</c>: terminal; confirmed by the service, not by a local stop.</summary>
    Canceled,

    /// <summary><c>TASK_STATE_REJECTED</c>: terminal; the service refused the task.</summary>
    Rejected,

    /// <summary><c>TASK_STATE_INPUT_REQUIRED</c>: suspended, resumable with new input on the same task.</summary>
    InputRequired,

    /// <summary><c>TASK_STATE_AUTH_REQUIRED</c>: suspended, needs an external authorization workflow.</summary>
    AuthRequired,
}

/// <summary>Which operation produced a task snapshot.</summary>
public enum WorkIqSnapshotSource
{
    /// <summary>The <c>SendMessage</c> reply.</summary>
    Send,

    /// <summary>A <c>GetTask</c> reply (recovery or explicit read).</summary>
    GetTask,

    /// <summary>The first frame of a <c>SubscribeToTask</c> stream (replaces state).</summary>
    Subscribe,

    /// <summary>Reduced from <c>SendStreamingMessage</c> frames.</summary>
    Stream,
}

/// <summary>The kind of content a part carries.</summary>
public enum WorkIqPartKind
{
    /// <summary>A text part.</summary>
    Text,

    /// <summary>A JSON data part.</summary>
    Data,

    /// <summary>A file part (URL or raw bytes).</summary>
    File,

    /// <summary>A part whose content case was not recognised; its raw JSON is kept in <see cref="WorkIqPart.Data"/>.</summary>
    Unknown,
}

/// <summary>
/// One part of a message or artifact, with its boundaries, media type and metadata preserved. Every
/// <see cref="JsonElement"/> held here is a detached clone (enforced by the initializers); an undefined element
/// is rejected, a null means absent.
/// </summary>
public sealed record WorkIqPart
{
    private readonly JsonElement? _data;
    private readonly JsonElement? _metadata;

    /// <summary>Creates a part.</summary>
    /// <param name="kind">The content kind.</param>
    /// <param name="text">The text for a <see cref="WorkIqPartKind.Text"/> part.</param>
    /// <param name="data">The JSON for a <see cref="WorkIqPartKind.Data"/> part, or the raw part JSON for <see cref="WorkIqPartKind.Unknown"/>; cloned.</param>
    /// <param name="mediaType">The declared media type, when any.</param>
    /// <param name="filename">The declared file name, when any.</param>
    /// <param name="url">The file URL for a <see cref="WorkIqPartKind.File"/> part by reference.</param>
    /// <param name="rawBytes">The file bytes for a <see cref="WorkIqPartKind.File"/> part by value; default means none.</param>
    /// <param name="metadata">The part metadata object, when any; cloned.</param>
    public WorkIqPart(
        WorkIqPartKind kind,
        string? text = null,
        JsonElement? data = null,
        string? mediaType = null,
        string? filename = null,
        string? url = null,
        ImmutableArray<byte> rawBytes = default,
        JsonElement? metadata = null)
    {
        Kind = kind;
        Text = text;
        Data = data;
        MediaType = mediaType;
        Filename = filename;
        Url = url;
        RawBytes = rawBytes.IsDefault ? ImmutableArray<byte>.Empty : rawBytes;
        Metadata = metadata;
    }

    /// <summary>The content kind.</summary>
    public WorkIqPartKind Kind { get; init; }

    /// <summary>The text of a text part; null otherwise.</summary>
    public string? Text { get; init; }

    /// <summary>The JSON of a data part (or the raw JSON of an unknown part); always a detached clone.</summary>
    public JsonElement? Data
    {
        get => _data;
        init => _data = JsonElementOwnership.Detach(value, nameof(Data));
    }

    /// <summary>The declared media type, when any.</summary>
    public string? MediaType { get; init; }

    /// <summary>The declared file name, when any.</summary>
    public string? Filename { get; init; }

    /// <summary>The file URL of a file-by-reference part; null otherwise.</summary>
    public string? Url { get; init; }

    /// <summary>The bytes of a file-by-value part; empty otherwise.</summary>
    public ImmutableArray<byte> RawBytes { get; init; }

    /// <summary>The part's metadata object; always a detached clone.</summary>
    public JsonElement? Metadata
    {
        get => _metadata;
        init => _metadata = JsonElementOwnership.Detach(value, nameof(Metadata));
    }
}

/// <summary>An A2A message (user input, agent reply, or a task's status message) with its parts and metadata preserved.</summary>
public sealed record WorkIqMessage
{
    private readonly JsonElement? _metadata;

    /// <summary>Creates a message.</summary>
    /// <param name="messageId">The message id.</param>
    /// <param name="role">The raw role string (for example <c>ROLE_USER</c>, <c>ROLE_AGENT</c>).</param>
    /// <param name="parts">The parts in order.</param>
    /// <param name="metadata">The metadata object (where attributions live), when any; cloned.</param>
    /// <param name="contextId">The context id carried by the message, when any.</param>
    /// <param name="taskId">The task id carried by the message, when any.</param>
    public WorkIqMessage(
        string messageId,
        string role,
        ImmutableArray<WorkIqPart> parts,
        JsonElement? metadata = null,
        string? contextId = null,
        string? taskId = null)
    {
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        Role = role ?? throw new ArgumentNullException(nameof(role));
        Parts = parts.IsDefault ? ImmutableArray<WorkIqPart>.Empty : parts;
        Metadata = metadata;
        ContextId = contextId;
        TaskId = taskId;
    }

    /// <summary>The message id.</summary>
    public string MessageId { get; init; }

    /// <summary>The raw role string as sent.</summary>
    public string Role { get; init; }

    /// <summary>The parts in order (may be empty — the service has been observed to send empty status-message parts with metadata).</summary>
    public ImmutableArray<WorkIqPart> Parts { get; init; }

    /// <summary>The metadata object; always a detached clone.</summary>
    public JsonElement? Metadata
    {
        get => _metadata;
        init => _metadata = JsonElementOwnership.Detach(value, nameof(Metadata));
    }

    /// <summary>The context id carried by the message, when any.</summary>
    public string? ContextId { get; init; }

    /// <summary>The task id carried by the message, when any.</summary>
    public string? TaskId { get; init; }
}

/// <summary>
/// An output artifact as currently reduced: its parts (append/replace applied), its revision counter and whether
/// its chunk sequence was closed by <c>lastChunk</c>. Closing an artifact never closes the task.
/// </summary>
public sealed record WorkIqArtifact
{
    private readonly JsonElement? _metadata;

    /// <summary>Creates an artifact.</summary>
    /// <param name="artifactId">The artifact id (the reducer key together with the task id).</param>
    /// <param name="name">The declared name (for example <c>Answer</c>, <c>Developer Info</c>), when any.</param>
    /// <param name="description">The declared description, when any.</param>
    /// <param name="parts">The parts in order after reduction.</param>
    /// <param name="revision">The number of updates applied so far (0 for a snapshot-loaded artifact).</param>
    /// <param name="isClosed">Whether a <c>lastChunk: true</c> update was observed.</param>
    /// <param name="metadata">The artifact metadata object, when any; cloned.</param>
    public WorkIqArtifact(
        string artifactId,
        string? name,
        string? description,
        ImmutableArray<WorkIqPart> parts,
        int revision,
        bool isClosed,
        JsonElement? metadata = null)
    {
        ArtifactId = artifactId ?? throw new ArgumentNullException(nameof(artifactId));
        Name = name;
        Description = description;
        Parts = parts.IsDefault ? ImmutableArray<WorkIqPart>.Empty : parts;
        Revision = revision;
        IsClosed = isClosed;
        Metadata = metadata;
    }

    /// <summary>The artifact id.</summary>
    public string ArtifactId { get; init; }

    /// <summary>The declared name, when any.</summary>
    public string? Name { get; init; }

    /// <summary>The declared description, when any.</summary>
    public string? Description { get; init; }

    /// <summary>The parts in order after reduction.</summary>
    public ImmutableArray<WorkIqPart> Parts { get; init; }

    /// <summary>The number of updates applied so far.</summary>
    public int Revision { get; init; }

    /// <summary>Whether a <c>lastChunk: true</c> update closed the chunk sequence.</summary>
    public bool IsClosed { get; init; }

    /// <summary>The artifact metadata object; always a detached clone.</summary>
    public JsonElement? Metadata
    {
        get => _metadata;
        init => _metadata = JsonElementOwnership.Detach(value, nameof(Metadata));
    }
}

/// <summary>
/// The current reduced view of one remote task: handle, exact state (classified and raw), server status
/// timestamp, status message, ordered artifacts and history, which operation produced it, schema warnings
/// (unknown fields, one-of violations) and the raw capture it was reduced from. A snapshot establishes
/// completion only when <see cref="State"/> is <see cref="WorkIqTaskState.Completed"/>.
/// </summary>
/// <param name="Handle">The task's handle.</param>
/// <param name="State">The classified state.</param>
/// <param name="RawState">The raw <c>status.state</c> string as received (for example <c>TASK_STATE_COMPLETED</c>); null when absent.</param>
/// <param name="StatusTimestamp">The server's <c>status.timestamp</c>, when sent; distinct from local receipt time.</param>
/// <param name="StatusMessage">The status message (progress text and, on terminal states, attribution metadata); never answer text.</param>
/// <param name="Artifacts">The artifacts in first-seen order, append/replace applied.</param>
/// <param name="History">The task history messages, when the service returned them.</param>
/// <param name="Source">Which operation produced the snapshot.</param>
/// <param name="ObservedAt">Local receipt time of the material the snapshot was reduced from.</param>
/// <param name="SchemaWarnings">Unknown fields, unmatched ids, one-of violations and similar conformance notes.</param>
/// <param name="Capture">The raw capture the snapshot was reduced from, when retained.</param>
public sealed record WorkIqTaskSnapshot(
    WorkIqTaskHandle Handle,
    WorkIqTaskState State,
    string? RawState,
    DateTimeOffset? StatusTimestamp,
    WorkIqMessage? StatusMessage,
    ImmutableArray<WorkIqArtifact> Artifacts,
    ImmutableArray<WorkIqMessage> History,
    WorkIqSnapshotSource Source,
    DateTimeOffset ObservedAt,
    ImmutableArray<string> SchemaWarnings,
    RawCaptureRef? Capture)
{
    /// <summary><see langword="true"/> for <see cref="WorkIqTaskState.Completed"/>, <see cref="WorkIqTaskState.Failed"/>, <see cref="WorkIqTaskState.Canceled"/> and <see cref="WorkIqTaskState.Rejected"/>.</summary>
    public bool IsTerminal => State is WorkIqTaskState.Completed or WorkIqTaskState.Failed or WorkIqTaskState.Canceled or WorkIqTaskState.Rejected;

    /// <summary><see langword="true"/> for the resumable states <see cref="WorkIqTaskState.InputRequired"/> and <see cref="WorkIqTaskState.AuthRequired"/>.</summary>
    public bool IsSuspended => State is WorkIqTaskState.InputRequired or WorkIqTaskState.AuthRequired;
}
