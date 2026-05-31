// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.Benchmarks;

/// <summary>One endpoint's row in an auto-audit comparison (Glass Box flagship).</summary>
/// <param name="Endpoint">Endpoint display name (e.g. "Ollama-Llama3.1", "Azure-GPT-4o-mini").</param>
/// <param name="FidelityScore">Trace Fidelity root score, 0–1 (reported vs observed).</param>
/// <param name="GateBlocks">Number of gate Block verdicts recorded (PII / injection / safety).</param>
/// <param name="PromptTokens">Total prompt tokens across the run.</param>
/// <param name="CompletionTokens">Total completion tokens across the run.</param>
/// <param name="LatencyMs">Total wall-clock latency across the run.</param>
/// <param name="Completed">Whether the scenario completed without an unhandled error.</param>
/// <param name="TopDiscrepancies">The most severe fidelity discrepancies (for the report).</param>
public sealed record AutoAuditEndpointResult(
    string Endpoint,
    double FidelityScore,
    int GateBlocks,
    int PromptTokens,
    int CompletionTokens,
    long LatencyMs,
    bool Completed,
    IReadOnlyList<string> TopDiscrepancies)
{
    /// <summary>Total tokens (prompt + completion).</summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>A cross-endpoint auto-audit comparison, rankable and renderable to Markdown.</summary>
public sealed record AutoAuditReport(IReadOnlyList<AutoAuditEndpointResult> Results)
{
    /// <summary>
    /// Endpoints ranked best-first: completed runs first, then highest fidelity, then fewest gate blocks,
    /// then lowest token cost.
    /// </summary>
    public IReadOnlyList<AutoAuditEndpointResult> Ranking => Results
        .OrderByDescending(r => r.Completed)
        .ThenByDescending(r => r.FidelityScore)
        .ThenBy(r => r.GateBlocks)
        .ThenBy(r => r.TotalTokens)
        .ToList();

    /// <summary>The winning endpoint (top of <see cref="Ranking"/>), or null when there are no results.</summary>
    public AutoAuditEndpointResult? Winner => Ranking.FirstOrDefault();

    /// <summary>Renders the comparison as a Markdown report.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Glass Box Auto-Audit — cross-endpoint comparison");
        sb.AppendLine();
        sb.AppendLine("| Rank | Endpoint | Fidelity | Gate blocks | Tokens | Latency (ms) | Completed | Top discrepancies |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        var rank = 1;
        foreach (var r in Ranking)
        {
            var top = r.TopDiscrepancies.Count > 0 ? string.Join("; ", r.TopDiscrepancies) : "—";
            sb.AppendLine($"| {rank++} | {r.Endpoint} | {r.FidelityScore * 100:F0}% | {r.GateBlocks} | {r.TotalTokens} | {r.LatencyMs} | {(r.Completed ? "yes" : "no")} | {top} |");
        }

        sb.AppendLine();
        if (Winner is not null)
        {
            sb.AppendLine($"**Winner: {Winner.Endpoint}** — fidelity {Winner.FidelityScore * 100:F0}%, {Winner.GateBlocks} gate block(s), {Winner.TotalTokens} tokens.");
        }

        return sb.ToString();
    }
}
