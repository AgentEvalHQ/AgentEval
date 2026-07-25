// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Turns a <see cref="GateReplayComparison"/> into a readable human diff (Phase 3, P3-9) — per captured call, what
/// the baseline gate config did vs. what a candidate config WOULD do (allow / block / mutate), with the divergences
/// called out. Together with <see cref="ExtractToolCalls"/> (the fixture-capture → replayer bridge) this unlocks a
/// safe "what would this new gate have done to real traffic?" rollout check before a gate goes live.
/// </summary>
public static class GateReplayRenderer
{
    /// <summary>
    /// Extracts the tool calls a model actually made from captured chat messages (the fixture-capture → replayer
    /// bridge, P3-9) so they can be replayed against a candidate gate config. Reads every
    /// <see cref="FunctionCallContent"/> across the messages, in order.
    /// </summary>
    public static IReadOnlyList<GatedToolCall> ExtractToolCalls(IEnumerable<ChatMessage> capturedMessages, string? agentName = null)
    {
        ArgumentNullException.ThrowIfNull(capturedMessages);
        var calls = new List<GatedToolCall>();
        var messages = capturedMessages.ToList();
        var iteration = 0;
        foreach (var message in messages)
        {
            var functionCalls = message.Contents.OfType<FunctionCallContent>().ToList();
            for (var i = 0; i < functionCalls.Count; i++)
            {
                var fc = functionCalls[i];
                calls.Add(new GatedToolCall(
                    FunctionName: fc.Name,
                    Arguments: fc.Arguments as IReadOnlyDictionary<string, object?>,
                    AgentName: agentName,
                    Iteration: iteration,
                    FunctionCallIndex: i,
                    FunctionCount: functionCalls.Count,
                    IsStreaming: false,
                    Messages: messages));
            }

            if (functionCalls.Count > 0)
            {
                iteration++;
            }
        }

        return calls;
    }

    /// <summary>Renders the full comparison — one line per call, the divergences first, then a summary.</summary>
    public static string Render(GateReplayComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var sb = new StringBuilder();
        sb.Append("Gate replay — ").Append(comparison.Rows.Count).Append(" call(s), ")
          .Append(comparison.Diverged.Count).AppendLine(" diverged").AppendLine();

        foreach (var row in comparison.Rows)
        {
            sb.Append(row.Diverged ? "  ✗ " : "  · ")
              .Append(row.Call.FunctionName)
              .Append(": baseline=").Append(Describe(row.Baseline))
              .Append("  candidate=").Append(Describe(row.Candidate));
            if (row.Diverged)
            {
                sb.Append("   ← DIVERGED");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Describe(ToolGateVerdict verdict) => verdict.Action switch
    {
        ToolGateAction.Allow => "Allow",
        ToolGateAction.Block => $"Block({verdict.PolicyName})",
        ToolGateAction.Mutate => $"Mutate({verdict.PolicyName})",
        _ => verdict.Action.ToString(),
    };
}
