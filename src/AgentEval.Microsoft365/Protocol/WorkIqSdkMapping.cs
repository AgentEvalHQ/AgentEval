// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// Maps the A2A SDK's wire objects (<see cref="AgentTask"/>, <see cref="Message"/>, <see cref="Artifact"/>,
/// <see cref="Part"/>, <see cref="TaskState"/>, <see cref="Role"/>) onto this provider's immutable model
/// (<see cref="WorkIqTaskSnapshot"/>, <see cref="WorkIqMessage"/>, <see cref="WorkIqArtifact"/>,
/// <see cref="WorkIqPart"/>). Every <see cref="JsonElement"/> placed into a model record is detached (the records'
/// initializers clone; nothing here keeps a reference to an SDK-owned document). Nothing is invented: an absent
/// SDK value stays null, <see cref="TaskState.Unspecified"/> maps to <see cref="WorkIqTaskState.Unknown"/>, and
/// a conformance deviation the typed objects can still show (a part with several or no content fields, an
/// unspecified role, a blank id, mismatched ids) is reported through the optional <c>warnings</c> collection
/// rather than silently repaired. Unknown wire fields are not visible here — the SDK drops them while
/// deserializing — so they are counted only where the raw bytes are (the capture) and passed in as a count.
/// Owned by lane C (M13).
/// </summary>
internal static class WorkIqSdkMapping
{
    private static readonly IReadOnlyDictionary<TaskState, string> RawTaskStates = BuildWireNames<TaskState>();
    private static readonly IReadOnlyDictionary<Role, string> RawRoles = BuildWireNames<Role>();

    /// <summary>Classifies an SDK task state; <see cref="TaskState.Unspecified"/> and any undefined value are <see cref="WorkIqTaskState.Unknown"/>.</summary>
    /// <param name="state">The SDK state.</param>
    public static WorkIqTaskState ToTaskState(TaskState state) => state switch
    {
        TaskState.Submitted => WorkIqTaskState.Submitted,
        TaskState.Working => WorkIqTaskState.Working,
        TaskState.Completed => WorkIqTaskState.Completed,
        TaskState.Failed => WorkIqTaskState.Failed,
        TaskState.Canceled => WorkIqTaskState.Canceled,
        TaskState.Rejected => WorkIqTaskState.Rejected,
        TaskState.InputRequired => WorkIqTaskState.InputRequired,
        TaskState.AuthRequired => WorkIqTaskState.AuthRequired,
        _ => WorkIqTaskState.Unknown,
    };

    /// <summary>
    /// The wire name of an SDK task state (<c>TASK_STATE_COMPLETED</c>…), obtained from the SDK's own serializer so
    /// it is never guessed; null for a value the SDK does not define (such a value cannot come off the wire — the
    /// SDK rejects unknown state strings — only from code).
    /// </summary>
    /// <param name="state">The SDK state.</param>
    public static string? ToRawState(TaskState state) => RawTaskStates.TryGetValue(state, out var raw) ? raw : null;

