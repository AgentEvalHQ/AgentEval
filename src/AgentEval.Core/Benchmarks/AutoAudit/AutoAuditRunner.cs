// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Benchmarks;

/// <summary>
/// Cross-endpoint auto-audit (Glass Box flagship): turns each endpoint's agent-boundary + chat-boundary
/// traces into a comparable <see cref="AutoAuditEndpointResult"/> (Trace Fidelity, gate blocks, tokens,
/// latency), then ranks them. The caller produces the traces per endpoint (real or scripted); this engine
/// is the deterministic, judge-free comparison + ranking step, so it is fully testable offline.
/// </summary>
public static class AutoAuditRunner
{
    /// <summary>
    /// Evaluates one endpoint from its agent-boundary and chat-boundary traces. Gate Block verdicts are read
    /// from the chat trace's top-level <see cref="AgentTrace.Metadata"/> (where <c>EvalGatingChatClient</c> records them).
    /// </summary>
    public static AutoAuditEndpointResult Evaluate(string endpoint, AgentTrace agentBoundary, AgentTrace chatBoundary, bool completed = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(agentBoundary);
        ArgumentNullException.ThrowIfNull(chatBoundary);

        var fidelity = new TraceFidelityRunner().Reconcile(agentBoundary, chatBoundary);

        var responses = chatBoundary.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .ToList();
        var promptTokens = responses.Sum(r => r.TokenUsage?.PromptTokens ?? 0);
        var completionTokens = responses.Sum(r => r.TokenUsage?.CompletionTokens ?? 0);
        var latency = responses.Sum(r => r.DurationMs ?? 0);

        var gateBlocks = CountGateBlocks(chatBoundary);

        var top = fidelity.Discrepancies
            .Where(d => d.Count > 0)
            .OrderByDescending(d => TraceFidelityRubric.SeverityPenalty(d.Severity))
            .Select(d => $"{d.ClassKey} ×{d.Count}")
            .Take(3)
            .ToList();

        return new AutoAuditEndpointResult(
            endpoint, fidelity.OverallScore, gateBlocks, promptTokens, completionTokens, latency, completed, top);
    }

    /// <summary>Assembles a cross-endpoint <see cref="AutoAuditReport"/> from per-endpoint results.</summary>
    public static AutoAuditReport Compare(IEnumerable<AutoAuditEndpointResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return new AutoAuditReport(results.ToList());
    }

    private static int CountGateBlocks(AgentTrace trace)
    {
        if (trace.Metadata is null)
        {
            return 0;
        }

        var blocks = 0;
        foreach (var kv in trace.Metadata)
        {
            // GateMetadataReader handles both the in-memory dictionary and the JsonElement of a reloaded trace,
            // so a persisted-trace auto-audit reports the same block count as an in-memory one.
            if (GateMetadataReader.IsGateKey(kv.Key) && GateMetadataReader.IsBlock(kv.Value))
            {
                blocks++;
            }
        }

        return blocks;
    }
}
