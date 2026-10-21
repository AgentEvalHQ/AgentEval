// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Comparison;

namespace AgentEval.Output;

/// <summary>
/// Formats stochastic and comparison results in table format.
/// Similar to ai-rag-chat-evaluator output style.
/// </summary>
public static class TableFormatter
{
    /// <summary>
    /// Prints a stochastic result as a compact table.
    /// </summary>
    /// <param name="result">The stochastic result to print.</param>
    /// <param name="title">Optional title for the table.</param>
    /// <param name="options">Output options controlling which columns are displayed.</param>
    public static void PrintTable(
        StochasticResult result, 
        string? title = null, 
        OutputOptions? options = null)
    {
        options ??= OutputOptions.Default;
        var writer = options.Writer;
        var indent = options.Indent;
        
        if (!string.IsNullOrEmpty(title))
        {
            writer.WriteLine($"\n{indent}📊 {title}");
        }
        writer.WriteLine($"{indent}" + new string('─', 95));
        
        // Header
        writer.WriteLine($"{indent}{{0,-25}} {{1,10}} {{2,10}} {{3,10}} {{4,10}} {{5,10}}", 
            "Metric", "Min", "Max", "Mean", "Variance", "Samples");
        writer.WriteLine($"{indent}" + new string('─', 95));
        
        // The score and pass-rate rows are over the measured runs (Samples); a run that errored or got no verdict is not
        // one, and with none measured there is nothing to show but n/a.
        var measured = result.Statistics.SampleSize > 0;

        // Score row
        if (options.ShowScore)
        {
            if (measured)
            {
                var scoreVariance = CalculateVariancePercent(result.Statistics.MinScore, result.Statistics.MaxScore, result.Statistics.MeanScore);
                writer.WriteLine($"{indent}{{0,-25}} {{1,10}} {{2,10}} {{3,10:F1}} {{4,10}} {{5,10}}",
                    "Score",
                    result.Statistics.MinScore,
                    result.Statistics.MaxScore,
                    result.Statistics.MeanScore,
                    FormatVarianceWithColor(scoreVariance, options.UseColors),
                    result.Statistics.SampleSize);
            }
            else
            {
                WriteNotMeasuredRow(writer, indent, "Score");
            }
        }

        // Pass Rate: over every run, so its sample is every run, not the measured ones the score row counts.
        if (options.ShowPassRate)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10}} {{2,10}} {{3,10}} {{4,10}} {{5,10}}",
                "Pass Rate",
                "-",
                "-",
                PassRateOverEveryRun(result),
                "-",
                result.IndividualResults.Count);
        }

        if (result.NotMeasuredCount > 0)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1}}",
                "Not measured",
                $"{result.NotMeasuredCount} of {result.IndividualResults.Count} runs (errored, or the judge produced no verdict)");
        }

        // Duration
        if (options.ShowDuration)
        {
            if (result.DurationStats is { } durationStats)
            {
                var durationVariance = CalculateVariancePercent(durationStats.Min, durationStats.Max, durationStats.Mean);
                writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}} {{5,10}}",
                    "Duration (ms)",
                    durationStats.Min,
                    durationStats.Max,
                    durationStats.Mean,
                    FormatVarianceWithColor(durationVariance, options.UseColors),
                    durationStats.SampleSize);
            }
            else
            {
                // No run recorded a duration: not a 0 ms run.
                WriteNotMeasuredRow(writer, indent, "Duration (ms)");
            }
        }
        
        // TTFT
        if (options.ShowTimeToFirstToken && result.TimeToFirstTokenStats != null)
        {
            var ttftVariance = CalculateVariancePercent(result.TimeToFirstTokenStats.Min, result.TimeToFirstTokenStats.Max, result.TimeToFirstTokenStats.Mean);
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}} {{5,10}}",
                "TTFT (ms)",
                result.TimeToFirstTokenStats.Min,
                result.TimeToFirstTokenStats.Max,
                result.TimeToFirstTokenStats.Mean,
                FormatVarianceWithColor(ttftVariance, options.UseColors),
                result.TimeToFirstTokenStats.SampleSize);
        }
        
        // Tokens and cost: a row when any run reported them, a recorded 0 included (it is a value, not a gap); no row
        // when none did. The comparison table follows the same rule.
        if (options.ShowTokens && result.TotalTokenStats != null)
        {
            var tokenVariance = CalculateVariancePercent(result.TotalTokenStats.Min, result.TotalTokenStats.Max, result.TotalTokenStats.Mean);
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}} {{5,10}}",
                "Total Tokens",
                result.TotalTokenStats.Min,
                result.TotalTokenStats.Max,
                result.TotalTokenStats.Mean,
                FormatVarianceWithColor(tokenVariance, options.UseColors),
                result.TotalTokenStats.SampleSize);
        }
        
        if (options.ShowPromptTokens && result.PromptTokenStats != null)
        {
            var promptVariance = CalculateVariancePercent(result.PromptTokenStats.Min, result.PromptTokenStats.Max, result.PromptTokenStats.Mean);
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}} {{5,10}}",
                "Prompt Tokens",
                result.PromptTokenStats.Min,
                result.PromptTokenStats.Max,
                result.PromptTokenStats.Mean,
                FormatVarianceWithColor(promptVariance, options.UseColors),
                result.PromptTokenStats.SampleSize);
        }
        
        if (options.ShowCompletionTokens && result.CompletionTokenStats != null)
        {
            var completionVariance = CalculateVariancePercent(result.CompletionTokenStats.Min, result.CompletionTokenStats.Max, result.CompletionTokenStats.Mean);
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}} {{5,10}}",
                "Completion Tokens",
                result.CompletionTokenStats.Min,
                result.CompletionTokenStats.Max,
                result.CompletionTokenStats.Mean,
                FormatVarianceWithColor(completionVariance, options.UseColors),
                result.CompletionTokenStats.SampleSize);
        }
        
        // Cost
        if (options.ShowCost && result.CostStats != null)
        {
            var costVariance = CalculateVariancePercent(result.CostStats.Min, result.CostStats.Max, result.CostStats.Mean);
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F6}} {{2,10:F6}} {{3,10:F6}} {{4,10}} {{5,10}}",
                "Cost ($)",
                result.CostStats.Min,
                result.CostStats.Max,
                result.CostStats.Mean,
                FormatVarianceWithColor(costVariance, options.UseColors),
                result.CostStats.SampleSize);
        }
        
        // Tool Calls
        if (options.ShowToolCalls)
        {
            if (result.ToolCallCountStats is { } toolCallStats)
            {
                var toolCallVariance = CalculateVariancePercent(toolCallStats.Min, toolCallStats.Max, toolCallStats.Mean);
                writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F1}} {{4,10}} {{5,10}}",
                    "Tool Calls/Run",
                    toolCallStats.Min,
                    toolCallStats.Max,
                    toolCallStats.Mean,
                    FormatVarianceWithColor(toolCallVariance, options.UseColors),
                    toolCallStats.SampleSize);
            }
            else
            {
                // No run tracked tool usage: not a run with zero calls.
                WriteNotMeasuredRow(writer, indent, "Tool Calls/Run");
            }
        }
        
        // Evaluation Metrics
        if (options.ShowMetrics && result.MetricDistributions.Count > 0)
        {
            foreach (var (name, stats) in result.MetricDistributions)
            {
                var metricVariance = CalculateVariancePercent(stats.Min, stats.Max, stats.Mean);
                writer.WriteLine($"{indent}{{0,-25}} {{1,10:F2}} {{2,10:F2}} {{3,10:F2}} {{4,10}} {{5,10}}",
                    name,
                    stats.Min,
                    stats.Max,
                    stats.Mean,
                    FormatVarianceWithColor(metricVariance, options.UseColors),
                    stats.SampleSize);
            }
        }
        
        writer.WriteLine($"{indent}" + new string('─', 95));
    }

    /// <summary>
    /// Prints tool usage summary for a specific tool.
    /// </summary>
    public static void PrintToolSummary(
        ToolUsageSummary summary, 
        OutputOptions? options = null)
    {
        options ??= OutputOptions.Default;
        var writer = options.Writer;
        var indent = options.Indent;
        
        writer.WriteLine($"\n{indent}🔧 Tool: {summary.ToolName}");
        writer.WriteLine($"{indent}" + new string('─', 50));
        // The rates are over the runs that tracked tool usage; with none there is no rate, not a 0% (0/N runs).
        writer.WriteLine($"{indent}{{0,-20}} {{1}}", "Call Rate:",
            summary.CallCountStats is { } calls && summary.CallRate is { } callRate
                ? $"{callRate * 100:F0}% ({summary.RunsWithTool}/{calls.SampleSize} runs that tracked tools)"
                : "N/A (no run tracked tool usage)");
        writer.WriteLine($"{indent}{{0,-20}} {{1}}", "Error Rate:",
            summary.ErrorRate is { } errorRate ? $"{errorRate * 100:F0}%" : "N/A (no run tracked tool usage)");
        writer.WriteLine($"{indent}{{0,-20}} {{1}}", "Total Calls:", summary.TotalCalls);
        
        var statusIcon = options.UseColors 
            ? (summary.AllCallsSucceeded ? "✅" : "⚠️") 
            : (summary.AllCallsSucceeded ? "[OK]" : "[WARN]");
        var statusText = summary.TotalCalls == 0
            ? "N/A (No calls made)"
            : summary.AllCallsSucceeded 
                ? $"{statusIcon} 100% Success" 
                : $"{statusIcon} {summary.RunsWithErrors} errors";
        writer.WriteLine($"{indent}{{0,-20}} {{1}}", "Status:", statusText);
        writer.WriteLine($"{indent}" + new string('─', 50));
    }

    /// <summary>
    /// Prints model comparison results in table format.
    /// Columns are dynamically built based on available data and OutputOptions.
    /// </summary>
    public static void PrintComparisonTable(
        IReadOnlyList<(string ModelName, StochasticResult Result)> modelResults,
        OutputOptions? options = null)
    {
        if (modelResults.Count == 0) return;
        
        options ??= OutputOptions.Default;
        var writer = options.Writer;
        var indent = options.Indent;
        
        writer.WriteLine($"\n{indent}📊 Model Comparison Results");
        writer.WriteLine($"{indent}" + new string('=', 100));
        
        // Determine column widths
        int modelColWidth = Math.Max(15, modelResults.Max(m => m.ModelName.Length) + 2);
        
        // Build header dynamically based on options
        var sb = new StringBuilder();
        sb.Append(indent);
        sb.Append("Model".PadRight(modelColWidth));
        
        if (options.ShowPassRate)
        {
            sb.Append(" | ");
            sb.Append("Pass%".PadLeft(6));
        }
        
        if (options.ShowScore)
        {
            sb.Append(" | ");
            sb.Append("Score".PadLeft(6));
        }
        
        if (options.ShowDuration)
        {
            sb.Append(" | ");
            sb.Append("Dur(ms)".PadLeft(8));
            sb.Append(" | ");
            sb.Append("DurVar".PadLeft(7));
        }
        
        if (options.ShowTimeToFirstToken)
        {
            sb.Append(" | ");
            sb.Append("TTFT".PadLeft(6));
        }
        
        if (options.ShowTokens)
        {
            sb.Append(" | ");
            sb.Append("Tokens".PadLeft(7));
            sb.Append(" | ");
            sb.Append("TokVar".PadLeft(7));
        }
        
        if (options.ShowCost)
        {
            sb.Append(" | ");
            sb.Append("Cost($)".PadLeft(9));
            sb.Append(" | ");
            sb.Append("CostVar".PadLeft(8));
        }
        
        if (options.ShowToolSuccess)
        {
            sb.Append(" | ");
            sb.Append("ToolOK%".PadLeft(7));
        }
        
        // Add metric columns if present
        var allMetrics = options.ShowMetrics
            ? modelResults
                .Where(m => m.Result.MetricDistributions.Count > 0)
                .SelectMany(m => m.Result.MetricDistributions.Keys)
                .Distinct()
                .OrderBy(m => m)
                .ToList()
            : new List<string>();
        
        foreach (var metric in allMetrics)
        {
            sb.Append(" | ");
            sb.Append(TruncateMetricName(metric).PadLeft(8));
        }
        
        writer.WriteLine(sb.ToString());
        writer.WriteLine($"{indent}" + new string('-', 100));
        
        // Data rows
        foreach (var (modelName, result) in modelResults)
        {
            sb.Clear();
            sb.Append(indent);
            sb.Append(modelName.PadRight(modelColWidth));
            
            // Over the measured runs; a model none of whose runs was measured has no pass rate or score to show.
            var measured = result.Statistics.SampleSize > 0;
            if (options.ShowPassRate)
            {
                sb.Append(" | ");
                sb.Append(PassRateOverEveryRun(result).PadLeft(6));
            }

            if (options.ShowScore)
            {
                sb.Append(" | ");
                sb.Append((measured ? $"{result.Statistics.MeanScore:F1}" : "N/A").PadLeft(6));
            }

            if (options.ShowDuration)
            {
                // No run recorded a duration: N/A, not a 0 ms mean.
                var durationStats = result.DurationStats;
                sb.Append(" | ");
                sb.Append((durationStats is { } timed ? $"{timed.Mean:F0}" : "N/A").PadLeft(8));
                sb.Append(" | ");
                sb.Append((durationStats is { } spread
                    ? FormatVarianceWithColor(CalculateVariancePercent(spread.Min, spread.Max, spread.Mean), options.UseColors)
                    : "N/A").PadLeft(7));
            }
            
            if (options.ShowTimeToFirstToken)
            {
                sb.Append(" | ");
                var ttft = result.TimeToFirstTokenStats?.Mean;
                sb.Append(ttft.HasValue ? $"{ttft:F0}".PadLeft(6) : "N/A".PadLeft(6));
            }
            
            if (options.ShowTokens)
            {
                // N/A only when no run reported tokens: a true 0 is a value, not a gap.
                sb.Append(" | ");
                var tokens = result.TotalTokenStats?.Mean ?? 0;
                sb.Append(result.TotalTokenStats != null ? $"{tokens:F0}".PadLeft(7) : "N/A".PadLeft(7));
                sb.Append(" | ");
                if (result.TotalTokenStats != null)
                {
                    var tokVariance = CalculateVariancePercent(result.TotalTokenStats.Min, result.TotalTokenStats.Max, result.TotalTokenStats.Mean);
                    sb.Append(FormatVarianceWithColor(tokVariance, options.UseColors).PadLeft(7));
                }
                else
                {
                    sb.Append("N/A".PadLeft(7));
                }
            }
            
            if (options.ShowCost)
            {
                // N/A only when no run was priced: a true $0 is a value, not a gap.
                sb.Append(" | ");
                var cost = result.CostStats?.Mean ?? 0;
                sb.Append(result.CostStats != null ? $"{cost:F5}".PadLeft(9) : "N/A".PadLeft(9));
                sb.Append(" | ");
                if (result.CostStats != null)
                {
                    var costVariance = CalculateVariancePercent(result.CostStats.Min, result.CostStats.Max, result.CostStats.Mean);
                    sb.Append(FormatVarianceWithColor(costVariance, options.UseColors).PadLeft(8));
                }
                else
                {
                    sb.Append("N/A".PadLeft(8));
                }
            }
            
            if (options.ShowToolSuccess)
            {
                sb.Append(" | ");
                var toolSuccess = GetToolSuccessRate(result);
                sb.Append(toolSuccess.HasValue ? $"{toolSuccess.Value * 100:F0}%".PadLeft(7) : "N/A".PadLeft(7));
            }
            
            // Metric values
            foreach (var metric in allMetrics)
            {
                sb.Append(" | ");
                if (result.MetricDistributions.TryGetValue(metric, out var stats))
                {
                    sb.Append($"{stats.Mean:F2}".PadLeft(8));
                }
                else
                {
                    sb.Append("N/A".PadLeft(8));
                }
            }
            
            writer.WriteLine(sb.ToString());
        }
        
        writer.WriteLine($"{indent}" + new string('=', 100));
    }

    /// <summary>
    /// Prints a summary showing fastest and slowest runs.
    /// </summary>
    public static void PrintPerformanceSummary(
        StochasticResult result, 
        OutputOptions? options = null)
    {
        options ??= OutputOptions.Default;
        var writer = options.Writer;
        var indent = options.Indent;
        
        writer.WriteLine($"\n{indent}⚡ Performance Summary");
        writer.WriteLine($"{indent}" + new string('─', 50));
        
        if (result.FastestRun != null)
        {
            var icon = options.UseColors ? "🏃" : "[FAST]";
            writer.WriteLine($"{indent}{icon} Fastest: {result.FastestRun.Value.Duration.TotalMilliseconds:F0}ms");
        }
        
        if (result.SlowestRun != null)
        {
            var icon = options.UseColors ? "🐢" : "[SLOW]";
            writer.WriteLine($"{indent}{icon} Slowest: {result.SlowestRun.Value.Duration.TotalMilliseconds:F0}ms");
        }
        
        var icon3 = options.UseColors ? "📏" : "[RANGE]";
        if (result.DurationStats is { } durationStats)
        {
            var range = durationStats.Max - durationStats.Min;
            var variance = durationStats.Mean > 0
                ? (range / durationStats.Mean * 100)
                : 0;
            writer.WriteLine($"{indent}{icon3} Range:   {range:F0}ms ({variance:F1}% variance)");
        }
        else
        {
            writer.WriteLine($"{indent}{icon3} Range:   N/A (no run recorded a duration)");
        }
        writer.WriteLine($"{indent}" + new string('─', 50));
    }

    /// <summary>
    /// Generates the table as a string instead of printing to console.
    /// </summary>
    public static string ToTableString(
        StochasticResult result, 
        string? title = null, 
        OutputOptions? options = null)
    {
        using var sw = new StringWriter();
        options = (options ?? OutputOptions.Default).With(o => o.Writer = sw);
        PrintTable(result, title, options);
        return sw.ToString();
    }

    /// <summary>
    /// Generates comparison table as a string.
    /// </summary>
    public static string ToComparisonTableString(
        IReadOnlyList<(string ModelName, StochasticResult Result)> modelResults,
        OutputOptions? options = null)
    {
        using var sw = new StringWriter();
        options = (options ?? OutputOptions.Default).With(o => o.Writer = sw);
        PrintComparisonTable(modelResults, options);
        return sw.ToString();
    }

    private static string TruncateMetricName(string name)
    {
        return name switch
        {
            "Faithfulness" => "Faith",
            "Relevance" => "Relev",
            "Coherence" => "Coher",
            "Groundedness" => "Ground",
            "ContextPrecision" => "CtxPrec",
            "ContextRecall" => "CtxRecl",
            "AnswerCorrectness" => "AnsCorr",
            _ when name.Length > 8 => name[..8],
            _ => name
        };
    }

    private static double? GetToolSuccessRate(StochasticResult result)
    {
        var runsWithTools = result.IndividualResults
            .Where(r => r.ToolUsage != null && r.ToolUsage.Count > 0)
            .ToList();
        
        if (runsWithTools.Count == 0) return null;
        
        var runsWithoutErrors = runsWithTools.Count(r => !r.ToolUsage!.Calls.Any(c => c.HasError));
        return (double)runsWithoutErrors / runsWithTools.Count;
    }

    // A metric row with nothing to describe: N/A in every column and 0 samples, never a row of zeros.
    private static void WriteNotMeasuredRow(TextWriter writer, string indent, string metric) =>
        writer.WriteLine($"{indent}{{0,-25}} {{1,10}} {{2,10}} {{3,10}} {{4,10}} {{5,10}}",
            metric, "N/A", "N/A", "N/A", "-", 0);

    private static string CalculateVariancePercent(double min, double max, double mean)
    {
        if (mean == 0 || min == max) return "0.0%";
        var range = max - min;
        var variance = (range / mean) * 100;
        return $"{variance:F1}%";
    }

    private static string FormatVarianceWithColor(string varianceString, bool useColors)
    {
        if (!useColors || varianceString == "0.0%" || varianceString == "-")
        {
            return varianceString;
        }

        // Extract numeric value from "XX.X%" format
        var numericPart = varianceString.TrimEnd('%');
        if (!double.TryParse(numericPart, out var variance))
        {
            return varianceString;
        }

        // Color-code based on thresholds:
        // > 75% = Red (high variance, less consistent)
        // > 50% = Yellow (moderate variance)
        // <= 50% = Green (good consistency)
        if (variance > 75)
        {
            return $"\u001b[91m{varianceString}\u001b[0m"; // Bright red
        }
        else if (variance > 50)
        {
            return $"\u001b[93m{varianceString}\u001b[0m"; // Bright yellow
        }
        else
        {
            return $"\u001b[92m{varianceString}\u001b[0m"; // Bright green
        }
    }

    // Pass rate over every run, a crashed or unjudged one included: it counts against reliability, as in
    // ModelResult.PassRate (D-15). Scores stay over the measured runs.
    private static string PassRateOverEveryRun(StochasticResult result) =>
        result.IndividualResults.Count > 0
            ? $"{(double)result.PassedCount / result.IndividualResults.Count * 100:F0}%"
            : "N/A";
}
