// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The per-invocation enrichment context a Gatekeeper writer stamps onto every <see cref="GateEvidence"/> record
/// (Phase 3, F-C / P3-2). Captured once at a pipeline seam — the fields not available from ambient state
/// (<see cref="AgentRunScope"/> for run id, <c>ToolCorrelationScope</c> for correlation id) — and passed into the
/// record helpers so the "complete block evidence" (agent / tool / iteration / config fingerprint) is filled in
/// uniformly rather than each writer re-deriving it. The timestamp is stamped at <see cref="Build"/> time.
/// </summary>
internal readonly record struct GateEvidenceContext(string? AgentName, string? ToolName, int? Iteration, string? ConfigFingerprint, IGateEvidenceSink? Sink = null)
{
    /// <summary>Whether a record must be BUILT even with no trace — true when an extra sink (ledger / alerting) is registered, so evidence still flows with tracing off (P3-1).</summary>
    public bool HasSink => Sink is not null;

    /// <summary>Fan a built record out to the registered extra sink (ledger / alerting), if any. The trace write is done separately by the writer, so this never double-writes the trace.</summary>
    public void Emit(GateEvidence record, int sequence) => Sink?.Record(record, sequence);

    /// <summary>Assembles a full <see cref="GateEvidence"/> record from this context plus the per-finding fields (timestamp stamped now).</summary>
    public GateEvidence Build(
        string stage,
        string policy,
        string action,
        string referenceId,
        string? reason,
        GateSeverity severity,
        string? correlationId = null,
        AgentEval.Guardrails.GateProvenance? provenance = null,
        IReadOnlyDictionary<string, object?>? extra = null)
        => new()
        {
            ReferenceId = referenceId,
            TimestampUtc = DateTimeOffset.UtcNow,
            Stage = stage,
            Policy = policy,
            Action = action,
            Severity = severity,
            RunId = AgentRunScope.Current?.RunId,
            AgentName = AgentName ?? AgentRunScope.Current?.AgentName,
            ToolName = ToolName,
            Iteration = Iteration,
            Reason = reason,
            Provenance = provenance,
            ConfigFingerprint = ConfigFingerprint,
            CorrelationId = correlationId,
            Extra = extra,
        };
}
