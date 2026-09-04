// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Models;
using AgentEval.Tracing;

namespace AgentEval.Core;

/// <summary>
/// Utility class for extracting tool usage information from agent responses.
/// </summary>
public static class ToolUsageExtractor
{
    /// <summary>
    /// Extract tool usage report from raw chat messages.
    /// </summary>
    /// <param name="rawMessages">The raw messages from an agent response.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    /// <remarks>
    /// This default path reads <c>FunctionCallContent</c> / <c>FunctionResultContent</c> only. An
    /// approval-gated call (MAF's <c>ToolApprovalRequestContent</c>, which wraps the call) is <b>not</b>
    /// recorded; it is counted in <see cref="ToolUsageReport.DroppedApprovalRequestCount"/> so the
    /// blindness is visible. Use <see cref="Extract(IReadOnlyList{object}?, bool)"/> with
    /// <c>includeApprovalGatedCalls: true</c> to record them (ADR-030 Slice 0.5).
    /// </remarks>
    public static ToolUsageReport Extract(IReadOnlyList<object>? rawMessages)
        => Extract(rawMessages, includeApprovalGatedCalls: false);

    /// <summary>
    /// Extract tool usage report from raw chat messages, optionally including approval-gated calls.
    /// </summary>
    /// <param name="rawMessages">The raw messages from an agent response.</param>
    /// <param name="includeApprovalGatedCalls">
    /// When <see langword="true"/>, a <c>ToolApprovalRequestContent</c> wrapping a <c>FunctionCallContent</c>
    /// is recorded as that call with <see cref="ToolCallRecord.WasExecuted"/> <c>false</c> and
    /// <see cref="ToolCallRecord.ApprovalState"/> <see cref="ToolCallRecord.ApprovalRequested"/>; a later
    /// <c>ToolApprovalResponseContent</c> moves it to <see cref="ToolCallRecord.ApprovalApproved"/> /
    /// <see cref="ToolCallRecord.ApprovalRejected"/>, and a paired <c>FunctionResultContent</c> marks it executed
    /// unless it was rejected (the framework generates a "failed" result for a rejection; that is not an
    /// execution). When <see langword="false"/> (the default), such calls are dropped and counted in
    /// <see cref="ToolUsageReport.DroppedApprovalRequestCount"/>.
    /// </param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    /// <remarks>
    /// <para>
    /// <b>Opt-in on purpose</b> (ADR-030 Slice 0.5): this is the one change that can turn a red test green
    /// (<c>NeverCallTool</c> can now fail, <c>MustCallTool</c> can now pass), so nobody's numbers move
    /// silently. Note MAF's all-or-nothing rule — one approval-required tool in a response converts
    /// <i>every</i> <c>FunctionCallContent</c> in that response to an approval request, even for tools that
    /// need no approval — so with the default path one gated tool erases every call in its turn.
    /// </para>
    /// <para>
    /// <c>FunctionInvokingChatClient</c> re-creates the <c>FunctionCallContent</c> for an approval response
    /// before invoking it. A re-created call (same <c>CallId</c> and name as a still-unpaired approval
    /// record) is the same call, not a second one, and is folded into the existing record.
    /// </para>
    /// </remarks>
    public static ToolUsageReport Extract(IReadOnlyList<object>? rawMessages, bool includeApprovalGatedCalls)
    {
        if (rawMessages == null || rawMessages.Count == 0)
            return new ToolUsageReport();

        // L8: pair results to calls in MESSAGE ORDER, one result per call, instead of a single global last-wins map.
        // A CallId may legitimately repeat across turns (multi-turn tool loops); the old flat map paired an
        // emitted-only call in an earlier turn with a later turn's result that REUSED the same id, falsely reading
        // WasExecuted = true (a Behavioral over-claim). We now pair each result to the NEAREST PRECEDING still-unpaired
        // call with the same id (a LIFO stack per id), and remove it so one result cannot satisfy two calls. A
        // null/empty CallId can't be paired, so it stays unpaired (WasExecuted = false — the safe under-claim direction).
        var records = new List<ToolCallRecord>();
        var unpairedByCallId = new Dictionary<string, Stack<ToolCallRecord>>(StringComparer.Ordinal);
        var droppedApprovalCallIds = new HashSet<string>(StringComparer.Ordinal);
        int order = 0;
        int dropped = 0;

        ToolCallRecord AddCall(FunctionCallContent call, string? approvalState)
        {
            order++;
            var record = new ToolCallRecord
            {
                Name = call.Name,
                CallId = call.CallId,
                Arguments = call.Arguments,
                Order = order,
                ApprovalState = approvalState,
            };
            records.Add(record);
            if (!string.IsNullOrEmpty(call.CallId))
            {
                if (!unpairedByCallId.TryGetValue(call.CallId, out var stack))
                    unpairedByCallId[call.CallId] = stack = new Stack<ToolCallRecord>();
                stack.Push(record);
            }
            return record;
        }

        // The still-unpaired approval record this call id refers to, if any (top of the LIFO stack).
        ToolCallRecord? UnpairedApprovalRecord(string callId, string name) =>
            !string.IsNullOrEmpty(callId)
            && unpairedByCallId.TryGetValue(callId, out var stack) && stack.Count > 0
            && stack.Peek() is { ApprovalState: not null } top
            && string.Equals(top.Name, name, StringComparison.Ordinal)
                ? top
                : null;

        foreach (var message in rawMessages.OfType<ChatMessage>())
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        // FunctionInvokingChatClient re-creates the FunctionCallContent it previously replaced
                        // with an approval request. Same id, same name, still unpaired ⇒ the same call.
                        if (includeApprovalGatedCalls && UnpairedApprovalRecord(call.CallId, call.Name) is not null)
                            break;
                        AddCall(call, approvalState: null);
                        break;

                    case ToolApprovalRequestContent { ToolCall: FunctionCallContent gated }:
                        if (!includeApprovalGatedCalls)
                        {
                            dropped++;
                            if (!string.IsNullOrEmpty(gated.CallId)) droppedApprovalCallIds.Add(gated.CallId);
                            break;
                        }
                        AddCall(gated, ToolCallRecord.ApprovalRequested);
                        break;

                    case ToolApprovalResponseContent { ToolCall: FunctionCallContent decided } decision:
                        if (!includeApprovalGatedCalls)
                        {
                            // A response whose request was not in these messages is evidence of a call we
                            // are dropping; one whose request was already counted is not a second drop.
                            if (string.IsNullOrEmpty(decided.CallId) || !droppedApprovalCallIds.Contains(decided.CallId))
                                dropped++;
                            break;
                        }
                        var state = decision.Approved ? ToolCallRecord.ApprovalApproved : ToolCallRecord.ApprovalRejected;
                        var pending = LatestApprovalRecord(records, decided.CallId, decided.Name);
                        if (pending is not null)
                            pending.ApprovalState = state;
                        else
                            AddCall(decided, state);   // lone response: it wraps the call the agent emitted earlier
                        break;

                    case ToolApprovalRequestContent:
                    case ToolApprovalResponseContent:
                        // A gated tool call that is not a function call (e.g. a hosted MCP tool) cannot be
                        // represented as a ToolCallRecord on either path. It is still a drop.
                        dropped++;
                        break;

                    case FunctionResultContent result when !string.IsNullOrEmpty(result.CallId)
                                                          && unpairedByCallId.TryGetValue(result.CallId, out var stack)
                                                          && stack.Count > 0:
                        var record = stack.Pop();    // nearest preceding unpaired call with this id
                        record.Result = result.Result;
                        record.Exception = result.Exception;
                        // A paired result was observed ⇒ the tool actually ran (even if Result is null) — EXCEPT for a
                        // rejected approval, whose "failed" result the framework generates itself. Not an execution.
                        if (record.ApprovalState != ToolCallRecord.ApprovalRejected)
                            record.WasExecuted = true;
                        break;

                    // A result with no matching unpaired call (precedes its call, or a stray) is ignored — we never
                    // fabricate a tool call from a lone result.
                }
            }
        }

        var report = new ToolUsageReport { DroppedApprovalRequestCount = dropped };
        foreach (var record in records)
            report.AddCall(record);

        return report;
    }

    // The most recent record for this call id that came through the approval path (any state), whatever its
    // pairing status: a decision can arrive after the call was executed and paired.
    private static ToolCallRecord? LatestApprovalRecord(List<ToolCallRecord> records, string callId, string name)
    {
        if (string.IsNullOrEmpty(callId)) return null;
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var r = records[i];
            if (r.ApprovalState is not null
                && string.Equals(r.CallId, callId, StringComparison.Ordinal)
                && string.Equals(r.Name, name, StringComparison.Ordinal))
                return r;
        }
        return null;
    }

    /// <summary>
    /// The names of the approval-gated function calls in <paramref name="rawMessages"/>, in message order —
    /// what the default <see cref="Extract(IReadOnlyList{object}?)"/> path drops. Empty when there are none.
    /// Used to make a drop loud (the harness and <see cref="DefaultToolUsageExtractor"/> log them).
    /// </summary>
    /// <param name="rawMessages">The raw messages from an agent response.</param>
    public static IReadOnlyList<string> ApprovalGatedToolNames(IReadOnlyList<object>? rawMessages)
    {
        if (rawMessages is null || rawMessages.Count == 0) return Array.Empty<string>();
        var names = new List<string>();
        foreach (var message in rawMessages.OfType<ChatMessage>())
        {
            foreach (var content in message.Contents)
            {
                if (content is ToolApprovalRequestContent { ToolCall: FunctionCallContent call })
                    names.Add(call.Name);
            }
        }
        return names;
    }

    /// <summary>
    /// Extract tool usage report from an agent response.
    /// </summary>
    /// <param name="response">The agent response.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    /// <remarks>Default path — approval-gated calls are counted, not recorded. See <see cref="Extract(AgentResponse, bool)"/>.</remarks>
    public static ToolUsageReport Extract(AgentResponse response)
    {
        _ = response ?? throw new ArgumentNullException(nameof(response));
        return Extract(response.RawMessages);
    }

    /// <summary>
    /// Extract tool usage report from an agent response, optionally including approval-gated calls.
    /// </summary>
    /// <param name="response">The agent response.</param>
    /// <param name="includeApprovalGatedCalls">See <see cref="Extract(IReadOnlyList{object}?, bool)"/>.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    public static ToolUsageReport Extract(AgentResponse response, bool includeApprovalGatedCalls)
    {
        _ = response ?? throw new ArgumentNullException(nameof(response));
        return Extract(response.RawMessages, includeApprovalGatedCalls);
    }

    /// <summary>
    /// Glass Box Part 2 (P2.A2): back-fills real per-tool execution timing onto an already-extracted
    /// <see cref="ToolUsageReport"/> from the <see cref="TraceEntryScope.ToolExecution"/> entries a Glass Box
    /// trace captured at the actual invocation site (<c>EvaluatingAIFunction</c>). Non-streaming reports carry
    /// timing only when this runs, so <c>WithDurationUnder</c> / <c>HaveAverageToolTimeUnder</c> /
    /// <c>HaveTotalToolTimeUnder</c> stay inert until it is called.
    /// <para>
    /// <b>Not yet auto-wired.</b> This is a library primitive — no harness calls it automatically today, so in
    /// the default <c>MAFEvaluationHarness</c> flow the duration assertions are still inert. Wiring it in
    /// (call it on <c>result.ToolUsage</c> with the captured trace before <c>PerformanceMetrics.TotalToolTime</c>
    /// is read) is the follow-up. Call it explicitly to activate the assertions in the meantime.
    /// </para>
    /// <para>
    /// Correlation is by tool <b>name + order</b> among <b>executed</b> calls: only calls that actually ran
    /// (<see cref="ToolCallRecord.WasExecuted"/>) have a matching <see cref="TraceEntryScope.ToolExecution"/>
    /// entry, so emitted-but-not-executed calls are skipped and never consume an execution slot. Each executed
    /// call dequeues its tool's next recorded duration to stay aligned even if it is skipped for being already
    /// timed — so a mix of streaming-timed and untimed executed calls of the same tool never cross-attributes.
    /// A call that already has timing (streaming path) is never overwritten. Uses a deterministic UnixEpoch
    /// anchor because only <see cref="ToolCallRecord.Duration"/> is load-bearing for the assertions, and a
    /// fixed anchor keeps enrichment deterministic (a hand-built <c>ForToolExecution</c> entry has a default
    /// <c>Timestamp</c>). Correlation mismatches are skipped, never thrown.
    /// </para>
    /// </summary>
    /// <param name="report">The report to enrich in place.</param>
    /// <param name="trace">The Glass Box trace whose <see cref="TraceEntryScope.ToolExecution"/> entries carry timing.</param>
    public static void EnrichFromTrace(ToolUsageReport report, AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(trace);

        // Per-name FIFO queue of execution durations (ms), in trace order.
        var durationsByName = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        foreach (var entry in trace.Entries)
        {
            if (entry.EffectiveScope != TraceEntryScope.ToolExecution)
                continue;

            var toolCall = entry.ToolCalls is { Count: > 0 } ? entry.ToolCalls[0] : null;
            var name = toolCall?.Name;
            if (string.IsNullOrEmpty(name))
                continue;

            var durationMs = entry.DurationMs ?? toolCall?.DurationMs ?? 0;
            if (!durationsByName.TryGetValue(name, out var queue))
                durationsByName[name] = queue = new Queue<long>();
            queue.Enqueue(durationMs);
        }

        if (durationsByName.Count == 0)
            return;

        foreach (var call in report.Calls)
        {
            // Only executed calls have a ToolExecution entry; skipping emitted-only calls keeps the
            // per-name queues aligned to real executions (never attribute a duration to a non-executed emit).
            if (!call.WasExecuted)
                continue;

            if (!durationsByName.TryGetValue(call.Name, out var queue) || queue.Count == 0)
                continue;

            // Consume this execution's slot regardless of whether we set timing, so a later executed call
            // of the same tool maps to its OWN execution (not this one's).
            var durationMs = queue.Dequeue();
            if (call.HasTiming)
                continue; // streaming path already timed this call — keep it, but the slot is consumed

            call.StartTime = DateTimeOffset.UnixEpoch;
            call.EndTime = DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(durationMs);
        }
    }
}
