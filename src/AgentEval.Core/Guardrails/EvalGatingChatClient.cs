// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
public sealed class EvalGatingChatClient : DelegatingChatClient
{
    private readonly IReadOnlyList<IChatGate> _pre;
    private readonly IReadOnlyList<IChatGate> _post;
    private readonly EvalGatePolicy _policy;
    private readonly AgentTrace? _trace;
    private int _gateSeq;

    /// <summary>Wraps <paramref name="inner"/> with pre/post gates enforced per <paramref name="policy"/>.</summary>
    public EvalGatingChatClient(
        IChatClient inner, IReadOnlyList<IChatGate>? pre, IReadOnlyList<IChatGate>? post,
        EvalGatePolicy policy, AgentTrace? trace = null)
        : base(inner)
    {
        _pre = pre ?? Array.Empty<IChatGate>();
        _post = post ?? Array.Empty<IChatGate>();
        _policy = policy;
        _trace = trace;
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
        if (_pre.Count == 0)
        {
            return;
        }

        // Inspect AND redact the SAME message so a Redact verdict is never silently dropped: target the last
        // User message; if there is none, fall back to the last message of any role.
        var userIdx = LastIndexOfRole(msgs, ChatRole.User);
        var targetIdx = userIdx >= 0 ? userIdx : msgs.Count - 1;
        var text = targetIdx >= 0 ? msgs[targetIdx].Text ?? string.Empty : string.Empty;

        foreach (var gate in _pre)
        {
            var verdict = await gate.InspectAsync(text, cancellationToken).ConfigureAwait(false);
            Record(verdict, "pre");
            if (verdict.Action == GateAction.Allow)
            {
                continue;
            }

            if (_policy == EvalGatePolicy.ThrowOnFail)
            {
                throw new EvalGateRefusalException(verdict, "pre");
            }

            if (_policy == EvalGatePolicy.Redact && verdict.RedactedText is not null && targetIdx >= 0)
            {
                msgs[targetIdx] = new ChatMessage(msgs[targetIdx].Role, verdict.RedactedText);   // preserve role
                text = verdict.RedactedText;                                                     // chain over redacted text
            }

            // WarnOnly (or a non-maskable Block under Redact): recorded, proceed.
        }
    }

    private async Task<ChatResponse> ApplyPostAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        if (_post.Count == 0)
        {
            return response;
        }

        var text = response.Text ?? string.Empty;
        foreach (var gate in _post)
        {
            var verdict = await gate.InspectAsync(text, cancellationToken).ConfigureAwait(false);
            Record(verdict, "post");
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
                // Replace the response content IN PLACE so response-level correlation/threading fields
                // (ResponseId, ConversationId, CreatedAt, AdditionalProperties, RawRepresentation, …) survive —
                // rebuilding a fresh ChatResponse silently dropped them. When the gate cannot produce redacted
                // text (a non-maskable Block, e.g. a toxicity/safety gate), substitute a safe placeholder rather
                // than delivering the offending content unchanged — Redact must never silently pass a Block.
                var replacement = verdict.RedactedText ?? BlockedPlaceholder(verdict);
                response.Messages = new List<ChatMessage> { new(ChatRole.Assistant, replacement) };
                text = replacement;
            }
        }

        return response;
    }

    /// <summary>Safe stand-in delivered under <see cref="EvalGatePolicy.Redact"/> when a Block cannot be masked.</summary>
    private static string BlockedPlaceholder(GateVerdict verdict)
        => string.IsNullOrEmpty(verdict.Reason)
            ? $"[content withheld by EvalGate policy '{verdict.PolicyName}']"
            : $"[content withheld by EvalGate policy '{verdict.PolicyName}': {verdict.Reason}]";

    /// <summary>
    /// Records a gate verdict into the trace's top-level <see cref="AgentTrace.Metadata"/> under a unique
    /// key "gate.{stage}.{seq}.{PolicyName}" (stage = pre|post) — never as a TraceEntry, so gate evidence
    /// stays out of the Index pairing / replay path. The recorded value carries action/reason/matches/correlationId.
    /// </summary>
    private void Record(GateVerdict verdict, string stage)
    {
        if (_trace is null)
        {
            return;
        }

        var seq = Interlocked.Increment(ref _gateSeq);
        _trace.SetMetadata($"gate.{stage}.{seq}.{verdict.PolicyName}", new Dictionary<string, object?>
        {
            ["action"] = verdict.Action.ToString(),
            ["reason"] = verdict.Reason,
            ["matches"] = verdict.Matches,
            ["correlationId"] = ToolCorrelationScope.Current,
        });
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
