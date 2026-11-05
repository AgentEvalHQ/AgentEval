// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365;

/// <summary>
/// Which output modes a turn requests from Work IQ, and therefore which evidence channels can be
/// <c>Provided</c> at all. Requesting diagnostics is a visible choice, never a default.
/// </summary>
public enum DiagnosticProfile
{
    /// <summary>
    /// Default. <c>acceptedOutputModes: ["text/plain"]</c>: the final answer, its citations and attributions.
    /// Tool, retrieval and skill channels are reported as <c>NotRequested</c>.
    /// </summary>
    PublicAnswerOnly,

    /// <summary>
    /// Experimental. Additionally requests the <c>application/vnd.ms-workiq…</c> diagnostic modes observed in the
    /// native evaluator 1.18.0 (<c>Developer Info</c>, <c>Retrieval Info</c>). Observed, not a documented
    /// Microsoft contract; if the service rejects the request the result is surfaced, never silently retried
    /// in answer-only mode.
    /// </summary>
    Native118Diagnostics,
}

/// <summary>How consecutive <c>InvokeAsync</c> calls on one agent relate to the server conversation context.</summary>
public enum ConversationPolicy
{
    /// <summary>
    /// Default. Every invocation starts a fresh context (the local context is dropped before each turn), so dataset
    /// cases and benchmark scenarios are independent even though the batch harness reuses one agent.
    /// </summary>
    FreshContextPerInvocation,

    /// <summary>
    /// Every invocation continues the current context (multi-turn in code). A completed task id is never
    /// re-sent; <c>ResetSessionAsync</c> starts a new context.
    /// </summary>
    ContinueContext,
}

/// <summary>
/// Which content the evidence document and the raw capture retain. Requesting a diagnostic channel, using it in
/// memory, retaining raw bytes and exporting evidence are separate decisions; this policy governs retention.
/// </summary>
public enum EvidenceContentPolicy
{
    /// <summary>Availability, states, counts, profile ids, safe error codes and opaque identifiers only; no answer text, prompts, URLs, passages or payloads.</summary>
    MetadataOnly,

    /// <summary>Default. Answer text, citation display names/URLs, counts and states through a versioned allow-list; removed fields are marked <c>Redacted</c>.</summary>
    SelectedContent,

    /// <summary>Everything, plus bounded raw request/response bytes and SSE frames written under <see cref="WorkIqOptions.RawCaptureDirectory"/>.</summary>
    FullEvidence,
}

