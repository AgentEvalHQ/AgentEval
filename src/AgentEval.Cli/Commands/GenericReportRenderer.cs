// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Tiny helper that emits the generic <c>report.html</c> + <c>report.pdf</c> alongside
/// the family-specific JSON / Markdown reports written by red-team and perf CLI commands.
/// Closes plan-13 T0.5: OWASP / MITRE / Perf CLI report parity with the compliance
/// commands (GDPR / EU AI Act / Agentic), which already shipped PDF via their family-
/// specific renderers.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="HtmlEvalResultRenderer"/> + <see cref="PdfEvalResultRenderer"/> — the
/// universal renderers introduced in v0.10.1 Phase A. They walk any <see cref="EvalResult"/>
/// tree generically; OWASP / MITRE / Perf all produce one (OWASP/MITRE via
/// <c>BuildEvalResult(RedTeamResult)</c>; Perf via the registry's <c>EvaluateAsync</c>
/// adapter). Family-specific renderers stay in place for the compliance commands because
/// those consume bespoke evidence envelopes (<c>GdprComplianceEvidence</c> etc.) with
/// cover-page metadata the universal envelope doesn't carry.
/// </para>
/// <para>
/// Failures are reported as warnings and do not abort the run — the JSON + Markdown
/// already shipped above are the authoritative artefacts; HTML / PDF are cherry-on-top.
/// </para>
/// </remarks>
internal static class GenericReportRenderer
{
    /// <summary>
    /// Renders <paramref name="result"/> to <c>report.html</c> + <c>report.pdf</c> under
    /// <paramref name="outputDir"/>. Both renderers are best-effort; failures log a
    /// warning to stderr and skip the file. Returns the paths actually written (subset of
    /// {html, pdf}) so callers can echo them to stdout.
    /// </summary>
    public static async Task<(string? HtmlPath, string? PdfPath)> WriteHtmlAndPdfAsync(
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
        string? pdfPath = null;

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

        try
        {
            var pdfBytes = await new PdfEvalResultRenderer().RenderAsync(result, options, ct);
            pdfPath = Path.Combine(outputDir, "report.pdf");
            await File.WriteAllBytesAsync(pdfPath, pdfBytes, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write PDF report: {ex.Message}");
            pdfPath = null;
        }

        return (htmlPath, pdfPath);
    }
}
