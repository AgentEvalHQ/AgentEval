// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// How a tool-gate <see cref="AgentEval.Guardrails.GateAction.Block"/> is enforced. Defaults to
/// <see cref="WarnOnly"/> (matches the shipped chat-gate default) so adding a gate never silently changes an
/// agent's behavior; callers opt into blocking for production. (M1 adds <c>ThrowOnFail</c> / <c>Terminate</c>.)
/// </summary>
public enum ToolGatePolicy
{
    /// <summary>Record <c>gate.tool.*</c> evidence but let the tool run (safe default).</summary>
    WarnOnly,

    /// <summary>Block: replace the tool result with a refusal string the model sees, and never execute the tool.</summary>
    ReplaceResult,
}
