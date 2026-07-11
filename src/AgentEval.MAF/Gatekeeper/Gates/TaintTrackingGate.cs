// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
/// <para><b>Coarse — a tripwire, not a proof.</b> It matches tainted <i>tokens</i> by <b>case-sensitive substring</b>,
/// so a value that is transformed (re-encoded, summarized, split, or case-folded) before the sink can slip past, and
/// an incidental long token shared between a source result and a benign sink argument can false-alarm. Tune
/// <c>minTaintLength</c> to trade recall for precision, and run it <see cref="ToolGatePolicy.WarnOnly"/> first to
/// measure false alarms before enforcing. The block reason never echoes the tainted value (that would itself leak
/// the secret into the trace).</para>
/// <para><b>Per-call, from history.</b> Taint is recomputed from the run's tool results in <c>call.Messages</c>, so
/// it needs no cross-run state — but a source result must have returned (be present in the history) before the sink
/// call for the flow to be caught. A source result is attributed to its tool by <b>CallId</b>; a result whose CallId
/// is missing/unpaired is not tainted (a fail-open edge — keep source CallIds intact through any history reducer).</para>
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
    public TaintTrackingGate(IEnumerable<string> sourceTools, IEnumerable<string> sinkTools, int minTaintLength = 8)
    {
        ArgumentNullException.ThrowIfNull(sourceTools);
        ArgumentNullException.ThrowIfNull(sinkTools);
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

        // Only sink tools with arguments can leak — everything else is an immediate allow (skip the taint scan).
        if (!_sinks.Contains(call.FunctionName) || call.Arguments is null || call.Arguments.Count == 0)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        HashSet<string> tainted;
        try
        {
            tainted = CollectTaintedTokens(call.Messages);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological history can time out the bounded token scan. Fail closed WITHIN the policy (a WarnOnly
            // registration still only warns) rather than let the exception become an unconditional block upstream.
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                $"taint scan timed out for sink '{call.FunctionName}' — cannot verify, failing closed"));
        }

        if (tainted.Count >= MaxTaintedTokens)
        {
            // A source flooded the taint set past the cost bound — fail closed within the policy (WarnOnly warns).
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                $"too many tainted tokens to scan for sink '{call.FunctionName}' — cannot verify, failing closed"));
        }

        if (tainted.Count == 0)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        foreach (var kv in call.Arguments)
        {
            var argText = GateText.Stringify(kv.Value);
            if (argText.Length == 0)
            {
                continue;
            }

            foreach (var token in tainted)
            {
                if (argText.Contains(token, StringComparison.Ordinal))
                {
                    // Deliberately does NOT include the tainted value — echoing it would leak the secret into the trace.
                    return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                        $"argument '{kv.Key}' carries data tainted by a confidential source tool to external sink '{call.FunctionName}'"));
                }
            }
        }

        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
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

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent fr && fr.CallId is not null && sourceCallIds.Contains(fr.CallId))
                {
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
                }
            }
        }

        return tainted;
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
