// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;   // IEvaluableAgent, AgentResponse

namespace AgentEval.RedTeam;

/// <summary>
/// Opt-in surface for an agent that can be handed canary tool schemas and return a trace of emitted/executed calls
/// (Wave B, Pillar 1). Mirrors the <c>IStreamableAgent</c> ISP pattern — <see cref="IEvaluableAgent"/> is untouched,
/// so a plain text agent simply does not implement this and the harness runs it at <see cref="SutTier.TextOnly"/>.
/// </summary>
public interface IToolCapableAgent : IEvaluableAgent
{
    /// <summary>The capabilities this agent advertises (the <see cref="TierNegotiator"/> classifies the tier from these).</summary>
    AgentToolCapability Capabilities { get; }

    /// <summary>
    /// Invoke the agent with <paramref name="tools"/> advertised. The returned
    /// <see cref="AgentResponse.RawMessages"/> MUST contain the emitted <c>FunctionCallContent</c> (Tier 1) and,
    /// when a tool is executed, the paired <c>FunctionResultContent</c> (Tier 2) — that pairing is what lets the
    /// evaluator distinguish <see cref="EvidenceFidelity.IntentToAct"/> (emitted) from
    /// <see cref="EvidenceFidelity.Behavioral"/> (executed).
    /// <para>Implementations SHOULD emit a unique <c>CallId</c> per emitted call. <c>ToolUsageExtractor</c> pairs
    /// results to calls by CallId in message order, one result per call; a CallId reused across turns is paired to the
    /// nearest preceding still-unpaired call (real providers issue globally-unique ids, so this only matters for a
    /// synthetic agent).</para>
    /// </summary>
    Task<AgentResponse> InvokeWithToolsAsync(
        string prompt, IReadOnlyList<CanaryTool> tools, CancellationToken cancellationToken = default);
}
