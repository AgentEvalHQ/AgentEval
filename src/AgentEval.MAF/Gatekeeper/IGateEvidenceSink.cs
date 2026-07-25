// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A destination for <see cref="GateEvidence"/> (Phase 3, F-C / P3-3) — the DIP that lets the trace writer, the
/// reference-id ledger (P3-1), and the alerting path (P3-4) all be REGISTERED against one evidence stream instead
/// of each editing the pipeline. Implementations must be cheap and non-throwing on the hot path (a sink that
/// throws must not break enforcement) — the composite guards that.
/// </summary>
public interface IGateEvidenceSink
{
    /// <summary>Record one gate finding. <paramref name="sequence"/> is the per-pipeline ordering index used for the trace key.</summary>
    void Record(GateEvidence evidence, int sequence);
}

/// <summary>Writes each <see cref="GateEvidence"/> into an <see cref="AgentTrace"/> under its <c>gate.{stage}.{seq}.{policy}</c> key — the audit projection, and the default sink.</summary>
public sealed class TraceGateEvidenceSink : IGateEvidenceSink
{
    private readonly AgentTrace? _trace;

    /// <summary>Creates the sink over <paramref name="trace"/> (a null trace makes every record a no-op — the tracing-off path).</summary>
    public TraceGateEvidenceSink(AgentTrace? trace) => _trace = trace;

    /// <inheritdoc/>
    public void Record(GateEvidence evidence, int sequence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _trace?.SetMetadata(evidence.TraceKey(sequence), evidence.ToMetadata());
    }
}

/// <summary>
/// Fans one <see cref="GateEvidence"/> out to several sinks (P3-3). A sink that throws is isolated — the remaining
/// sinks still receive the record and enforcement is never broken by an evidence destination — because a broken
/// audit destination must not become a fail-open in the enforcement path.
/// </summary>
public sealed class CompositeGateEvidenceSink : IGateEvidenceSink
{
    private readonly IReadOnlyList<IGateEvidenceSink> _sinks;

    /// <summary>Creates the composite over <paramref name="sinks"/> (nulls are dropped).</summary>
    public CompositeGateEvidenceSink(params IGateEvidenceSink?[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks.Where(s => s is not null).Cast<IGateEvidenceSink>().ToArray();
    }

    /// <inheritdoc/>
    public void Record(GateEvidence evidence, int sequence)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Record(evidence, sequence);
            }
            catch
            {
                // An evidence sink failing must never break enforcement or suppress the other sinks.
            }
        }
    }
}
