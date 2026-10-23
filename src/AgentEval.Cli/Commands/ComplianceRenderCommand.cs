// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using EuAiActLoader = AgentEval.Compliance.EuAiAct.Articles.Loading.ArticleScenarioYamlLoader;
using EuAiActScenarioToAtomicEval = AgentEval.Compliance.EuAiAct.Articles.Building.ScenarioToAtomicEval;
using EuAiActArticleCompositeBuilder = AgentEval.Compliance.EuAiAct.Articles.Building.ArticleCompositeBuilder;
using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Reporting;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval compliance render</c> subcommand.
/// Reads a verified AEF compliance artifact (converting legacy reports when needed) and renders without LLM cost.
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
        var sanitizedSubject = FileSystemLayout.Sanitize(subject);
        var regulationFolder = isGdpr ? "GDPR" : "EU-AI-Act";
        var complianceDir = Path.Combine(agentEvalDir, "compliance", regulationFolder, sanitizedSubject);

        AefComplianceReport? report;
        try
        {
            var legacyDirectories = Directory.Exists(complianceDir)
                ? Directory.EnumerateDirectories(complianceDir).Take(1001).ToArray()
                : [];
            report = new AefComplianceReportReader().Read(Path.Combine(agentEvalDir, "aef", "runs"),
                legacyDirectories, subject, regulationFolder, string.IsNullOrWhiteSpace(ts) ? null : ts);
            if (report is null)
            {
                Console.Error.WriteLine("No captured rich compliance report is available for this selection. Native evidence takes precedence; absent content cannot be reconstructed from legacy siblings.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read verified compliance evidence: {ex.Message}");
            return 1;
        }
        var evidenceDir = Path.Combine(complianceDir, report.Timestamp);
        var pdfPath = Path.Combine(evidenceDir, "report.pdf");
        Console.WriteLine($"Evidence integrity: {report.Run.Integrity}; signature trust: {report.Run.SignatureTrust}.");
        if (report.Run.Imported)
            Console.WriteLine("Imported: not sealed when it ran. Original execution provenance is not established by the ingest seal.");
        if (isEuAiAct) return await RenderEuAiActAsync(report.Document, pdfPath);

        GdprComplianceEvidence evidence;
        try
        {
            evidence = ComplianceReportJson.Read<GdprComplianceEvidence>(report.Document.Document, s_jsonOpts, "gdpr-evidence.schema.json");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read GDPR report: {ex.Message}");
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
        try
        {
            Directory.CreateDirectory(evidenceDir);
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

    private static async Task<int> RenderEuAiActAsync(AefComplianceDocument document, string pdfPath)
    {
        EuAiActComplianceEvidence evidence;
        try
        {
            evidence = ComplianceReportJson.Read<EuAiActComplianceEvidence>(document.Document, s_jsonOpts, "eu-ai-act-evidence.schema.json");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read EU AI Act report: {ex.Message}");
            return 1;
        }

        // ── Build optional registry for PII redaction ────────────────────────
        // Regression fix: this used to hard-fail (return 1) when the registry couldn't be loaded,
        // contradicting its own "Registry is optional" comment and diverging from the GDPR path (above),
        // which proceeds with a null registry (no redaction lookup) on the identical failure. Matches now —
        // EuAiActPdfRenderer's constructor and every _articles?.GetSpec(...) call site already tolerate null.
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

        // ── Render PDF ───────────────────────────────────────────────────────
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
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

    /// <summary>
    /// Minimal stub evaluator used ONLY to satisfy the <see cref="ArticlesRegistry"/>
    /// constructor's <c>IEvaluator</c> dependency. The PDF render path reads a
    /// pre-existing <c>gdpr-evidence.json</c> (or EU-AI-Act equivalent) end-to-end
    /// and never invokes <see cref="EvaluateAsync"/> — no judgment occurs.
    /// <para>
    /// It is not a stand-in judge: it never grades, and there is no grading on the render path. The registry is
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
                "judgment flow, which must resolve a real judge.");
    }
}
