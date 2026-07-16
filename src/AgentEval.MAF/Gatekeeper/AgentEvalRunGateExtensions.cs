// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
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
        AgentTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var preGates = pre ?? Array.Empty<IChatGate>();
        var postGates = post ?? Array.Empty<IChatGate>();
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

        return builder.Use(
            runFunc: async (messages, session, options, innerAgent, ct) =>
            {
                using var scope = AgentRunScope.Begin(session, innerAgent.Name, trace);

                var preRefusal = await RunGatesAsync(preGates, ConcatText(messages), "run-pre", effectivePolicy, trace, seq, ct).ConfigureAwait(false);
                if (preRefusal is not null)
                {
                    return Refusal(innerAgent, preRefusal);
                }

                var response = await innerAgent.RunAsync(messages, session, options, ct).ConfigureAwait(false);

                var postRefusal = await RunGatesAsync(postGates, response.Text, "run-post", effectivePolicy, trace, seq, ct).ConfigureAwait(false);
                return postRefusal is not null ? Refusal(innerAgent, postRefusal) : response;
            },
            runStreamingFunc: (messages, session, options, innerAgent, ct) =>
                StreamCore(messages, session, options, innerAgent, preGates, postGates, effectivePolicy, trace, seq, ct));
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

        var preRefusal = await RunGatesAsync(preGates, ConcatText(messages), "run-pre", policy, trace, seq, ct).ConfigureAwait(false);
        if (preRefusal is not null)
        {
            var synth = new AgentResponse(new ChatMessage(ChatRole.Assistant, preRefusal)) { AgentId = innerAgent.Id };
            foreach (var update in synth.ToAgentResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

        // Accumulate the response ONLY when WarnOnly post-gates need to inspect it (no allocation otherwise, so
        // TTFT and the gate-less path are untouched).
        var accumulated = postGates.Count > 0 ? new StringBuilder() : null;

        // PERF-01: an AsyncLocal set once atop an async iterator does not survive the first yield (later
        // MoveNext runs on the consumer's context) — so RE-ENTER the SAME scope instance inside each segment.
        await using var e = innerAgent.RunStreamingAsync(messages, session, options, ct).GetAsyncEnumerator(ct);
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
                await RunGatesAsync(postGates, accumulated.ToString(), "run-post", policy, trace, seq, ct).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<string?> RunGatesAsync(
        IReadOnlyList<IChatGate> gates, string text, string stage, EvalGatePolicy policy, AgentTrace? trace, int[] seq, CancellationToken ct)
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
                RecordGate(trace, Interlocked.Increment(ref seq[0]), stage, failVerdict, "Block", throwReferenceId);
                return GateReferenceId.RefusalBody(throwReferenceId);
            }

            if (verdict.Action != GateAction.Block)
            {
                continue;
            }

            // Honest evidence: only an ENFORCED block records action="Block" (so GateBlockCount never counts a
            // run that proceeded). Under WarnOnly the block is recorded as action="Warn".
            var enforced = policy != EvalGatePolicy.WarnOnly;
            var referenceId = GateReferenceId.New();
            RecordGate(trace, Interlocked.Increment(ref seq[0]), stage, verdict, enforced ? "Block" : "Warn", referenceId);

            if (policy == EvalGatePolicy.ThrowOnFail)
            {
                throw new EvalGateRefusalException(verdict, stage);   // propagates at the run boundary — full detail is fine, this is app code, not model-visible content
            }

            if (policy == EvalGatePolicy.Redact)
            {
                // #12: RedactedText (when a gate supplies one) IS meant to be seen — it's the SAFE version of the
                // content, not a refusal. Only the fallback (no redaction available) gets the non-revealing shape.
                return verdict.RedactedText ?? GateReferenceId.RefusalBody(referenceId);
            }

            // WarnOnly: recorded as a warning; let the run proceed.
        }

        return null;
    }

    private static AgentResponse Refusal(AIAgent innerAgent, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { AgentId = innerAgent.Id };

    private static string ConcatText(IEnumerable<ChatMessage> messages)
        => string.Join("\n", messages.Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)));

    // #12: the full policy name (the metadata key itself) and reason live ONLY here — audit-visible trace
    // evidence, never returned as run-refusal content (see GateReferenceId.RefusalBody). referenceId is the
    // ONLY thing the two are allowed to share, so an auditor can correlate what was returned with what happened.
    private static void RecordGate(AgentTrace? trace, int seq, string stage, GateVerdict verdict, string action, string referenceId)
    {
        if (trace is null)
        {
            return;
        }

        trace.SetMetadata($"gate.{stage}.{seq}.{verdict.PolicyName}", new Dictionary<string, object?>
        {
            ["action"] = action,   // "Block" (enforced) or "Warn" (WarnOnly — recorded but the run proceeded)
            ["reason"] = verdict.Reason,
            ["matches"] = verdict.Matches,
            ["correlationId"] = ToolCorrelationScope.Current,
            ["referenceId"] = referenceId,
        });
    }
}
