// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval render</c> subcommand.
/// Reads an existing <c>agentic-result.json</c> and renders a Markdown report without any LLM cost.
/// PDF rendering is deferred (plan-05 §G/A1.26).
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
        var sanitizedSubject = SanitizeForPath(subject);
        var benchmarkDir = Path.Combine(agentEvalDir, "benchmarks", "agentic", sanitizedSubject);

        if (!Directory.Exists(benchmarkDir))
        {
            Console.Error.WriteLine($"No agentic benchmark results found at {benchmarkDir}.");
            return 1;
        }

        return await RenderAgenticAsync(benchmarkDir, ts, resultOverride);
    }

    private static async Task<int> RenderAgenticAsync(
        string benchmarkDir,
        string? ts,
        AgenticBenchmarkResult? resultOverride)
    {
        // ── Resolve timestamp directory ──────────────────────────────────────
        string resultDir;
        if (!string.IsNullOrWhiteSpace(ts))
        {
            resultDir = Path.Combine(benchmarkDir, ts);
        }
        else
        {
            var dirs = Directory.GetDirectories(benchmarkDir).OrderByDescending(d => d).ToList();
            if (dirs.Count == 0)
            {
                Console.Error.WriteLine($"No result directories found under {benchmarkDir}.");
                return 1;
            }
            resultDir = dirs[0];
        }

        if (!Directory.Exists(resultDir))
        {
            Console.Error.WriteLine($"Result directory not found: {resultDir}.");
            return 1;
        }

        // ── Load agentic-result.json ─────────────────────────────────────────
        AgenticBenchmarkResult result;
        if (resultOverride is not null)
        {
            result = resultOverride;
        }
        else
        {
            var resultFile = Path.Combine(resultDir, "agentic-result.json");
            if (!File.Exists(resultFile))
            {
                Console.Error.WriteLine($"agentic-result.json not found at {resultFile}.");
                return 1;
            }

            try
            {
                await using var stream = File.OpenRead(resultFile);
                result = await JsonSerializer.DeserializeAsync<AgenticBenchmarkResult>(stream, s_jsonOpts)
                    ?? throw new InvalidOperationException("Deserialized result was null.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read result file: {ex.Message}");
                return 1;
            }
        }

        // ── Render Markdown ──────────────────────────────────────────────────
        var mdPath = Path.Combine(resultDir, "report.md");
        try
        {
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

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }
}
