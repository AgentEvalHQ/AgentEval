// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Tracing;

/// <summary>
/// Per-executor Glass Box chat-boundary recording for MAF workflows (Phase 5b). Wrap each executor's
/// <see cref="IChatClient"/> with <see cref="Instrument"/> before building its agent; this collects one
/// <see cref="AgentTrace"/> per executor so each can be reconciled with Trace Fidelity and surfaced
/// individually. Makes the v0.11 pre-wiring pattern a one-liner.
/// </summary>
/// <remarks>
/// This is the explicit-instrumentation form. A fully transparent adapter hook (zero pre-wiring) would
/// require intercepting MAF's per-executor chat-client construction, which depends on MAF-internal surface
/// not exposed by <c>MAFWorkflowAdapter</c> today; it is tracked as a follow-up and would not be
/// byte-identical to direct chat-boundary capture. For MAF executors the agent runtime drives the tool
/// loop, so wrapping the raw model with recording yields one entry per model round-trip.
/// </remarks>
public sealed class WorkflowChatRecording
{
    private readonly Dictionary<string, AgentTrace> _traces = new(StringComparer.Ordinal);
    private readonly SamplePreset _preset;

    /// <summary>Creates a recorder set with the given capture preset (applied to every executor).</summary>
    public WorkflowChatRecording(SamplePreset preset = SamplePreset.Standard) => _preset = preset;

    /// <summary>The per-executor traces collected so far, keyed by executor id.</summary>
    public IReadOnlyDictionary<string, AgentTrace> Traces => _traces;

    /// <summary>
    /// Wraps <paramref name="inner"/> with chat-boundary recording for executor <paramref name="executorId"/>
    /// and returns the instrumented client to hand to that executor's agent. Each call registers a fresh trace.
    /// </summary>
    public IChatClient Instrument(string executorId, IChatClient inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executorId);
        ArgumentNullException.ThrowIfNull(inner);

        var trace = new AgentTrace { AgentName = executorId };
        _traces[executorId] = trace;
        return inner.AsBuilder().UseTraceRecording(executorId, trace, _preset).Build();
    }

    /// <summary>The trace recorded for <paramref name="executorId"/>, or null if it was never instrumented.</summary>
    public AgentTrace? TraceFor(string executorId) => _traces.GetValueOrDefault(executorId);
}