    /// <summary>The wire name of an SDK role (<c>ROLE_USER</c>, <c>ROLE_AGENT</c>, <c>ROLE_UNSPECIFIED</c>); an undefined value is kept as its number.</summary>
    /// <param name="role">The SDK role.</param>
    public static string ToRawRole(Role role) =>
        RawRoles.TryGetValue(role, out var raw) ? raw : ((int)role).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps a part by the SDK's <see cref="Part.ContentCase"/>: text → <see cref="WorkIqPartKind.Text"/>, raw/url →
    /// <see cref="WorkIqPartKind.File"/> (bytes copied; URL kept), data → <see cref="WorkIqPartKind.Data"/>, none →
    /// <see cref="WorkIqPartKind.Unknown"/> with the part's JSON kept in <see cref="WorkIqPart.Data"/>. A part with
    /// more than one content field set is a one-of violation: the SDK's case wins and a warning is recorded.
    /// </summary>
    /// <param name="part">The SDK part.</param>
    /// <param name="warnings">Where conformance notes are appended (<c>part.one_of_violation</c>, <c>part.no_content</c>); null ignores them.</param>
    public static WorkIqPart ToPart(Part part, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(part);

        var hasData = part.Data is { ValueKind: not JsonValueKind.Undefined };
        var populated = (part.Text is null ? 0 : 1) + (part.Raw is null ? 0 : 1) + (part.Url is null ? 0 : 1) + (hasData ? 1 : 0);
        if (populated > 1)
        {
            warnings?.Add($"part.one_of_violation: {populated} of text|raw|url|data are set on one part; the SDK's ContentCase ({part.ContentCase}) was used");
        }

        var metadata = ToMetadata(part.Metadata, warnings);
        switch (part.ContentCase)
        {
            case PartContentCase.Text:
                return new WorkIqPart(WorkIqPartKind.Text, text: part.Text, mediaType: part.MediaType, filename: part.Filename, metadata: metadata);

            case PartContentCase.Raw:
                return new WorkIqPart(
                    WorkIqPartKind.File,
                    mediaType: part.MediaType,
                    filename: part.Filename,
                    rawBytes: [.. part.Raw!],
                    metadata: metadata);

            case PartContentCase.Url:
                return new WorkIqPart(WorkIqPartKind.File, mediaType: part.MediaType, filename: part.Filename, url: part.Url, metadata: metadata);

            case PartContentCase.Data when hasData:
                return new WorkIqPart(WorkIqPartKind.Data, data: part.Data, mediaType: part.MediaType, filename: part.Filename, metadata: metadata);

            default:
                warnings?.Add("part.no_content: none of text|raw|url|data is set on a part; it is kept as Unknown with its JSON");
                var serializable = hasData || part.Data is null
                    ? part
                    : new Part { Text = part.Text, Raw = part.Raw, Url = part.Url, Metadata = part.Metadata, Filename = part.Filename, MediaType = part.MediaType };
                return new WorkIqPart(
                    WorkIqPartKind.Unknown,
                    data: JsonSerializer.SerializeToElement(serializable, A2AJsonUtilities.DefaultOptions).Clone(),
                    mediaType: part.MediaType,
                    filename: part.Filename,
                    metadata: metadata);
        }
    }

    /// <summary>
    /// Maps a message with its parts, metadata (where attributions live), context and task ids. The SDK's
    /// <c>extensions</c> and <c>referenceTaskIds</c> have no slot in the model and are not carried (they remain in
    /// the raw capture).
    /// </summary>
    /// <param name="message">The SDK message.</param>
    /// <param name="warnings">Where conformance notes are appended (<c>message.id_missing</c>, <c>message.role_unspecified</c>, part notes); null ignores them.</param>
    public static WorkIqMessage ToMessage(Message message, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrEmpty(message.MessageId))
        {
            warnings?.Add("message.id_missing: a message has no messageId (required by A2A 1.0)");
        }

        if (!RawRoles.ContainsKey(message.Role))
        {
            warnings?.Add($"message.role_unknown: role value {(int)message.Role} is not defined by the SDK");
        }
        else if (message.Role == Role.Unspecified)
        {
            warnings?.Add("message.role_unspecified: a message carries ROLE_UNSPECIFIED");
        }

        var parts = message.Parts is null
            ? ImmutableArray<WorkIqPart>.Empty
            : message.Parts.Select(p => ToPart(p, warnings)).ToImmutableArray();

