// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;

namespace AgentEval.Samples;

/// <summary>
/// Glass Box — Auto-Audit (Observability). Offline (no credentials): benchmarks three "endpoints"
/// (clean / silent-retry / PII+content-filter) on honesty (Trace Fidelity), safety (gate blocks), and
/// cost (tokens/latency) in one pass, then prints a ranked cross-endpoint comparison.
/// </summary>
public static class AutoAudit
{
    /// <summary>Runs the offline auto-audit demonstration.</summary>
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Glass Box — Auto-Audit (offline, 3 endpoints) ===\n");

        var report = await AutoAuditDemo.BuildOfflineReportAsync();

        foreach (var r in report.Ranking)
        {
            var top = r.TopDiscrepancies.Count > 0 ? string.Join(", ", r.TopDiscrepancies) : "none";
            Console.WriteLine($"  {r.Endpoint,-20} fidelity={r.FidelityScore * 100,3:F0}%  gateBlocks={r.GateBlocks}  tokens={r.TotalTokens}  discrepancies: {top}");
        }

        Console.WriteLine();
        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine("One command, multiple endpoints, ranked on honesty + safety + cost — the Glass Box flagship.");
    }
}
