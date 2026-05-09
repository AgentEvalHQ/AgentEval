// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Reporting;
using AgentEval.GdprBenchmark.Reporting.Pdf;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval compliance render</c> subcommand.
/// Reads an existing <c>gdpr-evidence.json</c> and renders a PDF report without any LLM cost.
/// </summary>
public static class ComplianceRenderCommand
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Runs the compliance render command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(
        string regulation,
        string subject,
        string? ts,
        string? rootOverride) =>
        RunAsync(regulation, subject, ts, rootOverride, registryOverride: null);

    /// <summary>Runs the compliance render command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunAsync(
        string regulation,
        string subject,
        string? ts,
        string? rootOverride,
        ArticlesRegistry? registryOverride)
    {
        if (!string.Equals(regulation, "gdpr", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unsupported regulation '{regulation}'. Only 'gdpr' is supported in v1.");
            return 1;
        }

        // ── Workspace setup ──────────────────────────────────────────────────
        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("Could not find a solution root. Provide --root or run from within a solution directory.");
            return 1;
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        var sanitizedSubject = SanitizeForPath(subject);
        var complianceDir = Path.Combine(agentEvalDir, "compliance", "GDPR", sanitizedSubject);

        if (!Directory.Exists(complianceDir))
        {
            Console.Error.WriteLine($"No compliance evidence found at {complianceDir}.");
            return 1;
        }

        // ── Resolve timestamp directory ──────────────────────────────────────
        string evidenceDir;
        if (!string.IsNullOrWhiteSpace(ts))
        {
            evidenceDir = Path.Combine(complianceDir, ts);
        }
        else
        {
            // Use the most recent timestamp directory
            var dirs = Directory.GetDirectories(complianceDir)
                .OrderByDescending(d => d)
                .ToList();
            if (dirs.Count == 0)
            {
                Console.Error.WriteLine($"No evidence directories found under {complianceDir}.");
                return 1;
            }
            evidenceDir = dirs[0];
        }

        if (!Directory.Exists(evidenceDir))
        {
            Console.Error.WriteLine($"Evidence directory not found: {evidenceDir}.");
            return 1;
        }

        // ── Load gdpr-evidence.json ──────────────────────────────────────────
        var evidenceFile = Path.Combine(evidenceDir, "gdpr-evidence.json");
        if (!File.Exists(evidenceFile))
        {
            Console.Error.WriteLine($"gdpr-evidence.json not found at {evidenceFile}.");
            return 1;
        }

        GdprComplianceEvidence evidence;
        try
        {
            await using var stream = File.OpenRead(evidenceFile);
            evidence = await JsonSerializer.DeserializeAsync<GdprComplianceEvidence>(stream, s_jsonOpts)
                ?? throw new InvalidOperationException("Deserialized evidence was null.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read evidence file: {ex.Message}");
            return 1;
        }

        // ── Build optional articles registry for PII redaction ───────────────
        ArticlesRegistry? articles = registryOverride;
        if (articles is null)
        {
            try
            {
                var loader = new ArticleScenarioYamlLoader();
                var stubEval = new StubEvaluatorForRegistry();
                var scenarioBuilder = new ScenarioToAtomicEval(stubEval, judgeModel: "none");
                var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
                articles = new ArticlesRegistry(loader, articleBuilder);
            }
            catch
            {
                // Registry is optional — proceed without redaction on failure
                articles = null;
            }
        }

        // ── Render PDF ───────────────────────────────────────────────────────
        var pdfPath = Path.Combine(evidenceDir, "report.pdf");
        try
        {
            var renderer = new GDPRPdfRenderer(articles);
            await renderer.RenderAsync(evidence, pdfPath);
            Console.WriteLine($"PDF report written to: {pdfPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to render PDF: {ex.Message}");
            return 1;
        }
    }

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }

    /// <summary>Minimal stub evaluator required only to construct the ArticlesRegistry.</summary>
    private sealed class StubEvaluatorForRegistry : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentEval.Core.EvaluationResult
            {
                OverallScore = 100,
                Summary = "registry-only stub"
            });
    }
}
