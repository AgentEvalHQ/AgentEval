// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval render</c> subcommand.
/// Reads a verified original agentic report (converting legacy inputs) and renders Markdown/PDF without LLM cost.
/// </summary>
public static class RenderCommand
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Runs the render command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(
        string benchmark,
        string subject,
        string? ts,
        string? rootOverride) =>
        RunAsync(benchmark, subject, ts, rootOverride, resultOverride: null);

    /// <summary>Runs the render command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunAsync(
        string benchmark,
        string subject,
        string? ts,
        string? rootOverride,
        AgenticBenchmarkResult? resultOverride)
    {
        var isAgentic = string.Equals(benchmark, "agentic", StringComparison.OrdinalIgnoreCase);
        if (!isAgentic)
        {
            Console.Error.WriteLine($"Unsupported benchmark '{benchmark}'. Currently supported: agentic.");
            return 1;
        }

        // --ts is joined into a filesystem path via Path.Combine(benchmarkDir, ts), so a `..`-laced
        // value would escape the benchmark subtree (controlling which result is read / where reports
        // are written). Validate as a single safe path segment, matching the hardened sibling
        // ComplianceRenderCommand and the Mission Control REST endpoint (SEC-05).
        if (!string.IsNullOrWhiteSpace(ts)
            && !FileSystemLayout.IsSafePathSegment(ts))
        {
            Console.Error.WriteLine($"Error: --ts '{ts}' is not a safe path segment.");
            return 1;
        }

        // ── Workspace setup ──────────────────────────────────────────────────
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null) return 1;
            rootOverride = canonical;
        }
        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("Could not find a solution root. Provide --root or run from within a solution directory.");
            return 1;
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        var sanitizedSubject = FileSystemLayout.Sanitize(subject);
        var benchmarkDir = Path.Combine(agentEvalDir, "benchmarks", "agentic", sanitizedSubject);

        var result = resultOverride;
        var selectedTimestamp = ts;
        if (result is null)
        {
            try
            {
                var legacy = Directory.Exists(benchmarkDir) ? Directory.EnumerateDirectories(benchmarkDir).Take(1001).ToArray() : [];
                var report = new AefAgenticReportReader().Read(Path.Combine(agentEvalDir, "aef", "runs"), legacy,
                    subject, string.IsNullOrWhiteSpace(ts) ? null : ts);
                if (report is null)
                {
                    Console.Error.WriteLine("No captured original agentic report is available. Native evidence takes precedence; missing content cannot be recovered from a legacy sibling.");
                    return 1;
                }
                result = BenchmarkReportJson.Read<AgenticBenchmarkResult>(report.Document, s_jsonOpts, "agentic-result.schema.json");
                selectedTimestamp = report.Timestamp;
                Console.WriteLine($"Evidence integrity: {report.Run.Integrity}; signature trust: {report.Run.SignatureTrust}.");
                if (report.Run.Imported) Console.WriteLine("Imported: not sealed when it ran. The ingest seal does not establish original execution custody.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read verified agentic report: {ex.Message}");
                return 1;
            }
        }
        var resultDir = Path.Combine(benchmarkDir, string.IsNullOrWhiteSpace(selectedTimestamp)
            ? result.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) : selectedTimestamp);
        return await RenderAgenticAsync(resultDir, result);
    }

    private static async Task<int> RenderAgenticAsync(string resultDir, AgenticBenchmarkResult result)
    {
        // ── Render Markdown ──────────────────────────────────────────────────
        var mdPath = Path.Combine(resultDir, "report.md");
        try
        {
            Directory.CreateDirectory(resultDir);
            var renderer = new AgenticMarkdownRenderer();
            var md = renderer.Render(result);
            await File.WriteAllTextAsync(mdPath, md);
            Console.WriteLine($"Markdown report written to: {mdPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to render Markdown: {ex.Message}");
            return 1;
        }

        // PDF report
        var pdfPath = Path.Combine(resultDir, "report.pdf");
        try
        {
            await new AgenticPdfRenderer().RenderAsync(result, pdfPath);
            Console.WriteLine($"PDF report written to: {pdfPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write PDF report: {ex.Message}");
        }

        return 0;
    }
}
