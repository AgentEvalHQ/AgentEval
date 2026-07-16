// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper ⇄ MAF human-in-the-loop approval interop. Where <see cref="AgentEvalToolGateExtensions"/> BLOCKS a
/// forbidden tool call, this routes a BORDERLINE call to MAF's native approval flow (<c>UseToolApproval</c>): the
/// run pauses and surfaces a <see cref="ToolApprovalRequestContent"/> that a human approves or rejects.
/// <para>The gates' "auto-approve vs escalate" decisions are compiled into a single MAF auto-approval rule that
/// auto-approves ONLY when EVERY gate agrees the call is safe; any escalation — or a gate that throws — sends the
/// call to a human (fail-closed). Only tools wrapped as <c>ApprovalRequiredAIFunction</c> (see
/// <see cref="RequiresApproval"/>) enter the approval flow; every other tool runs normally.</para>
/// <para><b>Experimental (AEGK001):</b> this interop is built on MAF's evaluation-only approval API
/// (<c>MAAI001</c>: <c>UseToolApproval</c> / <c>ToolApprovalAgentOptions</c>), which may change or be removed in a
/// future MAF release. Suppress <c>AEGK001</c> to opt in.</para>
/// </summary>
[Experimental("AEGK001")]
public static class AgentEvalToolApprovalExtensions
{
    /// <summary>
    /// Marks a tool as requiring approval so a Gatekeeper approval gate can gate it. Tools that are NOT marked
    /// this way bypass the approval flow entirely and run normally.
    /// </summary>
    public static AIFunction RequiresApproval(this AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new ApprovalRequiredAIFunction(function);
    }

    /// <summary>
    /// Registers Gatekeeper approval gates on top of MAF's <c>UseToolApproval</c> middleware. A call to an
    /// approval-required tool is auto-approved only if <b>every</b> gate returns auto-approvable; otherwise it is
    /// escalated to a human and recorded as <c>gate.approval.*</c> evidence.
    /// </summary>
    /// <param name="builder">The agent builder. This middleware sits at the agent boundary, OUTSIDE the
    /// function-invocation seam used by <see cref="AgentEvalToolGateExtensions.UseAgentEvalToolGate"/> — compose
    /// both to hard-block the dangerous calls and human-review the borderline ones.</param>
    /// <param name="gates">The approval gates to consult, in order. A null element is rejected.</param>
    /// <param name="trace">Optional Glass Box trace to record <c>gate.approval.*</c> escalation evidence into.</param>
    public static AIAgentBuilder UseAgentEvalToolApproval(
        this AIAgentBuilder builder,
        IReadOnlyList<IToolApprovalGate> gates,
        AgentTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(gates);
        ValidateApprovalGates(gates);

        var frozen = new IToolApprovalGate[gates.Count];
        for (var i = 0; i < gates.Count; i++)
        {
            frozen[i] = gates[i];
        }

        var seq = 0;

        // A single MAF auto-approval rule that returns true (auto-approve, no human) only when EVERY gate agrees
        // the call is routine. Any escalation — or a gate that throws — returns false, which MAF turns into a
        // human approval prompt (a ToolApprovalRequestContent surfaced to the caller).
        async ValueTask<bool> AutoApproveWhenAllGatesAgree(FunctionCallContent call)
        {
            foreach (var gate in frozen)
            {
                bool autoApprovable;
                try
                {
                    autoApprovable = await gate.IsAutoApprovableAsync(call).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Cannot classify ⇒ escalate to a human (fail-closed: never silently auto-approve).
                    RecordEscalation(trace, Interlocked.Increment(ref seq), gate.PolicyName, call.Name,
                        $"gate evaluation threw ({ex.GetType().Name}) — escalating to human approval");
                    return false;
                }

                if (!autoApprovable)
                {
                    RecordEscalation(trace, Interlocked.Increment(ref seq), gate.PolicyName, call.Name,
                        "gate escalated the call to human approval");
                    return false;
                }
            }

            return true;   // every gate agreed the call is routine ⇒ auto-approve (no human prompt)
        }

#pragma warning disable MAAI001 // UseToolApproval / ToolApprovalAgentOptions are evaluation-only MAF APIs — deliberately adopted.
        return builder.UseToolApproval(new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [AutoApproveWhenAllGatesAgree],
        });
#pragma warning restore MAAI001
    }

    // Build-time gate-list validation: empty-list fail-closed rejection, null elements, and well-formed
    // PolicyName (a malformed one would produce a gate.approval.<seq>.<policy> key GateMetadataReader rejects,
    // silently dropping escalation evidence). Internal (not private): AgentEvalGatekeeperExtensions.UseGatekeeper
    // calls this as a PREFLIGHT — before any Use(...)/UseToolApproval(...) call mutates the caller's builder
    // (AIAgentBuilder.Use(...) mutates and returns the SAME instance, not an immutable copy) — so a malformed
    // approval gate can never be discovered only after UseGatekeeper has already composed the run/tool gates
    // onto the caller's builder with no way to roll back.
    internal static void ValidateApprovalGates(IReadOnlyList<IToolApprovalGate> gates)
    {
        // FAIL-CLOSED: an empty gate list would make the "all gates agree" rule vacuously true, auto-approving
        // EVERY .RequiresApproval() tool with no human — strictly more permissive than not calling this at all
        // (an approval-required tool escalates by default absent any auto-approval rule). Require at least one gate.
        if (gates.Count == 0)
        {
            throw new ArgumentException(
                "At least one approval gate is required — an empty list would auto-approve every call (fail-open).",
                nameof(gates));
        }

        for (var i = 0; i < gates.Count; i++)
        {
            var gate = gates[i] ?? throw new ArgumentException("gates contains a null element.", nameof(gates));

            if (string.IsNullOrWhiteSpace(gate.PolicyName) ||
                gate.PolicyName.Split('.').Any(static seg => seg.Length == 0))
            {
                throw new ArgumentException(
                    $"Gate at index {i} has an invalid PolicyName ('{gate.PolicyName}') — it must be non-empty with no empty dot-segments.",
                    nameof(gates));
            }
        }
    }

    // Escalation evidence lives in its OWN namespace (gate.approval.*), distinct from gate.tool.* blocks — an
    // escalation is NOT a block (the call may still run once a human approves), so it must not inflate the Glass
    // Box GateBlockCount.
    private static void RecordEscalation(AgentTrace? trace, int seq, string policyName, string? functionName, string reason)
    {
        if (trace is null)
        {
            return;
        }

        trace.SetMetadata($"gate.approval.{seq}.{policyName}", new Dictionary<string, object?>
        {
            ["action"] = "RequireApproval",
            ["function"] = functionName,
            ["reason"] = reason,
            ["correlationId"] = ToolCorrelationScope.Current,   // stamp like gate.tool.*/gate.run-*.* for trace correlation
        });
    }
}
