// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Articles;
using AgentEval.EuAiActBenchmark.Articles.Building;
using AgentEval.EuAiActBenchmark.Articles.Loading;
using AgentEval.EuAiActBenchmark.Composition;
using AgentEval.EuAiActBenchmark.DomainPacks.HighRiskCredit;
using AgentEval.EuAiActBenchmark.DomainPacks.HighRiskEducation;
using AgentEval.EuAiActBenchmark.DomainPacks.HighRiskEmployment;
using AgentEval.EuAiActBenchmark.Reporting;
using AgentEval.EuAiActBenchmark.Reporting.Pdf;
using AgentEval.Output;
using EuAiActBenchmarkFactory = AgentEval.EuAiActBenchmark.EuAiActBenchmark;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench eu-ai-act</c> subcommand.
/// Runs an EU AI Act benchmark against an agent response, persists the full evidence chain,
/// and writes both a Markdown and a PDF report.
/// </summary>
public static class BenchEuAiActCommand
{
    /// <summary>Runs the bench eu-ai-act command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText) =>
        RunAsync(preset, subject, rootOverride, inputText, evaluatorOverride: null);

    /// <summary>Runs the bench eu-ai-act command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        IEvaluator? evaluatorOverride)
    {
        // ── Workspace setup ──────────────────────────────────────────────────
        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("Could not find a solution root (.sln, .slnx, or .git). " +
                "Provide --root or run from within a solution directory.");
            return 1;
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        if (!Directory.Exists(agentEvalDir))
        {
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init` first.");
            return 1;
        }

        // ── Judge / evaluator ────────────────────────────────────────────────
        IEvaluator judge;
        if (evaluatorOverride is not null)
        {
            judge = evaluatorOverride;
        }
        else
        {
            var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            if (string.IsNullOrWhiteSpace(azureEndpoint))
            {
                Console.Error.WriteLine(
                    "Warning: No real LLM evaluator wired; using stub. " +
                    "Set AZURE_OPENAI_* env vars to enable real judging.");
            }
            judge = new StubEvaluator();
        }

        // ── Build registry ───────────────────────────────────────────────────
        EuAiActArticlesRegistry articles;
        ScenarioToAtomicEval scenarioBuilder;
        try
        {
            var loader = new ArticleScenarioYamlLoader();
            scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "stub");
            var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
            articles = new EuAiActArticlesRegistry(loader, articleBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load EU AI Act article registry: {ex.Message}");
            return 1;
        }

        // ── Select preset ────────────────────────────────────────────────────
        CompositeEval benchmark;
        try
        {
            benchmark = ResolvePreset(preset, articles, scenarioBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build EU AI Act preset '{preset}': {ex.Message}");
            return 1;
        }

        // ── Build input ──────────────────────────────────────────────────────
        var query = inputText ??
            "I'm building an AI assistant. What should it disclose about itself when interacting with users?";
        var agentResponse =
            "I should clearly identify myself as an AI assistant when interacting with users. " +
            "For high-risk decisions, I'll defer to human review.";
        var evalInput = new EvalInput(Query: query, Response: agentResponse);

        // ── Run benchmark ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);

        Console.WriteLine($"Running EU AI Act benchmark ({preset}) for subject '{subject}'...");

        string runId;
        EvalResult compositeResult;
        try
        {
            var runner = new EuAiActBenchmarkRunner();
            (runId, compositeResult) = await runner.RunAsync(store, subjectIdentity, benchmark, evalInput);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark run failed: {ex.Message}");
            return 1;
        }

        // ── Generate reports ─────────────────────────────────────────────────
        var reporter = new EuAiActComplianceReporter(articles);
        var options = new EuAiActReportOptions(Preset: preset);
        EuAiActComplianceEvidence evidence;
        try
        {
            evidence = await reporter.SaveReportAsync(store, subjectIdentity, runId, compositeResult, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to generate compliance report: {ex.Message}");
            return 1;
        }

        // Derive output directory (mirrors FileSystemOutputStore path convention)
        var sanitizedSubject = SanitizeForPath(subject);
        var ts = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var outputDir = Path.Combine(agentEvalDir, "compliance", "EU-AI-Act", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        // Markdown report
        try
        {
            var md = new MarkdownRenderer().Render(evidence);
            var mdPath = Path.Combine(outputDir, "report.md");
            await File.WriteAllTextAsync(mdPath, md);
            Console.WriteLine($"Markdown report: {mdPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write Markdown report: {ex.Message}");
        }

        // PDF report
        try
        {
            var pdfRenderer = new EuAiActPdfRenderer(articles);
            var pdfPath = Path.Combine(outputDir, "report.pdf");
            await pdfRenderer.RenderAsync(evidence, pdfPath);
            Console.WriteLine($"PDF report: {pdfPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write PDF report: {ex.Message}");
        }

        // ── Exit code ─────────────────────────────────────────────────────────
        var overall = evidence.Summary.OverallStatus;
        Console.WriteLine($"Overall result: {overall} (score {evidence.Summary.OverallScore:P0})");

        return overall switch
        {
            "PASS" => 0,
            "FAIL" => 2,
            _ => 2  // WARN also returns non-zero for CI strictness
        };
    }

    /// <summary>
    /// Resolves a preset specification into a <see cref="CompositeEval"/>.
    /// Base presets: <c>smoke</c>, <c>standard</c>, <c>audit</c>/<c>auditgrade</c>.
    /// Supports additive domain-pack composition with <c>+</c>:
    /// <list type="bullet">
    ///   <item><c>standard+high-risk-employment</c> — Standard with employment domain-pack additions.</item>
    ///   <item><c>standard+high-risk-credit</c> — Standard with credit-scoring domain-pack additions.</item>
    ///   <item><c>standard+high-risk-education</c> — Standard with education domain-pack additions.</item>
    ///   <item><c>standard+high-risk-employment+high-risk-credit</c> — multiple packs accumulated.</item>
    /// </list>
    /// </summary>
    /// <param name="presetSpec">The preset specification, e.g. <c>"standard"</c> or <c>"standard+high-risk-employment+high-risk-credit"</c>.</param>
    /// <param name="articles">The loaded articles registry.</param>
    /// <param name="scenarioBuilder">The scenario-to-eval builder for domain-pack scenarios.</param>
    /// <returns>The resolved <see cref="CompositeEval"/>.</returns>
    /// <exception cref="ArgumentException">Thrown for unknown base presets or domain packs.</exception>
    internal static CompositeEval ResolvePreset(
        string presetSpec,
        EuAiActArticlesRegistry articles,
        ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(scenarioBuilder);

        var tokens = presetSpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw new ArgumentException("Empty preset specification.", nameof(presetSpec));

        var basePreset = tokens[0].ToLowerInvariant() switch
        {
            "smoke"                  => EuAiActBenchmarkFactory.Smoke(articles),
            "standard"               => EuAiActBenchmarkFactory.Standard(articles),
            "audit" or "auditgrade"  => EuAiActBenchmarkFactory.AuditGrade(articles),
            _                        => throw new ArgumentException($"Unknown EU AI Act preset '{tokens[0]}'.", nameof(presetSpec))
        };

        if (tokens.Length == 1) return basePreset;

        // Accumulate additions from all domain packs, then apply in one pass.
        var allAdditions = new Dictionary<string, List<EvalComponent>>();
        foreach (var pack in tokens.Skip(1))
        {
            var packAdditions = pack.ToLowerInvariant() switch
            {
                "high-risk-employment" => HighRiskEmploymentScenarios.Load(scenarioBuilder),
                "high-risk-credit"     => HighRiskCreditScenarios.Load(scenarioBuilder),
                "high-risk-education"  => HighRiskEducationScenarios.Load(scenarioBuilder),
                _                      => throw new ArgumentException($"Unknown EU AI Act domain pack '{pack}'.", nameof(presetSpec))
            };

            foreach (var (key, components) in packAdditions)
            {
                if (!allAdditions.TryGetValue(key, out var list))
                {
                    list = new List<EvalComponent>();
                    allAdditions[key] = list;
                }
                list.AddRange(components);
            }
        }

        var merged = allAdditions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<EvalComponent>)kv.Value);

        return basePreset.WithExtraScenarios(merged);
    }

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }

    /// <summary>
    /// Stub evaluator used when no real LLM wiring is available.
    /// Returns a fixed score (75/100) for every scenario.
    /// </summary>
    private sealed class StubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var criteriaList = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 75,
                Summary = "Stub evaluation — no real LLM judge configured.",
                CriteriaResults = criteriaList
                    .Select(c => new CriterionResult
                    {
                        Criterion = c,
                        Met = true,
                        Explanation = "Stub: assumed met."
                    })
                    .ToList()
            });
        }
    }
}
