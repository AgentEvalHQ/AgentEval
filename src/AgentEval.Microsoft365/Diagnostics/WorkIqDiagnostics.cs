// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// The one diagnostics object every seam of the Work IQ provider receives (auth handler, capture handler, A2A
/// client, reducer, conversation, agent, CLI doctor). It stamps the run id and the clock time on every event,
/// redacts messages and data through <see cref="WorkIqRedaction"/>, fans events out to the trace sinks and issues
/// out to the issue sinks, keeps the last <see cref="RecentEventCapacity"/> events in memory for tests and the
/// doctor report, and never throws out of a trace: a failing sink is reported once on stderr and dropped.
/// Thread-safe. <see cref="None"/> is the no-op used wherever nothing was configured.
/// </summary>
public sealed class WorkIqDiagnostics : IDisposable
{
    /// <summary>How many events <see cref="RecentEvents"/> retains.</summary>
    public const int RecentEventCapacity = 200;

    /// <summary>The A2A protocol version stamped on issues.</summary>
    public const string A2AVersion = Microsoft365Evidence.A2AVersion;

    /// <summary>The run id of <see cref="None"/>.</summary>
    public const string NoneRunId = "none";

    private readonly IWorkIqTraceSink[] _traceSinks;
    private readonly IWorkIqIssueSink[] _issueSinks;
    private readonly bool[] _traceSinkDropped;
    private readonly bool[] _issueSinkDropped;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly WorkIqTraceEvent?[] _recent = new WorkIqTraceEvent?[RecentEventCapacity];
    private int _recentCount;
    private int _recentNext;
    private int _issueCount;
    private int _disposed;

    private WorkIqDiagnostics()
    {
        RunId = NoneRunId;
        IsNone = true;
        _traceSinks = [];
        _issueSinks = [];
        _traceSinkDropped = [];
        _issueSinkDropped = [];
        _clock = TimeProvider.System;
    }

