// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
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
    /// <param name="mutationCaptureMode">
    /// How much of a <see cref="ToolGateAction.Mutate"/> verdict's before/after arguments are captured into
    /// the trace (Phase 1, #13). Defaults to <see cref="TraceCaptureMode.Redacted"/> — a prior version always
    /// captured arguments verbatim, which could put a secret an argument carries into the trace. Pass
    /// <see cref="TraceCaptureMode.Full"/> explicitly to restore that behavior when you know your arguments
    /// never carry secrets and want the exact before/after values for debugging.
    /// </param>
    /// <remarks>
    /// <b>Only a best-effort RUNTIME <see cref="GateRequirements.RunScope"/> signal at this seam, not a
    /// construction-time guard.</b> A <paramref name="gates"/> entry that declares
    /// <see cref="GateRequirements.RunScope"/> (e.g. <see cref="RunBudgetGate"/>, <see cref="SequenceGate"/>)
    /// still runs here even when no <see cref="AgentRunScope"/> is established. This method CANNOT check that
    /// at registration time — <see cref="AgentRunScope.Current"/> is an <c>AsyncLocal</c> only ever set inside
    /// an actual run (<c>UseAgentEvalGate</c>'s <c>runFunc</c>), so at registration (before any run has
    /// happened) it is unconditionally null regardless of whether the pipeline is correctly wired — a
    /// registration-time check could not distinguish "correctly configured" from "not." Instead, on the FIRST
    /// tool call actually made through this pipeline, if no scope is established, ONE non-blocking
    /// <c>gate.warning.*.MissingRunScope</c> trace entry is recorded (never throws, never blocks — a hard
    /// construction-time refusal for this is <c>UseGatekeeper</c>'s job). That still means: no run at all
    /// happens ⇒ no warning either. Calling this method directly (bypassing <c>UseGatekeeper</c>) forfeits the
    /// construction-time refusal <c>UseGatekeeper</c> performs for exactly this case — the gate silently falls
    /// back to its documented single-process-wide shared state instead. Prefer <c>UseGatekeeper(...)</c> for
    /// RunScope-requiring gates, or call <c>UseAgentEvalGate()</c> yourself first.
    /// <para><b>⚠ Never chain two SEPARATE <c>UseAgentEvalToolGate</c> calls on the same builder</b> — register
    /// every gate in ONE call, in ONE list. This method is built on MAF's function-invocation middleware seam
    /// (<c>FunctionInvocationDelegatingAgentBuilderExtensions.Use</c>), where each registration wraps the
    /// pipeline built so far. Empirically confirmed against MAF 1.13.0: the SECOND (later) of two chained
    /// registrations becomes the OUTERMOST layer and its gates see every call FIRST — the opposite of what
    /// "register A's gates, then B's" reads as. If the later registration's gate blocks (without forwarding),
    /// the earlier registration's gates are never even invoked for that call — silently starved, not merely
    /// "run second." <c>UseGatekeeper(...)</c> already avoids this (it composes exactly one
    /// <c>UseAgentEvalToolGate</c> call over the full gate list) — use it, or pass every gate to a single
    /// <c>UseAgentEvalToolGate</c> call yourself.</para>
    /// </remarks>
    public static AIAgentBuilder UseAgentEvalToolGate(
        this AIAgentBuilder builder,
        IReadOnlyList<IToolGate> gates,
        ToolGatePolicy policy,
        AgentTrace? trace = null,
        GateTelemetry? telemetry = null,
        TraceCaptureMode mutationCaptureMode = TraceCaptureMode.Redacted)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(gates);
        ValidateGates(gates, policy);

        // Snapshot NOW — gates may be a caller-owned mutable List<IToolGate>. The closure below runs on every
        // tool call for the lifetime of the built agent; without a defensive copy, a later mutation to the
        // same list instance would silently change what this pipeline enforces (bypassing the validation
        // above entirely for anything added afterward) and risks a mid-request "Collection was modified" if
        // the caller mutates it while a call is in flight (AllowConcurrentInvocation).
        var frozenGates = gates.ToArray();

        // #4-revisit: a REGISTRATION-time check of AgentRunScope.Current is not just hard but structurally
        // impossible — Current is an AsyncLocal only ever set inside an actual RunAsync call
        // (AgentRunScope.Begin, invoked from UseAgentEvalGate's runFunc), so at THIS point (pipeline
        // construction, before any run has happened) it is unconditionally null for every caller, correctly
        // configured or not. A registration-time check could only ever "always fire" or "never fire" — it
        // cannot distinguish the two configurations the check exists to tell apart. This is instead a RUNTIME,
        // best-effort signal: on the first tool call actually made through this pipeline, if a RunScope-
        // requiring gate is registered and no scope is established, record ONE non-blocking gate.warning.*
        // trace entry (never throws or blocks the call — a hard, construction-time refusal for this is already
        // UseGatekeeper's job; this only helps direct UseAgentEvalToolGate callers who skip it).
        var scopeRequiringGateNames = frozenGates
            .Where(g => g.Requirements.HasFlag(GateRequirements.RunScope))
            .Select(g => g.PolicyName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var missingScopeWarned = 0;   // Interlocked-guarded: at most once per pipeline instance, not once per call

        var gateSeq = 0;

        return builder.Use(async (agent, context, next, ct) =>
        {
            if (scopeRequiringGateNames.Length > 0 && AgentRunScope.Current is null &&
                Interlocked.CompareExchange(ref missingScopeWarned, 1, 0) == 0)
            {
                RecordMissingScopeWarning(trace, Interlocked.Increment(ref gateSeq), scopeRequiringGateNames);
            }

            var call = new GatedToolCall(
                FunctionName: context.Function.Name,
                Arguments: context.Arguments as IReadOnlyDictionary<string, object?>,
                AgentName: agent.Name,
                Iteration: context.Iteration,
                FunctionCallIndex: context.FunctionCallIndex,
                FunctionCount: context.FunctionCount,
                IsStreaming: context.IsStreaming,
                Messages: context.Messages as IReadOnlyList<ChatMessage>);

            foreach (var gate in frozenGates)
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
                        // Snapshot BEFORE mutating — a live reference would show the post-mutation values for
                        // both "before" and "after" once context.Arguments is cleared/rewritten below. Skip the
                        // copy entirely when there's no trace (RecordMutate is a no-op then) or capture is off —
                        // no point allocating a dictionary nobody will ever render.
                        var before = trace is null || mutationCaptureMode == TraceCaptureMode.None
                            ? null
                            : new Dictionary<string, object?>(context.Arguments);

                        if (verdict.NewArguments is not null)
                        {
                            // Mutate the AIFunctionArguments in place (it IS an IDictionary<string,object?>).
                            context.Arguments.Clear();
                            foreach (var kv in verdict.NewArguments)
                            {
                                context.Arguments[kv.Key] = kv.Value;
                            }
                        }

                        RecordMutate(trace, Interlocked.Increment(ref gateSeq), verdict, before, context.Arguments, mutationCaptureMode);
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

    // Build-time gate-list validation: null elements, Network/Llm cost rejection (those belong in shadow mode,
    // not the inline hot path), and the enforcement-floor check (a gate whose purpose is to STOP an action —
    // e.g. a honeypot canary — must not be silently downgraded to observe-only, which would let the forbidden
    // action run). Internal (not private): AgentEvalGatekeeperExtensions.UseGatekeeper calls this as a
    // PREFLIGHT — before it starts mutating the caller's AIAgentBuilder (Use(...) mutates and returns the same
    // instance, it does not hand back an immutable copy) — so a validation failure here can never leave
    // UseGatekeeper's composition half-applied to the caller's builder with no way to roll it back. One
    // authoritative check, reused by both callers, rather than two copies that can drift.
    internal static void ValidateGates(IReadOnlyList<IToolGate> gates, ToolGatePolicy policy)
    {
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

            if (EnforcementRank(policy) < EnforcementRank(g.MinimumPolicy))
            {
                throw new ArgumentException(
                    $"Gate '{g.PolicyName}' requires at least policy {g.MinimumPolicy} to enforce, but was registered " +
                    $"under {policy} (which would let a blocked call run). Register it under {g.MinimumPolicy} or stronger.",
                    nameof(policy));
            }
        }
    }

    // Enforcement strength ordering: WarnOnly (observe) < ReplaceResult (block) < Terminate (block + stop loop).
    // Internal (not private): AgentEvalGatekeeperExtensions.UseGatekeeper reuses this to compose its own,
    // more actionable message for the MinimumPolicy-floor case specifically, ahead of the general ValidateGates
    // preflight below.
    internal static int EnforcementRank(ToolGatePolicy policy) => policy switch
    {
        ToolGatePolicy.WarnOnly => 0,
        ToolGatePolicy.ReplaceResult => 1,
        ToolGatePolicy.Terminate => 2,
        _ => 0,
    };

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

    // #4-revisit: the best-effort RUNTIME missing-run-scope signal — see the field comment where
    // scopeRequiringGateNames is computed. Uses the "warning" stage token (not "tool") and action="Warn" so it
    // reads naturally alongside real gate verdicts in GateVoice/GlassBoxEvidence (never counted as a block —
    // GlassBoxEvidence.CountGateBlocks only counts action="Block").
    private static void RecordMissingScopeWarning(AgentTrace? trace, int seq, IReadOnlyList<string> offenders)
    {
        if (trace is null)
        {
            return;
        }

        trace.SetMetadata($"gate.warning.{seq}.MissingRunScope", new Dictionary<string, object?>
        {
            ["action"] = "Warn",
            ["reason"] = $"{string.Join(", ", offenders)} declare GateRequirements.RunScope, but no AgentRunScope " +
                          "is established for this run — they fall back to shared, process-wide state (self-" +
                          "documented on RunLedger/SequenceGate). Call UseAgentEvalGate() before this middleware, " +
                          "or prefer UseGatekeeper(...), which refuses construction for this case instead of only warning.",
            ["matches"] = null,
            ["correlationId"] = ToolCorrelationScope.Current,
        });
    }

    // A Mutate is recorded (action="Mutate", NOT counted as a block) with before/after args so the change is
    // auditable/reconstructable (SEC-06). #13: how MUCH of the args is captured is governed by TraceCaptureMode
    // (default Redacted) — a prior version always serialized verbatim, which could put an argument's secret
    // value into the trace.
    private static void RecordMutate(
        AgentTrace? trace, int seq, ToolGateVerdict verdict,
        IReadOnlyDictionary<string, object?>? argsBefore, IReadOnlyDictionary<string, object?>? argsAfter, TraceCaptureMode captureMode)
    {
        if (trace is null)
        {
            return;
        }

        trace.SetMetadata($"gate.tool.{seq}.{verdict.PolicyName}", new Dictionary<string, object?>
        {
            ["action"] = "Mutate",
            ["reason"] = verdict.Reason,
            ["argsBefore"] = MutationEvidenceRenderer.Render(argsBefore, captureMode),
            ["argsAfter"] = MutationEvidenceRenderer.Render(argsAfter, captureMode),
            ["captureMode"] = captureMode.ToString(),
            ["correlationId"] = ToolCorrelationScope.Current,
        });
    }
}
