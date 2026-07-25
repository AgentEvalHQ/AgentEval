// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

// AgentEval.Tracing also declares a ChatRole; alias the MEAI one (used for ChatMessage roles here).
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Guardrails;

/// <summary>
/// Applies <see cref="IChatGate"/> checks pre- and post-model-call on live traffic, recording every
/// decision into the <see cref="AgentTrace"/>. Same policy intent as the post-hoc behavioural assertions,
/// applied inline. ⚠️ <b>Redact OR ThrowOnFail</b> combined with post-gates is NOT supported for streaming
/// (the builder cannot know in advance whether the caller streams, so this client throws at the START of
/// <see cref="GetStreamingResponseAsync"/> in those configurations — the full output cannot be inspected,
/// blocked, or redacted once bytes are in flight). Streaming with WarnOnly, or pre-gates only, is fine.
/// </summary>
// This class is the documented, intended consumption point for the Experimental FleetCorrelator (an optional
// constructor parameter) — suppressed file-wide rather than sprinkling a pragma pair around every one of the
// several _correlator?.* call sites below.
#pragma warning disable AGENTEVAL_GATEKEEPER_PREVIEW001
public sealed class EvalGatingChatClient : DelegatingChatClient
{
    private readonly IReadOnlyList<IChatGate> _pre;
    private readonly IReadOnlyList<IChatGate> _post;
    private readonly EvalGatePolicy _policy;
    private readonly AgentTrace? _trace;
    private readonly FleetCorrelator? _correlator;
    private int _gateSeq;