    /// <summary>Creates the diagnostics of one run.</summary>
    /// <param name="runId">The run id stamped on every event and issue (non-blank).</param>
    /// <param name="configSha256">SHA-256 of the config's canonical identity, stamped on issues; null when unknown.</param>
    /// <param name="traceSinks">Where events go (may be empty).</param>
    /// <param name="issueSinks">Where issues go (may be empty).</param>
    /// <param name="clock">The clock that stamps <see cref="WorkIqTraceEvent.At"/> and measures scopes; null uses <see cref="TimeProvider.System"/>.</param>
    public WorkIqDiagnostics(
        string runId,
        string? configSha256,
        IReadOnlyList<IWorkIqTraceSink> traceSinks,
        IReadOnlyList<IWorkIqIssueSink> issueSinks,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(traceSinks);
        ArgumentNullException.ThrowIfNull(issueSinks);
        if (traceSinks.Any(s => s is null) || issueSinks.Any(s => s is null))
        {
            throw new ArgumentException("A sink list must not contain null.");
        }

        RunId = runId;
        ConfigSha256 = configSha256;
        _traceSinks = traceSinks.ToArray();
        _issueSinks = issueSinks.ToArray();
        _traceSinkDropped = new bool[_traceSinks.Length];
        _issueSinkDropped = new bool[_issueSinks.Length];
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The no-op diagnostics: no sinks, nothing retained, every call returns immediately.</summary>
    public static WorkIqDiagnostics None { get; } = new();

    /// <summary>The provider assembly's informational version (stamped on issues); null only if the assembly carries no version at all.</summary>
    public static string? AdapterVersion { get; } = ResolveAdapterVersion();

    /// <summary>The run id stamped on every event and issue.</summary>
    public string RunId { get; }

    /// <summary>SHA-256 of the config's canonical identity, when known.</summary>
    public string? ConfigSha256 { get; }

    /// <summary>Whether this is <see cref="None"/>.</summary>
    public bool IsNone { get; }

    /// <summary>How many issues were registered on this object.</summary>
    public int IssueCount => Volatile.Read(ref _issueCount);

    /// <summary>The clock used for stamps and scope timing.</summary>
    internal TimeProvider Clock => _clock;

    /// <summary>Where a failing sink is reported (one line, then the sink is dropped); defaults to <see cref="Console.Error"/>.</summary>
    internal TextWriter? FallbackWriter { get; init; }

    /// <summary>The last <see cref="RecentEventCapacity"/> traced events, oldest first (a snapshot).</summary>
    public IReadOnlyList<WorkIqTraceEvent> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                var snapshot = new WorkIqTraceEvent[_recentCount];
                var start = _recentCount < RecentEventCapacity ? 0 : _recentNext;
                for (var i = 0; i < _recentCount; i++)
                {
                    snapshot[i] = _recent[(start + i) % RecentEventCapacity]!;
                }

                return snapshot;
            }
        }
    }

    /// <summary>
    /// Traces an event: stamps <see cref="WorkIqTraceEvent.At"/> when <c>default</c> and <see cref="WorkIqTraceEvent.RunId"/>
    /// when null, redacts the message and data, retains it in <see cref="RecentEvents"/> and fans it out. Never throws
    /// because of a sink.
    /// </summary>
    /// <param name="e">The event.</param>
    public void Trace(WorkIqTraceEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (IsNone)
        {
            return;
        }

        var stamped = Stamp(e);
        lock (_gate)
        {
            _recent[_recentNext] = stamped;
            _recentNext = (_recentNext + 1) % RecentEventCapacity;
            _recentCount = Math.Min(_recentCount + 1, RecentEventCapacity);
        }

        for (var i = 0; i < _traceSinks.Length; i++)
        {
            if (Volatile.Read(ref _traceSinkDropped[i]))
            {
                continue;
            }

            try
            {
                _traceSinks[i].Write(stamped);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _traceSinkDropped[i], true);
                ReportSinkFailure("trace", _traceSinks[i], ex);
            }
        }
    }

    /// <summary>Traces an event built from its parts (stamped and redacted like <see cref="Trace(WorkIqTraceEvent)"/>).</summary>
    /// <param name="stage">The pipeline stage.</param>
    /// <param name="code">The stable event code (non-blank).</param>
    /// <param name="message">The safe message.</param>
    /// <param name="level">The severity.</param>
    /// <param name="caseId">The case id, when known.</param>
    /// <param name="turnId">The turn id, when known.</param>
    /// <param name="taskId">The task id, when known.</param>
    /// <param name="contextId">The context id, when known.</param>
    /// <param name="requestId">The service <c>request-id</c>, when received.</param>
    /// <param name="httpStatus">The HTTP status, when a response was received.</param>
    /// <param name="rpcErrorCode">The JSON-RPC error code, when an error object was received.</param>
    /// <param name="a2aState">The raw A2A task state, when observed.</param>
    /// <param name="elapsedMs">Elapsed milliseconds, when measured.</param>
    public void Trace(
        WorkIqTraceStage stage,
        string code,
        string message,
        WorkIqTraceLevel level = WorkIqTraceLevel.Info,
        string? caseId = null,
        string? turnId = null,
        string? taskId = null,
        string? contextId = null,
        string? requestId = null,
        int? httpStatus = null,
        int? rpcErrorCode = null,
        string? a2aState = null,
        double? elapsedMs = null)
    {
        if (IsNone)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            return;
        }

        Trace(new WorkIqTraceEvent(default, stage, level, code, message ?? "")
        {
            CaseId = caseId,
            TurnId = turnId,
            TaskId = taskId,
            ContextId = contextId,
            RequestId = requestId,
            HttpStatus = httpStatus,
            RpcErrorCode = rpcErrorCode,
            A2AState = a2aState,
            ElapsedMs = elapsedMs,
        });
    }

    /// <summary>
    /// Registers an issue: stamps <see cref="WorkIqIssue.At"/> (when <c>default</c>), <see cref="WorkIqIssue.RunId"/>,
    /// <see cref="WorkIqIssue.ConfigSha256"/>, <see cref="WorkIqIssue.AdapterVersion"/> and <see cref="WorkIqIssue.A2AVersion"/>
    /// when null, redacts the message and the suggested cause, fans it out to the issue sinks, and also traces it as
    /// an event with code <c>issue.&lt;category&gt;</c> (lower-case) at <paramref name="level"/> so the trace shows
    /// where in the sequence the anomaly happened. Never throws because of a sink.
    /// </summary>
    /// <param name="issue">The issue.</param>
    /// <param name="level">The level of the mirrored trace event; <see cref="WorkIqTraceLevel.Info"/> is raised to <see cref="WorkIqTraceLevel.Warning"/>.</param>
    public void Issue(WorkIqIssue issue, WorkIqTraceLevel level = WorkIqTraceLevel.Error)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (IsNone)
        {
            return;
        }

        var stamped = Stamp(issue, _clock.GetUtcNow(), RunId, ConfigSha256);

        Interlocked.Increment(ref _issueCount);

        for (var i = 0; i < _issueSinks.Length; i++)
        {
            if (Volatile.Read(ref _issueSinkDropped[i]))
            {
                continue;
            }

            try
            {
                _issueSinks[i].Write(stamped);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _issueSinkDropped[i], true);
                ReportSinkFailure("issue", _issueSinks[i], ex);
            }
        }

        var traceLevel = level == WorkIqTraceLevel.Info ? WorkIqTraceLevel.Warning : level;
        Trace(new WorkIqTraceEvent(stamped.At, stamped.Stage, traceLevel, "issue." + CategoryCode(stamped.Category), stamped.Message)
        {
            RunId = stamped.RunId,
            CaseId = stamped.CaseId,
            TurnId = stamped.TurnId,
            TaskId = stamped.TaskId,
            ContextId = stamped.ContextId,
            RequestId = stamped.RequestId,
            HttpStatus = stamped.HttpStatus,
            RpcErrorCode = stamped.RpcErrorCode,
            A2AState = stamped.A2AState,
            Data = JsonSerializer.SerializeToElement(new IssueTraceData(stamped.Code, stamped.Category.ToString(), stamped.SuggestedCause, stamped.AadstsCode), WorkIqDiagnosticsJson.Options),
        });
    }

    /// <summary>
    /// The stamping every registered issue gets: <see cref="WorkIqIssue.At"/> (when <c>default</c>), and
    /// <see cref="WorkIqIssue.RunId"/>, <see cref="WorkIqIssue.ConfigSha256"/>, <see cref="WorkIqIssue.AdapterVersion"/> and
    /// <see cref="WorkIqIssue.A2AVersion"/> when null; the message and the suggested cause are redacted. Shared by
    /// <see cref="Issue"/> and the readiness check, which stamps the issue it attaches to a failed stage the same way.
    /// </summary>
    /// <param name="issue">The issue.</param>
    /// <param name="now">The time stamped when the issue carries none.</param>
    /// <param name="runId">The run id stamped when the issue carries none.</param>
    /// <param name="configSha256">The config hash stamped when the issue carries none.</param>
    internal static WorkIqIssue Stamp(WorkIqIssue issue, DateTimeOffset now, string runId, string? configSha256)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue with
        {
            At = issue.At == default ? now : issue.At,
            RunId = issue.RunId ?? runId,
            ConfigSha256 = issue.ConfigSha256 ?? configSha256,
            AdapterVersion = issue.AdapterVersion ?? AdapterVersion,
            A2AVersion = issue.A2AVersion ?? A2AVersion,
            Message = WorkIqRedaction.Redact(issue.Message),
            SuggestedCause = WorkIqRedaction.Redact(issue.SuggestedCause),
        };
    }

    /// <summary>
    /// Opens a measured scope: traces <c>&lt;code&gt;.start</c> now and, on dispose, <c>&lt;code&gt;.end</c> with
    /// <see cref="WorkIqTraceEvent.ElapsedMs"/>; <see cref="WorkIqTraceScope.Fail"/> traces <c>&lt;code&gt;.failed</c>
    /// at <see cref="WorkIqTraceLevel.Error"/> instead of <c>.end</c>.
    /// </summary>
    /// <param name="stage">The pipeline stage.</param>
    /// <param name="code">The base code (non-blank).</param>
    /// <param name="message">The safe message describing the operation.</param>
    /// <param name="caseId">The case id, when known.</param>
    /// <param name="turnId">The turn id, when known.</param>
    /// <param name="taskId">The task id, when known.</param>
    /// <param name="contextId">The context id, when known.</param>
    /// <param name="requestId">The service <c>request-id</c>, when known.</param>
    public WorkIqTraceScope Measure(
        WorkIqTraceStage stage,
        string code,
        string message,
        string? caseId = null,
        string? turnId = null,
        string? taskId = null,
        string? contextId = null,
        string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new WorkIqTraceScope(this, stage, code, message ?? "", caseId, turnId, taskId, contextId, requestId);
    }

    /// <summary>
    /// Disposes every sink that is <see cref="IDisposable"/> (once; <see cref="None"/> owns nothing). A caller-owned
    /// sink (<see cref="WorkIqOptions.TraceSinks"/>, <see cref="WorkIqOptions.IssueSinks"/>) is held through a
    /// non-disposable wrapper and is never disposed here.
    /// </summary>
    public void Dispose()
    {
        if (IsNone || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var sink in _traceSinks)
        {
            TryDispose(sink);
        }

        foreach (var sink in _issueSinks)
        {
            TryDispose(sink);
        }
    }

    private WorkIqTraceEvent Stamp(WorkIqTraceEvent e)
    {
        var stamped = e;
        if (stamped.At == default)
        {
            stamped = stamped with { At = _clock.GetUtcNow() };
        }

        if (stamped.RunId is null)
        {
            stamped = stamped with { RunId = RunId };
        }

        var message = WorkIqRedaction.Redact(stamped.Message);
        if (!string.Equals(message, stamped.Message, StringComparison.Ordinal))
        {
            stamped = stamped with { Message = message };
        }

        if (stamped.Data is { } data)
        {
            var redacted = RedactData(data);
            if (redacted is not null)
            {
                stamped = stamped with { Data = redacted };
            }
        }

        return stamped;
    }

    /// <summary>Redacts a JSON element's text; null when nothing changed.</summary>
    private static JsonElement? RedactData(JsonElement data)
    {
        var raw = data.GetRawText();
        var redacted = WorkIqRedaction.Redact(raw);
        if (string.Equals(raw, redacted, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(redacted);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(WorkIqRedaction.RedactedMarker + " (data dropped: not valid JSON after redaction)");
        }
    }

    private void ReportSinkFailure(string kind, object sink, Exception ex)
    {
        try
        {
            var writer = FallbackWriter ?? Console.Error;
            var name = sink is ICallerOwnedSink owned ? owned.Inner.GetType().Name + " (caller-owned)" : sink.GetType().Name;
            writer.WriteLine(
                $"[m365 diagnostics] {kind} sink {name} failed and is dropped for the rest of run {RunId}: " +
                $"{ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}");
            writer.Flush();
        }
        catch (Exception)
        {
            // Last resort: nothing else to report to.
        }
    }

    private static void TryDispose(object sink)
    {
        if (sink is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // A sink that fails to close must not fail the run.
            }
        }
    }

    private static string CategoryCode(WorkIqIssueCategory category) => category.ToString().ToLowerInvariant();

    private static string? ResolveAdapterVersion()
    {
        var assembly = typeof(WorkIqDiagnostics).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString();
    }

    private sealed record IssueTraceData(string IssueCode, string Category, string SuggestedCause, string? AadstsCode);
}
