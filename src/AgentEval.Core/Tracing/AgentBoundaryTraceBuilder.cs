// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

// AgentEval.Tracing declares its own ChatRole; alias the MEAI one so the unqualified local enum
// cannot silently shadow it in the role comparison below.
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tracing;

/// <summary>
/// Reconstructs an <b>agent-boundary</b> <see cref="AgentTrace"/> from what an agent framework natively
/// surfaces after a run — the final message list, aggregate token usage, and finish reason (e.g. MAF's
/// <c>AgentRunResponse.Messages</c> / <c>.Usage</c> / <c>.FinishReason</c>).
/// </summary>
/// <remarks>
/// This is the "framework's self-report" side of a Trace Fidelity reconciliation: pair it with a
/// chat-boundary trace from <see cref="TraceRecordingChatClient"/> and feed both to
/// <c>TraceFidelityRunner.Reconcile(agentBoundary, chatBoundary)</c>. The reconciler reads tool <em>calls</em>
/// (<see cref="TraceEntry.ToolCalls"/>) and token usage from <b>any</b> Response entry on the agent side, so a
/// single Response entry carrying every surviving tool call plus the aggregate usage is sufficient and faithful.
/// <para>
/// Tool-call arguments are serialized via <see cref="TraceMapping"/> — the SAME path the chat recorder uses —
/// so a clean run never shows a fabricated <c>argument_drift</c> from a serialization mismatch.
/// </para>
/// <para>
/// The entries are <see cref="TraceEntryScope.AgentInvocation"/>-scoped (NOT <see cref="TraceEntryScope.ChatTurn"/>):
/// this is the framework's coarse account, deliberately distinct from the per-round-trip chat boundary it is
/// compared against.
/// </para>
/// </remarks>
public static class AgentBoundaryTraceBuilder
{
    /// <summary>
    /// Builds the agent-boundary trace from a framework run's final messages, aggregate usage, and finish reason.
    /// </summary>
    /// <param name="finalMessages">The messages the framework surfaces after the run (MAF <c>AgentRunResponse.Messages</c>).</param>
    /// <param name="usage">The framework's aggregate token usage (MAF <c>AgentRunResponse.Usage</c>), if any.</param>
    /// <param name="finishReason">The framework's single finish reason for the whole run, if any.</param>
    /// <param name="agentName">Optional agent name to stamp on the trace.</param>
    /// <param name="durationMs">Optional wall-clock duration of the whole run, recorded on the response entry.</param>
    public static AgentTrace FromAgentResponse(
        IEnumerable<ChatMessage> finalMessages,
        UsageDetails? usage = null,
        string? finishReason = null,
        string? agentName = null,
        long? durationMs = null)
    {
        ArgumentNullException.ThrowIfNull(finalMessages);

        var messages = finalMessages as IReadOnlyList<ChatMessage> ?? finalMessages.ToList();

        // Every tool call the framework's final account retains — duplicates preserved so the occurrence
        // count matches the chat boundary (a name called twice yields two entries, not a deduped one).
        var toolCalls = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(TraceMapping.ToToolCall)
            .ToList();

        var text = string.Join(
            "\n",
            messages.Where(m => m.Role.Equals(MeaiChatRole.Assistant))
                    .Select(m => m.Text)
                    .Where(t => !string.IsNullOrEmpty(t)));

        var trace = new AgentTrace { AgentName = agentName, Version = "1.1" };
        trace.Entries.Add(new TraceEntry
        {
            Type = TraceEntryType.Response,
            Scope = TraceEntryScope.AgentInvocation,
            Index = 0,
            Timestamp = DateTimeOffset.UtcNow,
            Text = string.IsNullOrEmpty(text) ? null : text,
            DurationMs = durationMs,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            TokenUsage = TraceMapping.ToTokenUsage(usage),
            FinishReason = finishReason,
        });
        return trace;
    }
}
