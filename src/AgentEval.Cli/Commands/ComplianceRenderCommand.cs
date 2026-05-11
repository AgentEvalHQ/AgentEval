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
using EuAiActLoader = AgentEval.EuAiActBenchmark.Articles.Loading.ArticleScenarioYamlLoader;
using EuAiActScenarioToAtomicEval = AgentEval.EuAiActBenchmark.Articles.Building.ScenarioToAtomicEval;
using EuAiActArticleCompositeBuilder = AgentEval.EuAiActBenchmark.Articles.Building.ArticleCompositeBuilder;
using AgentEval.EuAiActBenchmark.Articles;
using AgentEval.EuAiActBenchmark.Reporting;
using AgentEval.EuAiActBenchmark.Reporting.Pdf;

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
        var isGdpr = string.Equals(regulation, "gdpr", StringComparison.OrdinalIgnoreCase);
        var isEuAiAct = string.Equals(regulation, "eu-ai-act", StringComparison.OrdinalIgnoreCase);
        if (!isGdpr && !isEuAiAct)
        {
            Console.Error.WriteLine($"Unsupported regulation '{regulation}'. Supported in v1: gdpr, eu-ai-act.");
            return 1;
        }

        // Phase-7 Task 7.1: validate the --ts path segment if supplied. The CLI
        // already canonicalises --root; --ts goes straight into a filesystem
        // join via Path.Combine, so a `..`-laced value would escape the
        // workspace. The MC REST endpoint already rejects this; the CLI should
        // too for symmetry.
        if (!string.IsNullOrWhiteSpace(ts)
            && !AgentEval.Output.FileSystemLayout.IsSafePathSegment(ts))
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
        var sanitizedSubject = SanitizeForPath(subject);
        var regulationFolder = isGdpr ? "GDPR" : "EU-AI-Act";
        var complianceDir = Path.Combine(agentEvalDir, "compliance", regulationFolder, sanitizedSubject);

        if (!Directory.Exists(complianceDir))
        {
            Console.Error.WriteLine($"No compliance evidence found at {complianceDir}.");
            return 1;
        }

        if (isEuAiAct)
        {
            return await RenderEuAiActAsync(complianceDir, ts);
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

    private static async Task<int> RenderEuAiActAsync(string complianceDir, string? ts)
    {
        // ── Resolve timestamp directory ──────────────────────────────────────
        string evidenceDir;
        if (!string.IsNullOrWhiteSpace(ts))
        {
            evidenceDir = Path.Combine(complianceDir, ts);
        }
        else
        {
            var dirs = Directory.GetDirectories(complianceDir).OrderByDescending(d => d).ToList();
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

        // ── Load eu-ai-act-evidence.json ─────────────────────────────────────
        var evidenceFile = Path.Combine(evidenceDir, "eu-ai-act-evidence.json");
        if (!File.Exists(evidenceFile))
        {
            Console.Error.WriteLine($"eu-ai-act-evidence.json not found at {evidenceFile}.");
            return 1;
        }

        EuAiActComplianceEvidence evidence;
        try
        {
            await using var stream = File.OpenRead(evidenceFile);
            evidence = await JsonSerializer.DeserializeAsync<EuAiActComplianceEvidence>(stream, s_jsonOpts)
                ?? throw new InvalidOperationException("Deserialized evidence was null.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read evidence file: {ex.Message}");
            return 1;
        }

        // ── Build optional registry for PII redaction ────────────────────────
        EuAiActArticlesRegistry? articles = null;
        try
        {
            var loader = new EuAiActLoader();
            var stubEval = new StubEvaluatorForRegistry();
            var scenarioBuilder = new EuAiActScenarioToAtomicEval(stubEval, judgeModel: "none");
            var articleBuilder = new EuAiActArticleCompositeBuilder(scenarioBuilder);
            articles = new EuAiActArticlesRegistry(loader, articleBuilder);
        }
        catch
        {
            // Registry is optional — proceed without redaction on failure
            articles = null;
        }

        if (articles is null)
        {
            Console.Error.WriteLine("EU AI Act registry could not be loaded; PDF rendering requires the registry for PII redaction lookup.");
            return 1;
        }

        // ── Render PDF ───────────────────────────────────────────────────────
        var pdfPath = Path.Combine(evidenceDir, "report.pdf");
        try
        {
            var renderer = new EuAiActPdfRenderer(articles);
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

    /// <summary>
    /// Minimal stub evaluator used ONLY to satisfy the <see cref="ArticlesRegistry"/>
    /// constructor's <c>IEvaluator</c> dependency. The PDF render path reads a
    /// pre-existing <c>gdpr-evidence.json</c> (or EU-AI-Act equivalent) end-to-end
    /// and never invokes <see cref="EvaluateAsync"/> — no judgment occurs.
    /// <para>
    /// This stub therefore does NOT need to be gated by
    /// <c>AGENTEVAL_ALLOW_STUB_JUDGE</c>: that gate exists to prevent stub-graded
    /// CI gates, and there is no grading on the render path. The registry is
    /// needed only for PII-redaction metadata lookup during PDF rendering. If
    /// <see cref="EvaluateAsync"/> is ever called on this type, it throws — that
    /// would indicate the render path regressed into a grading flow and should
    /// fail loudly.
    /// </para>
    /// </summary>
    private sealed class StubEvaluatorForRegistry : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "StubEvaluatorForRegistry.EvaluateAsync was invoked. This stub is " +
                "registry-construction-only — no grading should occur on the render " +
                "path. If you see this, the render path has regressed into a " +
                "judgment flow without the AGENTEVAL_ALLOW_STUB_JUDGE gate.");
    }
}
