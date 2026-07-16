// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// A short, opaque, unpredictable reference id used to correlate a non-revealing, model-visible (or otherwise
/// externally-surfaced) refusal with the full audit-visible detail recorded elsewhere (a Glass Box trace, an
/// exception's structured properties). Dependency-free so it can live in <c>AgentEval.Core</c> and be shared by
/// every layer that needs one — <c>AgentEval.MAF.Gatekeeper</c>'s <c>GateReferenceId</c> (the model-visible tool
/// gate refusal shape) and <see cref="EvalGateRefusalException"/> (whose <see cref="Exception.Message"/> may
/// cross into model-visible territory at an agent-as-tool / sub-agent boundary) both delegate to this one
/// generator, rather than each rolling its own.
/// </summary>
public static class GateCorrelationId
{
    /// <summary>A short, opaque, unpredictable reference id — e.g. <c>gk_a1b2c3d4e5f6</c>.</summary>
    public static string New() => "gk_" + Guid.NewGuid().ToString("N")[..12];
}
