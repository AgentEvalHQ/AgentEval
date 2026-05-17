// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Shared helpers for the v0.10.1 <c>Benchmarks/</c> sample suite. Centralises
/// output-directory resolution, JSON + HTML + PDF rendering, console summary
/// printing, and gentle "skip if Azure OpenAI is missing" boilerplate so the
/// per-family samples can stay tight and read like a story.
/// </summary>
internal static class BenchmarkSampleHelpers
{
    /// <summary>
    /// Returns a per-benchmark, per-timestamp output directory under
    /// <c>samples/AgentEval.Samples/output/{benchmark}/run-{utc-timestamp}/</c>.
    /// The directory is created if it does not exist.
    /// </summary>
    public static string EnsureRunDirectory(string benchmarkName)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output", benchmarkName, $"run-{stamp}");
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// Writes the result as canonical JSON next to an HTML report rendered by
    /// <see cref="HtmlEvalResultRenderer"/>. PDF is optional — pass
    /// <paramref name="includePdf"/>=true for audit-grade families.
    /// </summary>
    /// <returns>Tuple of (jsonPath, htmlPath, pdfPath?).</returns>
    public static async Task<(string json, string html, string? pdf)> WriteReportsAsync(
        EvalResult result,
        SubjectIdentity subject,
        string benchmarkName,
        string regulationOrBenchmark,
        bool includePdf,
        string? auditHash = null,
        CancellationToken ct = default)
    {
        var outDir = EnsureRunDirectory(benchmarkName);

        // ── JSON (canonical) ─────────────────────────────────────────────────
        var jsonPath = Path.Combine(outDir, "report.json");
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, jsonOpts), ct);

        // ── HTML (always) ────────────────────────────────────────────────────
        var renderOpts = new EvalResultRenderOptions(
            Subject: subject,
            Title: result.Metric.Name,
            RegulationOrBenchmark: regulationOrBenchmark,
            RunId: Path.GetFileName(outDir),
            GeneratedAt: DateTimeOffset.UtcNow,
            IncludeProvenance: true,
            AuditHash: auditHash,
            AgentEvalVersion: "0.10.1-beta");

        var htmlBytes = await new HtmlEvalResultRenderer().RenderAsync(result, renderOpts, ct);
        var htmlPath = Path.Combine(outDir, "report.html");
        await File.WriteAllBytesAsync(htmlPath, htmlBytes, ct);

        // ── PDF (opt-in) ─────────────────────────────────────────────────────
        string? pdfPath = null;
        if (includePdf)
        {
            var pdfBytes = await new PdfEvalResultRenderer().RenderAsync(result, renderOpts, ct);
            pdfPath = Path.Combine(outDir, "report.pdf");
            await File.WriteAllBytesAsync(pdfPath, pdfBytes, ct);
        }

        return (jsonPath, htmlPath, pdfPath);
    }

    /// <summary>Prints a per-sample header banner.</summary>
    public static void PrintHeader(string title, string subtitle)
    {
        Console.WriteLine();
        Console.WriteLine("+============================================================================+");
        Console.WriteLine($"|  {title,-72}  |");
        Console.WriteLine($"|  {subtitle,-72}  |");
        Console.WriteLine("+============================================================================+");
        Console.WriteLine();
    }

    /// <summary>Prints the canonical "skipping — Azure OpenAI required" box.</summary>
    public static void PrintMissingCredentialsBox(string sampleName)
    {
        Console.WriteLine("+-----------------------------------------------------------------------------+");
        Console.WriteLine($"|  SKIPPING {sampleName,-66}|");
        Console.WriteLine("|  Azure OpenAI credentials required — set:                                   |");
        Console.WriteLine("|    AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT     |");
        Console.WriteLine("+-----------------------------------------------------------------------------+");
    }

    /// <summary>Prints a short summary table with the report file paths.</summary>
    public static void PrintReportPaths(EvalResult result, (string json, string html, string? pdf) paths)
    {
        Console.WriteLine();
        Console.WriteLine("   +------------------------------------------------------------+");
        Console.WriteLine("   |                       SUMMARY                              |");
        Console.WriteLine("   +------------------------------------------------------------+");
        var label = string.Equals(result.Score.Label, "skipped", StringComparison.OrdinalIgnoreCase)
            ? "NOT TESTED"
            : result.Score.Label.ToUpperInvariant();
        Console.WriteLine($"   Verdict:  {label}   Score: {result.Score.Value:P1}   Severity: {result.Score.Severity}");
        Console.WriteLine();
        Console.WriteLine($"   JSON: {paths.json}");
        Console.WriteLine($"   HTML: {paths.html}");
        if (paths.pdf is not null)
            Console.WriteLine($"   PDF:  {paths.pdf}");
    }
}
