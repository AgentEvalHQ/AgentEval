// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AgentEval.Tracing;

/// <summary>
/// A compact, regulation-neutral summary of the Glass Box signals in a v1.1 <see cref="AgentTrace"/> —
/// per-turn counts, runtime-gate Block verdicts, suppressed finish reasons, and token totals. Intended as
/// <b>additive evidence metadata</b> for compliance packs (e.g. GDPR Art. 32 / EU AI Act Art. 14): it is
/// recorded alongside a run's evidence and is <b>never an input to any compliance score</b> (calibration is frozen — ADR-018).
/// </summary>
public sealed record GlassBoxEvidence
{
    /// <summary>Number of chat-boundary LLM round-trips recorded.</summary>
    [JsonPropertyName("chatTurnCount")]
    public int ChatTurnCount { get; init; }

    /// <summary>Number of tool/function executions recorded.</summary>
    [JsonPropertyName("toolExecutionCount")]
    public int ToolExecutionCount { get; init; }

    /// <summary>Number of runtime-gate Block verdicts recorded (PII / injection / safety).</summary>
    [JsonPropertyName("gateBlockCount")]
    public int GateBlockCount { get; init; }

    /// <summary>Distinct policy names that blocked at least once.</summary>
    [JsonPropertyName("gateBlockPolicies")]
    public IReadOnlyList<string> GateBlockPolicies { get; init; } = Array.Empty<string>();

    /// <summary>Number of chat turns whose finish reason was content_filter or length (provider-side intervention).</summary>
    [JsonPropertyName("suppressedFinishTurns")]
    public int SuppressedFinishTurns { get; init; }

    /// <summary>Total prompt tokens across chat-boundary turns.</summary>
    [JsonPropertyName("totalPromptTokens")]
    public int TotalPromptTokens { get; init; }

    /// <summary>Total completion tokens across chat-boundary turns.</summary>
    [JsonPropertyName("totalCompletionTokens")]
    public int TotalCompletionTokens { get; init; }

    /// <summary>
    /// Extracts the Glass Box evidence summary from a trace. Returns null when the trace carries no
    /// chat-boundary (v1.1) detail, so callers can omit the section entirely for v1.0 runs.
    /// </summary>
    public static GlassBoxEvidence? FromTrace(AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var chatResponses = trace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .ToList();
        var toolExecutions = trace.Entries.Count(e => e.EffectiveScope == TraceEntryScope.ToolExecution);

        var hasGateMetadata = trace.Metadata is not null
            && trace.Metadata.Keys.Any(k => k.StartsWith("gate.", StringComparison.Ordinal));

        if (chatResponses.Count == 0 && toolExecutions == 0 && !hasGateMetadata)
        {
            return null;   // no Glass Box detail in this trace
        }

        var (gateBlocks, policies) = CountGateBlocks(trace);

        return new GlassBoxEvidence
        {
            ChatTurnCount = chatResponses.Count,
            ToolExecutionCount = toolExecutions,
            GateBlockCount = gateBlocks,
            GateBlockPolicies = policies,
            SuppressedFinishTurns = chatResponses.Count(r =>
                string.Equals(r.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.FinishReason, "length", StringComparison.OrdinalIgnoreCase)),
            TotalPromptTokens = chatResponses.Sum(r => r.TokenUsage?.PromptTokens ?? 0),
            TotalCompletionTokens = chatResponses.Sum(r => r.TokenUsage?.CompletionTokens ?? 0),
        };
    }

    private static (int Count, IReadOnlyList<string> Policies) CountGateBlocks(AgentTrace trace)
    {
        if (trace.Metadata is null)
        {
            return (0, Array.Empty<string>());
        }

        var count = 0;
        var policies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in trace.Metadata)
        {
            if (!kv.Key.StartsWith("gate.", StringComparison.Ordinal)
                || kv.Value is not IReadOnlyDictionary<string, object?> dict
                || !dict.TryGetValue("action", out var action)
                || !string.Equals(action as string, "Block", StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            // Key shape: gate.{stage}.{seq}.{policy}
            var parts = kv.Key.Split('.');
            if (parts.Length >= 4)
            {
                policies.Add(string.Join('.', parts[3..]));
            }
        }

        return (count, policies.OrderBy(p => p, StringComparer.Ordinal).ToList());
    }
}