        return new WorkIqMessage(
            message.MessageId ?? "",
            ToRawRole(message.Role),
            parts,
            ToMetadata(message.Metadata, warnings),
            message.ContextId,
            message.TaskId);
    }

    /// <summary>Maps an artifact as observed (not closed; <paramref name="revision"/> as the caller counts it — 0 for a snapshot-loaded artifact).</summary>
    /// <param name="artifact">The SDK artifact.</param>
    /// <param name="revision">The revision counter to stamp.</param>
    /// <param name="warnings">Where conformance notes are appended (<c>artifact.id_missing</c>, part notes); null ignores them.</param>
    public static WorkIqArtifact ToArtifact(Artifact artifact, int revision, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (string.IsNullOrEmpty(artifact.ArtifactId))
        {
            warnings?.Add("artifact.id_missing: an artifact has no artifactId (required by A2A 1.0)");
        }

        var parts = artifact.Parts is null
            ? ImmutableArray<WorkIqPart>.Empty
            : artifact.Parts.Select(p => ToPart(p, warnings)).ToImmutableArray();

        return new WorkIqArtifact(
            artifact.ArtifactId ?? "",
            artifact.Name,
            artifact.Description,
            parts,
            revision,
            isClosed: false,
            ToMetadata(artifact.Metadata, warnings));
    }

    /// <summary>
    /// Maps a full task into a snapshot: the handle reconciled with the task's own ids (a differing task id or
    /// context id is recorded as a warning and the task's id wins, so the snapshot always describes the task it
    /// came from), state classified and raw, status timestamp and message, artifacts in wire order with
    /// revision 0, history, and the mapping's own conformance notes. <paramref name="schemaWarnings"/> is the number
    /// of warnings the caller counted on the raw bytes before mapping (unknown fields are invisible here); when
    /// positive it is recorded as one <c>upstream.schema_warnings</c> entry so the count is never lost.
    /// </summary>
    /// <param name="task">The SDK task.</param>
    /// <param name="handle">The handle the caller addressed the task with (endpoint, agent, principal); its ids are reconciled with the task's.</param>
    /// <param name="source">Which operation produced the task.</param>
    /// <param name="at">Local receipt time of the material.</param>
    /// <param name="schemaWarnings">Warnings counted upstream on the raw bytes (0 when none or unknown).</param>
    public static WorkIqTaskSnapshot ToSnapshot(AgentTask task, WorkIqTaskHandle handle, WorkIqSnapshotSource source, DateTimeOffset at, int schemaWarnings)
    {
        var warnings = new List<string>();
        if (schemaWarnings > 0)
        {
            warnings.Add($"upstream.schema_warnings: {schemaWarnings} warning(s) were counted on the raw bytes before mapping (unknown fields are only visible in the raw capture)");
        }

        return ToSnapshot(task, handle, source, at, warnings, capture: null);
    }

    /// <summary>The same mapping with the caller's warning list (appended to) and the raw capture reference.</summary>
    /// <param name="task">The SDK task.</param>
    /// <param name="handle">The handle the caller addressed the task with.</param>
    /// <param name="source">Which operation produced the task.</param>
    /// <param name="at">Local receipt time of the material.</param>
    /// <param name="warnings">The warnings so far; the mapping appends its own.</param>
    /// <param name="capture">The raw capture the task was read from, when retained.</param>
    internal static WorkIqTaskSnapshot ToSnapshot(
        AgentTask task,
        WorkIqTaskHandle handle,
        WorkIqSnapshotSource source,
        DateTimeOffset at,
        List<string> warnings,
        RawCaptureRef? capture)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(warnings);

        var reconciled = ReconcileHandle(handle, task.Id, task.ContextId, warnings, "task");
        var status = task.Status ?? new A2A.TaskStatus();
        NoteState(status.State, warnings, "task");

        var artifacts = ImmutableArray<WorkIqArtifact>.Empty;
        if (task.Artifacts is { Count: > 0 })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var builder = ImmutableArray.CreateBuilder<WorkIqArtifact>(task.Artifacts.Count);
            foreach (var artifact in task.Artifacts)
            {
                var mapped = ToArtifact(artifact, revision: 0, warnings);
                if (!seen.Add(mapped.ArtifactId))
                {
                    warnings.Add($"artifact.duplicate_id_in_snapshot: artifact id '{mapped.ArtifactId}' appears more than once in one task snapshot; every occurrence is kept");
                }

                builder.Add(mapped);
            }

            artifacts = builder.MoveToImmutable();
        }

        var history = task.History is { Count: > 0 }
            ? task.History.Select(m => ToMessage(m, warnings)).ToImmutableArray()
            : ImmutableArray<WorkIqMessage>.Empty;

        return new WorkIqTaskSnapshot(
            reconciled,
            ToTaskState(status.State),
            ToRawState(status.State),
            status.Timestamp,
            status.Message is null ? null : ToMessage(status.Message, warnings),
            artifacts,
            history,
            source,
            at,
            warnings.ToImmutableArray(),
            capture);
    }

    /// <summary>
    /// Reconciles a handle with the ids a wire object carries: a blank handle task id is "not yet known" and is
    /// filled; a different non-blank one is a <c>&lt;scope&gt;.id_mismatch</c> warning and the wire id wins; the
    /// context id is filled when the handle has none, and a differing one is a <c>&lt;scope&gt;.context_id_mismatch</c>
    /// warning (the wire id wins). A blank wire task id is a <c>&lt;scope&gt;.id_missing</c> warning and leaves the handle.
    /// </summary>
    /// <param name="handle">The current handle.</param>
    /// <param name="taskId">The wire task id.</param>
    /// <param name="contextId">The wire context id.</param>
    /// <param name="warnings">Where notes are appended.</param>
    /// <param name="scope">The warning prefix (<c>task</c>, <c>status</c>, <c>artifact</c>, <c>message</c>).</param>
    internal static WorkIqTaskHandle ReconcileHandle(WorkIqTaskHandle handle, string? taskId, string? contextId, ICollection<string> warnings, string scope)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(warnings);

        var result = handle;
        if (string.IsNullOrEmpty(taskId))
        {
            warnings.Add($"{scope}.id_missing: the {scope} carries no task id (required by A2A 1.0)");
        }
        else if (string.IsNullOrEmpty(handle.TaskId))
        {
            result = result with { TaskId = taskId };
        }
        else if (!string.Equals(handle.TaskId, taskId, StringComparison.Ordinal))
        {
            warnings.Add($"{scope}.id_mismatch: expected task '{handle.TaskId}' but the {scope} carries '{taskId}'; the {scope}'s id is used");
            result = result with { TaskId = taskId };
        }

        if (string.IsNullOrEmpty(contextId))
        {
            if (string.Equals(scope, "task", StringComparison.Ordinal))
            {
                warnings.Add("task.context_id_missing: the task carries no contextId (required by A2A 1.0)");
            }
        }
        else if (string.IsNullOrEmpty(handle.ContextId))
        {
            result = result with { ContextId = contextId };
        }
        else if (!string.Equals(handle.ContextId, contextId, StringComparison.Ordinal))
        {
            warnings.Add($"{scope}.context_id_mismatch: expected context '{handle.ContextId}' but the {scope} carries '{contextId}'; the {scope}'s id is used");
            result = result with { ContextId = contextId };
        }

        return result;
    }

    /// <summary>Records <c>&lt;scope&gt;.state_unspecified</c> / <c>&lt;scope&gt;.state_unknown</c> for a state that classifies as <see cref="WorkIqTaskState.Unknown"/>.</summary>
    /// <param name="state">The SDK state.</param>
    /// <param name="warnings">Where notes are appended.</param>
    /// <param name="scope">The warning prefix.</param>
    internal static void NoteState(TaskState state, ICollection<string> warnings, string scope)
    {
        if (state == TaskState.Unspecified)
        {
            warnings.Add($"{scope}.state_unspecified: the status carries TASK_STATE_UNSPECIFIED; it never implies completion");
        }
        else if (!RawTaskStates.ContainsKey(state))
        {
            warnings.Add($"{scope}.state_unknown: state value {(int)state} is not defined by the SDK; it never implies completion");
        }
    }

    /// <summary>Serializes an SDK metadata dictionary into one detached JSON object; null for null. An undefined element value is dropped with a warning.</summary>
    /// <param name="metadata">The SDK metadata.</param>
    /// <param name="warnings">Where <c>metadata.undefined_value</c> notes are appended; null ignores them.</param>
    internal static JsonElement? ToMetadata(Dictionary<string, JsonElement>? metadata, ICollection<string>? warnings = null)
    {
        if (metadata is null)
        {
            return null;
        }

        Dictionary<string, JsonElement> safe = metadata;
        if (metadata.Values.Any(v => v.ValueKind == JsonValueKind.Undefined))
        {
            safe = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, value) in metadata)
            {
                if (value.ValueKind == JsonValueKind.Undefined)
                {
                    warnings?.Add($"metadata.undefined_value: metadata key '{key}' had no JSON value and was dropped");
                    continue;
                }

                safe[key] = value;
            }
        }

        return JsonSerializer.SerializeToElement(safe, A2AJsonUtilities.DefaultOptions).Clone();
    }

    /// <summary>Builds enum → wire-name from the SDK's own serializer, so the names are the SDK's, never typed here.</summary>
    private static IReadOnlyDictionary<TEnum, string> BuildWireNames<TEnum>()
        where TEnum : struct, Enum
    {
        var names = new Dictionary<TEnum, string>();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var json = JsonSerializer.Serialize(value, A2AJsonUtilities.DefaultOptions);
            var name = JsonSerializer.Deserialize<string>(json);
            if (!string.IsNullOrEmpty(name))
            {
                names[value] = name;
            }
        }

        return names;
    }
}
