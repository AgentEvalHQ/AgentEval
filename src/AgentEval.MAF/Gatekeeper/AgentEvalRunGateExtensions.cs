// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentEval.Guardrails;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M2) — a RUN-level gate over the MAF agent-run seam. Reuses the shipped chat gates
/// (<see cref="IChatGate"/>) to inspect the run's <b>input</b> (run-pre — "assess incoming attacks") and its
/// <b>output</b> (run-post) text, records <c>gate.run-pre.*</c> / <c>gate.run-post.*</c> evidence, and
/// establishes an <see cref="AgentRunScope"/> so inner tool/session gates can read the run context.
/// <para>Register the run gate <b>outermost</b>. Streaming runs run-pre gates only; a run-post gate under a
/// blocking policy throws at stream start (a stream cannot be inspected without buffering, which would destroy
/// time-to-first-token).</para>
/// </summary>
public static class AgentEvalRunGateExtensions
{
    /// <summary>Adds run-level gates to the agent pipeline.</summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="pre">Gates run on the input text before the model sees it (incoming-attack detection).</param>
    /// <param name="post">Gates run on the response text (non-streaming; streaming under a blocking policy throws).</param>
    /// <param name="policy">
    /// How a block is enforced (WarnOnly / ThrowOnFail / Redact). Phase 1, P0-2: no implicit default when any
    /// <paramref name="pre"/>/<paramref name="post"/> gate is registered — passing <see langword="null"/> then
    /// throws, forcing an explicit choice (a prior version silently defaulted to WarnOnly, so a gate's Block
    /// verdict was, by default, only logged). When BOTH <paramref name="pre"/> and <paramref name="post"/> are
    /// empty/omitted — the common "just establish the run scope for SequenceGate/RunLedger" call shown
    /// throughout the docs — <paramref name="policy"/> is inert (there is no gate whose verdict it could ever
    /// enforce), so <see langword="null"/> is accepted and treated as <see cref="EvalGatePolicy.WarnOnly"/>
    /// without complaint. Prefer <c>UseGatekeeper(...)</c> / <c>ObserveWithAgentEvalGates(...)</c> /
    /// <c>EnforceAgentEvalGates(...)</c> for new code.
    /// </param>
    /// <param name="trace">Optional Glass Box trace for <c>gate.run-*.*</c> evidence.</param>
    public static AIAgentBuilder UseAgentEvalGate(
        this AIAgentBuilder builder,
        IReadOnlyList<IChatGate>? pre = null,
        IReadOnlyList<IChatGate>? post = null,
        EvalGatePolicy? policy = null,
        AgentTrace? trace = null,
        IGateEvidenceSink? evidenceSink = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Snapshot NOW, at registration — pre/post may be a caller-owned mutable List<IChatGate>. Without a
        // defensive copy, the closure captured below would see any LATER mutation to that same list instance
        // (a gate added after this call returns), but effectivePolicy is decided ONCE, right here, from the
        // gate counts AT THIS MOMENT — a gate added afterward would run under a policy decision made before it
        // existed (e.g. "no gates yet ⇒ inert WarnOnly" locked in, even once a real gate is later added,
        // silently never enforcing it). A live list also risks a mid-request "Collection was modified" if the
        // caller mutates it while a run is in flight. ToArray() makes UseAgentEvalGate's contract exactly what
        // its signature implies: the pipeline is fixed by the gates passed to THIS call.
        IReadOnlyList<IChatGate> preGates = (pre ?? Array.Empty<IChatGate>()).ToArray();
        IReadOnlyList<IChatGate> postGates = (post ?? Array.Empty<IChatGate>()).ToArray();
        foreach (var g in preGates)
        {
            ArgumentNullException.ThrowIfNull(g, nameof(pre));
        }

        foreach (var g in postGates)
        {
            ArgumentNullException.ThrowIfNull(g, nameof(post));
        }

        EvalGatePolicy effectivePolicy;
        if (policy is { } explicitPolicy)
        {
            effectivePolicy = explicitPolicy;
        }
        else if (preGates.Count == 0 && postGates.Count == 0)
        {
            effectivePolicy = EvalGatePolicy.WarnOnly;   // inert: no gate is registered, so no verdict is ever enforced
        }
        else
        {
            throw new ArgumentException(
                "policy must be specified explicitly when any pre/post gate is registered — there is no implicit " +
                "default (a Block verdict must be an explicit choice to warn-only or enforce). Pass " +
                "EvalGatePolicy.WarnOnly to keep the prior behavior, or ThrowOnFail/Redact to enforce.",
                nameof(policy));
        }

        var seq = new int[1];   // shared block-seq counter across both branches

        // F-C / P3-6: fingerprint the pre/post gate configuration once; every run-gate evidence record is stamped
        // with it. AgentName / RunId are filled from AgentRunScope at record time (the run gate establishes it).
        var evidenceContext = new GateEvidenceContext(null, null, null,
            GateConfigFingerprint.Compute(chatGates: preGates.Concat(postGates).ToArray()), evidenceSink);

        return builder.Use(
            runFunc: async (messages, session, options, innerAgent, ct) =>
            {
                using var scope = AgentRunScope.Begin(session, innerAgent.Name, trace);

                var preOutcome = await RunGatesAsync(preGates, ConcatText(messages), "run-pre", isPreStage: true, effectivePolicy, trace, seq, evidenceContext, ct).ConfigureAwait(false);
                if (preOutcome.ReturnBody is not null)
                {
                    return Refusal(innerAgent, preOutcome.ReturnBody);
                }

                // P2-1: a run-pre Redact rewrites the (flattened) input; the model STILL RUNS, on the redacted text.
                var effectiveMessages = RewrittenOrOriginal(preOutcome, messages);

                var response = await innerAgent.RunAsync(effectiveMessages, session, options, ct).ConfigureAwait(false);

                var postOutcome = await RunGatesAsync(postGates, response.Text, "run-post", isPreStage: false, effectivePolicy, trace, seq, evidenceContext, ct).ConfigureAwait(false);
                if (postOutcome.ReturnBody is not null)
                {
                    // P2-3: an enforced run-post Block/Redact swapped what the CALLER sees, but the model's original
                    // (unsafe) response is already persisted in the session and would re-enter context next turn.
                    // Reconcile the persisted turn to the caller-safe text (for sessions that expose the seam) and
                    // emit divergence evidence either way.
                    ReconcileSession(session, safeText: postOutcome.ReturnBody, withheldText: response.Text, trace, seq, evidenceContext);
                    return Refusal(innerAgent, postOutcome.ReturnBody);
                }

                EmitRunReceipt(trace, seq, evidenceContext);   // P3-11: summarize the completed run
                return response;
            },
            runStreamingFunc: (messages, session, options, innerAgent, ct) =>
                StreamCore(messages, session, options, innerAgent, preGates, postGates, effectivePolicy, trace, seq, evidenceContext, ct));
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> StreamCore(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        IReadOnlyList<IChatGate> preGates,
        IReadOnlyList<IChatGate> postGates,
        EvalGatePolicy policy,
        AgentTrace? trace,
        int[] seq,
        GateEvidenceContext evidence,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // A run-post gate cannot BLOCK a streamed response without buffering the whole stream (which destroys
        // TTFT), so under a blocking policy we fail closed at stream start rather than let output through. Under
        // WarnOnly (observe-only) the response IS accumulated and the post-gates run AFTER the stream to record
        // evidence — consistent with non-streaming WarnOnly (record, never block), so monitoring is never silent.
        if (postGates.Count > 0 && policy is EvalGatePolicy.Redact or EvalGatePolicy.ThrowOnFail)
        {
            throw new NotSupportedException(
                "Run-post gates cannot block a streamed response under Redact/ThrowOnFail (that would require " +
                "buffering the whole stream). Use a non-streaming run, or WarnOnly post-gates (recorded after the stream).");
        }

        // ONE scope for the whole streaming run — a STABLE identity across segments, so per-run state keyed on
        // the scope (e.g. SequenceGate) is not fragmented. The pre-gates run under it (before any yield, so a
        // SessionContextGate pre-gate sees the session, exactly as on the non-streaming path).
        using var runScope = AgentRunScope.Begin(session, innerAgent.Name, trace);

        var preOutcome = await RunGatesAsync(preGates, ConcatText(messages), "run-pre", isPreStage: true, policy, trace, seq, evidence, ct).ConfigureAwait(false);
        if (preOutcome.ReturnBody is not null)
        {
            var synth = new AgentResponse(new ChatMessage(ChatRole.Assistant, preOutcome.ReturnBody)) { AgentId = innerAgent.Id };
            foreach (var update in synth.ToAgentResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

        // P2-1: a run-pre Redact rewrites the streamed input; the model STILL STREAMS, over the redacted text.
        var effectiveMessages = RewrittenOrOriginal(preOutcome, messages);

        // Accumulate the response ONLY when WarnOnly post-gates need to inspect it (no allocation otherwise, so
        // TTFT and the gate-less path are untouched).
        var accumulated = postGates.Count > 0 ? new StringBuilder() : null;

        // PERF-01: an AsyncLocal set once atop an async iterator does not survive the first yield (later
        // MoveNext runs on the consumer's context) — so RE-ENTER the SAME scope instance inside each segment.
        await using var e = innerAgent.RunStreamingAsync(effectiveMessages, session, options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool has;
            using (runScope.Enter())
            {
                has = await e.MoveNextAsync().ConfigureAwait(false);
            }

            if (!has)
            {
                break;
            }

            accumulated?.Append(e.Current.Text);
            yield return e.Current;
        }

        // WarnOnly post-gates: record output-monitoring evidence over the fully-streamed response. Observe-only,
        // so the (already-delivered) response is never altered — the return is intentionally ignored.
        if (accumulated is not null)
        {
            using (runScope.Enter())
            {
                await RunGatesAsync(postGates, accumulated.ToString(), "run-post", isPreStage: false, policy, trace, seq, evidence, ct).ConfigureAwait(false);
            }
        }
    }

    // The outcome of running one stage's chat gates (P2-1). A stage either PROCEEDS (both fields null), RETURNS a
    // body instead of proceeding (ReturnBody — a refusal, or on run-post a redacted replacement response), or —
    // run-pre Redact only — REWRITES the model input and continues (RewrittenInput).
    private readonly record struct GateStageOutcome(string? ReturnBody, string? RewrittenInput)
    {
        public static readonly GateStageOutcome Proceed = default;
        public static GateStageOutcome Return(string body) => new(body, null);
        public static GateStageOutcome Rewrite(string input) => new(null, input);
    }

    // P2-1: a run-pre Redact only ever saw ConcatText(messages) — the flattened conversation — so the honest
    // representation of the redacted input is a single user message carrying the sanitized text. Message ROLES /
    // multi-turn structure and non-text parts are not reconstructable from the flattened view, so they are
    // intentionally collapsed; this is a narrow redaction (strip a secret/PII from the incoming prompt), not a
    // faithful history rewrite. NOTE: an agent's own system/developer INSTRUCTIONS live on the agent
    // (ChatClientAgentOptions.Instructions), NOT in the `messages` passed to a run, so they are UNAFFECTED by this
    // collapse in the common case. A caller who instead passes a system message INSIDE the run input, or relies on
    // multi-turn structure/tool-call parts, should treat a run-pre Redact as lossy for those and prefer a run-POST
    // gate (or a per-message redactor upstream) when that structure must survive.
    private static IEnumerable<ChatMessage> RewrittenOrOriginal(GateStageOutcome outcome, IEnumerable<ChatMessage> original)
        => outcome.RewrittenInput is null ? original : [new ChatMessage(ChatRole.User, outcome.RewrittenInput)];

    private static async ValueTask<GateStageOutcome> RunGatesAsync(
        IReadOnlyList<IChatGate> gates, string text, string stage, bool isPreStage, EvalGatePolicy policy, AgentTrace? trace, int[] seq, GateEvidenceContext evidence, CancellationToken ct)
    {
        foreach (var gate in gates)
        {
            GateVerdict verdict;
            try
            {
                verdict = await gate.InspectAsync(text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FAIL CLOSED (cannot-inspect ⇒ deny): a gate that throws cannot prove the run safe. Record and
                // block regardless of policy (mirrors the tool gate).
                var failVerdict = GateVerdict.Block(gate.PolicyName, $"gate evaluation threw ({ex.GetType().Name}) — failing closed");
                var throwReferenceId = GateReferenceId.New();
                RecordGate(trace, Interlocked.Increment(ref seq[0]), stage, failVerdict, "Block", throwReferenceId, evidence, failedClosedOnThrow: true);
                return GateStageOutcome.Return(GateReferenceId.RefusalBody(throwReferenceId));
            }

            if (verdict.Action != GateAction.Block)
            {
                continue;
            }

            // Honest evidence: only an ENFORCED block records action="Block" (so GateBlockCount never counts a
            // run that proceeded). Under WarnOnly the block is recorded as action="Warn".
            var enforced = policy != EvalGatePolicy.WarnOnly;
            var referenceId = GateReferenceId.New();
            RecordGate(trace, Interlocked.Increment(ref seq[0]), stage, verdict, enforced ? "Block" : "Warn", referenceId, evidence);

            if (policy == EvalGatePolicy.ThrowOnFail)
            {
                throw new EvalGateRefusalException(verdict, stage);   // propagates at the run boundary — full detail is fine, this is app code, not model-visible content
            }

            if (policy == EvalGatePolicy.Redact)
            {
                // No safe version supplied ⇒ a hard block: the non-revealing refusal shape, both stages.
                if (verdict.RedactedText is null)
                {
                    return GateStageOutcome.Return(GateReferenceId.RefusalBody(referenceId));
                }

                // #12 / P2-1: RedactedText IS the SAFE version of the content, not a refusal. On run-POST it
                // REPLACES the response the caller sees. On run-PRE it REWRITES the input and the model STILL
                // RUNS on the redacted text — a prior version returned it as the final answer, short-circuiting
                // the model entirely (objectively a bug: run-pre Redact is sanitize-then-continue, not
                // sanitize-and-answer).
                return isPreStage
                    ? GateStageOutcome.Rewrite(verdict.RedactedText)
                    : GateStageOutcome.Return(verdict.RedactedText);
            }

            // WarnOnly: recorded as a warning; let the run proceed.
        }

        return GateStageOutcome.Proceed;
    }

    private static AgentResponse Refusal(AIAgent innerAgent, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { AgentId = innerAgent.Id };

    private static string ConcatText(IEnumerable<ChatMessage> messages)
        => string.Join("\n", messages.Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)));

    // P2-3: after an enforced run-post Block/Redact, reconcile the session's persisted turn with what the caller
    // actually saw and record the divergence. The scrub itself is only possible for a session that opts into the
    // IReconcilableSession seam (directly or via GetService) — MAF's built-in session exposes no mutable history,
    // so it is left untouched and the evidence honestly records scrubbed=false (never a false success).
    private static void ReconcileSession(AgentSession? session, string safeText, string withheldText, AgentTrace? trace, int[] seq, GateEvidenceContext evidence)
    {
        var reconciler = session as IReconcilableSession;
        if (reconciler is null && session is not null)
        {
            // A custom session's GetService could throw; a decided, enforced refusal must not be turned into a
            // propagating exception at the run boundary just because the reconciliation lookup failed.
            try
            {
                reconciler = session.GetService(typeof(IReconcilableSession), null) as IReconcilableSession;
            }
            catch
            {
                reconciler = null;
            }
        }

        var scrubbed = false;
        string? reason = null;
        if (reconciler is null)
        {
            reason = session is null
                ? "no session on the run — nothing to reconcile"
                : "session does not implement IReconcilableSession — its persisted turn still diverges from what the caller "
                  + "saw; rotate the session/thread if that turn must not re-enter the model's context next turn";
        }
        else
        {
            try
            {
                scrubbed = reconciler.TryReconcileLastAssistantMessage(safeText);
                if (!scrubbed)
                {
                    reason = "IReconcilableSession found no assistant turn to reconcile";
                }
            }
            catch (Exception ex)
            {
                reason = $"IReconcilableSession.TryReconcileLastAssistantMessage threw ({ex.GetType().Name}) — persisted turn NOT scrubbed";
            }
        }

        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        // Per-turn hash-divergence record: the caller-safe text vs. the withheld model text, so an auditor can
        // confirm a scrub happened (or see, honestly, that it could not) without either text stored verbatim.
        var record = evidence.Build(
            stage: "session", policy: "reconciliation", action: "Reconcile", referenceId: GateReferenceId.New(),
            reason: reason, severity: GateSeverity.Suspicious,
            extra: new Dictionary<string, object?>
            {
                ["scrubbed"] = scrubbed,
                ["safeHash"] = ShortHash(safeText),
                ["withheldHash"] = ShortHash(withheldText),
                ["diverged"] = !string.Equals(safeText, withheldText, StringComparison.Ordinal),
            });
        EmitEvidence(trace, evidence, Interlocked.Increment(ref seq[0]), record);
    }

    private static string ShortHash(string text)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();

    // #12: the full policy name (the metadata key itself) and reason live ONLY here — audit-visible trace
    // evidence, never returned as run-refusal content (see GateReferenceId.RefusalBody). referenceId is the
    // ONLY thing the two are allowed to share, so an auditor can correlate what was returned with what happened.
    // F-C: write one record to the trace (audit projection) AND fan it out to any registered extra sink
    // (ledger / alerting). The trace write is null-safe so evidence still reaches the sink with tracing off.
    private static void EmitEvidence(AgentTrace? trace, GateEvidenceContext evidence, int seq, GateEvidence record)
    {
        evidence.Emit(record, seq);
        trace?.SetMetadata(record.TraceKey(seq), record.ToMetadata());
    }

    // P3-11: at the end of a completed (non-refused) run, summarize the run's RunLedger into a receipt and emit it
    // as a gate.receipt.* record on the same F-C stream. Skipped entirely when there is nowhere to record it.
    private static void EmitRunReceipt(AgentTrace? trace, int[] seq, GateEvidenceContext evidence)
    {
        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        var scope = AgentRunScope.Current;
        var receipt = RunLedger.ForCurrentRun().Summarize(scope?.RunId, scope?.AgentName, evidence.ConfigFingerprint, DateTimeOffset.UtcNow);
        var record = evidence.Build(
            stage: "receipt", policy: "run", action: "Receipt", referenceId: GateReferenceId.New(), reason: null,
            severity: GateSeverity.Routine,
            extra: new Dictionary<string, object?>
            {
                ["totalToolCalls"] = receipt.TotalToolCalls,
                ["toolCallCounts"] = receipt.ToolCallCounts,
            });
        EmitEvidence(trace, evidence, Interlocked.Increment(ref seq[0]), record);
    }

    private static void RecordGate(AgentTrace? trace, int seq, string stage, GateVerdict verdict, string action, string referenceId, GateEvidenceContext evidence, bool failedClosedOnThrow = false)
    {
        if (trace is null && !evidence.HasSink)
        {
            return;
        }

        // #6: the two SensitiveJudgeAxes (exfiltration-intent, system-prompt-extraction) can have Reason/
        // Matches carry the offending phrase verbatim — an LLM judge's own rationale can quote back the exact
        // secret/leaked-prompt text it detected ("the offending phrase may BE the secret," the same rule
        // GateVerdictDto.RedactAxes already applies to the CLI verdict JSON). This is otherwise the one
        // trace-evidence write path TraceCaptureMode (#13) does NOT cover — that only gates Mutate tool-call
        // arguments. Redact both fields unconditionally for these two axes; audit-visible for every other gate.
        var sensitive = SensitiveJudgeAxes.IsSensitive(verdict.PolicyName);

        // F-C / P3-3: persist the verdict's GateProvenance for the first time (a judge's threshold/actual/evidence),
        // sensitive-axis-redacted like Reason/Matches so a sensitive judge's rationale is never leaked into the trace.
        var provenance = sensitive ? null : verdict.Provenance;

        var record = evidence.Build(
            stage: stage, policy: verdict.PolicyName, action: action, referenceId: referenceId,
            reason: sensitive ? "[redacted — sensitive judge axis; see SensitiveJudgeAxes.RedactAxes]" : verdict.Reason,
            severity: GateSeverityClassifier.Classify(verdict.PolicyName, failedClosedOnThrow),
            correlationId: ToolCorrelationScope.Current, provenance: provenance,
            extra: new Dictionary<string, object?> { ["matches"] = sensitive ? null : verdict.Matches });
        EmitEvidence(trace, evidence, seq, record);
    }
}