    /// <summary>Wraps <paramref name="inner"/> with pre/post gates enforced per <paramref name="policy"/>.</summary>
    /// <param name="inner">The chat client to wrap.</param>
    /// <param name="pre">Run-pre gates, inspecting the input text before the model sees it.</param>
    /// <param name="post">Run-post gates, inspecting the response text.</param>
    /// <param name="policy">How a gate's <see cref="GateAction.Block"/> finding is enforced.</param>
    /// <param name="trace">Optional Glass Box trace every gate verdict is recorded into.</param>
    /// <param name="correlator">
    /// Optional, session-scoped Fleet Correlation Layer (Experimental — see <see cref="FleetCorrelator"/>'s own
    /// remarks). When set, every <paramref name="pre"/>/<paramref name="post"/> gate's verdict is also observed
    /// by it, and its <see cref="FleetCorrelator.CheckCorrelation"/> result is enforced through the SAME
    /// <paramref name="policy"/> path as every other gate here — no new enforcement mechanism.
    /// </param>
    public EvalGatingChatClient(
        IChatClient inner, IReadOnlyList<IChatGate>? pre, IReadOnlyList<IChatGate>? post,
        EvalGatePolicy policy, AgentTrace? trace = null, FleetCorrelator? correlator = null)
        : base(inner)
    {
        _pre = pre ?? Array.Empty<IChatGate>();
        _post = post ?? Array.Empty<IChatGate>();
        _policy = policy;
        _trace = trace;
        _correlator = correlator;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgs = new List<ChatMessage>(messages);   // fresh mutable list — pre-gate Redact may rewrite a message
        await ApplyPreAsync(msgs, cancellationToken).ConfigureAwait(false);
        var response = await base.GetResponseAsync(msgs, options, cancellationToken).ConfigureAwait(false);
        return await ApplyPostAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Runtime guard (the builder can't know streaming will be used). BOTH Redact AND ThrowOnFail with
        // post-gates are unsupported for streaming — the full output cannot be inspected/blocked/redacted
        // before bytes are in flight. Reject loudly rather than silently downgrading to WarnOnly.
        if (_post.Count > 0 && _policy is EvalGatePolicy.Redact or EvalGatePolicy.ThrowOnFail)
        {
            throw new NotSupportedException(
                $"EvalGatePolicy.{_policy} with post-gates is not supported for streaming responses: the full " +
                "output cannot be inspected before transmission. Use non-streaming, or WarnOnly for streaming.");
        }

        return StreamCore(messages, options, cancellationToken);

        async IAsyncEnumerable<ChatResponseUpdate> StreamCore(
            IEnumerable<ChatMessage> m, ChatOptions? o,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c)
        {
            var msgs = new List<ChatMessage>(m);
            await ApplyPreAsync(msgs, c).ConfigureAwait(false);   // pre-gates apply to streaming too
            await foreach (var update in base.GetStreamingResponseAsync(msgs, o, c).WithCancellation(c).ConfigureAwait(false))
            {
                yield return update;
            }

            // Post-gates do not run on streams: WarnOnly post-gates are a no-op here (no full text to inspect);
            // Redact/ThrowOnFail post-gate configs were rejected above. Pre-gates fully apply.
        }
    }

    // ── gate plumbing (private) ──
    // Gate verdicts are recorded at the TRACE level (AgentTrace.Metadata), NOT as TraceEntry instances:
    // synthetic Request/Response entries would collide with the per-round-trip Index pairing space and
    // corrupt replay. Trace-level Metadata keeps gate evidence out of the pairing/replay path while still
    // being serialized, hash-anchored, and surfaced to compliance / Mission Control. `msgs` is always a
    // fresh List<>, so the in-place Redact index-set is safe.
    private async Task ApplyPreAsync(List<ChatMessage> msgs, CancellationToken cancellationToken)
    {
        // Advance unconditionally, before any gate runs this round-trip — the ONE call site both the
        // streaming and non-streaming paths share (see GetResponseAsync/GetStreamingResponseAsync, both of
        // which call this method first). A no-op when _correlator is null.
        _correlator?.AdvanceTurn();

        if (_pre.Count == 0 && _correlator is null)
        {
            return;
        }

        // Inspect AND redact the SAME message so a Redact verdict is never silently dropped: target the last
        // User message; if there is none, fall back to the last message of any role.
        var userIdx = LastIndexOfRole(msgs, ChatRole.User);
        var targetIdx = userIdx >= 0 ? userIdx : msgs.Count - 1;
        var text = targetIdx >= 0 ? msgs[targetIdx].Text ?? string.Empty : string.Empty;

        if (_policy == EvalGatePolicy.WarnOnly && _pre.Count > 1)
        {
            // P5-6: WarnOnly pre-gates are independent — no Redact text-chaining, no ThrowOnFail short-circuit —
            // so their (often LLM-backed) inspections overlap. Record/Observe afterward IN LIST ORDER, so on the
            // normal (no gate throws) path evidence and correlation are byte-identical to the sequential path; only
            // wall-clock shrinks (sum of judge latencies → slowest judge). If a gate throws, the turn faults (as an
            // uncaught throw on the sequential path also would) before this Record/Observe loop — see the helper.
            var verdicts = await InspectConcurrentlyAsync(_pre, text, cancellationToken).ConfigureAwait(false);
            foreach (var verdict in verdicts)
            {
                Record(verdict, "pre");
                _correlator?.Observe(verdict, "pre");
                // WarnOnly: recorded, proceed (no throw, no redact).
            }
        }
        else
        {
            foreach (var gate in _pre)
            {
                var verdict = await gate.InspectAsync(text, cancellationToken).ConfigureAwait(false);
                Record(verdict, "pre");
                _correlator?.Observe(verdict, "pre");
                if (verdict.Action == GateAction.Allow)
                {
                    continue;
                }

                if (_policy == EvalGatePolicy.ThrowOnFail)
                {
                    throw new EvalGateRefusalException(verdict, "pre");
                }

                if (_policy == EvalGatePolicy.Redact && targetIdx >= 0)
                {
                    text = RedactRequestMessageInPlace(msgs, targetIdx, verdict);   // chain over redacted text
                }

                // WarnOnly with a single gate: recorded, proceed.
            }
        }

        // Fleet Correlation: folded into the SAME EvalGatePolicy enforcement path every gate above already
        // uses — no new enforcement mechanism. Checked AFTER the per-gate loop so it sees every verdict this
        // turn contributed, not just a partial view.
        if (_correlator is not null)
        {
            var correlated = _correlator.CheckCorrelation();
            if (correlated is not null)
            {
                Record(correlated, "pre");
                if (_policy == EvalGatePolicy.ThrowOnFail)
                {
                    throw new EvalGateRefusalException(correlated, "pre");
                }

                if (_policy == EvalGatePolicy.Redact && targetIdx >= 0)
                {
                    // A correlation Block never carries RedactedText (it names a cross-gate PATTERN, not a
                    // single offending span to mask) — RedactRequestMessageInPlace falls back to a safe
                    // placeholder rather than leaving the prompt unmasked, same as every per-gate Block above.
                    RedactRequestMessageInPlace(msgs, targetIdx, correlated);
                }

                // WarnOnly: recorded, proceed.
            }
        }
    }

    private async Task<ChatResponse> ApplyPostAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        if (_post.Count == 0 && _correlator is null)
        {
            return response;
        }

        var text = response.Text ?? string.Empty;
        if (_policy == EvalGatePolicy.WarnOnly && _post.Count > 1)
        {
            // P5-6: WarnOnly post-gates are independent (see ApplyPreAsync's matching note) — overlap them,
            // then Record/Observe in list order for byte-identical evidence.
            var verdicts = await InspectConcurrentlyAsync(_post, text, cancellationToken).ConfigureAwait(false);
            foreach (var verdict in verdicts)
            {
                Record(verdict, "post");
                _correlator?.Observe(verdict, "post");
            }
        }
        else
        {
            foreach (var gate in _post)
            {
                var verdict = await gate.InspectAsync(text, cancellationToken).ConfigureAwait(false);
                Record(verdict, "post");
                _correlator?.Observe(verdict, "post");
                if (verdict.Action == GateAction.Allow)
                {
                    continue;
                }

                if (_policy == EvalGatePolicy.ThrowOnFail)
                {
                    throw new EvalGateRefusalException(verdict, "post");
                }

                if (_policy == EvalGatePolicy.Redact)
                {
                    text = RedactResponseInPlace(response, verdict);
                }
            }
        }

        // Fleet Correlation: same "folded into the SAME EvalGatePolicy path" discipline as ApplyPreAsync —
        // see its matching comment. Post-gates (and therefore this check) never run during streaming; see
        // GetStreamingResponseAsync's own NotSupportedException guard for why that's already the case for
        // Redact/ThrowOnFail post-gate configs.
        if (_correlator is not null)
        {
            var correlated = _correlator.CheckCorrelation();
            if (correlated is not null)
            {
                Record(correlated, "post");
                if (_policy == EvalGatePolicy.ThrowOnFail)
                {
                    throw new EvalGateRefusalException(correlated, "post");
                }

                if (_policy == EvalGatePolicy.Redact)
                {
                    RedactResponseInPlace(response, correlated);
                }
            }
        }

        return response;
    }

