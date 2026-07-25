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

    /// <summary>
    /// Number of gate verdicts that WOULD have blocked but did not, because the gate ran under a non-enforcing policy
    /// (Observe / WarnOnly) — Phase 6, P6-2. Counted separately from <see cref="GateBlockCount"/> (which stays
    /// enforced-only), this is the counterfactual that turns an Observe-mode dry run into a data-driven
    /// Observe→Enforce decision: "flipping to Enforce would have blocked this many calls."
    /// </summary>
    [JsonPropertyName("wouldBlockCount")]
    public int WouldBlockCount { get; init; }

    /// <summary>Number of gate verdicts that WOULD have mutated a call's arguments but did not (non-enforcing policy) — P6-2.</summary>
    [JsonPropertyName("wouldMutateCount")]
    public int WouldMutateCount { get; init; }

    /// <summary>Number of gate verdicts that WOULD have redacted a tool result but did not (non-enforcing policy) — P6-2.</summary>
    [JsonPropertyName("wouldRedactCount")]
    public int WouldRedactCount { get; init; }

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
            && trace.Metadata.Keys.Any(GateMetadataReader.IsGateKey);

        if (chatResponses.Count == 0 && toolExecutions == 0 && !hasGateMetadata)
        {
            return null;   // no Glass Box detail in this trace
        }

        var (gateBlocks, policies) = CountGateBlocks(trace);
        var (wouldBlock, wouldMutate, wouldRedact) = CountCounterfactuals(trace);

        return new GlassBoxEvidence
        {
            ChatTurnCount = chatResponses.Count,
            ToolExecutionCount = toolExecutions,
            GateBlockCount = gateBlocks,
            GateBlockPolicies = policies,
            WouldBlockCount = wouldBlock,
            WouldMutateCount = wouldMutate,
            WouldRedactCount = wouldRedact,
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
            // Handles both the in-memory dictionary shape and the JsonElement shape of a reloaded trace.
            if (!GateMetadataReader.IsGateKey(kv.Key) || !GateMetadataReader.IsBlock(kv.Value))
            {
                continue;
            }

            count++;
            if (GateMetadataReader.PolicyFromKey(kv.Key) is { } policy)
            {
                policies.Add(policy);
            }
        }

        return (count, policies.OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    // P6-2: count the Observe/WarnOnly counterfactuals recorded alongside real verdicts. A would-block carries the
    // explicit "wouldAction":"Block" marker (a non-enforced block records action="Warn", which alone can't be told
    // from a genuine warning); a would-mutate/would-redact is an action="Mutate"/"Redact" record with applied=false
    // (the P2-2 recorded-not-applied flag). None of these are enforced, so none inflate GateBlockCount.
    private static (int WouldBlock, int WouldMutate, int WouldRedact) CountCounterfactuals(AgentTrace trace)
    {
        if (trace.Metadata is null)
        {
            return (0, 0, 0);
        }

        int wouldBlock = 0, wouldMutate = 0, wouldRedact = 0;
        foreach (var kv in trace.Metadata)
        {
            if (!GateMetadataReader.IsGateKey(kv.Key))
            {
                continue;
            }

            if (string.Equals(GateMetadataReader.ReadField(kv.Value, "wouldAction"), "Block", StringComparison.Ordinal))
            {
                wouldBlock++;
                continue;
            }

            // applied is a bool: the in-memory dict renders "False", a reloaded JsonElement renders "false" — compare
            // case-insensitively so the counterfactual survives serialization.
            var notApplied = string.Equals(GateMetadataReader.ReadField(kv.Value, "applied"), "false", StringComparison.OrdinalIgnoreCase);
            if (!notApplied)
            {
                continue;
            }

            switch (GateMetadataReader.ReadField(kv.Value, "action"))
            {
                case "Mutate":
                    wouldMutate++;
                    break;
                case "Redact":
                    wouldRedact++;
                    break;
            }
        }

        return (wouldBlock, wouldMutate, wouldRedact);
    }
}
