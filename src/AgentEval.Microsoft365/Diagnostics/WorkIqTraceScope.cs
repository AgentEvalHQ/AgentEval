// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// A measured operation opened by <see cref="WorkIqDiagnostics.Measure"/>: <c>&lt;code&gt;.start</c> was traced on
/// creation; <see cref="Dispose"/> traces <c>&lt;code&gt;.end</c> with the elapsed milliseconds, <see cref="Fail"/>
/// traces <c>&lt;code&gt;.failed</c> at <see cref="WorkIqTraceLevel.Error"/> with the exception type and its
/// redacted message (and suppresses <c>.end</c>). Facts learned during the operation (task id, HTTP status…) are
/// added with <see cref="Observe"/> so the closing event carries them. Exactly one closing event is traced.
/// </summary>
public sealed class WorkIqTraceScope : IDisposable
{
    private readonly WorkIqDiagnostics _owner;
    private readonly long _startTimestamp;
    private readonly string _message;
    private string? _caseId;
    private string? _turnId;
    private string? _taskId;
    private string? _contextId;
    private string? _requestId;
    private int? _httpStatus;
    private int? _rpcErrorCode;
    private string? _a2aState;
    private int _closed;

    internal WorkIqTraceScope(
        WorkIqDiagnostics owner,
        WorkIqTraceStage stage,
        string code,
        string message,
        string? caseId,
        string? turnId,
        string? taskId,
        string? contextId,
        string? requestId)
    {
        _owner = owner;
        Stage = stage;
        Code = code;
        _message = message;
        _caseId = caseId;
        _turnId = turnId;
        _taskId = taskId;
        _contextId = contextId;
        _requestId = requestId;
        _startTimestamp = owner.Clock.GetTimestamp();
        owner.Trace(Build(".start", WorkIqTraceLevel.Info, message, elapsedMs: null, data: null));
    }

    /// <summary>The pipeline stage.</summary>
    public WorkIqTraceStage Stage { get; }

    /// <summary>The base code; the events are <c>Code.start</c>, <c>Code.end</c> and <c>Code.failed</c>.</summary>
    public string Code { get; }

    /// <summary>Whether <see cref="Fail"/> was called.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>Whether the closing event was traced (by <see cref="Fail"/> or <see cref="Dispose"/>).</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Elapsed time since the scope was opened, from the diagnostics clock.</summary>
    public TimeSpan Elapsed => _owner.Clock.GetElapsedTime(_startTimestamp);

    /// <summary>Records facts learned during the operation; a null argument leaves the current value.</summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="contextId">The context id.</param>
    /// <param name="requestId">The service <c>request-id</c>.</param>
    /// <param name="httpStatus">The HTTP status.</param>
    /// <param name="rpcErrorCode">The JSON-RPC error code.</param>
    /// <param name="a2aState">The raw A2A task state.</param>
    /// <param name="caseId">The case id.</param>
    /// <param name="turnId">The turn id.</param>
    public WorkIqTraceScope Observe(
        string? taskId = null,
        string? contextId = null,
        string? requestId = null,
        int? httpStatus = null,
        int? rpcErrorCode = null,
        string? a2aState = null,
        string? caseId = null,
        string? turnId = null)
    {
        _taskId = taskId ?? _taskId;
        _contextId = contextId ?? _contextId;
        _requestId = requestId ?? _requestId;
        _httpStatus = httpStatus ?? _httpStatus;
        _rpcErrorCode = rpcErrorCode ?? _rpcErrorCode;
        _a2aState = a2aState ?? _a2aState;
        _caseId = caseId ?? _caseId;
        _turnId = turnId ?? _turnId;
        return this;
    }

    /// <summary>
    /// Traces <c>&lt;code&gt;.failed</c> at <see cref="WorkIqTraceLevel.Error"/> with the elapsed milliseconds, the
    /// exception type and its redacted message; a later <see cref="Dispose"/> traces nothing more.
    /// </summary>
    /// <param name="exception">The exception that ended the operation.</param>
    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        IsFailed = true;
        var elapsed = Elapsed.TotalMilliseconds;
        var message = $"{_message} failed: {exception.GetType().Name}: {WorkIqRedaction.Redact(exception.Message)}";
        var data = JsonSerializer.SerializeToElement(
            new FailureData(exception.GetType().FullName ?? exception.GetType().Name, exception.InnerException?.GetType().FullName),
            WorkIqDiagnosticsJson.Options);
        _owner.Trace(Build(".failed", WorkIqTraceLevel.Error, message, elapsed, data));
    }

    /// <summary>Traces <c>&lt;code&gt;.end</c> with the elapsed milliseconds unless the scope already closed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _owner.Trace(Build(".end", WorkIqTraceLevel.Info, _message, Elapsed.TotalMilliseconds, data: null));
    }

    private WorkIqTraceEvent Build(string suffix, WorkIqTraceLevel level, string message, double? elapsedMs, JsonElement? data) =>
        new(default, Stage, level, Code + suffix, message)
        {
            CaseId = _caseId,
            TurnId = _turnId,
            TaskId = _taskId,
            ContextId = _contextId,
            RequestId = _requestId,
            HttpStatus = _httpStatus,
            RpcErrorCode = _rpcErrorCode,
            A2AState = _a2aState,
            ElapsedMs = elapsedMs,
            Data = data,
        };

    private sealed record FailureData(string ExceptionType, string? InnerExceptionType);
}