/// <summary>
/// Runtime options of a Work IQ conversation/agent: timeouts and budgets, conversation isolation, the turn cap,
/// content policy and raw capture bounds. Every default is conservative; <see cref="Validate"/> rejects negative
/// values. The options carry no secret and no network setting beyond timeouts.
/// </summary>
public sealed record WorkIqOptions
{
    /// <summary>HTTP timeout of one blocking <c>SendMessage</c> call (the native evaluator's default is also 300 s).</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>Total wall-clock deadline of one turn including recovery; when exceeded the outcome is <c>Indeterminate</c> unless the task state is known.</summary>
    public TimeSpan TurnDeadline { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Budget for <c>GetTask</c>/<c>Subscribe</c> reconciliation after a stream drop or a non-terminal blocking reply; a send is never repeated inside it.</summary>
    public TimeSpan RecoveryBudget { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Interval between <c>GetTask</c> polls while a task is <c>submitted</c>/<c>working</c>.</summary>
    public TimeSpan RecoveryPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How consecutive invocations relate to the server context; the CLI always uses <see cref="ConversationPolicy.FreshContextPerInvocation"/>.</summary>
    public ConversationPolicy ConversationPolicy { get; init; } = ConversationPolicy.FreshContextPerInvocation;

    /// <summary>
    /// Maximum number of turns per agent instance (0 = no cap). Every turn that returned a typed result counts, whether
    /// it completed or not — a failed, paused, canceled or indeterminate turn, and also one that failed before its prompt
    /// went out (a sign-in or discovery failure); only a turn refused before it started (this cap, a disposed agent)
    /// does not (see <see cref="WorkIqEvaluableAgent.TurnsUsed"/>). Turns are counted; consumption is not metered — the
    /// service exposes no cost. Reaching the cap throws <see cref="WorkIqTurnBudgetExceededException"/> before the send
    /// that would cross it.
    /// </summary>
    public int MaxTurns { get; init; }

    /// <summary>Which content the evidence document and raw capture retain.</summary>
    public EvidenceContentPolicy ContentPolicy { get; init; } = EvidenceContentPolicy.SelectedContent;

    /// <summary>Where <see cref="EvidenceContentPolicy.FullEvidence"/> writes raw captures (<c>&lt;dir&gt;/&lt;runId&gt;/&lt;turnId&gt;/…</c>); null disables file output.</summary>
    public DirectoryInfo? RawCaptureDirectory { get; init; }

    /// <summary>Upper bound of retained bytes per capture; exceeding it marks the capture <c>Truncated</c>.</summary>
    public long MaxCaptureBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Upper bound of retained SSE frames per capture; exceeding it marks the capture <c>Truncated</c>.</summary>
    public int MaxCaptureFrames { get; init; } = 10_000;

    /// <summary>
    /// Opt-in: when the local wait times out and a task id is known, send <c>CancelTask</c> under its own bounded
    /// token. The outcome is <c>Canceled</c> only when the service confirms it. Off by default: a local stop
    /// never implies a remote cancel.
    /// </summary>
    public bool CancelRemoteTaskOnTimeout { get; init; }

    /// <summary>The <c>historyLength</c> sent in request configurations; null omits it.</summary>
    public int? HistoryLength { get; init; }

    /// <summary>Overrides <see cref="Microsoft365Config.DiagnosticProfile"/> for this agent; null keeps the config's profile.</summary>
    public DiagnosticProfile? ProfileOverride { get; init; }

    /// <summary>
    /// The instant used to compute the DST-aware <c>timeZoneOffset</c> of the <c>Location</c> metadata when
    /// <see cref="Microsoft365Config.TimeZone"/> is set; null uses the send time. Stored in evidence; it does not
    /// override the remote service clock.
    /// </summary>
    public DateTimeOffset? EvaluationReferenceInstant { get; init; }

    /// <summary>
    /// Writes every trace event to stderr (one line per event, see <see cref="Diagnostics.ConsoleTraceSink"/>) in
    /// addition to the JSONL trace file. Off by default; the CLI's <c>--m365-verbose</c> turns it on and
    /// <c>m365 doctor</c> defaults to on.
    /// </summary>
    public bool Verbose { get; init; }

    /// <summary>
    /// Where the JSONL trace (every event of the run) is written; null resolves to
    /// <c>&lt;DiagnosticsDirectory&gt;/m365-trace-&lt;RunId&gt;.jsonl</c>. Redacted: no token, auth code or
    /// <c>Authorization</c> header ever appears in it.
    /// </summary>
    public FileInfo? TraceFile { get; init; }

    /// <summary>
    /// Where the JSONL issue register (every warning/error with category and suggested cause) is written; null
    /// resolves to <c>&lt;DiagnosticsDirectory&gt;/m365-issues.jsonl</c>. Appended across runs; each entry carries
    /// its run id.
    /// </summary>
    public FileInfo? IssueFile { get; init; }

    /// <summary>
    /// The directory of the default trace and issue files; null resolves to the workspace's <c>.agenteval/m365/</c>
    /// (the first <c>.agenteval</c> directory found walking up from the current directory), else
    /// <c>&lt;temp&gt;/agenteval-m365</c>. The resolved paths are traced as the first event of the run.
    /// </summary>
    public DirectoryInfo? DiagnosticsDirectory { get; init; }

    /// <summary>
    /// The run id stamped on every trace event and issue (and used in the trace file name); null or blank mints
    /// <c>m365-&lt;yyyyMMdd-HHmmss UTC&gt;-&lt;4 hex&gt;</c>. Set it to correlate the diagnostics with an AEF run id.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Extra trace sinks of a live agent (<see cref="Microsoft365AgentFactory.BuildLive"/>), next to the JSONL trace
    /// file and the verbose console. Each receives every event the JSONL trace file receives, in the same order and
    /// after the same redaction (no token, auth code or <c>Authorization</c> header ever reaches it). The caller owns
    /// their lifetime: the agent never disposes them, so one sink may outlive several agents. A sink that throws is
    /// reported once on stderr and dropped for the rest of the run, like any other sink; it never fails a turn.
    /// Null or empty means none.
    /// </summary>
    public IReadOnlyList<Diagnostics.IWorkIqTraceSink>? TraceSinks { get; init; }

    /// <summary>
    /// Extra issue sinks of a live agent (<see cref="Microsoft365AgentFactory.BuildLive"/>), next to the JSONL issue
    /// register. Each receives every issue the JSONL issue file receives, stamped and redacted the same way. The caller
    /// owns their lifetime (never disposed by the agent); a sink that throws is reported once and dropped. Null or empty
    /// means none.
    /// </summary>
    public IReadOnlyList<Diagnostics.IWorkIqIssueSink>? IssueSinks { get; init; }

    /// <summary>
    /// Throws when any duration, count or bound is negative (naming every offending option), or when
    /// <see cref="TraceSinks"/> or <see cref="IssueSinks"/> contains a null entry.
    /// </summary>
    /// <exception cref="InvalidOperationException">One or more options are negative, or a sink list has a null entry.</exception>
    public void Validate()
    {
        if (TraceSinks?.Any(s => s is null) == true || IssueSinks?.Any(s => s is null) == true)
        {
            throw new InvalidOperationException(
                "WorkIqOptions.TraceSinks and WorkIqOptions.IssueSinks must not contain a null entry (pass an empty list or null for none).");
        }

        var invalid = new List<string>();
        if (SendTimeout < TimeSpan.Zero) { invalid.Add(nameof(SendTimeout)); }
        if (TurnDeadline < TimeSpan.Zero) { invalid.Add(nameof(TurnDeadline)); }
        if (RecoveryBudget < TimeSpan.Zero) { invalid.Add(nameof(RecoveryBudget)); }
        if (RecoveryPollInterval < TimeSpan.Zero) { invalid.Add(nameof(RecoveryPollInterval)); }
        if (MaxTurns < 0) { invalid.Add(nameof(MaxTurns)); }
        if (MaxCaptureBytes < 0) { invalid.Add(nameof(MaxCaptureBytes)); }
        if (MaxCaptureFrames < 0) { invalid.Add(nameof(MaxCaptureFrames)); }
        if (HistoryLength is < 0) { invalid.Add(nameof(HistoryLength)); }

        if (invalid.Count > 0)
        {
            throw new InvalidOperationException(
                "WorkIqOptions has negative value(s): " + string.Join(", ", invalid) +
                ". Every timeout, budget, cap and bound must be zero or positive (0 = no cap for MaxTurns).");
        }
    }
}
