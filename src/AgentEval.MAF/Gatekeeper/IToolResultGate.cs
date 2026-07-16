// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper Hardening Phase 2, P0-3 — a runtime policy over one already-executed tool call's RESULT, at the
/// MAF function-invocation boundary. Runs AFTER <see cref="IToolGate"/> (which inspects the proposed call
/// BEFORE it executes) and after the tool has actually run — a result gate can only decide what the model gets
/// to see of a result that already happened, not whether the call itself was allowed.
/// <para><b>Why this is a separate interface, not a mode on <see cref="IToolGate"/>:</b> the two run at
/// different points in the same middleware (before vs. after <c>next(...)</c>), inspect different subjects
/// (<see cref="GatedToolCall"/>'s proposed arguments vs. <see cref="GatedToolResult"/>'s actual return value),
/// and have a different action vocabulary (<see cref="ToolGateAction.Mutate"/> rewrites arguments pre-execution;
/// <see cref="ToolResultAction.Redact"/> sanitizes a result post-execution — conflating them into one verdict
/// shape would blur "this call shouldn't run" with "this already-real result shouldn't be shown verbatim").
/// </para>
/// </summary>
public interface IToolResultGate
{
    /// <summary>Stable policy name, recorded into <c>gate.tool-result.*</c> trace evidence.</summary>
    string PolicyName { get; }

    /// <summary>
    /// How expensive this gate is. Only <see cref="GateCost.PureCode"/> / <see cref="GateCost.Bounded"/> gates
    /// may run inline; <c>UseAgentEvalToolGate</c> rejects <see cref="GateCost.Network"/> / <see cref="GateCost.Llm"/>
    /// at construction (those belong in shadow mode) — same rule as <see cref="IToolGate.Cost"/>.
    /// </summary>
    GateCost Cost { get; }

    /// <summary>Inspect one already-executed call's result and return a verdict. Deterministic gates should complete synchronously.</summary>
    ValueTask<ToolResultVerdict> InspectAsync(GatedToolResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// The weakest <see cref="ToolGatePolicy"/> under which this gate is safe to run. Defaults to
    /// <see cref="ToolGatePolicy.WarnOnly"/> — same shape and same enforcement-floor mechanics as
    /// <see cref="IToolGate.MinimumPolicy"/>.
    /// </summary>
    ToolGatePolicy MinimumPolicy => ToolGatePolicy.WarnOnly;
}
