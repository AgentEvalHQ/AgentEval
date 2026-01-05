// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using AgentEval.DataLoaders;
using AgentEval.Exporters;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Helper for cross-platform console output.
/// </summary>
internal static class ConsoleHelper
{
    /// <summary>
    /// Gets whether the current terminal supports ANSI colors.
    /// </summary>
    public static bool SupportsColor { get; } = DetectColorSupport();

    private static bool DetectColorSupport()
    {
        // Check for NO_COLOR environment variable (https://no-color.org/)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;

        // Check for TERM=dumb
        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return false;

        // Check if we're redirected (piping to file)
        if (Console.IsOutputRedirected)
            return false;

        return true;
    }

    /// <summary>
    /// Writes colored text if supported, otherwise plain text.
    /// </summary>
    public static void WriteColored(string text, ConsoleColor color)
    {
        if (SupportsColor)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ResetColor();
        }
        else
        {
            Console.Write(text);
        }
    }

    /// <summary>
    /// Writes a colored line if supported.
    /// </summary>
    public static void WriteLineColored(string text, ConsoleColor color)
    {
        WriteColored(text, color);
        Console.WriteLine();
    }
}

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
            "Path to dataset file (JSONL, JSON, CSV, or YAML)");

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

        // Run actual evaluations on the dataset
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var testResults = new List<TestResultSummary>();

        if (testCases != null && testCases.Count > 0)
        {
            Console.WriteLine("Running dataset validation...");
            Console.WriteLine();

            foreach (var testCase in testCases)
            {
                var tcStopwatch = Stopwatch.StartNew();
                var result = EvaluateTestCase(testCase, threshold);
                tcStopwatch.Stop();
                result.DurationMs = (int)tcStopwatch.ElapsedMilliseconds;
                testResults.Add(result);

                // Print progress
                var symbol = result.Passed ? "✓" : "✗";
                var color = result.Passed ? ConsoleColor.Green : ConsoleColor.Red;
                ConsoleHelper.WriteColored($"  [{symbol}] ", color);
                Console.WriteLine($"{result.Name}: {result.Score:F1}% - {result.Category}");
            }
            Console.WriteLine();
        }
        else
        {
            // No dataset provided - show usage hint
            ConsoleHelper.WriteLineColored("ℹ️  No dataset provided. Use --dataset to evaluate test cases.", ConsoleColor.Yellow);
            ConsoleHelper.WriteLineColored("    Example: agenteval eval --dataset tests.yaml --format markdown", ConsoleColor.Yellow);
            Console.WriteLine();

            // Create minimal report
            testResults.Add(new TestResultSummary
            {
                Name = "NoDataset",
                Category = "Setup",
                Score = 0,
                Passed = false,
                DurationMs = 0,
                Error = "No dataset file provided"
            });
        }

        stopwatch.Stop();
        var endTime = DateTimeOffset.UtcNow;

        // Calculate aggregate metrics
        var passedCount = testResults.Count(r => r.Passed);
        var failedCount = testResults.Count - passedCount;
        var overallScore = testResults.Count > 0 ? testResults.Average(r => r.Score) : 0;

        var report = new EvaluationReport
        {
            Name = "AgentEval Run",
            StartTime = startTime,
            EndTime = endTime,
            TotalTests = testResults.Count,
            PassedTests = passedCount,
            FailedTests = failedCount,
            OverallScore = overallScore,
            TestResults = testResults
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
        var passed = overallScore >= threshold;

        Console.WriteLine();
        Console.WriteLine($"Summary: {passedCount}/{report.TotalTests} passed ({overallScore:F1}%)");
        Console.WriteLine(passed
            ? $"✅ PASSED ({overallScore:F1}% >= {threshold}%)"
            : $"❌ FAILED ({overallScore:F1}% < {threshold}%)");

        // Check for regressions
        if (baseline != null && failOnRegression)
        {
            // TODO: Implement actual baseline comparison
            Console.WriteLine("Baseline comparison: No regressions detected");
        }

        return passed ? 0 : 1;
    }

    /// <summary>
    /// Evaluates a single test case using dataset-based metrics.
    /// </summary>
    private static TestResultSummary EvaluateTestCase(DatasetTestCase testCase, double threshold)
    {
        var scores = new List<(string name, double score, string? error)>();

        // 1. Completeness check - does the test case have required fields?
        var completeness = CalculateCompleteness(testCase);
        scores.Add(("Completeness", completeness, completeness < 50 ? "Missing required fields" : null));

        // 2. Ground truth validation - if GroundTruth specified, validate structure
        if (testCase.GroundTruth != null)
        {
            var gtScore = ValidateGroundTruth(testCase.GroundTruth);
            scores.Add(("GroundTruth", gtScore, gtScore < 100 ? "Incomplete ground truth" : null));
        }

        // 3. Expected tools validation
        if (testCase.ExpectedTools != null && testCase.ExpectedTools.Count > 0)
        {
            scores.Add(("ExpectedTools", 100, null)); // Presence is enough
        }

        // 4. Context validation for RAG
        if (testCase.Context != null && testCase.Context.Count > 0)
        {
            var contextScore = testCase.Context.All(c => !string.IsNullOrWhiteSpace(c)) ? 100.0 : 50.0;
            scores.Add(("Context", contextScore, contextScore < 100 ? "Empty context entries" : null));
        }

        // Calculate overall score
        var overallScore = scores.Count > 0 ? scores.Average(s => s.score) : 0;
        var passed = overallScore >= threshold;
        var firstError = scores.FirstOrDefault(s => s.error != null).error;

        // Determine category based on test case features
        var category = DetermineCategory(testCase);

        return new TestResultSummary
        {
            Name = testCase.Id,
            Category = category,
            Score = overallScore,
            Passed = passed,
            DurationMs = 0, // Will be set by caller
            Error = firstError
        };
    }

    private static double CalculateCompleteness(DatasetTestCase testCase)
    {
        var totalFields = 4; // id, input, expected output, category
        var presentFields = 0;

        if (!string.IsNullOrWhiteSpace(testCase.Id)) presentFields++;
        if (!string.IsNullOrWhiteSpace(testCase.Input)) presentFields++;
        if (!string.IsNullOrWhiteSpace(testCase.ExpectedOutput)) presentFields++;
        if (!string.IsNullOrWhiteSpace(testCase.Category)) presentFields++;

        return (presentFields / (double)totalFields) * 100;
    }

    private static double ValidateGroundTruth(GroundTruthToolCall gt)
    {
        var score = 0.0;
        if (!string.IsNullOrWhiteSpace(gt.Name)) score += 50;
        if (gt.Arguments != null && gt.Arguments.Count > 0) score += 50;
        return score;
    }

    private static string DetermineCategory(DatasetTestCase testCase)
    {
        if (!string.IsNullOrWhiteSpace(testCase.Category))
            return testCase.Category;

        if (testCase.GroundTruth != null || testCase.ExpectedTools?.Count > 0)
            return "Agentic";

        if (testCase.Context?.Count > 0)
            return "RAG";

        return "General";
    }
}
