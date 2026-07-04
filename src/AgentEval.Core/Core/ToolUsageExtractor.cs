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
    public static ToolUsageReport Extract(IReadOnlyList<object>? rawMessages)
    {
        var report = new ToolUsageReport();

        if (rawMessages == null || rawMessages.Count == 0)
            return report;

        // L8: pair results to calls in MESSAGE ORDER, one result per call, instead of a single global last-wins map.
        // A CallId may legitimately repeat across turns (multi-turn tool loops); the old flat map paired an
        // emitted-only call in an earlier turn with a later turn's result that REUSED the same id, falsely reading
        // WasExecuted = true (a Behavioral over-claim). We now pair each result to the NEAREST PRECEDING still-unpaired
        // call with the same id (a LIFO stack per id), and remove it so one result cannot satisfy two calls. A
        // null/empty CallId can't be paired, so it stays unpaired (WasExecuted = false — the safe under-claim direction).
        var records = new List<ToolCallRecord>();
        var unpairedByCallId = new Dictionary<string, Stack<ToolCallRecord>>(StringComparer.Ordinal);
        int order = 0;

        foreach (var message in rawMessages.OfType<ChatMessage>())
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    order++;
                    var record = new ToolCallRecord
                    {
                        Name = call.Name,
                        CallId = call.CallId,
                        Arguments = call.Arguments,
                        Order = order,
                    };
                    records.Add(record);
                    if (!string.IsNullOrEmpty(call.CallId))
                    {
                        if (!unpairedByCallId.TryGetValue(call.CallId, out var stack))
                            unpairedByCallId[call.CallId] = stack = new Stack<ToolCallRecord>();
                        stack.Push(record);
                    }
                }
                else if (content is FunctionResultContent result && !string.IsNullOrEmpty(result.CallId)
                         && unpairedByCallId.TryGetValue(result.CallId, out var stack) && stack.Count > 0)
                {
                    var record = stack.Pop();    // nearest preceding unpaired call with this id
                    record.Result = result.Result;
                    record.Exception = result.Exception;
                    record.WasExecuted = true;   // a paired result was observed ⇒ the tool actually ran (even if Result is null)
                }
                // A result with no matching unpaired call (precedes its call, or a stray) is ignored — we never
                // fabricate a tool call from a lone result.
            }
        }

        foreach (var record in records)
            report.AddCall(record);

        return report;
    }
    
    /// <summary>
    /// Extract tool usage report from an agent response.
    /// </summary>
    /// <param name="response">The agent response.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    public static ToolUsageReport Extract(AgentResponse response)
    {
        _ = response ?? throw new ArgumentNullException(nameof(response));
        return Extract(response.RawMessages);
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
