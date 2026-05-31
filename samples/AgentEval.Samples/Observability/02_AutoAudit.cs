// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;

namespace AgentEval.Samples;

/// <summary>
/// Glass Box — Auto-Audit (Observability). Offline PREVIEW of the table shape (no credentials): the three
/// "endpoints" (clean / silent-retry / PII+content-filter) are <b>scripted</b> with pre-baked behaviours, so
/// this shows what a ranked honesty/safety/cost comparison LOOKS like — it is not a real endpoint comparison.
/// For real, run "Real vs Framework: Agent / Workflow" against a live model.
/// </summary>
public static class AutoAudit
{
    /// <summary>Runs the offline auto-audit demonstration.</summary>
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Glass Box — Auto-Audit (OFFLINE PREVIEW, 3 scripted endpoints) ===\n");
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("  [synthetic — these endpoints are scripted to show the table shape, not a real comparison]");
        Console.WriteLine("  [for a real framework-vs-Glass-Box audit, run \"Real vs Framework: Agent / Workflow\"]\n");
        Console.ResetColor();

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