    /// <summary>
    /// Redacts <paramref name="response"/> IN PLACE so response-level correlation/threading fields (ResponseId,
    /// ConversationId, CreatedAt, AdditionalProperties, RawRepresentation, …) survive — rebuilding a fresh
    /// <see cref="ChatResponse"/> would silently drop them. When <paramref name="verdict"/> carries no
    /// maskable text (a non-maskable Block, e.g. a toxicity/safety gate, or a Fleet Correlation verdict, which
    /// never has one — it names a cross-gate PATTERN, not a single offending span), substitutes a safe
    /// placeholder rather than delivering the offending content unchanged — Redact must never silently pass a
    /// Block. Replaces only the TEXT and PRESERVES every non-text content (especially <see cref="FunctionCallContent"/>):
    /// if this gate sits inner of <c>UseFunctionInvocation</c>, dropping the tool calls on a tool-call turn
    /// would desync the tool loop (finish reason says tool_calls but no calls remain). Returns the replacement
    /// text, for the caller to chain subsequent gates over.
    /// </summary>
    private static string RedactResponseInPlace(ChatResponse response, GateVerdict verdict)
    {
        var replacement = verdict.RedactedText ?? BlockedPlaceholder(verdict);
        var preserved = response.Messages
            .SelectMany(m => m.Contents)
            .Where(c => c is not TextContent)
            .ToList();
        var contents = new List<AIContent>(preserved.Count + 1) { new TextContent(replacement) };
        contents.AddRange(preserved);
        response.Messages = new List<ChatMessage> { new(ChatRole.Assistant, contents) };
        return replacement;
    }

    /// <summary>
    /// Pre-gate counterpart to <see cref="RedactResponseInPlace"/>: replaces <paramref name="msgs"/>'s
    /// <paramref name="targetIdx"/> message with <paramref name="verdict"/>'s redacted text, falling back to
    /// <see cref="BlockedPlaceholder"/> when it carries no maskable span (a non-maskable Block, e.g. a
    /// toxicity/safety gate, or a Fleet Correlation verdict, which never has one). Regression fix: this
    /// fallback used to be missing here — a pre-gate Block with no RedactedText, or ANY Fleet Correlation
    /// Block (which structurally never has one), previously passed through completely unmasked under
    /// <see cref="EvalGatePolicy.Redact"/>, identical to WarnOnly. Preserves the message's own role. Returns
    /// the replacement text, for the caller to chain subsequent gates over.
    /// </summary>
    private static string RedactRequestMessageInPlace(List<ChatMessage> msgs, int targetIdx, GateVerdict verdict)
    {
        var replacement = verdict.RedactedText ?? BlockedPlaceholder(verdict);
        msgs[targetIdx] = new ChatMessage(msgs[targetIdx].Role, replacement);
        return replacement;
    }

