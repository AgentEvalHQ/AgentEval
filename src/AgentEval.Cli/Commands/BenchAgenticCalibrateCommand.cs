// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Evals.Agentic.Process;
using AgentEval.Evals.Agentic.System;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench agentic calibrate</c> subcommand.
/// Loads golden calibration datasets for the 11 agentic evaluators (5 system + 6 process),
/// evaluates them through the configured LLM judge, and writes a Markdown report.
/// Exits with 2 if any category fails accuracy or Cohen's kappa thresholds.
/// </summary>
/// <remarks>
/// TODO (Program.cs wiring): If the <c>benchAgenticCmd</c> block was not yet added to
/// Program.cs by the time this file was compiled, wire the subcommand by adding:
/// <code>
/// var agenticCalibrateRootOpt = new Option&lt;string?&gt;("--root") { ... };
/// var agenticCalibrateOutOpt  = new Option&lt;string?&gt;("--out")  { ... };
/// var agenticCalibrateCmd = new Command("calibrate", "Run agentic judge calibration ...");
/// agenticCalibrateCmd.Add(agenticCalibrateRootOpt);
/// agenticCalibrateCmd.Add(agenticCalibrateOutOpt);
/// agenticCalibrateCmd.SetAction(async (ParseResult p, CancellationToken ct) =>
/// {
///     var root = p.GetValue(agenticCalibrateRootOpt);
///     var outPath = p.GetValue(agenticCalibrateOutOpt);
///     return await BenchAgenticCalibrateCommand.RunAsync(root, outPath);
/// });
/// benchAgenticCmd.Add(agenticCalibrateCmd);
/// </code>
/// </remarks>
public static class BenchAgenticCalibrateCommand
{
    private const double AccuracyThreshold = 0.85;
    private const double KappaThreshold = 0.70;

    /// <summary>Runs the agentic calibrate subcommand.</summary>
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
        var (resolvedJudge, _, exitCode) = JudgeFactory.Resolve(evaluatorOverride, "agentic calibration");
        if (resolvedJudge is null) return exitCode;
        IEvaluator judge = resolvedJudge;

        // ── Build evaluator dispatch table ───────────────────────────────────
        // Maps each evaluator key string to a factory that produces IEval instances.
        // The ToolCallAccuracyAggregateEval uses the convenience single-judge overload.
        var evalRegistry = new Dictionary<string, IEval>(StringComparer.OrdinalIgnoreCase)
        {
            // System evaluators
            ["task_completion"]           = new TaskCompletionEval(judge, judgeModel: "calibration"),
            ["task_adherence"]            = new TaskAdherenceEval(judge, judgeModel: "calibration"),
            ["intent_identification"]     = new IntentIdentificationEval(judge, judgeModel: "calibration"),
            ["intent_resolution"]         = new IntentResolutionEval(judge, judgeModel: "calibration"),
            ["task_navigation_efficiency"] = new TaskNavigationEfficiencyEval(judge, judgeModel: "calibration"),
            // Process evaluators
            ["tool_selection"]            = new ToolSelectionEval(judge, judgeModel: "calibration"),
            ["tool_input_accuracy"]       = new ToolInputAccuracyEval(judge, judgeModel: "calibration"),
            ["tool_output_utilization"]   = new ToolOutputUtilizationEval(judge, judgeModel: "calibration"),
            ["tool_call_success"]         = new ToolCallSuccessEval(judge, judgeModel: "calibration"),
            ["tool_efficiency"]           = new ToolEfficiencyEval(judge, judgeModel: "calibration"),
            ["tool_call_accuracy"]        = new ToolCallAccuracyAggregateEval(judge, judgeModel: "calibration"),
        };

        IEval? Resolver(string key) =>
            evalRegistry.TryGetValue(key, out var eval) ? eval : null;

