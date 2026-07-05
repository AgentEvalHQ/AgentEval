// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2) — registers deterministic tool gates at the MAF function-invocation seam so a live
/// tool call can be blocked, terminated, or have its arguments rewritten before it executes. Verdicts are
/// recorded as <c>gate.tool.*</c> trace evidence in the exact shape the shipped Glass Box evidence reader
/// already counts (zero read-path change).
/// <para><b>Fail-closed:</b> a block returns a non-null refusal at the return-to-FICC boundary, so a block can
/// never surface as MEAI's "Success: Function completed." fabrication (HAZARD-1). A non-FICC inner agent throws
/// (MAF, HAZARD-2) — we keep that fail-closed behavior. Only <see cref="GateCost.PureCode"/> /
/// <see cref="GateCost.Bounded"/> gates may run inline; network/LLM gates are rejected at construction.</para>
/// </summary>
public static class AgentEvalToolGateExtensions
{
    /// <summary>
    /// Adds tool gates to the agent pipeline. Requires the inner agent to include a
    /// <c>FunctionInvokingChatClient</c> (a default <see cref="ChatClientAgent"/> inserts one); otherwise MAF
    /// throws (fail-closed).
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="gates">The gates to run, in order, on every tool call. Rejected if any has a Network/Llm cost.</param>
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

        // Build-time cost rejection: Network/Llm gates belong in shadow mode, not the inline hot path.
        foreach (var g in gates)
        {
            if (g.Cost is GateCost.Network or GateCost.Llm)
            {
                throw new ArgumentException(
                    $"Gate '{g.PolicyName}' has Cost={g.Cost}, which cannot run inline on the tool-invocation hot path. " +
                    "Use shadow mode (a later milestone) for network/LLM gates.", nameof(gates));
            }
        }

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

                switch (verdict.Action)
                {
                    case ToolGateAction.Allow:
                        continue;

                    case ToolGateAction.Mutate:
                    {
                        var before = SerializeArgs(context.Arguments);
                        if (verdict.NewArguments is not null)
                        {
                            // Mutate the AIFunctionArguments in place (it IS an IDictionary<string,object?>).
                            context.Arguments.Clear();
                            foreach (var kv in verdict.NewArguments)
                            {
                                context.Arguments[kv.Key] = kv.Value;
                            }
                        }

                        RecordMutate(trace, Interlocked.Increment(ref gateSeq), verdict, before, SerializeArgs(context.Arguments));
                        continue;   // the (mutated) tool still runs
                    }

                    case ToolGateAction.Block:
                    {
                        var seq = Interlocked.Increment(ref gateSeq);
                        var terminating = policy == ToolGatePolicy.Terminate;
                        RecordBlock(trace, seq, verdict, terminating);

                        if (policy == ToolGatePolicy.WarnOnly)
                        {
                            continue;   // recorded; let the tool run
                        }

                        if (policy == ToolGatePolicy.Terminate)
                        {
                            context.Terminate = true;   // stop the function-calling loop after this
                        }

                        // ReplaceResult + Terminate: block the tool, surface a non-null refusal (fail-closed).
                        return SynthesizedRefusal(verdict.PolicyName, verdict.Reason);
                    }
                }
            }

            return await next(context, ct).ConfigureAwait(false);
        });
    }

    private static string SynthesizedRefusal(string policyName, string? reason)
        => $"BLOCKED by policy '{policyName}': {reason ?? "not permitted"}. Choose a different action.";

    private static string SerializeArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "{}";
        }

        try
        {
            return JsonSerializer.Serialize(args);
        }
        catch (NotSupportedException)
        {
            return "(unserializable)";
        }
    }

    // Mirrors EvalGatingChatClient.Record's value shape — stage token "tool" (dot-free), seq via Interlocked,
    // {action,reason,matches,correlationId}. A Terminate is still action="Block" (it blocked) + terminate=true,
    // so the shipped GlassBoxEvidence.CountGateBlocks counts it.
    private static void RecordBlock(AgentTrace? trace, int seq, ToolGateVerdict verdict, bool terminating)
    {
        if (trace is null)
        {
            return;
        }

        var value = new Dictionary<string, object?>
        {
            ["action"] = "Block",
            ["reason"] = verdict.Reason,
            ["matches"] = null,
            ["correlationId"] = ToolCorrelationScope.Current,
        };
        if (terminating)
        {
            value["terminate"] = true;
        }

        trace.SetMetadata($"gate.tool.{seq}.{verdict.PolicyName}", value);
    }

    // A Mutate is recorded (action="Mutate", NOT counted as a block) with before/after args so the change is
    // auditable/reconstructable (SEC-06). NOTE: args are serialized verbatim — do not put secrets in tool args.
    private static void RecordMutate(AgentTrace? trace, int seq, ToolGateVerdict verdict, string argsBefore, string argsAfter)
    {
        if (trace is null)
        {
            return;
        }

        trace.SetMetadata($"gate.tool.{seq}.{verdict.PolicyName}", new Dictionary<string, object?>
        {
            ["action"] = "Mutate",
            ["reason"] = verdict.Reason,
            ["argsBefore"] = argsBefore,
            ["argsAfter"] = argsAfter,
            ["correlationId"] = ToolCorrelationScope.Current,
        });
    }
}
