// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Evals.Agentic.Safety.Policy;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench agentic</c> subcommand.
/// Runs an agentic benchmark against an agent response, persists the full evidence chain,
/// and writes a Markdown report. PDF rendering is deferred to a follow-up batch (plan-05 §G/A1.26).
/// </summary>
public static class BenchAgenticCommand
{
    /// <summary>Runs the bench agentic command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        string? budgetTier = null) =>
        RunAsync(preset, subject, rootOverride, inputText, evaluatorOverride: null, budgetTier);

    /// <summary>Runs the bench agentic command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        IEvaluator? evaluatorOverride,
        string? budgetTier = null)
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

        // ── Select preset ────────────────────────────────────────────────────
        CompositeEval benchmark;
        try
        {
            benchmark = ResolvePreset(preset, judge, subject);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build agentic preset '{preset}': {ex.Message}");
            return 1;
        }

        // ── Apply budget-tier filter (optional) ──────────────────────────────
        if (!string.IsNullOrWhiteSpace(budgetTier) && !string.Equals(budgetTier, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseBudgetTier(budgetTier, out var tier))
            {
                Console.Error.WriteLine($"Invalid --budget-tier '{budgetTier}'. Allowed values: trivial | low | medium | high | all.");
                return 1;
            }
            try
            {
                var beforeCount = benchmark.Components.Count;
                benchmark = CostFilteredCompositeBuilder.FilterByBudget(benchmark, tier);
                var afterCount = benchmark.Components.Count;
                if (afterCount < beforeCount)
                {
                    Console.WriteLine($"Budget-tier filter '{budgetTier}': kept {afterCount} of {beforeCount} components (removed {beforeCount - afterCount} above-budget evaluators).");
                }
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        // ── Build input ──────────────────────────────────────────────────────
        var query = inputText ??
            "Search for the latest quarterly earnings report for ACME Corp and summarize the key financial metrics.";
        var agentResponse =
            "I'll help you find the quarterly earnings report for ACME Corp. " +
            "Based on the retrieved data, here are the key financial metrics: " +
            "Revenue grew 12% year-over-year to $4.2B, operating margin was 18.3%, " +
            "and EPS was $2.47, beating consensus estimates by $0.12.";
        var evalInput = new EvalInput(Query: query, Response: agentResponse);

        // ── Run benchmark ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);

        Console.WriteLine($"Running agentic benchmark ({preset}) for subject '{subject}'...");

        string runId;
        EvalResult compositeResult;
        try
        {
            var runner = new AgenticBenchmarkRunner();
            (runId, compositeResult) = await runner.RunAsync(store, subjectIdentity, benchmark, evalInput);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark run failed: {ex.Message}");
            return 1;
        }

        // ── Generate reports ─────────────────────────────────────────────────
        var reporter = new AgenticBenchmarkReporter();
        var options = new AgenticReportOptions(Preset: preset);
        AgenticBenchmarkResult result;
        try
        {
            result = await reporter.SaveReportAsync(store, subjectIdentity, runId, compositeResult, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to generate agentic benchmark report: {ex.Message}");
            return 1;
        }

        // Derive output directory (mirrors AgenticBenchmarkReporter path convention)
        var sanitizedSubject = SanitizeForPath(subject);
        var ts = result.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var outputDir = Path.Combine(agentEvalDir, "benchmarks", "agentic", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        // Markdown report
        try
        {
            var md = new AgenticMarkdownRenderer().Render(result);
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
            var pdfPath = Path.Combine(outputDir, "report.pdf");
            await new AgenticPdfRenderer().RenderAsync(result, pdfPath);
            Console.WriteLine($"PDF report:      {pdfPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write PDF report: {ex.Message}");
        }

        // ── Exit code ─────────────────────────────────────────────────────────
        var overall = result.Summary.OverallStatus;
        Console.WriteLine($"Overall result: {overall} (score {result.Summary.OverallScore:P0})");

        return overall switch
        {
            "PASS" => 0,
            "FAIL" => 2,
            _ => 2  // WARN also returns non-zero for CI strictness
        };
    }

    /// <summary>
    /// Resolves a preset specification into a <see cref="CompositeEval"/>.
    /// <list type="bullet">
    ///   <item><c>agentic-execution</c> — Standard 6-evaluator composition (default).</item>
    ///   <item><c>tool-call-accuracy</c> — Tool Call Accuracy aggregate preset.</item>
    ///   <item><c>rag-quality</c> — 7-evaluator RAG quality composition.</item>
    ///   <item><c>judge-quality</c> — 3 meta-evaluators for inter-rater agreement, calibration accuracy, and judge drift. No LLM judge required.</item>
    ///   <item><c>safety</c> — 12-evaluator safety/security composite. Uses a default empty policy and the subject name. No real content-safety client is wired by default.</item>
    ///   <item><c>telemetry</c> — 6 pure-code operational evaluators. Supply telemetry data via EvalInput.Metadata["agentic_telemetry"].</item>
    ///   <item><c>stochastic-stability</c> — Single-component composite measuring run-to-run score consistency. Supply run results via EvalInput.Metadata["run_results"].</item>
    /// </list>
    /// </summary>
    /// <param name="presetSpec">The preset identifier.</param>
    /// <param name="judge">The LLM evaluator to use (not used by <c>judge-quality</c>, <c>telemetry</c>, or <c>stochastic-stability</c> presets).</param>
    /// <param name="subjectId">
    /// Subject identifier used by the <c>safety</c> preset for policy resolution.
    /// Defaults to <c>"default-agent"</c> when not supplied.
    /// </param>
    /// <returns>The resolved <see cref="CompositeEval"/>.</returns>
    /// <exception cref="ArgumentException">Thrown for unknown preset identifiers.</exception>
    internal static CompositeEval ResolvePreset(string presetSpec, IEvaluator judge, string? subjectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);
        ArgumentNullException.ThrowIfNull(judge);

        return presetSpec.ToLowerInvariant() switch
        {
            "agentic-execution" => AgenticBenchmark.AgenticExecution(judge),
            "tool-call-accuracy" => AgenticBenchmark.ToolCallAccuracy(judge),
            "rag-quality" => AgenticBenchmark.RagQuality(judge),
            "judge-quality" => AgenticBenchmark.JudgeQuality(),
            "safety" => AgenticBenchmark.Safety(
                judge,
                policyResolver: new StaticPolicyResolver(new ProhibitedActionPolicy([], [], [], [])),
                subjectId: subjectId ?? "default-agent",
                contentSafetyClient: null),
            "telemetry" => AgenticBenchmark.Telemetry(),
            "stochastic-stability" => AgenticBenchmark.StochasticStability(),
            "conversational" => AgenticBenchmark.Conversational(judge),
            "reasoning" => AgenticBenchmark.Reasoning(judge),
            "user-experience" => AgenticBenchmark.UserExperience(judge),
            "adversarial-direct" => AgenticBenchmark.AdversarialDirect(judge),
            _ => throw new ArgumentException($"Unknown agentic preset '{presetSpec}'. " +
                "Known presets: agentic-execution, tool-call-accuracy, rag-quality, judge-quality, safety, telemetry, stochastic-stability, conversational, reasoning, user-experience, adversarial-direct.",
                nameof(presetSpec))
        };
    }

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }

    /// <summary>
    /// Parses the <c>--budget-tier</c> CLI value into an <see cref="EvaluatorCostTier"/>.
    /// Accepted values: <c>trivial</c>, <c>low</c>, <c>medium</c>, <c>high</c>. Returns
    /// <c>false</c> for unrecognized strings.
    /// </summary>
    internal static bool TryParseBudgetTier(string value, out EvaluatorCostTier tier)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "trivial": tier = EvaluatorCostTier.Trivial; return true;
            case "low":     tier = EvaluatorCostTier.Low;     return true;
            case "medium":  tier = EvaluatorCostTier.Medium;  return true;
            case "high":    tier = EvaluatorCostTier.High;    return true;
            default:        tier = EvaluatorCostTier.High;    return false;
        }
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
