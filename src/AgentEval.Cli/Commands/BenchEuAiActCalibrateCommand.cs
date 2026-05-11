// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using AgentEval.Core;
using AgentEval.EuAiActBenchmark.Articles;
using AgentEval.EuAiActBenchmark.Articles.Building;
using AgentEval.EuAiActBenchmark.Articles.Loading;
using AgentEval.EuAiActBenchmark.Calibration;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench eu-ai-act calibrate</c> subcommand.
/// Loads golden calibration datasets, evaluates them through the configured judge,
/// and writes a Markdown report. Exits with 2 if any pillar fails thresholds.
/// </summary>
public static class BenchEuAiActCalibrateCommand
{
    private const double AccuracyThreshold = 0.85;
    private const double KappaThreshold = 0.70;

    /// <summary>Runs the EU AI Act calibrate subcommand.</summary>
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
        // Calibration requires AGENTEVAL_ALLOW_STUB_JUDGE=1 to use stub mode —
        // stub-graded calibration gates the wrong thing.
        // Workspace root canonicalisation (defense-in-depth against --root traversal).
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null) return 1;
            rootOverride = canonical;
        }

        var (resolvedJudge, judgeModelName, exitCode) = JudgeFactory.Resolve(evaluatorOverride, "EU AI Act calibration");
        if (resolvedJudge is null) return exitCode;
        IEvaluator judge = resolvedJudge;

        // ── Load EU AI Act article registry ──────────────────────────────────
        EuAiActArticlesRegistry articles;
        try
        {
            var loader = new ArticleScenarioYamlLoader();
            var scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: judgeModelName);
            var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
            articles = new EuAiActArticlesRegistry(loader, articleBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load EU AI Act article registry: {ex.Message}");
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

            // Filter to only EU AI Act datasets (containing 'pillar' key patterns from EU files)
            // The loader matches on .Calibration.Golden. in the resource name, so both GDPR and
            // EU AI Act files match. We need only the EU AI Act ones.
            // EU AI Act golden files have control IDs starting with "eu_ai." — filter by probing first entry.
            datasets = datasets
                .Where(ds => ds.Entries.Count > 0 &&
                             ds.Entries[0].ArticleControlId.StartsWith("eu_ai.", StringComparison.Ordinal))
                .ToList();

            if (datasets.Count == 0)
            {
                Console.Error.WriteLine(
                    "No EU AI Act calibration datasets found in the test assembly. " +
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
        Console.WriteLine($"Running EU AI Act calibration across {datasets.Count} pillar dataset(s)...");
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
            "docs", "benchmarks", "eu-ai-act", $"calibration-{dateStr}.md");
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
        // Phase-6 Task 6.6: see BenchCalibrateCommand for rationale — gate on
        // EvaluationFailures > 0 with a distinct INFRA-FAIL status.
        bool allPass = true;
        foreach (var (pillar, pillarReport) in report.PerPillar)
        {
            var accOk = pillarReport.Accuracy >= AccuracyThreshold;
            var kappaOk = pillarReport.CohensKappa >= KappaThreshold;
            var noInfraFail = pillarReport.EvaluationFailures == 0;
            var status = !noInfraFail
                ? "INFRA-FAIL"
                : (accOk && kappaOk ? "PASS" : "FAIL");
            Console.WriteLine(
                $"  [{status}] {pillar}: accuracy={pillarReport.Accuracy:P1}, " +
                $"kappa={pillarReport.CohensKappa:F3}, entries={pillarReport.EntryCount}, " +
                $"failures={pillarReport.EvaluationFailures}");
            if (!accOk || !kappaOk || !noInfraFail) allPass = false;
        }

        Console.WriteLine(allPass
            ? "EU AI Act calibration gate PASSED — all pillars meet thresholds with zero evaluation failures."
            : $"EU AI Act calibration gate FAILED — one or more pillars below accuracy>={AccuracyThreshold:P0} or kappa>={KappaThreshold:F2}, or had non-zero evaluation_failures.");

        return allPass ? 0 : 2;
    }

    private static Assembly? LoadTestAssembly()
    {
        // Try already-loaded assemblies first.
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AgentEval.Tests");
        if (loaded is not null) return loaded;

        // Try to locate the assembly on disk relative to this binary.
        var thisDir = Path.GetDirectoryName(typeof(BenchEuAiActCalibrateCommand).Assembly.Location);
        if (thisDir is null) return null;

        var candidate = Path.Combine(thisDir, "AgentEval.Tests.dll");
        if (File.Exists(candidate))
            return Assembly.LoadFrom(candidate);

        return null;
    }

    private static string BuildMarkdownReport(CalibrationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# EU AI Act Calibration Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine($"Thresholds: accuracy >= {AccuracyThreshold:P0}, Cohen's kappa >= {KappaThreshold:F2}");
        sb.AppendLine();

        foreach (var (pillar, pr) in report.PerPillar.OrderBy(kv => kv.Key))
        {
            var accOk = pr.Accuracy >= AccuracyThreshold;
            var kappaOk = pr.CohensKappa >= KappaThreshold;
            var noInfraFail = pr.EvaluationFailures == 0;
            var badge = !noInfraFail
                ? "INFRA-FAIL"
                : (accOk && kappaOk ? "PASS" : "FAIL");

            sb.AppendLine($"## {pillar} [{badge}]");
            sb.AppendLine();
            sb.AppendLine($"| Metric | Value | Threshold | Status |");
            sb.AppendLine($"|--------|-------|-----------|--------|");
            sb.AppendLine($"| Entries evaluated | {pr.EntryCount} | — | — |");
            sb.AppendLine($"| Evaluation failures | {pr.EvaluationFailures} | == 0 | {(noInfraFail ? "OK" : "INFRA-FAIL")} |");
            sb.AppendLine($"| Accuracy | {pr.Accuracy:P1} | >= {AccuracyThreshold:P0} | {(accOk ? "OK" : "BELOW")} |");
            sb.AppendLine($"| Cohen's kappa | {pr.CohensKappa:F3} | >= {KappaThreshold:F2} | {(kappaOk ? "OK" : "BELOW")} |");
            sb.AppendLine($"| Within score range | {pr.WithinScoreRange} / {pr.EntryCount} | — | — |");
            sb.AppendLine($"| Mean score delta | {pr.MeanScoreDelta:+0.000;-0.000;0.000} | — | — |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

}