    /// <summary>Safe stand-in delivered under <see cref="EvalGatePolicy.Redact"/> when a Block cannot be masked.</summary>
    private static string BlockedPlaceholder(GateVerdict verdict)
        => string.IsNullOrEmpty(verdict.Reason)
            ? $"[content withheld by EvalGate policy '{verdict.PolicyName}']"
            : $"[content withheld by EvalGate policy '{verdict.PolicyName}': {verdict.Reason}]";

    /// <summary>
    /// Records a gate verdict into the trace's top-level <see cref="AgentTrace.Metadata"/> under a unique
    /// key "gate.{stage}.{seq}.{PolicyName}" (stage = pre|post) — never as a TraceEntry, so gate evidence
    /// stays out of the Index pairing / replay path. The recorded value carries
    /// action/reason/matches/confidence/correlationId.
    /// </summary>
    private void Record(GateVerdict verdict, string stage)
    {
        if (_trace is null)
        {
            return;
        }

        // The two SensitiveJudgeAxes (exfiltration-intent, system-prompt-extraction) can have Reason/Matches
        // carry the offending phrase verbatim — an LLM judge's own rationale can quote back the exact
        // secret/leaked-prompt text it detected ("the offending phrase may BE the secret"). Regression fix:
        // AgentEvalRunGateExtensions.RecordGate (the MAF-layer sibling that writes the identical metadata
        // shape) already redacts both fields for these two axes; this Core-layer writer previously did not,
        // so the same judge used as an EvalGatingChatClient pre/post gate leaked the secret into the trace.
        var sensitive = SensitiveJudgeAxes.IsSensitive(verdict.PolicyName);

        var seq = Interlocked.Increment(ref _gateSeq);
        _trace.SetMetadata($"gate.{stage}.{seq}.{verdict.PolicyName}", new Dictionary<string, object?>
        {
            ["action"] = verdict.Action.ToString(),
            ["reason"] = sensitive ? "[redacted — sensitive judge axis; see SensitiveJudgeAxes.RedactAxes]" : verdict.Reason,
            ["matches"] = sensitive ? null : verdict.Matches,
            // The same soft signal FleetCorrelator.Observe acts on — without it here, a trace/compliance
            // reviewer looking back at WHY a "fleet-correlation" Block fired has no record of the individual
            // sub-threshold confidences that fed it.
            ["confidence"] = verdict.Confidence,
            ["correlationId"] = ToolCorrelationScope.Current,
        });
    }

    // P5-6: run an independent (WarnOnly) gate panel's inspections concurrently. Each InspectAsync is started
    // before any is awaited, so the (typically LLM-bound) calls overlap; Task.WhenAll preserves input order, so
    // the returned verdicts align 1:1 with the gate list and the caller can Record/Observe them deterministically.
    // Used ONLY under WarnOnly, where gates neither chain redacted text nor short-circuit — so overlapping them
    // changes nothing but wall-clock. (Gates are already required to be thread-safe: this client is itself invoked
    // concurrently across in-flight requests, sharing the same gate instances.)
    // Exception behavior: if a gate throws, Task.WhenAll faults and this rethrows BEFORE the caller records any
    // verdict — so a faulting WarnOnly gate yields no evidence for that turn (vs. the sequential path, whose uncaught
    // throw records verdicts up to the thrower). Either way the turn faults; only the partial-evidence trail differs.
    private static async Task<GateVerdict[]> InspectConcurrentlyAsync(
        IReadOnlyList<IChatGate> gates, string text, CancellationToken cancellationToken)
    {
        var tasks = new Task<GateVerdict>[gates.Count];
        for (var i = 0; i < gates.Count; i++)
        {
            tasks[i] = gates[i].InspectAsync(text, cancellationToken).AsTask();
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static int LastIndexOfRole(IList<ChatMessage> messages, ChatRole role)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role.Equals(role))
            {
                return i;
            }
        }

        return -1;
    }
}
#pragma warning restore AGENTEVAL_GATEKEEPER_PREVIEW001
