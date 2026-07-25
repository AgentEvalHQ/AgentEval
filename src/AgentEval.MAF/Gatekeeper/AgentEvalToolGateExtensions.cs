// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    /// <param name="mutationCaptureMode">
    /// How much of a <see cref="ToolGateAction.Mutate"/> verdict's before/after arguments are captured into
    /// the trace (Phase 1, #13). Defaults to <see cref="TraceCaptureMode.Redacted"/> — a prior version always
    /// captured arguments verbatim, which could put a secret an argument carries into the trace. Pass
    /// <see cref="TraceCaptureMode.Full"/> explicitly to restore that behavior when you know your arguments
    /// never carry secrets and want the exact before/after values for debugging.
    /// </param>
    /// <param name="resultGates">
    /// Gatekeeper Hardening Phase 2, P0-3 — gates that inspect this call's already-executed RESULT (not the
    /// proposed call) before it flows back to the model. Run in order, AFTER every <paramref name="gates"/>
    /// entry has allowed the call and the tool has actually executed. <paramref name="policy"/> is reused for
    /// enforcement, reinterpreted for a post-execution subject: <see cref="ToolGatePolicy.WarnOnly"/> records a
    /// <see cref="ToolResultAction.Block"/> finding but still returns the real result;
    /// <see cref="ToolGatePolicy.ReplaceResult"/>/<see cref="ToolGatePolicy.Terminate"/> swap in the same
    /// non-revealing <c>{error, referenceId}</c> shape <paramref name="gates"/> blocks use. Null/empty ⇒ no
    /// result inspection at all (the exact prior behavior — fully backward compatible).
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
        TraceCaptureMode mutationCaptureMode = TraceCaptureMode.Redacted,
        IReadOnlyList<IToolResultGate>? resultGates = null,
        IGateEvidenceSink? evidenceSink = null)
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

        // P0-3: same snapshot-and-validate discipline for result gates.
        var frozenResultGates = (resultGates ?? Array.Empty<IToolResultGate>()).ToArray();
        ValidateResultGates(frozenResultGates, policy);

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
            // P2-6: a RunLedger-backed RESULT gate falls back to the same shared, process-wide state when no scope
            // is established — surface it in the same best-effort runtime warning as the call gates.
            .Concat(frozenResultGates
                .Where(g => g.Requirements.HasFlag(GateRequirements.RunScope))
                .Select(g => g.PolicyName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var missingScopeWarned = 0;   // Interlocked-guarded: at most once per pipeline instance, not once per call

        var gateSeq = 0;

        // F-C / P3-6: fingerprint the frozen gate configuration ONCE — every evidence record this pipeline writes
        // is stamped with it, tying the record to the exact gate list (and order) that produced it.
        var configFingerprint = GateConfigFingerprint.Compute(frozenGates, frozenResultGates);

        return builder.Use(async (agent, context, next, ct) =>
        {
            // F-C / P3-2: the per-call enrichment context stamped onto every record from this invocation.
            var evidence = new GateEvidenceContext(agent.Name, context.Function.Name, context.Iteration, configFingerprint, evidenceSink);

            if (scopeRequiringGateNames.Length > 0 && AgentRunScope.Current is null &&
                Interlocked.CompareExchange(ref missingScopeWarned, 1, 0) == 0)
            {
                RecordMissingScopeWarning(trace, Interlocked.Increment(ref gateSeq), scopeRequiringGateNames, evidence);
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

            // P2-4: the call-gate pass runs to a FIXED POINT. When an (enforced) Mutate actually CHANGES the
            // arguments, the tool call is re-validated from the top so a gate that would have blocked the NEW
            // arguments still gets its say — a mutation must never smuggle in a pattern an earlier gate rejects
            // (e.g. a "normalize path" mutator producing a "../.." traversal a path gate would have blocked).
            // A GateRequirements.RunScope (stateful) gate is invoked EXACTLY ONCE — the FIRST time it is reached —
            // because re-invoking it would double-count its ledger, and its verdict keys on accumulated state or
            // tool IDENTITY, not the mutated ARGUMENT VALUES a re-scan surfaces. It must still run once even when an
            // earlier mutation short-circuited the pass before reaching it — tracked per gate below, NOT by a
            // blanket "skip on every revalidation pass" (which would let a stateful gate ordered AFTER a mutator be
            // bypassed on every pass — a fail-open). A run that never converges fails closed after a bounded cap.
            const int maxMutationRevalidations = 8;
            var revalidations = 0;
            var statefulGateInvoked = new bool[frozenGates.Length];

            while (true)
            {
                var argumentsChanged = false;

                for (var gateIndex = 0; gateIndex < frozenGates.Length; gateIndex++)
                {
                    var gate = frozenGates[gateIndex];

                    // Run a stateful gate once (first reach), then skip it on later passes; run pure gates every pass.
                    if (gate.Requirements.HasFlag(GateRequirements.RunScope))
                    {
                        if (statefulGateInvoked[gateIndex])
                        {
                            continue;
                        }

                        statefulGateInvoked[gateIndex] = true;
                    }

                    ToolGateVerdict verdict;
                    var stopwatch = telemetry is null ? null : Stopwatch.StartNew();
                    try
                    {
                        verdict = await gate.InspectAsync(call, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        telemetry?.Record(gate.PolicyName, ToolGateAction.Block, stopwatch!.Elapsed);

                        // FAIL CLOSED (cannot-inspect ⇒ deny): a gate that throws cannot prove the call safe, so
                        // block it — regardless of policy. FICC would otherwise swallow the exception and run the tool.
                        var throwReferenceId = GateReferenceId.New();
                        RecordBlock(trace, Interlocked.Increment(ref gateSeq),
                            ToolGateVerdict.Block(gate.PolicyName, $"gate evaluation threw ({ex.GetType().Name}) — failing closed"),
                            action: "Block", terminating: policy == ToolGatePolicy.Terminate, referenceId: throwReferenceId,
                            evidence, failedClosedOnThrow: true);
                        if (policy == ToolGatePolicy.Terminate)
                        {
                            context.Terminate = true;
                        }

                        return GateReferenceId.RefusalBody(throwReferenceId, RefusalDispositionClassifier.Classify(gate.PolicyName, failedClosedOnThrow: true));
                    }

                    telemetry?.Record(gate.PolicyName, verdict.Action, stopwatch!.Elapsed);

                    switch (verdict.Action)
                    {
                        case ToolGateAction.Allow:
                            continue;

                        case ToolGateAction.Mutate:
                        {
                            // P2-2: under WarnOnly (the policy Observe maps to) a Mutate is RECORDED but NOT applied —
                            // rewriting the live arguments the tool receives is a behavior change, and observe-only
                            // must never change behavior. Only real enforcement (ReplaceResult/Terminate) rewrites
                            // them. The evidence still shows what the gate WOULD have done (argsAfter = the proposed
                            // NewArguments), so observe mode is a faithful dry-run, never a silent no-op.
                            var applied = policy != ToolGatePolicy.WarnOnly;

                            // A mutation only triggers revalidation (and only actually rewrites) when it CHANGES the
                            // arguments. An idempotent / no-op Mutate — proposed == current, e.g. a normalizer re-run
                            // on already-normalized args — is a fixed point, not a new state to re-scan, so it neither
                            // rewrites nor restarts (which also stops an always-Mutate-with-identical-args gate from
                            // spinning to the cap). ArgumentsEqual short-circuits the alias case (P2-4a) too.
                            var changesArgs = applied && verdict.NewArguments is not null
                                && !ArgumentsEqual(call.Arguments, verdict.NewArguments);

                            // Snapshot BEFORE mutating — a live reference would show the post-mutation values for
                            // both "before" and "after" once context.Arguments is cleared/rewritten below. Skip the
                            // copy entirely when there's no trace (RecordMutate is a no-op then) or capture is off.
                            var before = trace is null || mutationCaptureMode == TraceCaptureMode.None
                                ? null
                                : new Dictionary<string, object?>(context.Arguments);

                            if (changesArgs)
                            {
                                // P2-4a (WM-5): snapshot NewArguments into an independent dictionary BEFORE clearing.
                                // A gate may return the very dictionary it was handed — context.Arguments is exposed
                                // to gates as call.Arguments (an IReadOnlyDictionary view over THIS same instance), so
                                // a gate that rewrites a key on that view and hands it back aliases context.Arguments.
                                // Without the snapshot, Clear() below wipes the entries we are about to copy.
                                var replacement = new Dictionary<string, object?>(verdict.NewArguments!);

                                // Mutate the AIFunctionArguments in place (it IS an IDictionary<string,object?>).
                                context.Arguments.Clear();
                                foreach (var kv in replacement)
                                {
                                    context.Arguments[kv.Key] = kv.Value;
                                }
                            }

                            // Record a Mutate that either actually rewrote the arguments (changesArgs) or was a
                            // WarnOnly dry-run (!applied, a genuine observed finding). A no-op ENFORCEMENT Mutate —
                            // NewArguments == current, which is what an idempotent normalizer returns once its
                            // fixed point is reached on a revalidation pass — is neither, so it is not re-recorded
                            // (that would bloat the trace with a redundant fixed-point confirmation every pass).
                            // argsAfter is what the gate PROPOSED, applied or not — when applied it equals the
                            // now-live context.Arguments; when observed it is the counterfactual "would have been".
                            if (changesArgs || !applied)
                            {
                                RecordMutate(trace, Interlocked.Increment(ref gateSeq), verdict, before,
                                    verdict.NewArguments ?? context.Arguments, mutationCaptureMode, applied, evidence);
                            }

                            if (changesArgs)
                            {
                                argumentsChanged = true;   // re-validate from the top against the new arguments
                            }

                            break;   // out of the switch; the post-switch check decides continue vs. revalidate
                        }

                        case ToolGateAction.Block:
                        {
                            var seq = Interlocked.Increment(ref gateSeq);
                            var enforced = policy != ToolGatePolicy.WarnOnly;
                            var referenceId = GateReferenceId.New();

                            // Honest evidence: only an ENFORCED block records action="Block" (so GlassBoxEvidence's
                            // GateBlockCount never counts a call that actually ran). WarnOnly records action="Warn".
                            RecordBlock(trace, seq, verdict, action: enforced ? "Block" : "Warn",
                                terminating: policy == ToolGatePolicy.Terminate, referenceId: referenceId, evidence);

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
                            // P4-3: tally this denial by (tool + arg-shape); an equivalent retry surfaces "attempts":N
                            // so a good agent sees it's looping — without leaking why. Review #2: only tally when a
                            // run scope is established — a scopeless caller would otherwise accumulate denials in the
                            // process-wide fallback ledger, bleeding the count across runs/sessions; omit attempts then.
                            int? attempts = null;
                            if (AgentRunScope.Current is not null)
                            {
                                attempts = RunLedger.ForCurrentRun().RecordDenial(DenialKey(call));   // P4-3 per-(tool+arg-shape) retry tally
                                RunLedger.ForRootRun().RecordTreeDenial();   // P6-1 block-storm total, aggregated at the run-tree root
                            }

                            return GateReferenceId.RefusalBody(referenceId, RefusalDispositionClassifier.Classify(verdict.PolicyName), attempts);
                        }
                    }

                    if (argumentsChanged)
                    {
                        break;   // an arg-changing mutation this pass ⇒ restart the scan against the new arguments
                    }
                }

                if (!argumentsChanged)
                {
                    break;   // a full pass with no arg-changing mutation ⇒ fixed point reached, proceed to the tool
                }

                if (++revalidations > maxMutationRevalidations)
                {
                    // Non-convergence: a gate keeps changing the arguments. Fail closed rather than loop forever.
                    var refId = GateReferenceId.New();
                    RecordBlock(trace, Interlocked.Increment(ref gateSeq),
                        ToolGateVerdict.Block("MutationRevalidation",
                            $"tool-call arguments did not converge after {maxMutationRevalidations} mutation re-validations — failing closed"),
                        action: "Block", terminating: policy == ToolGatePolicy.Terminate, referenceId: refId, evidence,
                        failedClosedOnThrow: true);
                    if (policy == ToolGatePolicy.Terminate)
                    {
                        context.Terminate = true;
                    }

                    return GateReferenceId.RefusalBody(refId, RefusalDisposition.Transient);   // non-convergence is a fail-closed defensive block
                }
            }

            var toolResult = await next(context, ct).ConfigureAwait(false);

            if (frozenResultGates.Length == 0)
            {
                return toolResult;   // exact prior behavior — zero overhead when no result gate is registered
            }

            // P0-3: the tool has now ACTUALLY executed — result gates only ever decide what the model gets to
            // see of a result that already happened, never whether the call itself was allowed.
            // P5-5: ONE shared view whose ResultText is serialized lazily and reused across result gates while the
            // result is unchanged (serialize once, not once per gate); re-created only when a Redact rewrites the
            // result (a genuinely new value to serialize).
            GatedToolResult MakeResultView() => new(
                context.Function.Name, context.Arguments as IReadOnlyDictionary<string, object?>, toolResult,
                agent.Name, context.Iteration, context.FunctionCallIndex, context.FunctionCount, context.IsStreaming,
                context.Messages as IReadOnlyList<ChatMessage>);
            var resultView = MakeResultView();

            foreach (var resultGate in frozenResultGates)
            {
                ToolResultVerdict resultVerdict;
                var resultStopwatch = telemetry is null ? null : Stopwatch.StartNew();

                try
                {
                    resultVerdict = await resultGate.InspectAsync(resultView, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    telemetry?.Record(resultGate.PolicyName, ToolResultAction.Block, resultStopwatch!.Elapsed);

                    // FAIL CLOSED, same rule as the call-gate loop above, and — unlike a normal verdict-based
                    // Block below — NOT subject to WarnOnly: a gate that throws cannot prove the RESULT safe,
                    // so it must be withheld regardless of policy, exactly like the call-gate throw handler
                    // above. This branch previously special-cased WarnOnly to let the raw, uninspected result
                    // through unchanged — a real fail-OPEN bug found in review, the opposite of every other
                    // "gate threw" path in this file.
                    var throwReferenceId = GateReferenceId.New();
                    RecordResultBlock(trace, Interlocked.Increment(ref gateSeq), resultGate.PolicyName,
                        $"result gate evaluation threw ({ex.GetType().Name}) — failing closed",
                        action: "Block", terminating: policy == ToolGatePolicy.Terminate, referenceId: throwReferenceId,
                        evidence, failedClosedOnThrow: true);

                    if (policy == ToolGatePolicy.Terminate)
                    {
                        context.Terminate = true;
                    }

                    return GateReferenceId.RefusalBody(throwReferenceId, RefusalDispositionClassifier.Classify(resultGate.PolicyName, failedClosedOnThrow: true));
                }

                telemetry?.Record(resultGate.PolicyName, resultVerdict.Action, resultStopwatch!.Elapsed);

                switch (resultVerdict.Action)
                {
                    case ToolResultAction.Allow:
                        continue;

                    case ToolResultAction.Redact:
                        // P2-2: under WarnOnly (the policy Observe maps to) a Redact is RECORDED but NOT applied —
                        // replacing a live result is a behavior change, and observe-only must not change behavior.
                        // The real result flows through unchanged; the evidence shows a redaction WOULD have fired.
                        if (policy == ToolGatePolicy.WarnOnly)
                        {
                            RecordResultRedact(trace, Interlocked.Increment(ref gateSeq), resultVerdict, applied: false, evidence);
                            continue;
                        }

                        // Enforcement (ReplaceResult/Terminate): the redaction IS applied.
                        if (resultVerdict.RedactedResult is null)
                        {
                            // P2-5 / HAZARD-1: a null RedactedResult would blank the result to null, which the
                            // model-facing layer renders as the "Success: Function completed." fabrication this whole
                            // result-gate family exists to prevent (a withheld result must never read as a successful
                            // empty one). Fall back to the same stable, non-revealing refusal body a Block uses —
                            // never null — and record the referenceId embedded in it so an auditor can correlate.
                            // Recorded as action="Block" (Phase 3 review): the model receives a REFUSAL here (the
                            // result is withheld), so this is a block — counted by GateBlockCount and, crucially,
                            // written to the durable reference index (which only persists model-facing refusals).
                            var redactReferenceId = GateReferenceId.New();
                            RecordResultBlock(trace, Interlocked.Increment(ref gateSeq), resultVerdict.PolicyName,
                                resultVerdict.Reason ?? "result redacted with no replacement supplied — failing closed to a non-revealing refusal",
                                action: "Block", terminating: false, referenceId: redactReferenceId, evidence);
                            toolResult = GateReferenceId.RefusalBody(redactReferenceId, RefusalDispositionClassifier.Classify(resultVerdict.PolicyName));
                        }
                        else
                        {
                            toolResult = resultVerdict.RedactedResult;
                            RecordResultRedact(trace, Interlocked.Increment(ref gateSeq), resultVerdict, applied: true, evidence);
                        }

                        resultView = MakeResultView();   // P5-5: the result changed — the next gate needs a fresh view + serialization
                        continue;

                    case ToolResultAction.Block:
                    {
                        var seq = Interlocked.Increment(ref gateSeq);
                        var enforced = policy != ToolGatePolicy.WarnOnly;
                        var referenceId = GateReferenceId.New();

                        RecordResultBlock(trace, seq, resultVerdict.PolicyName, resultVerdict.Reason,
                            action: enforced ? "Block" : "Warn", terminating: policy == ToolGatePolicy.Terminate,
                            referenceId: referenceId, evidence);

                        if (!enforced)
                        {
                            continue;   // WarnOnly: recorded as a warning; the real result still flows through
                        }

                        if (policy == ToolGatePolicy.Terminate)
                        {
                            context.Terminate = true;
                        }

                        // Same non-revealing shape the call-gate loop uses — #12's design applies identically here.
                        return GateReferenceId.RefusalBody(referenceId, RefusalDispositionClassifier.Classify(resultVerdict.PolicyName));
                    }
                }
            }

            return toolResult;
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

    // P0-3: same validation shape as ValidateGates (null elements, Network/Llm cost rejection, enforcement
    // floor), for the separate IToolResultGate interface. Not generalized into one shared method over both
    // interfaces — IToolGate and IToolResultGate are deliberately distinct types (see IToolResultGate's own
    // remarks), and a generic constraint here would need a common base interface that doesn't otherwise exist
    // and isn't worth introducing just to save this small a duplication.
    internal static void ValidateResultGates(IReadOnlyList<IToolResultGate> resultGates, ToolGatePolicy policy)
    {
        foreach (var g in resultGates)
        {
            if (g is null)
            {
                throw new ArgumentException("resultGates contains a null element.", nameof(resultGates));
            }

            if (g.Cost is GateCost.Network or GateCost.Llm)
            {
                throw new ArgumentException(
                    $"Result gate '{g.PolicyName}' has Cost={g.Cost}, which cannot run inline on the tool-invocation " +
                    "hot path. Use shadow mode (a later milestone) for network/LLM gates.", nameof(resultGates));
            }

            if (EnforcementRank(policy) < EnforcementRank(g.MinimumPolicy))
            {
                throw new ArgumentException(
                    $"Result gate '{g.PolicyName}' requires at least policy {g.MinimumPolicy} to enforce, but was " +
                    $"registered under {policy} (which would let a blocked result reach the model). Register it " +
                    $"under {g.MinimumPolicy} or stronger.", nameof(policy));
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
    // F-C: write one record to the trace (audit projection) AND fan it out to any registered extra sink
    // (ledger / alerting). The trace write is null-safe so evidence still reaches the sink with tracing off.
    private static void EmitEvidence(AgentTrace? trace, GateEvidenceContext evidence, int seq, GateEvidence record)
    {
        evidence.Emit(record, seq);
        trace?.SetMetadata(record.TraceKey(seq), record.ToMetadata());
    }

    private static void RecordBlock(AgentTrace? trace, int seq, ToolGateVerdict verdict, string action, bool terminating, string referenceId, GateEvidenceContext evidence, bool failedClosedOnThrow = false)
    {
        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        var extra = new Dictionary<string, object?> { ["matches"] = null };
        if (terminating)
        {
            extra["terminate"] = true;
        }

        // P6-2: a non-enforced (WarnOnly/Observe) block records action="Warn"; capture what it WOULD have done so an
        // Observe→Enforce dry-run diff can count would-have-blocks (GlassBoxEvidence.WouldBlockCount) without
        // inflating the enforced GateBlockCount.
        if (action != "Block")
        {
            extra["wouldAction"] = "Block";
        }

        // F-C: one canonical record, projected to the same superset trace value the readers parse.
        var record = evidence.Build(
            stage: "tool", policy: verdict.PolicyName, action: action, referenceId: referenceId, reason: verdict.Reason,
            severity: GateSeverityClassifier.Classify(verdict.PolicyName, failedClosedOnThrow),
            correlationId: ToolCorrelationScope.Current, extra: extra);
        EmitEvidence(trace, evidence, seq, record);
    }

    // P0-3: same shape as RecordBlock, stage token "tool-result" (not "tool") so a result-gate block is
    // distinguishable from a call-gate block in trace evidence while still being picked up by
    // GateMetadataReader/GlassBoxEvidence.CountGateBlocks (both are stage-agnostic — any gate.{stage}.{seq}.{policy}
    // key with action="Block" counts). Reason is passed as a plain string (not a verdict) since this is also
    // called from the fail-closed catch block, which has no ToolResultVerdict to hand in.
    private static void RecordResultBlock(AgentTrace? trace, int seq, string policyName, string? reason, string action, bool terminating, string referenceId, GateEvidenceContext evidence, bool failedClosedOnThrow = false)
    {
        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        var extra = new Dictionary<string, object?> { ["matches"] = null };
        if (terminating)
        {
            extra["terminate"] = true;
        }

        // P6-2: same counterfactual marker as RecordBlock (see there) — a non-enforced result-gate block is a would-block.
        if (action != "Block")
        {
            extra["wouldAction"] = "Block";
        }

        var record = evidence.Build(
            stage: "tool-result", policy: policyName, action: action, referenceId: referenceId, reason: reason,
            severity: GateSeverityClassifier.Classify(policyName, failedClosedOnThrow),
            correlationId: ToolCorrelationScope.Current, extra: extra);
        EmitEvidence(trace, evidence, seq, record);
    }

    // P0-3: a Redact verdict is recorded (action="Redact", NOT counted as a block) — deliberately does NOT
    // capture the before/after result content itself (unlike RecordMutate's TraceCaptureMode-gated capture of
    // tool ARGUMENTS). A tool RESULT can be arbitrarily large/shaped content from anywhere (a web page, a file,
    // a database row) — capturing it into the trace at all, even redacted-by-default, is a bigger surface than
    // this pass scopes; only the fact that a redaction happened, and why, is recorded. Full result-capture
    // (mirroring MutationEvidenceRenderer/TraceCaptureMode) is deliberately deferred, not an oversight.
    private static void RecordResultRedact(AgentTrace? trace, int seq, ToolResultVerdict verdict, bool applied, GateEvidenceContext evidence)
    {
        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        // P2-2: applied=false under WarnOnly, where the redaction is recorded but the real result was NOT replaced.
        var record = evidence.Build(
            stage: "tool-result", policy: verdict.PolicyName, action: "Redact", referenceId: GateReferenceId.New(),
            reason: verdict.Reason, severity: GateSeverityClassifier.Classify(verdict.PolicyName),
            correlationId: ToolCorrelationScope.Current,
            extra: new Dictionary<string, object?> { ["applied"] = applied });
        EmitEvidence(trace, evidence, seq, record);
    }

    // #4-revisit: the best-effort RUNTIME missing-run-scope signal — see the field comment where
    // scopeRequiringGateNames is computed. Uses the "warning" stage token (not "tool") and action="Warn" so it
    // reads naturally alongside real gate verdicts in GateVoice/GlassBoxEvidence (never counted as a block —
    // GlassBoxEvidence.CountGateBlocks only counts action="Block").
    private static void RecordMissingScopeWarning(AgentTrace? trace, int seq, IReadOnlyList<string> offenders, GateEvidenceContext evidence)
    {
        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        var reason = $"{string.Join(", ", offenders)} declare GateRequirements.RunScope, but no AgentRunScope " +
                     "is established for this run — they fall back to shared, process-wide state (self-" +
                     "documented on RunLedger/SequenceGate). Call UseAgentEvalGate() before this middleware, " +
                     "or prefer UseGatekeeper(...), which refuses construction for this case instead of only warning.";
        var record = evidence.Build(
            stage: "warning", policy: "MissingRunScope", action: "Warn", referenceId: GateReferenceId.New(), reason: reason,
            severity: GateSeverity.Suspicious, correlationId: ToolCorrelationScope.Current,
            extra: new Dictionary<string, object?> { ["matches"] = null });
        EmitEvidence(trace, evidence, seq, record);
    }

    // A Mutate is recorded (action="Mutate", NOT counted as a block) with before/after args so the change is
    // auditable/reconstructable (SEC-06). #13: how MUCH of the args is captured is governed by TraceCaptureMode
    // (default Redacted) — a prior version always serialized verbatim, which could put an argument's secret
    // value into the trace.
    // P4-3: a stable signature of a tool call (name + sorted arguments, JSON-serialized) so an EQUIVALENT retry —
    // same tool, same argument shape — shares the key. Review #4: JSON serialization (not key=value string-join)
    // distinguishes structured values (["/a"] vs ["/b"] — a bare ToString() would render both to the same type
    // name) and can't be spoofed by a '\n'/'=' in a value. Hashed so argument VALUES never linger in the key.
    private static string DenialKey(GatedToolCall call)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (call.Arguments is not null)
        {
            foreach (var kv in call.Arguments)
            {
                sorted[kv.Key] = kv.Value;
            }
        }

        var payload = call.FunctionName + "\n" + JsonSerializer.Serialize(sorted);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    // P2-4: structural equality over two argument sets — tells a REAL mutation (re-scan) from a no-op / idempotent
    // one (fixed point). Null is treated as empty. Cheap: reference short-circuit, count check, then a key+value
    // comparison via the CURRENT dictionary's own lookup (so it honors that dictionary's key comparer).
    internal static bool ArgumentsEqual(IReadOnlyDictionary<string, object?>? current, IReadOnlyDictionary<string, object?>? proposed)
    {
        if (ReferenceEquals(current, proposed))
        {
            return true;
        }

        var currentCount = current?.Count ?? 0;
        var proposedCount = proposed?.Count ?? 0;
        if (currentCount != proposedCount)
        {
            return false;
        }

        if (currentCount == 0)
        {
            return true;
        }

        foreach (var kv in proposed!)
        {
            if (!current!.TryGetValue(kv.Key, out var currentValue) || !Equals(currentValue, kv.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static void RecordMutate(
        AgentTrace? trace, int seq, ToolGateVerdict verdict,
        IReadOnlyDictionary<string, object?>? argsBefore, IReadOnlyDictionary<string, object?>? argsAfter, TraceCaptureMode captureMode, bool applied, GateEvidenceContext evidence)
    {
        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        var record = evidence.Build(
            stage: "tool", policy: verdict.PolicyName, action: "Mutate", referenceId: GateReferenceId.New(),
            reason: verdict.Reason, severity: GateSeverityClassifier.Classify(verdict.PolicyName),
            correlationId: ToolCorrelationScope.Current,
            extra: new Dictionary<string, object?>
            {
                // P2-2: applied is false under WarnOnly, where argsAfter is the counterfactual the gate proposed
                // but the tool did NOT receive (the live arguments were left unchanged).
                ["applied"] = applied,
                ["argsBefore"] = MutationEvidenceRenderer.Render(argsBefore, captureMode),
                ["argsAfter"] = MutationEvidenceRenderer.Render(argsAfter, captureMode),
                ["captureMode"] = captureMode.ToString(),
            });
        EmitEvidence(trace, evidence, seq, record);
    }
}
