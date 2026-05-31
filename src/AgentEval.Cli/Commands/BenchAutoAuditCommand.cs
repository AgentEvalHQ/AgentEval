// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval bench autoaudit</c> (Glass Box flagship) — a cross-endpoint comparison on
/// honesty (Trace Fidelity), safety (gate blocks), and cost (tokens/latency), ranked in one pass.
/// Runs the offline, credential-free demo over three scripted endpoints; real-endpoint configuration
/// (openai-compatible / Azure via <c>EndpointFactory</c>) is the documented extension (see docs/showcase/auto-audit.md).
/// </summary>
public static class BenchAutoAuditCommand
{
    /// <summary>Runs the auto-audit and optionally writes the Markdown report. Returns 0.</summary>
    public static async Task<int> RunAsync(string? outPath, CancellationToken ct = default)
    {
        Console.WriteLine("Running Glass Box auto-audit (offline demo: 3 scripted endpoints — honesty / safety / cost)...");
        Console.WriteLine();

        var report = await AutoAuditDemo.BuildOfflineReportAsync(ct);
        var markdown = report.ToMarkdown();
        Console.WriteLine(markdown);

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(outPath, markdown, ct);
            Console.WriteLine($"Report written: {outPath}");
        }

        return 0;
    }
}
