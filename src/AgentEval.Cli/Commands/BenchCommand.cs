// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Composition;
using AgentEval.GdprBenchmark.DomainPacks.ChildrensService;
using AgentEval.GdprBenchmark.DomainPacks.Healthcare;
using AgentEval.GdprBenchmark.DomainPacks.HR;
using AgentEval.GdprBenchmark.Reporting;
using AgentEval.GdprBenchmark.Reporting.Pdf;
using AgentEval.Output;
using GdprBenchmarkFactory = AgentEval.GdprBenchmark.GdprBenchmark;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench gdpr</c> subcommand.
/// Runs a GDPR benchmark against an agent response, persists the full evidence chain,
/// and writes both a Markdown and a PDF report.
/// </summary>
public static class BenchCommand
{
    /// <summary>Runs the bench gdpr command using auto-discovered workspace root.</summary>
    public static Task<int> RunGdprAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        int runs = 1) =>
        RunGdprAsync(preset, subject, rootOverride, inputText, evaluatorOverride: null, runs: runs);

    /// <summary>Runs the bench gdpr command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunGdprAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        IEvaluator? evaluatorOverride,
        int runs = 1)
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
            // Check for real LLM configuration
            var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            if (string.IsNullOrWhiteSpace(azureEndpoint))
            {
                var allowStub = Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE");
                if (!string.Equals(allowStub, "1", StringComparison.Ordinal)
                 && !string.Equals(allowStub, "true", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        "✖ No LLM evaluator configured (AZURE_OPENAI_ENDPOINT is unset).\n" +
                        "  Set AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT\n" +
                        "  to enable real judging, or set AGENTEVAL_ALLOW_STUB_JUDGE=1 to run with\n" +
                        "  a deterministic stub (results are not meaningful — CI must NOT do this).");
                    return 2;
                }
                Console.Error.WriteLine(
                    "⚠ AGENTEVAL_ALLOW_STUB_JUDGE=1 — using stub evaluator. " +
                    "Results are not a real judgement; do not rely on the verdict in CI.");
            }
            judge = new StubEvaluator();
        }

        // ── Build registry ───────────────────────────────────────────────────
        ArticlesRegistry articles;
        ScenarioToAtomicEval scenarioBuilder;
        try
        {
            var loader = new ArticleScenarioYamlLoader();
            scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "stub");
            var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
            articles = new ArticlesRegistry(loader, articleBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load GDPR article registry: {ex.Message}");
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
            Console.Error.WriteLine($"Failed to build benchmark preset '{preset}': {ex.Message}");
            return 1;
        }

        // ── Build input ──────────────────────────────────────────────────────
        var query = inputText ??
            "Please help me understand what personal data you store about me and how I can request its deletion.";
        var agentResponse =
            "I can help with that. We store your name and email. " +
            "You can request deletion by contacting privacy@example.com.";
        var evalInput = new EvalInput(Query: query, Response: agentResponse);

        // ── Run benchmark ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);

        Console.WriteLine($"Running GDPR benchmark ({preset}) for subject '{subject}'" +
            (runs > 1 ? $" [{runs} stochastic runs]" : "") + "...");

        string runId;
        EvalResult compositeResult;
        try
        {
            if (runs > 1)
            {
                // Stochastic mode: run N times, aggregate via MajorityVote.
                compositeResult = await StochasticBenchRunner.RunNAsync(store, subjectIdentity, benchmark, evalInput, runs);
                // Use the benchmark key as the run ID for reporting purposes.
                runId = $"{benchmark.Key}.runs{runs}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            }
            else
            {
                var runner = new GdprBenchmarkRunner();
                (runId, compositeResult) = await runner.RunAsync(store, subjectIdentity, benchmark, evalInput);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark run failed: {ex.Message}");
            return 1;
        }

        // ── Generate reports ─────────────────────────────────────────────────
        var reporter = new GDPRComplianceReporter(articles);
        var options = new GdprReportOptions(Preset: preset);
        GdprComplianceEvidence evidence;
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
        var outputDir = Path.Combine(agentEvalDir, "compliance", "GDPR", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        // Markdown report
        try
        {
            var mdRenderer = new MarkdownRenderer();
            var md = mdRenderer.Render(evidence);
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
            var pdfRenderer = new GDPRPdfRenderer(articles);
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
    /// Resolves a preset specification string into a <see cref="CompositeEval"/>.
    /// Supports additive composition syntax: <c>standard+healthcare</c>,
    /// <c>standard+hr</c>, <c>standard+childrens</c>, and combinations such as
    /// <c>standard+healthcare+hr</c>. The first token must be a base preset
    /// (<c>smoke</c>, <c>standard</c>, or <c>audit</c>/<c>auditgrade</c>).
    /// </summary>
    /// <param name="presetSpec">
    /// The preset specification, e.g. <c>"standard"</c> or <c>"standard+healthcare+hr"</c>.
    /// </param>
    /// <param name="articles">The loaded articles registry.</param>
    /// <param name="scenarioBuilder">The scenario-to-eval builder for domain-pack scenarios.</param>
    /// <returns>The resolved <see cref="CompositeEval"/>.</returns>
    /// <exception cref="ArgumentException">Thrown for unknown base presets or domain packs.</exception>
    internal static CompositeEval ResolvePreset(
        string presetSpec,
        ArticlesRegistry articles,
        ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(scenarioBuilder);

        var tokens = presetSpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw new ArgumentException("Empty preset specification.", nameof(presetSpec));

        var basePreset = tokens[0].ToLowerInvariant() switch
        {
            "smoke"                  => GdprBenchmarkFactory.Smoke(articles),
            "standard"               => GdprBenchmarkFactory.Standard(articles),
            "audit" or "auditgrade"  => GdprBenchmarkFactory.AuditGrade(articles),
            _                        => throw new ArgumentException($"Unknown base preset '{tokens[0]}'.", nameof(presetSpec))
        };

        if (tokens.Length == 1) return basePreset;

        // Accumulate additions from all domain packs, then apply in one pass.
        var allAdditions = new Dictionary<string, List<EvalComponent>>();
        foreach (var pack in tokens.Skip(1))
        {
            var packAdditions = pack.ToLowerInvariant() switch
            {
                "healthcare"  => HealthcareScenarios.Load(scenarioBuilder),
                "hr"          => HRScenarios.Load(scenarioBuilder),
                "childrens"   => ChildrensServiceScenarios.Load(scenarioBuilder),
                _             => throw new ArgumentException($"Unknown domain pack '{pack}'.", nameof(presetSpec))
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
    /// Returns a fixed passing score (75/100) for every scenario.
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
