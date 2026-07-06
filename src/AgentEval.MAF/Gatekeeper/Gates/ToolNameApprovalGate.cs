// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Approval gate that escalates a call to human review whenever the tool being called is on an escalate list —
/// regardless of its arguments. This is how you gate a sensitive tool by <i>identity</i> (including a
/// parameterless one that <see cref="ArgumentPatternApprovalGate"/> cannot reason about): any other tool is
/// auto-approvable as far as this gate is concerned, so compose it (AND-combined) with argument gates.
/// <para>Names are matched case-insensitively (<c>OrdinalIgnoreCase</c>, as with <see cref="ForbiddenToolGate"/> /
/// <see cref="SequenceGate"/>) so a casing variation can't bypass the escalation.</para>
/// </summary>
public sealed class ToolNameApprovalGate : IToolApprovalGate
{
    private readonly HashSet<string> _escalate;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <summary>Creates the gate from the set of tool names that must always be escalated to a human.</summary>
    public ToolNameApprovalGate(IEnumerable<string> escalateTools, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(escalateTools);
        // Case-insensitive, matching ForbiddenToolGate/SequenceGate — a casing variation must not bypass escalation.
        _escalate = new HashSet<string>(escalateTools, StringComparer.OrdinalIgnoreCase);
        PolicyName = policyName ?? "ToolNameApprovalGate";
    }

    /// <inheritdoc/>
    public ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        // Auto-approvable (from this gate's view) iff the tool is NOT on the escalate list.
        return new ValueTask<bool>(!_escalate.Contains(call.Name));
    }
}
