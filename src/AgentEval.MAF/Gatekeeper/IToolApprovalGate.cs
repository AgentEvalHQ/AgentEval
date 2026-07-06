// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper approval gate — classifies a pending tool call as safe-to-auto-approve or borderline (escalate to
/// a human). This is the softer sibling of <see cref="IToolGate"/>: where a tool gate BLOCKS a forbidden action
/// outright (fail-closed), an approval gate routes a borderline action to MAF's native human-in-the-loop approval
/// (<c>UseToolApproval</c>) instead — the run pauses and a person approves or rejects it.
/// <para><b>Fail-closed:</b> the enforcing extension treats a gate that throws (or otherwise cannot prove the
/// call routine) as an escalation — when in doubt a human decides, never a silent auto-approval.</para>
/// </summary>
public interface IToolApprovalGate
{
    /// <summary>A short, stable identifier used in the escalation evidence (<c>gate.approval.*</c>).</summary>
    string PolicyName { get; }

    /// <summary>
    /// Returns <see langword="true"/> if the call is safe to auto-approve without a human, or
    /// <see langword="false"/> to escalate it to human approval.
    /// </summary>
    /// <param name="call">The pending tool call (function name + arguments).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default);
}
