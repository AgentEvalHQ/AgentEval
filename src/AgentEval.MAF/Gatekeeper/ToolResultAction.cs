// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// What a tool RESULT gate <em>found</em> about a call's already-executed result. By the time a result gate
/// runs, the tool has already run — unlike <see cref="ToolGateAction"/> (which decides whether a call runs at
/// all), a result gate only ever decides what the MODEL gets to see of a result that already happened.
/// </summary>
public enum ToolResultAction
{
    /// <summary>The result is acceptable as-is.</summary>
    Allow,

    /// <summary>
    /// The result must not reach the model; enforced per <see cref="ToolGatePolicy"/> (reused — see
    /// <c>UseAgentEvalToolGate</c>'s <c>resultGates</c> parameter remarks for how each level is reinterpreted
    /// for a post-execution result rather than a pre-execution call).
    /// </summary>
    Block,

    /// <summary>
    /// The gate supplies a sanitized version of the result (e.g. a secret masked, an injection marker
    /// stripped) that DOES reach the model — applied regardless of policy, mirroring
    /// <see cref="ToolGateAction.Mutate"/>'s "always applied" precedent: a gate offering a safer version of
    /// real content is not a block decision an enforcement policy should gate.
    /// </summary>
    Redact,
}
