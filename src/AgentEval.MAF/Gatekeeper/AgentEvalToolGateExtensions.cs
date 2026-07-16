// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Encodings.Web;
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
    /// <param name="policy">
    /// How a block is enforced. Phase 1, P0-2: REQUIRED — there is no default. A prior version of this method
    /// defaulted to <see cref="ToolGatePolicy.WarnOnly"/>, so a gate that returned <c>Block</c> was, by
    /// default, only logged while the tool still ran — a silent behavior a name like "Gatekeeper" strongly
    /// implies is enforcement. Pass <see cref="ToolGatePolicy.WarnOnly"/> explicitly to keep that (still
    /// valid, still useful for staged rollout) behavior; pass <see cref="ToolGatePolicy.ReplaceResult"/> or
    /// <see cref="ToolGatePolicy.Terminate"/> to actually enforce. Prefer <c>UseGatekeeper(...)</c> /
    /// <c>ObserveWithAgentEvalGates(...)</c> / <c>EnforceAgentEvalGates(...)</c> for new code — this method
    /// remains for direct, single-mechanism composition.
    /// </param>
    /// <param name="trace">Optional Glass Box trace to record <c>gate.tool.*</c> evidence into.</param>
    /// <param name="telemetry">
    /// Optional <see cref="GateTelemetry"/> sink (Phase 1, #18) — records which gate fired, its verdict, and
    /// its latency on every invocation. Caller-owned; pass the same instance you read from later.
    /// </param>
    public static AIAgentBuilder UseAgentEvalToolGate(
        this AIAgentBuilder builder,
        IReadOnlyList<IToolGate> gates,
        ToolGatePolicy policy,
        AgentTrace? trace = null,
        GateTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(gates);

        // Build-time cost rejection: Network/Llm gates belong in shadow mode, not the inline hot path.
        foreach (var g in gates)
        {
            if (g is null)
            {
                throw new ArgumentException("gates contains a null element.", nameof(gates));
            }

            if (g.Cost is GateCost.Network or GateCost.Llm)
            {
                throw new ArgumentException(
                    $"Gate '{g.PolicyName}' has Cost={g.Cost}, which cannot run inline on the tool-invocation hot path. " +
                    "Use shadow mode (a later milestone) for network/LLM gates.", nameof(gates));
            }

            // Build-time enforcement floor: a gate whose purpose is to STOP an action (e.g. a honeypot canary)
            // must not be silently downgraded to observe-only, which would let the forbidden action run.
            if (EnforcementRank(policy) < EnforcementRank(g.MinimumPolicy))
            {
                throw new ArgumentException(
                    $"Gate '{g.PolicyName}' requires at least policy {g.MinimumPolicy} to enforce, but was registered " +
                    $"under {policy} (which would let a blocked call run). Register it under {g.MinimumPolicy} or stronger.",
                    nameof(policy));
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
                ToolGateVerdict verdict;
                var stopwatch = telemetry is null ? null : Stopwatch.StartNew();
                try
                {
                    verdict = await gate.InspectAsync(call, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    telemetry?.Record(gate.PolicyName, ToolGateAction.Block, stopwatch!.Elapsed);

                    // FAIL CLOSED (cannot-inspect ⇒ deny): a gate that throws cannot prove the call safe, so block
                    // it — regardless of policy. FICC would otherwise swallow the exception and run the tool.
                    var throwReferenceId = GateReferenceId.New();
                    RecordBlock(trace, Interlocked.Increment(ref gateSeq),
                        ToolGateVerdict.Block(gate.PolicyName, $"gate evaluation threw ({ex.GetType().Name}) — failing closed"),
                        action: "Block", terminating: policy == ToolGatePolicy.Terminate, referenceId: throwReferenceId);
                    if (policy == ToolGatePolicy.Terminate)
                    {
                        context.Terminate = true;
                    }

                    return GateReferenceId.RefusalBody(throwReferenceId);
                }

                telemetry?.Record(gate.PolicyName, verdict.Action, stopwatch!.Elapsed);

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
                        var enforced = policy != ToolGatePolicy.WarnOnly;
                        var referenceId = GateReferenceId.New();

                        // Honest evidence: only an ENFORCED block records action="Block" (so GlassBoxEvidence's
                        // GateBlockCount never counts a call that actually ran). WarnOnly records action="Warn".
                        RecordBlock(trace, seq, verdict, action: enforced ? "Block" : "Warn",
                            terminating: policy == ToolGatePolicy.Terminate, referenceId: referenceId);

                        if (!enforced)
                        {
                            continue;   // WarnOnly: recorded as a warning; let the tool run
                        }

                        if (policy == ToolGatePolicy.Terminate)
                        {
                            context.Terminate = true;   // stop the function-calling loop after this
                        }

                        // ReplaceResult + Terminate: block the tool, surface a non-null refusal (fail-closed).
                        // #12: the model sees only a stable, non-revealing {error, referenceId} shape — never the
                        // policy name or reason (that stays in the trace evidence below, audit-visible only).
                        return GateReferenceId.RefusalBody(referenceId);
                    }
                }
            }

            return await next(context, ct).ConfigureAwait(false);
        });
    }

    // Enforcement strength ordering: WarnOnly (observe) < ReplaceResult (block) < Terminate (block + stop loop).
    private static int EnforcementRank(ToolGatePolicy policy) => policy switch
    {
        ToolGatePolicy.WarnOnly => 0,
        ToolGatePolicy.ReplaceResult => 1,
        ToolGatePolicy.Terminate => 2,
        _ => 0,
    };

    // Relaxed encoder so the recorded args are FAITHFUL (not JSON-escaped): default escaping would render < > & '
    // and non-ASCII as \uXXXX, so the mutation audit would not match the values the tool actually receives.
    private static readonly JsonSerializerOptions ArgsSerializerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string SerializeArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "{}";
        }

        try
        {
            return JsonSerializer.Serialize(args, ArgsSerializerOptions);
        }
        catch (NotSupportedException)
        {
            return "(unserializable)";
        }
    }

    // Mirrors EvalGatingChatClient.Record's value shape — stage token "tool" (dot-free), seq via Interlocked,
    // {action,reason,matches,correlationId}. A Terminate is still action="Block" (it blocked) + terminate=true,
    // so the shipped GlassBoxEvidence.CountGateBlocks counts it.
    // #12: the full policy name (the metadata key itself) and reason live ONLY here — audit-visible trace
    // evidence, never returned to the model. referenceId is the ONLY thing the two are allowed to share, so an
    // auditor can correlate what the model saw with what actually happened.
    private static void RecordBlock(AgentTrace? trace, int seq, ToolGateVerdict verdict, string action, bool terminating, string referenceId)
    {
        if (trace is null)
        {
            return;
        }

        var value = new Dictionary<string, object?>
        {
            ["action"] = action,   // "Block" (enforced) or "Warn" (WarnOnly — recorded but the tool ran)
            ["reason"] = verdict.Reason,
            ["matches"] = null,
            ["correlationId"] = ToolCorrelationScope.Current,
            ["referenceId"] = referenceId,
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
