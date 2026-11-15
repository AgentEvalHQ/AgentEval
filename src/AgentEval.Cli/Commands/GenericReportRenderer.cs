// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>Writes the existing non-PDF HTML sidecar. PDF generation requires the optional companion.</summary>
internal static class GenericReportRenderer
{
    public static async Task<string?> WriteHtmlAsync(
        EvalResult result,
        string outputDir,
        SubjectIdentity subject,
        string benchmarkLabel,
        string? runId = null,
        string? auditHash = null,
        CancellationToken ct = default)
    {
        var options = new EvalResultRenderOptions(
            Subject: subject,
            Title: result.Metric?.Name,
            RegulationOrBenchmark: benchmarkLabel,
            RunId: runId,
            GeneratedAt: DateTimeOffset.UtcNow,
            IncludeProvenance: true,
            AuditHash: auditHash,
            AgentEvalVersion: typeof(GenericReportRenderer).Assembly.GetName().Version?.ToString());

        string? htmlPath = null;

        try
        {
            var htmlBytes = await new HtmlEvalResultRenderer().RenderAsync(result, options, ct);
            htmlPath = Path.Combine(outputDir, "report.html");
            await File.WriteAllBytesAsync(htmlPath, htmlBytes, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write HTML report: {ex.Message}");
            htmlPath = null;
        }

        return htmlPath;
    }
}
