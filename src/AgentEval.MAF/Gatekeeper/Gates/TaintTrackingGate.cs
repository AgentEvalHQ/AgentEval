// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate implementing coarse <b>information-flow control</b>: a value produced by a confidential
/// <b>source</b> tool this run (e.g. <c>read_secrets</c>, <c>get_ssn</c>) must not reach an external <b>sink</b>
/// tool (e.g. <c>http_post</c>, <c>send_email</c>). It taints the value-like tokens in each source tool's result
/// and blocks a sink call whose arguments carry any of them — closing the exfiltration path that is the payoff of
/// most indirect injection, without a keyword list.
/// <para><b>Coarse — a tripwire, not a proof.</b> It matches tainted <i>tokens</i> by <b>case-INsensitive
/// substring</b> — a re-cased copy of a tainted value no longer slips past (Fable 5 §6). Set <c>canonicalize</c> to
/// also scan decoded projections (percent / HTML-entity / unicode-escape / base64) of each sink argument, catching
/// a <i>re-encoded</i> copy too. A value transformed in other ways (summarized, split) before the sink can still
/// slip past, and an incidental long token shared between a source result and a benign sink argument can
/// false-alarm. Tune <c>minTaintLength</c> to trade recall for precision, and run it
/// <see cref="ToolGatePolicy.WarnOnly"/> first to measure false alarms before enforcing. The block reason never
/// echoes the tainted value (that would itself leak the secret into the trace).</para>
/// <para><b>Per-call, from history.</b> Taint is recomputed from the run's tool results in <c>call.Messages</c>, so
/// it needs no cross-run state — but a source result must have returned (be present in the history) before the sink
/// call for the flow to be caught. A source result is attributed to its tool by <b>CallId</b>; a result whose CallId
/// was stripped (some history reducers drop <c>tool_call_id</c> pairing) falls back to the nearest preceding call's
/// source-ness, so a CallId-nulling reducer no longer silently disables the gate. Keep source CallIds intact through
/// any history reducer for the most precise attribution.</para>
/// </summary>
public sealed class TaintTrackingGate : IToolGate
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // Value-like tokens (secrets, keys, emails, ids, SSNs): runs of value characters, incl. '_' (env-style
    // SECRET_TOKEN / base64url). Linear ⇒ ReDoS-safe.
    private static readonly Regex Token = new("[A-Za-z0-9][A-Za-z0-9._/+@_-]*", RegexOptions.Compiled, MatchTimeout);

    // Cost bound: cap the tainted-token set so a large/poisoned source result can't make the per-sink scan
    // (tokens × args) unexpectedly expensive. Hitting the cap fails closed (we can't fully verify the flow).
    private const int MaxTaintedTokens = 1024;

    private readonly HashSet<string> _sources;
    private readonly HashSet<string> _sinks;
    private readonly int _minTaintLength;
    private readonly bool _canonicalize;

    // P5-1: per-run incremental taint state, keyed by the run scope. Weak keys, so a run's ledger is collected with
    // its scope (no manual cleanup / leak). Owned by THIS gate instance, so two differently-configured taint gates in
    // one run never cross-contaminate. Populated only when a run scope is in effect; otherwise the stateless
    // CollectTaintedTokens fallback runs (see InspectAsync).
    private readonly ConditionalWeakTable<AgentRunScope, RunTaintState> _perRun = new();

    // The accumulating taint ledger for one run: the tainted-token set, the source CallIds seen, a monotonic cursor
    // into the run's history (how many messages have been folded in), the nearest-preceding-call name for the
    // CallId-less fallback, and a sticky cap flag. All access is under Lock.
    private sealed class RunTaintState
    {
        public readonly object Lock = new();
        public readonly HashSet<string> Tainted = new(StringComparer.Ordinal);
        public readonly HashSet<string> SourceCallIds = new(StringComparer.Ordinal);
        public int ProcessedCount;
        public string? LastCallName;
        public bool CapHit;
    }

    /// <inheritdoc/>
    public string PolicyName => "TaintTrackingGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.Bounded;

    /// <summary>Creates the gate.</summary>
    /// <param name="sourceTools">Tools whose results are confidential — their returned values become tainted. Required.</param>
    /// <param name="sinkTools">Tools that send data outside the agent — a tainted value reaching one is blocked. Required.</param>
    /// <param name="minTaintLength">
    /// Minimum length of a tainted token to track. Shorter tokens (trivial numbers, short words) are ignored to
    /// curb false alarms. Default 8. Lower it for shorter secrets at the cost of precision.
    /// </param>
    public TaintTrackingGate(IEnumerable<string> sourceTools, IEnumerable<string> sinkTools, int minTaintLength = 8, bool canonicalize = false)
    {
        ArgumentNullException.ThrowIfNull(sourceTools);
        ArgumentNullException.ThrowIfNull(sinkTools);
        _canonicalize = canonicalize;
        _sources = new HashSet<string>(sourceTools, StringComparer.OrdinalIgnoreCase);
        _sinks = new HashSet<string>(sinkTools, StringComparer.OrdinalIgnoreCase);
        if (_sources.Count == 0)
        {
            throw new ArgumentException("At least one source tool is required.", nameof(sourceTools));
        }

        if (_sinks.Count == 0)
        {
            throw new ArgumentException("At least one sink tool is required.", nameof(sinkTools));
        }

        if (minTaintLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minTaintLength), "must be at least 1.");
        }

        _minTaintLength = minTaintLength;
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // Only sink tools WITH arguments can leak; everything else is an immediate allow — but under a run scope we
        // still fold this call's history first (below), so taint from an earlier non-sink call is visible to a later sink.
        var isSink = _sinks.Contains(call.FunctionName) && call.Arguments is { Count: > 0 };
        var scope = AgentRunScope.Current;

        HashSet<string> tainted;
        if (scope is not null)
        {
            // P5-1 fast path: fold only this call's newly-appeared history into the run ledger ONCE (incremental),
            // then scan against the accumulated token set — O(total history) across the run, not O(n²).
            var state = _perRun.GetValue(scope, static _ => new RunTaintState());
            lock (state.Lock)
            {
                try
                {
                    IngestIncremental(state, call.Messages);
                }
                catch (RegexMatchTimeoutException)
                {
                    return TimedOut(call.FunctionName);   // fail closed WITHIN the policy (WarnOnly still only warns)
                }

                if (!isSink)
                {
                    return Allow();   // history folded; a non-sink (or arg-less) call has nothing to scan
                }

                if (state.CapHit)
                {
                    return CapExceeded(call.FunctionName);
                }

                if (state.Tainted.Count == 0)
                {
                    return Allow();
                }

                tainted = new HashSet<string>(state.Tainted, StringComparer.Ordinal);   // snapshot; scan outside the lock
            }
        }
        else
        {
            // No run scope ⇒ no stable per-run key (the shared fallback key would bleed taint across runs). Fall back
            // to the original stateless per-call computation: identical behavior to pre-P5-1, at the pre-P5-1 cost.
            if (!isSink)
            {
                return Allow();
            }

            try
            {
                tainted = CollectTaintedTokens(call.Messages);
            }
            catch (RegexMatchTimeoutException)
            {
                return TimedOut(call.FunctionName);
            }

            if (tainted.Count >= MaxTaintedTokens)
            {
                return CapExceeded(call.FunctionName);
            }

            if (tainted.Count == 0)
            {
                return Allow();
            }
        }

        return ScanArguments(call, tainted);
    }

    private ValueTask<ToolGateVerdict> Allow()
        => new(ToolGateVerdict.Allow(PolicyName));

    // A pathological history can time out the bounded token scan. Fail closed within the policy rather than let the
    // exception become an unconditional block upstream.
    private ValueTask<ToolGateVerdict> TimedOut(string sink)
        => new(ToolGateVerdict.Block(PolicyName, $"taint scan timed out for sink '{sink}' — cannot verify, failing closed"));

    // A source flooded the taint set past the cost bound — fail closed within the policy.
    private ValueTask<ToolGateVerdict> CapExceeded(string sink)
        => new(ToolGateVerdict.Block(PolicyName, $"too many tainted tokens to scan for sink '{sink}' — cannot verify, failing closed"));

    private ValueTask<ToolGateVerdict> ScanArguments(GatedToolCall call, HashSet<string> tainted)
    {
        foreach (var kv in call.Arguments!)   // non-null: only reached on the isSink path (Arguments has ≥1 entry)
        {
            var argText = GateText.Stringify(kv.Value);
            if (argText.Length == 0)
            {
                continue;
            }

            // Default: the raw arg surface. With canonicalize: also its decoded projections, so a re-encoded copy
            // of a tainted value (base64/percent/…) is caught too. Matching is case-INsensitive (§6): a re-cased
            // copy must not slip past.
            var surfaces = _canonicalize
                ? ArgumentCanonicalizer.Canonicalize(argText)
                : (IReadOnlyList<string>)new[] { argText };

            foreach (var surface in surfaces)
            {
                foreach (var token in tainted)
                {
                    if (surface.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        // Deliberately does NOT include the tainted value — echoing it would leak the secret into the trace.
                        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                            $"argument '{kv.Key}' carries data tainted by a confidential source tool to external sink '{call.FunctionName}'"));
                    }
                }
            }
        }

        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
    }

    // P5-1: fold the messages the run has produced since the last call into the ledger, in a single forward pass (a
    // source CALL always precedes its RESULT, so one pass suffices where the stateless path used two). Tokens and
    // source CallIds only accumulate. If the history shrank (a reducer rewrote the prefix), the cursor is stale, so
    // reprocess from the top — re-adds are idempotent and previously-tainted tokens are retained.
    private void IngestIncremental(RunTaintState state, IReadOnlyList<ChatMessage>? messages)
    {
        if (messages is null || state.CapHit)
        {
            return;
        }

        var start = state.ProcessedCount;
        if (messages.Count < start)
        {
            start = 0;
            state.LastCallName = null;   // prefix changed — rebuild the nearest-preceding-call name from scratch
        }

        for (var i = start; i < messages.Count; i++)
        {
            foreach (var content in messages[i].Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fcCall:
                        state.LastCallName = fcCall.Name;
                        if (!string.IsNullOrEmpty(fcCall.CallId) && _sources.Contains(fcCall.Name))
                        {
                            state.SourceCallIds.Add(fcCall.CallId);
                        }

                        break;

                    case FunctionResultContent fr when IsSourceResult(fr, state.SourceCallIds, state.LastCallName):
                        foreach (var text in TaintTexts(fr.Result))
                        {
                            foreach (Match match in Token.Matches(text))
                            {
                                if (match.Value.Length >= _minTaintLength)
                                {
                                    state.Tainted.Add(match.Value);
                                    if (state.Tainted.Count >= MaxTaintedTokens)
                                    {
                                        state.CapHit = true;      // saturated — the caller fails closed
                                        state.ProcessedCount = messages.Count;
                                        return;
                                    }
                                }
                            }
                        }

                        break;
                }
            }
        }

        state.ProcessedCount = messages.Count;
    }

    private HashSet<string> CollectTaintedTokens(IReadOnlyList<ChatMessage>? messages)
    {
        var tainted = new HashSet<string>(StringComparer.Ordinal);
        if (messages is null)
        {
            return tainted;
        }

        // CallIds produced by ANY source tool. Conservative under duplicate/reused CallIds: if a CallId has even one
        // source producer, a result on it is treated as tainted (a duplicate can't launder a secret out).
        var sourceCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fc && !string.IsNullOrEmpty(fc.CallId) && _sources.Contains(fc.Name))
                {
                    sourceCallIds.Add(fc.CallId);
                }
            }
        }

        // Second pass: taint the value tokens of every SOURCE result. Primary attribution is by CallId; a
        // CallId-less result (Fable 5 §16 — the shape a history reducer produces when it strips tool_call_id
        // pairing, which would otherwise silently disable the gate) falls back to the nearest preceding call's
        // source-ness. The fallback is additive: it never taints a result whose nearest preceding call was a
        // non-source, so it cannot over-block a legitimately-reduced benign result.
        string? lastCallName = null;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fcCall:
                        lastCallName = fcCall.Name;
                        break;

                    case FunctionResultContent fr when IsSourceResult(fr, sourceCallIds, lastCallName):
                        foreach (var text in TaintTexts(fr.Result))
                        {
                            foreach (Match match in Token.Matches(text))
                            {
                                if (match.Value.Length >= _minTaintLength)
                                {
                                    tainted.Add(match.Value);
                                    if (tainted.Count >= MaxTaintedTokens)
                                    {
                                        return tainted;   // cap hit — the caller fails closed rather than scan unboundedly
                                    }
                                }
                            }
                        }

                        break;
                }
            }
        }

        return tainted;
    }

    private bool IsSourceResult(FunctionResultContent result, HashSet<string> sourceCallIds, string? nearestPrecedingCall)
    {
        // Primary: the result's CallId pairs to a source call seen this run.
        if (result.CallId is not null && sourceCallIds.Contains(result.CallId))
        {
            return true;
        }

        // Fallback (§16): a CallId-less result is attributed to the nearest preceding call and treated as a source
        // only if THAT call was a source. Scoped to the empty-CallId case, so a legitimately non-source result
        // (CallId present but not a source) is never mis-tainted — additive, cannot over-block.
        return string.IsNullOrEmpty(result.CallId)
            && nearestPrecedingCall is not null
            && _sources.Contains(nearestPrecedingCall);
    }

    // The text a source result contributes to the taint set. When the result is (or serializes to) JSON, only the
    // string/number VALUES are tainted — property NAMES are field labels (accessToken, api_key) that would
    // systematically false-alarm a sink argument mentioning the field without the secret. Non-JSON results are
    // tainted whole.
    private static IReadOnlyList<string> TaintTexts(object? result)
    {
        var rendered = GateText.Stringify(result);
        if (rendered.Length == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(rendered);
            var values = new List<string>();
            CollectJsonValues(doc.RootElement, values);
            return values;
        }
        catch (JsonException)
        {
            return new[] { rendered };   // not JSON — taint the whole rendered string
        }
    }

    private static void CollectJsonValues(JsonElement element, List<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                into.Add(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                into.Add(element.GetRawText());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectJsonValues(property.Value, into);   // skip property.Name — taint values, not keys
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectJsonValues(item, into);
                }

                break;
        }
    }
}
