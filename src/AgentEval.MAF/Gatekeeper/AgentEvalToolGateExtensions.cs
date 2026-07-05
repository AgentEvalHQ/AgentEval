// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M0) — registers deterministic tool gates at the MAF function-invocation seam so a live
/// tool call can be blocked before it executes. Verdicts are recorded as <c>gate.tool.*</c> trace evidence in
/// the exact shape the shipped Glass Box evidence reader already counts (zero read-path change).
/// <para><b>Fail-closed:</b> a <see cref="ToolGatePolicy.ReplaceResult"/> block returns a non-null refusal
/// string at the return-to-FICC boundary, so a block can never surface as MEAI's "Success: Function completed."
/// fabrication (HAZARD-1). A non-FICC inner agent throws (MAF, HAZARD-2) — we keep that fail-closed behavior.</para>
/// </summary>
public static class AgentEvalToolGateExtensions
{
    /// <summary>
    /// Adds tool gates to the agent pipeline. Requires the inner agent to include a
    /// <c>FunctionInvokingChatClient</c> (a default <see cref="ChatClientAgent"/> inserts one); otherwise MAF
    /// throws (fail-closed).
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="gates">The gates to run, in order, on every tool call.</param>
    /// <param name="policy">How a block is enforced. Defaults to <see cref="ToolGatePolicy.WarnOnly"/>.</param>
    /// <param name="trace">Optional Glass Box trace to record <c>gate.tool.*</c> evidence into.</param>
    public static AIAgentBuilder UseAgentEvalToolGate(
        this AIAgentBuilder builder,
        IReadOnlyList<IToolGate> gates,
        ToolGatePolicy policy = ToolGatePolicy.WarnOnly,
        AgentTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(gates);

        var gateSeq = 0;

        return builder.Use(async (agent, context, next, ct) =>
        {
            var call = new GatedToolCall(
                FunctionName: context.Function.Name,
                Arguments: context.Arguments as IReadOnlyDictionary<string, object?>,
                AgentName: agent.Name,
                Iteration: context.Iteration,
                FunctionCallIndex: context.FunctionCallIndex,
                FunctionCount: context.FunctionCount,
                IsStreaming: context.IsStreaming,
                Messages: context.Messages as IReadOnlyList<ChatMessage>);

            foreach (var gate in gates)
            {
                var verdict = await gate.InspectAsync(call, ct).ConfigureAwait(false);
                if (verdict.Action == GateAction.Block)
                {
                    var seq = Interlocked.Increment(ref gateSeq);
                    RecordBlock(trace, seq, verdict);

                    if (policy == ToolGatePolicy.ReplaceResult)
                    {
                        // Structural fail-closed: return a non-null refusal so a block can NEVER surface as
                        // MEAI's "Success: Function completed." fabrication (HAZARD-1). The tool never runs.
                        return SynthesizedRefusal(verdict.PolicyName, verdict.Reason);
                    }

                    // WarnOnly: evidence recorded; fall through and let the tool run.
                }
            }

            return await next(context, ct).ConfigureAwait(false);
        });
    }

    private static string SynthesizedRefusal(string policyName, string? reason)
        => $"BLOCKED by policy '{policyName}': {reason ?? "not permitted"}. Choose a different action.";

    // Mirrors EvalGatingChatClient.Record's value shape exactly — stage token "tool" (dot-free, so the shipped
    // GateMetadataReader.PolicyFromKey split lands correctly), seq via Interlocked, {action,reason,matches,correlationId}.
    private static void RecordBlock(AgentTrace? trace, int seq, ToolGateVerdict verdict)
    {
        if (trace is null)
        {
            return;
        }

        trace.SetMetadata($"gate.tool.{seq}.{verdict.PolicyName}", new Dictionary<string, object?>
        {
            ["action"] = verdict.Action.ToString(),
            ["reason"] = verdict.Reason,
            ["matches"] = null,
            ["correlationId"] = ToolCorrelationScope.Current,
        });
    }
}