        // ── Load calibration datasets from the test assembly ─────────────────
        IReadOnlyList<AgentEval.Evals.Agentic.Calibration.CalibrationDataset> datasets;
        try
        {
            var testAssembly = LoadTestAssembly();
            if (testAssembly is null)
            {
                Console.Error.WriteLine(
                    "Could not locate AgentEval.Tests assembly. " +
                    "Ensure the solution has been built before running calibrate.");
                return 1;
            }

            var datasetLoader = new AgentEval.Evals.Agentic.Calibration.CalibrationDatasetLoader();
            datasets = await datasetLoader.LoadAllFromAssemblyAsync(testAssembly);

            if (datasets.Count == 0)
            {
                Console.Error.WriteLine(
                    "No agentic calibration datasets found in the test assembly. " +
                    "Ensure the AgenticBenchmark/Calibration/Golden/*.jsonl files are marked as EmbeddedResource.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load agentic calibration datasets: {ex.Message}");
            return 1;
        }

        // ── Run calibration ──────────────────────────────────────────────────
        Console.WriteLine($"Running agentic calibration across {datasets.Count} category dataset(s)...");
        AgentEval.Evals.Agentic.Calibration.CalibrationReport report;
        try
        {
            var runner = new AgentEval.Evals.Agentic.Calibration.CalibrationRunner(Resolver);
            report = await runner.RunAsync(datasets);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Agentic calibration run failed: {ex.Message}");
            return 1;
        }

        // ── Write Markdown report ────────────────────────────────────────────
        var dateStr = report.GeneratedAt.ToString("yyyy-MM-dd");
        var defaultOut = Path.Combine(
            rootOverride ?? Directory.GetCurrentDirectory(),
            "docs", "benchmarks", "agentic", $"calibration-{dateStr}.md");
        var outPath = outPathOverride ?? defaultOut;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var md = BuildMarkdownReport(report);
            await File.WriteAllTextAsync(outPath, md);
            Console.WriteLine($"Agentic calibration report: {outPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write agentic calibration report: {ex.Message}");
        }

        // ── Evaluate thresholds ──────────────────────────────────────────────
        bool allPass = true;
        foreach (var (category, categoryReport) in report.PerCategory.OrderBy(kv => kv.Key))
        {
            var accOk = categoryReport.Accuracy >= AccuracyThreshold;
            var kappaOk = categoryReport.CohensKappa >= KappaThreshold;
            var status = accOk && kappaOk ? "PASS" : "FAIL";
            Console.WriteLine(
                $"  [{status}] {category}: accuracy={categoryReport.Accuracy:P1}, " +
                $"kappa={categoryReport.CohensKappa:F3}, entries={categoryReport.EntryCount}");
            if (!accOk || !kappaOk) allPass = false;
        }

        Console.WriteLine(allPass
            ? "Agentic calibration gate PASSED — all categories meet thresholds."
            : $"Agentic calibration gate FAILED — one or more categories below " +
              $"accuracy>={AccuracyThreshold:P0} or kappa>={KappaThreshold:F2}.");

        return allPass ? 0 : 2;
    }

    private static Assembly? LoadTestAssembly()
    {
        // Try already-loaded assemblies first.
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AgentEval.Tests");
        if (loaded is not null) return loaded;

        // Try to locate the assembly on disk relative to this binary.
        var thisDir = Path.GetDirectoryName(typeof(BenchAgenticCalibrateCommand).Assembly.Location);
        if (thisDir is null) return null;

        var candidate = Path.Combine(thisDir, "AgentEval.Tests.dll");
        if (File.Exists(candidate))
            return Assembly.LoadFrom(candidate);

        return null;
    }

    private static string BuildMarkdownReport(
        AgentEval.Evals.Agentic.Calibration.CalibrationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Agentic Evaluator Calibration Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine($"Thresholds: accuracy >= {AccuracyThreshold:P0}, Cohen's kappa >= {KappaThreshold:F2}");
        sb.AppendLine();

        foreach (var (category, cr) in report.PerCategory.OrderBy(kv => kv.Key))
        {
            var accOk = cr.Accuracy >= AccuracyThreshold;
            var kappaOk = cr.CohensKappa >= KappaThreshold;
            var badge = accOk && kappaOk ? "PASS" : "FAIL";

            sb.AppendLine($"## {category} [{badge}]");
            sb.AppendLine();
            sb.AppendLine($"| Metric | Value | Threshold | Status |");
            sb.AppendLine($"|--------|-------|-----------|--------|");
            sb.AppendLine($"| Entries evaluated | {cr.EntryCount} | — | — |");
            sb.AppendLine($"| Accuracy | {cr.Accuracy:P1} | >= {AccuracyThreshold:P0} | {(accOk ? "OK" : "BELOW")} |");
            sb.AppendLine($"| Cohen's kappa | {cr.CohensKappa:F3} | >= {KappaThreshold:F2} | {(kappaOk ? "OK" : "BELOW")} |");
            sb.AppendLine($"| Within score range | {cr.WithinScoreRange} / {cr.EntryCount} | — | — |");
            sb.AppendLine($"| Mean score delta | {cr.MeanScoreDelta:+0.000;-0.000;0.000} | — | — |");
            if (cr.EvaluationFailures > 0)
                sb.AppendLine($"| Evaluation failures | {cr.EvaluationFailures} | — | — |");
            if (cr.SkippedUnknownKey > 0)
                sb.AppendLine($"| Skipped (unknown key) | {cr.SkippedUnknownKey} | — | — |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

}
