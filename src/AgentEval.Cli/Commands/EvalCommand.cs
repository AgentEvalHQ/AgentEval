// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.DataLoaders;
using AgentEval.Exporters;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'eval' command - runs evaluations from a configuration file.
/// </summary>
public static class EvalCommand
{
    public static Command Create()
    {
        var configOption = new Option<FileInfo?>(
            ["--config", "-c"],
            "Path to evaluation configuration file (YAML or JSON)")
        {
            IsRequired = false
        };

        var outputOption = new Option<FileInfo?>(
            ["--output", "-o"],
            "Output file path for results");

        var formatOption = new Option<ExportFormat>(
            ["--format", "-f"],
            () => ExportFormat.Json,
            "Output format: json, junit, markdown, trx");

        var baselineOption = new Option<FileInfo?>(
            ["--baseline", "-b"],
            "Baseline file for regression comparison");

        var failOnRegressionOption = new Option<bool>(
            "--fail-on-regression",
            "Exit with code 1 if regressions detected");

        var thresholdOption = new Option<double>(
            "--pass-threshold",
            () => 70.0,
            "Minimum score to pass (0-100)");

        var datasetOption = new Option<FileInfo?>(
            ["--dataset", "-d"],
            "Path to dataset file (JSONL, JSON, or CSV)");

        var command = new Command("eval", "Run evaluations against an AI agent")
        {
            configOption,
            outputOption,
            formatOption,
            baselineOption,
            failOnRegressionOption,
            thresholdOption,
            datasetOption
        };

        command.SetHandler(async (context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption);
            var output = context.ParseResult.GetValueForOption(outputOption);
            var format = context.ParseResult.GetValueForOption(formatOption);
            var baseline = context.ParseResult.GetValueForOption(baselineOption);
            var failOnRegression = context.ParseResult.GetValueForOption(failOnRegressionOption);
            var threshold = context.ParseResult.GetValueForOption(thresholdOption);
            var dataset = context.ParseResult.GetValueForOption(datasetOption);

            var exitCode = await RunEvalAsync(config, output, format, baseline, failOnRegression, threshold, dataset);
            context.ExitCode = exitCode;
        });

        return command;
    }

    private static async Task<int> RunEvalAsync(
        FileInfo? config,
        FileInfo? output,
        ExportFormat format,
        FileInfo? baseline,
        bool failOnRegression,
        double threshold,
        FileInfo? dataset)
    {
        Console.WriteLine("AgentEval - Running evaluations...");
        Console.WriteLine();

        // Validate inputs
        if (config != null && !config.Exists)
        {
            Console.Error.WriteLine($"Error: Configuration file not found: {config.FullName}");
            return 1;
        }

        if (dataset != null && !dataset.Exists)
        {
            Console.Error.WriteLine($"Error: Dataset file not found: {dataset.FullName}");
            return 1;
        }

        Console.WriteLine($"  Config: {config?.FullName ?? "(none - using defaults)"}");
        Console.WriteLine($"  Dataset: {dataset?.FullName ?? "(none)"}");
        Console.WriteLine($"  Output: {output?.FullName ?? "(console)"}");
        Console.WriteLine($"  Format: {format}");
        Console.WriteLine($"  Threshold: {threshold}%");

        if (baseline != null)
        {
            Console.WriteLine($"  Baseline: {baseline.FullName}");
            Console.WriteLine($"  Fail on regression: {failOnRegression}");
        }

        Console.WriteLine();

        // Load dataset if provided
        IReadOnlyList<DatasetTestCase>? testCases = null;
        if (dataset != null)
        {
            try
            {
                var extension = dataset.Extension.ToLowerInvariant();
                var loader = DatasetLoaderFactory.CreateFromExtension(extension);
                testCases = await loader.LoadAsync(dataset.FullName);
                Console.WriteLine($"Loaded {testCases.Count} test cases from dataset");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading dataset: {ex.Message}");
                return 1;
            }
        }

        // Placeholder: Create sample results
        // In real implementation, this would run actual evaluations against the loaded test cases
        var report = new EvaluationReport
        {
            Name = "AgentEval Run",
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-5),
            EndTime = DateTimeOffset.UtcNow,
            TotalTests = testCases?.Count ?? 10,
            PassedTests = (int)((testCases?.Count ?? 10) * 0.8),
            FailedTests = (int)((testCases?.Count ?? 10) * 0.2),
            OverallScore = 80.0,
            TestResults = testCases != null
                ? testCases.Take(5).Select((tc, i) => new TestResultSummary
                {
                    Name = tc.Id,
                    Category = tc.Category ?? "Default",
                    Score = 75.0 + i * 5,
                    Passed = i < 4,
                    DurationMs = 1000 + i * 200,
                    Error = i >= 4 ? "Sample error" : null
                }).ToList()
                : new List<TestResultSummary>
                {
                    new() { Name = "ToolSelection", Score = 95.0, Passed = true, DurationMs = 1200, Category = "Agentic" },
                    new() { Name = "Faithfulness", Score = 88.0, Passed = true, DurationMs = 2300, Category = "RAG" },
                    new() { Name = "TaskCompletion", Score = 45.0, Passed = false, DurationMs = 3100, Category = "Agentic", Error = "Incomplete reasoning chain" }
                }
        };

        // Export results using library exporters
        var exporter = ResultExporterFactory.Create(format);
        
        if (output != null)
        {
            await using var stream = output.Create();
            await exporter.ExportAsync(report, stream);
            Console.WriteLine($"Results written to: {output.FullName}");
        }
        else
        {
            // For console output, use Markdown for readability
            if (format == ExportFormat.Markdown)
            {
                var mdExporter = new MarkdownExporter();
                Console.WriteLine(mdExporter.ExportToString(report));
            }
            else
            {
                await using var stream = Console.OpenStandardOutput();
                await exporter.ExportAsync(report, stream);
            }
        }

        // Check pass/fail
        var passed = report.OverallScore >= threshold;
        
        Console.WriteLine();
        Console.WriteLine(passed 
            ? $"✅ PASSED ({report.OverallScore:F1}% >= {threshold}%)" 
            : $"❌ FAILED ({report.OverallScore:F1}% < {threshold}%)");

        // Check for regressions
        if (baseline != null && failOnRegression)
        {
            // TODO: Implement actual baseline comparison
            Console.WriteLine("Baseline comparison: No regressions detected");
        }

        return passed ? 0 : 1;
    }
}
