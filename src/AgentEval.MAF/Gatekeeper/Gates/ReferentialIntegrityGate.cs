// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate: a side-effecting tool call may only reference identifiers that were legitimately
/// <b>surfaced earlier this run</b> — by the <b>user</b>, or by an explicitly <b>trusted</b> lookup tool. Stops an
/// indirect injection from <b>inventing</b> an account / order / ticket id for the agent to act on
/// (<c>"refund order #FAKE-9931"</c>): the invented id was never provided by the user or a trusted lookup, so the
/// guarded call is blocked.
/// <para><b>Trust model — this is the point.</b> Only <see cref="ChatRole.User"/> turns and the results of
/// <c>trustedSourceTools</c> confer legitimacy. Model-generated content (assistant text, a prior tool call's
/// arguments) never does — otherwise the model could launder an invented id by mentioning it. And an
/// <i>untrusted</i> tool result (e.g. web/RAG content an injection can poison) deliberately does NOT count — else
/// the attacker would introduce the "legitimate" id in the very document carrying the injection. Mark only tools
/// whose results you trust (a first-party <c>lookup_order</c>) as sources. <b>Precondition:</b> the trust model
/// roots in the <see cref="ChatRole.User"/> role — if your harness stuffs retrieved/untrusted content into a
/// <i>user</i> message (a common RAG pattern) rather than a tool result, that content becomes "user-provided" and
/// can launder an id. Route retrieval through a (trusted or untrusted) tool, not the user turn.</para>
/// <para><b>A tripwire, not a proof.</b> "Is this token an id" is a heuristic — the default (<c>isIdentifier</c>)
/// only flags tokens that contain a <b>digit</b>, so an <b>all-letter</b> id (a username / slug / <c>admin_backup</c>
/// your backend accepts) is <i>not</i> validated by default, and since the attacker chooses the injected id this is
/// a deterministic gap — supply a custom <c>isIdentifier</c> when your guarded ids can be alpha-only. Run
/// <see cref="ToolGatePolicy.WarnOnly"/> first to measure false alarms, then promote to <c>ReplaceResult</c> /
/// <c>Terminate</c>.</para>
/// <para><b>Stateless.</b> Observed ids are recomputed per call from <c>call.Messages</c> (the run's history) — no
/// cross-run or cross-scope state, so it cannot over-permit an id leaked from another run.</para>
/// </summary>
public sealed class ReferentialIntegrityGate : IToolGate
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // Maximal runs of identifier characters. The "is this an id" decision (length + contains-a-digit) is done in
    // code, so the regex stays a simple linear character-class scan — no backtracking, ReDoS-safe.
    private static readonly Regex Token = new("[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.Compiled, MatchTimeout);

    private readonly HashSet<string> _idArgs;
    private readonly HashSet<string>? _guarded;
    private readonly HashSet<string>? _trusted;
    private readonly Func<string, bool> _isId;

    /// <inheritdoc/>
    public string PolicyName => "ReferentialIntegrityGate";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.Bounded;

    /// <summary>Creates the gate.</summary>
    /// <param name="idArgNames">The argument names that carry identifiers to validate (e.g. <c>order_id</c>). Required.</param>
    /// <param name="guardedTools">The side-effecting tools to check. Null or empty ⇒ every tool is checked.</param>
    /// <param name="trustedSourceTools">
    /// Tools whose results legitimately surface ids (e.g. a first-party <c>lookup_order</c>). Null or empty ⇒ ids
    /// must come from the <b>user</b> only. Do NOT list web/RAG tools an injection can poison.
    /// </param>
    /// <param name="isIdentifier">
    /// Predicate deciding whether a token is an id worth validating. Default: length ≥ 4 and contains at least one
    /// digit (fires on <c>A-1042</c> / <c>FAKE-9931</c>, not on plain words). Supply your own if guarded ids can be
    /// all-letter — the default does NOT validate alpha-only ids.
    /// </param>
    public ReferentialIntegrityGate(
        IEnumerable<string> idArgNames,
        IEnumerable<string>? guardedTools = null,
        IEnumerable<string>? trustedSourceTools = null,
        Func<string, bool>? isIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(idArgNames);
        _idArgs = new HashSet<string>(idArgNames, StringComparer.OrdinalIgnoreCase);
        if (_idArgs.Count == 0)
        {
            throw new ArgumentException("At least one id argument name is required.", nameof(idArgNames));
        }

        _guarded = ToSetOrNull(guardedTools);        // null ⇒ apply to every tool
        _trusted = ToSetOrNull(trustedSourceTools);  // null ⇒ only user turns confer legitimacy
        _isId = isIdentifier ?? DefaultIsIdentifier;
    }

    private static HashSet<string>? ToSetOrNull(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var set = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        return set.Count > 0 ? set : null;
    }

    private static bool DefaultIsIdentifier(string token) => token.Length >= 4 && token.Any(char.IsDigit);

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // Only guarded (side-effecting) tools are checked; a null guarded set means "every tool".
        if (_guarded is not null && !_guarded.Contains(call.FunctionName))
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        if (call.Arguments is null)
        {
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
        }

        try
        {
            HashSet<string>? observed = null;   // lazily harvested only when there is an id token to check

            foreach (var argName in _idArgs)
            {
                if (!call.Arguments.TryGetValue(argName, out var raw) || raw is null)
                {
                    continue;
                }

                foreach (var token in Identifiers(GateText.Stringify(raw)))
                {
                    observed ??= HarvestObserved(call.Messages);
                    if (!observed.Contains(token))
                    {
                        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                            $"argument '{argName}' references identifier '{token}', which the user never provided and no trusted lookup surfaced this run (a possible injected / invented id)"));
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological history/arg can time out the bounded id scan. Fail closed WITHIN the policy (a WarnOnly
            // registration still only warns) rather than let the exception become an unconditional block upstream.
            return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(PolicyName,
                $"identifier scan timed out for '{call.FunctionName}' — cannot verify, failing closed"));
        }

        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
    }

    // Recompute the ids legitimately surfaced this run — user turns + trusted tool results — from the history.
    private HashSet<string> HarvestObserved(IReadOnlyList<ChatMessage>? messages)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        if (messages is null)
        {
            return observed;
        }

        var trustedCallIds = _trusted is null ? null : BuildTrustedCallIds(messages);

        foreach (var message in messages)
        {
            var userTurn = message.Role == ChatRole.User;
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    // The user's own words are trusted; model-generated text/tool-calls are NOT (no self-laundering).
                    case TextContent text when userTurn:
                        AddIds(text.Text, observed);
                        break;
                    case FunctionResultContent fr when IsTrustedResult(fr, trustedCallIds):
                        AddIds(GateText.Stringify(fr.Result), observed);
                        break;
                }
            }
        }

        return observed;
    }

    private static bool IsTrustedResult(FunctionResultContent result, HashSet<string>? trustedCallIds)
        => trustedCallIds is not null && result.CallId is not null && trustedCallIds.Contains(result.CallId);

    // A CallId is trusted only if EVERY producer that emitted it is a trusted source — a duplicate/reused CallId
    // with any untrusted producer is excluded, so an untrusted result can't be laundered as trusted.
    private HashSet<string> BuildTrustedCallIds(IReadOnlyList<ChatMessage> messages)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal);
        var untrusted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fc && !string.IsNullOrEmpty(fc.CallId))
                {
                    (_trusted!.Contains(fc.Name) ? trusted : untrusted).Add(fc.CallId);
                }
            }
        }

        trusted.ExceptWith(untrusted);
        return trusted;
    }

    private void AddIds(string text, HashSet<string> observed)
    {
        foreach (var id in Identifiers(text))
        {
            observed.Add(id);
        }
    }

    private IEnumerable<string> Identifiers(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (Match match in Token.Matches(text))
        {
            if (_isId(match.Value))
            {
                yield return match.Value;
            }
        }
    }
}
