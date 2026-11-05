// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// One structured trace event emitted by a seam of the Work IQ provider. The positional members are what every
/// event has; the init properties carry whatever correlation facts the emitter knows at that moment (an absent
/// fact stays null — nothing unmeasured becomes a value). <see cref="Message"/> and <see cref="Data"/> are
/// redacted by <see cref="WorkIqDiagnostics"/> before any sink sees them, so an emitter may pass what it has.
/// </summary>
/// <param name="At">When the event happened; <c>default</c> means "stamp it" (<see cref="WorkIqDiagnostics.Trace(WorkIqTraceEvent)"/> fills it from its clock).</param>
/// <param name="Stage">The pipeline stage.</param>
/// <param name="Level">The severity.</param>
/// <param name="Code">A stable, machine-readable code such as <c>auth.device_code_prompted</c> or <c>rpc.error</c> (non-blank).</param>
/// <param name="Message">A human-readable, safe message (never null; may be empty).</param>
public sealed record WorkIqTraceEvent(DateTimeOffset At, WorkIqTraceStage Stage, WorkIqTraceLevel Level, string Code, string Message)
{
    private readonly string _code = RequireCode(Code);
    private readonly string _message = Message ?? throw new ArgumentNullException(nameof(Message));
    private readonly JsonElement? _data;

    /// <summary>A stable, machine-readable code such as <c>auth.device_code_prompted</c> or <c>rpc.error</c> (non-blank).</summary>
    public string Code
    {
        get => _code;
        init => _code = RequireCode(value);
    }

    /// <summary>A human-readable, safe message (never null; may be empty).</summary>
    public string Message
    {
        get => _message;
        init => _message = value ?? throw new ArgumentNullException(nameof(Message));
    }

    /// <summary>The run id; null means "stamp it" (<see cref="WorkIqDiagnostics"/> fills it with its own run id).</summary>
    public string? RunId { get; init; }

    /// <summary>The local benchmark case id, when known.</summary>
    public string? CaseId { get; init; }

    /// <summary>The client turn id, when known.</summary>
    public string? TurnId { get; init; }

    /// <summary>The JSON-RPC request id, when a request is involved.</summary>
    public string? RpcId { get; init; }

    /// <summary>The A2A message id (client or provider), when known.</summary>
    public string? MessageId { get; init; }

    /// <summary>The server task id, when known.</summary>
    public string? TaskId { get; init; }

    /// <summary>The server context id, when known.</summary>
    public string? ContextId { get; init; }

    /// <summary>The service's <c>request-id</c> response header, when received.</summary>
    public string? RequestId { get; init; }

    /// <summary>The HTTP status code, when a response was received.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>The JSON-RPC error code, when an error object was received.</summary>
    public int? RpcErrorCode { get; init; }

    /// <summary>The raw A2A task state (<c>TASK_STATE_*</c>), when observed.</summary>
    public string? A2AState { get; init; }

    /// <summary>Elapsed wall-clock milliseconds of the operation the event closes, when measured.</summary>
    public double? ElapsedMs { get; init; }

    /// <summary>
    /// Small structured detail (paths, counts, a header subset). Always a detached clone — never an element backed
    /// by a <see cref="JsonDocument"/> someone may dispose. Callers pass small objects; a JSONL sink truncates an
    /// oversized line and says so.
    /// </summary>
    /// <exception cref="ArgumentException">The value is an undefined element (a default struct); pass null for an absent value.</exception>
    public JsonElement? Data
    {
        get => _data;
        init => _data = JsonElementOwnership.Detach(value, nameof(Data));
    }

    /// <summary>
    /// Creates an event that stamps nothing: <see cref="At"/> is <c>default</c> and <see cref="RunId"/> is null so
    /// the <see cref="WorkIqDiagnostics"/> that traces it fills both from its clock and run id.
    /// </summary>
    /// <param name="stage">The pipeline stage.</param>
    /// <param name="code">The stable event code (non-blank).</param>
    /// <param name="message">The safe message.</param>
    /// <param name="level">The severity; <see cref="WorkIqTraceLevel.Info"/> by default.</param>
    public static WorkIqTraceEvent Create(WorkIqTraceStage stage, string code, string message, WorkIqTraceLevel level = WorkIqTraceLevel.Info) =>
        new(default, stage, level, code, message);

    private static string RequireCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code, nameof(Code));
        return code;
    }
}
