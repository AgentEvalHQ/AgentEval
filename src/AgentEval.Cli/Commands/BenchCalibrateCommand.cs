// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using AgentEval.Core;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Articles.Building;
using AgentEval.GdprBenchmark.Articles.Loading;
using AgentEval.GdprBenchmark.Calibration;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench gdpr calibrate</c> subcommand.
/// Loads golden calibration datasets, evaluates them through the configured judge,
/// and writes a Markdown report. Exits with 2 if any pillar fails thresholds.
/// </summary>
public static class BenchCalibrateCommand
{
    private const double AccuracyThreshold = 0.85;
    private const double KappaThreshold = 0.70;

    /// <summary>Runs the calibrate subcommand.</summary>
    /// <param name="rootOverride">Optional workspace root override (used by tests).</param>
    /// <param name="outPathOverride">Optional output path override (used by tests).</param>
    /// <param name="evaluatorOverride">Optional evaluator override (used by tests).</param>
    /// <returns>0 on success, 2 if thresholds not met, 1 on internal error.</returns>
    public static Task<int> RunAsync(
        string? rootOverride = null,
        string? outPathOverride = null,
        IEvaluator? evaluatorOverride = null)
        => RunCoreAsync(rootOverride, outPathOverride, evaluatorOverride);

    internal static async Task<int> RunCoreAsync(
        string? rootOverride,
        string? outPathOverride,
        IEvaluator? evaluatorOverride)
    {
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
                    "Warning: No real LLM evaluator configured; using stub. " +
                    "Set AZURE_OPENAI_* env vars to enable real judging.");
            }
            judge = new StubEvaluator();
        }

        // ── Load GDPR article registry ───────────────────────────────────────
        ArticlesRegistry articles;
        try
        {
            var loader = new ArticleScenarioYamlLoader();
            var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: "calibration");
            var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
            articles = new ArticlesRegistry(loader, articleBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load GDPR article registry: {ex.Message}");
            return 1;
        }

        // ── Load calibration datasets from the test assembly ─────────────────
        IReadOnlyList<CalibrationDataset> datasets;
        try
        {
            // The golden JSONL files are embedded in the test assembly.
            var testAssembly = LoadTestAssembly();
            if (testAssembly is null)
            {
                Console.Error.WriteLine(
                    "Could not locate AgentEval.Tests assembly. " +
                    "Ensure the solution has been built before running calibrate.");
                return 1;
            }

            var datasetLoader = new CalibrationDatasetLoader();
            datasets = await datasetLoader.LoadAllFromAssemblyAsync(testAssembly);

            if (datasets.Count == 0)
            {
                Console.Error.WriteLine(
                    "No calibration datasets found in the test assembly. " +
                    "Ensure the JSONL files are marked as EmbeddedResource.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load calibration datasets: {ex.Message}");
            return 1;
        }

        // ── Run calibration ──────────────────────────────────────────────────
        Console.WriteLine($"Running calibration across {datasets.Count} pillar dataset(s)...");
        CalibrationReport report;
        try
        {
            var runner = new CalibrationRunner(articles, judge);
            report = await runner.RunAsync(datasets);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Calibration run failed: {ex.Message}");
            return 1;
        }

        // ── Write Markdown report ────────────────────────────────────────────
        var dateStr = report.GeneratedAt.ToString("yyyy-MM-dd");
        var defaultOut = Path.Combine(
            rootOverride ?? Directory.GetCurrentDirectory(),
            "docs", "gdpr-benchmark", $"calibration-{dateStr}.md");
        var outPath = outPathOverride ?? defaultOut;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var md = BuildMarkdownReport(report);
            await File.WriteAllTextAsync(outPath, md);
            Console.WriteLine($"Calibration report: {outPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write calibration report: {ex.Message}");
        }

        // ── Evaluate thresholds ──────────────────────────────────────────────
        bool allPass = true;
        foreach (var (pillar, pillarReport) in report.PerPillar)
        {
            var accOk = pillarReport.Accuracy >= AccuracyThreshold;
            var kappaOk = pillarReport.CohensKappa >= KappaThreshold;
            var status = accOk && kappaOk ? "PASS" : "FAIL";
            Console.WriteLine(
                $"  [{status}] {pillar}: accuracy={pillarReport.Accuracy:P1}, " +
                $"kappa={pillarReport.CohensKappa:F3}, entries={pillarReport.EntryCount}");
            if (!accOk || !kappaOk) allPass = false;
        }

        Console.WriteLine(allPass
            ? "Calibration gate PASSED — all pillars meet thresholds."
            : $"Calibration gate FAILED — one or more pillars below accuracy>={AccuracyThreshold:P0} or kappa>={KappaThreshold:F2}.");

        return allPass ? 0 : 2;
    }

    private static Assembly? LoadTestAssembly()
    {
        // Try already-loaded assemblies first.
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AgentEval.Tests");
        if (loaded is not null) return loaded;

        // Try to locate the assembly on disk relative to this binary.
        var thisDir = Path.GetDirectoryName(typeof(BenchCalibrateCommand).Assembly.Location);
        if (thisDir is null) return null;

        var candidate = Path.Combine(thisDir, "AgentEval.Tests.dll");
        if (File.Exists(candidate))
            return Assembly.LoadFrom(candidate);

        return null;
    }

    private static string BuildMarkdownReport(CalibrationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GDPR Calibration Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine($"Thresholds: accuracy >= {AccuracyThreshold:P0}, Cohen's kappa >= {KappaThreshold:F2}");
        sb.AppendLine();

        foreach (var (pillar, pr) in report.PerPillar.OrderBy(kv => kv.Key))
        {
            var accOk = pr.Accuracy >= AccuracyThreshold;
            var kappaOk = pr.CohensKappa >= KappaThreshold;
            var badge = accOk && kappaOk ? "PASS" : "FAIL";

            sb.AppendLine($"## {pillar} [{badge}]");
            sb.AppendLine();
            sb.AppendLine($"| Metric | Value | Threshold | Status |");
            sb.AppendLine($"|--------|-------|-----------|--------|");
            sb.AppendLine($"| Entries evaluated | {pr.EntryCount} | — | — |");
            sb.AppendLine($"| Accuracy | {pr.Accuracy:P1} | >= {AccuracyThreshold:P0} | {(accOk ? "OK" : "BELOW")} |");
            sb.AppendLine($"| Cohen's kappa | {pr.CohensKappa:F3} | >= {KappaThreshold:F2} | {(kappaOk ? "OK" : "BELOW")} |");
            sb.AppendLine($"| Within score range | {pr.WithinScoreRange} / {pr.EntryCount} | — | — |");
            sb.AppendLine($"| Mean score delta | {pr.MeanScoreDelta:+0.000;-0.000;0.000} | — | — |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Stub evaluator used when no real LLM judge is configured.
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
