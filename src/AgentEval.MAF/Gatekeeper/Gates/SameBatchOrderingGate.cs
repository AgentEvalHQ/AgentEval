// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that blocks a forbidden trigger→guarded pair emitted in the <b>same</b> model
/// iteration — the concurrency seam <see cref="SequenceGate"/> documents it cannot cover. When the model requests
/// several tool calls at once (one assistant turn, multiple <see cref="FunctionCallContent"/>) they may be
/// invoked concurrently with no happens-before, so a <c>read_secrets</c> + <c>send_email</c> pair in a single
/// batch slips past a cross-invocation sequence check. This gate inspects the current iteration's sibling calls
/// (the most recent assistant tool-call message in <see cref="GatedToolCall.Messages"/>) and blocks a guarded call
/// whenever a trigger tool is present in the same batch — conservatively, regardless of which would run first
/// (concurrent same-batch calls cannot be proven safe).
/// <para>Pairs with <see cref="SequenceGate"/> (cross-iteration ordering); register both for full coverage.
/// Stateless (reads only the current batch), so it needs no run scope. Matching is case-insensitive.</para>
/// </summary>
public sealed class SameBatchOrderingGate : IToolGate
{
    private readonly HashSet<string> _triggers;
    private readonly HashSet<string> _guarded;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <summary>Creates the gate: a <paramref name="guardedTools"/> call is blocked when any
    /// <paramref name="triggerTools"/> call is requested in the same model turn.</summary>
    public SameBatchOrderingGate(IEnumerable<string> triggerTools, IEnumerable<string> guardedTools, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(triggerTools);
        ArgumentNullException.ThrowIfNull(guardedTools);
        _triggers = new HashSet<string>(triggerTools, StringComparer.OrdinalIgnoreCase);
        _guarded = new HashSet<string>(guardedTools, StringComparer.OrdinalIgnoreCase);
        PolicyName = policyName ?? "SameBatchOrderingGate";
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (_guarded.Contains(call.FunctionName))
        {
            foreach (var siblingName in CurrentBatchToolNames(call.Messages))
            {
                if (_triggers.Contains(siblingName))
                {
                    return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Block(
                        PolicyName,
                        $"tool '{call.FunctionName}' is blocked: a trigger tool ('{siblingName}') was requested in the same model turn — a same-batch ordering that cannot be proven safe"));
                }
            }
        }

        return new ValueTask<ToolGateVerdict>(ToolGateVerdict.Allow(PolicyName));
    }

    // The current iteration's tool calls live in the most recent assistant message that carries any
    // FunctionCallContent (that is the turn the model just emitted and the harness is now invoking). Earlier
    // assistant batches are prior iterations — cross-iteration ordering is SequenceGate's job, not this gate's.
    private static IEnumerable<string> CurrentBatchToolNames(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages is null)
        {
            yield break;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            var any = false;
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fc)
                {
                    any = true;
                    yield return fc.Name;
                }
            }

            if (any)
            {
                yield break;   // only the most recent assistant batch is "this turn"
            }
        }
    }
}
