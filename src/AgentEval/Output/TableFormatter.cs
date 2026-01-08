// Copyright (c) 2025-2026 AgentEval Contributors
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
        writer.WriteLine($"{indent}" + new string('─', 80));
        
        // Header
        writer.WriteLine($"{indent}{{0,-25}} {{1,10}} {{2,10}} {{3,10}} {{4,10}}", 
            "Metric", "Min", "Max", "Mean", "Samples");
        writer.WriteLine($"{indent}" + new string('─', 80));
        
        // Score row
        if (options.ShowScore)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10}} {{2,10}} {{3,10:F1}} {{4,10}}",
                "Score",
                result.Statistics.MinScore,
                result.Statistics.MaxScore,
                result.Statistics.MeanScore,
                result.Statistics.SampleSize);
        }
        
        // Pass Rate
        if (options.ShowPassRate)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10}} {{2,10}} {{3,10:P0}} {{4,10}}",
                "Pass Rate",
                "-",
                "-",
                result.Statistics.PassRate,
                result.Statistics.SampleSize);
        }
        
        // Duration
        if (options.ShowDuration)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}}",
                "Duration (ms)",
                result.DurationStats.Min,
                result.DurationStats.Max,
                result.DurationStats.Mean,
                result.DurationStats.SampleSize);
        }
        
        // TTFT
        if (options.ShowTimeToFirstToken && result.TimeToFirstTokenStats != null)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}}",
                "TTFT (ms)",
                result.TimeToFirstTokenStats.Min,
                result.TimeToFirstTokenStats.Max,
                result.TimeToFirstTokenStats.Mean,
                result.TimeToFirstTokenStats.SampleSize);
        }
        
        // Tokens
        if (options.ShowTokens && result.TotalTokenStats != null && result.TotalTokenStats.Max > 0)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}}",
                "Total Tokens",
                result.TotalTokenStats.Min,
                result.TotalTokenStats.Max,
                result.TotalTokenStats.Mean,
                result.TotalTokenStats.SampleSize);
        }
        
        if (options.ShowPromptTokens && result.PromptTokenStats != null && result.PromptTokenStats.Max > 0)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}}",
                "Prompt Tokens",
                result.PromptTokenStats.Min,
                result.PromptTokenStats.Max,
                result.PromptTokenStats.Mean,
                result.PromptTokenStats.SampleSize);
        }
        
        if (options.ShowCompletionTokens && result.CompletionTokenStats != null && result.CompletionTokenStats.Max > 0)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F0}} {{4,10}}",
                "Completion Tokens",
                result.CompletionTokenStats.Min,
                result.CompletionTokenStats.Max,
                result.CompletionTokenStats.Mean,
                result.CompletionTokenStats.SampleSize);
        }
        
        // Cost
        if (options.ShowCost && result.CostStats != null && result.CostStats.Max > 0)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F6}} {{2,10:F6}} {{3,10:F6}} {{4,10}}",
                "Cost ($)",
                result.CostStats.Min,
                result.CostStats.Max,
                result.CostStats.Mean,
                result.CostStats.SampleSize);
        }
        
        // Tool Calls
        if (options.ShowToolCalls)
        {
            writer.WriteLine($"{indent}{{0,-25}} {{1,10:F0}} {{2,10:F0}} {{3,10:F1}} {{4,10}}",
                "Tool Calls/Run",
                result.ToolCallCountStats.Min,
                result.ToolCallCountStats.Max,
                result.ToolCallCountStats.Mean,
                result.ToolCallCountStats.SampleSize);
        }
        
        // Evaluation Metrics
        if (options.ShowMetrics && result.MetricDistributions.Count > 0)
        {
            foreach (var (name, stats) in result.MetricDistributions)
            {
                writer.WriteLine($"{indent}{{0,-25}} {{1,10:F2}} {{2,10:F2}} {{3,10:F2}} {{4,10}}",
                    name,
                    stats.Min,
                    stats.Max,
                    stats.Mean,
                    stats.SampleSize);
            }
        }
        
        writer.WriteLine($"{indent}" + new string('─', 80));
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
        writer.WriteLine($"{indent}{{0,-20}} {{1}}", "Call Rate:", $"{summary.CallRate:P0} ({summary.RunsWithTool}/{summary.CallCountStats.SampleSize} runs)");
        writer.WriteLine($"{indent}{{0,-20}} {{1}}", "Error Rate:", $"{summary.ErrorRate:P0}");
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
        writer.WriteLine($"{indent}" + new string('═', 100));
        
        // Determine column widths
        int modelColWidth = Math.Max(15, modelResults.Max(m => m.ModelName.Length) + 2);
        
        // Build header dynamically based on options
        var sb = new StringBuilder();
        sb.Append(indent);
        sb.Append("Model".PadRight(modelColWidth));
        
        if (options.ShowPassRate)
        {
            sb.Append(" │ ");
            sb.Append("Pass%".PadLeft(6));
        }
        
        if (options.ShowScore)
        {
            sb.Append(" │ ");
            sb.Append("Score".PadLeft(6));
        }
        
        if (options.ShowDuration)
        {
            sb.Append(" │ ");
            sb.Append("Dur(ms)".PadLeft(8));
        }
        
        if (options.ShowTimeToFirstToken)
        {
            sb.Append(" │ ");
            sb.Append("TTFT".PadLeft(6));
        }
        
        if (options.ShowTokens)
        {
            sb.Append(" │ ");
            sb.Append("Tokens".PadLeft(7));
        }
        
        if (options.ShowCost)
        {
            sb.Append(" │ ");
            sb.Append("Cost($)".PadLeft(9));
        }
        
        if (options.ShowToolSuccess)
        {
            sb.Append(" │ ");
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
            sb.Append(" │ ");
            sb.Append(TruncateMetricName(metric).PadLeft(8));
        }
        
        writer.WriteLine(sb.ToString());
        writer.WriteLine($"{indent}" + new string('─', 100));
        
        // Data rows
        foreach (var (modelName, result) in modelResults)
        {
            sb.Clear();
            sb.Append(indent);
            sb.Append(modelName.PadRight(modelColWidth));
            
            if (options.ShowPassRate)
            {
                sb.Append(" │ ");
                sb.Append($"{result.Statistics.PassRate:P0}".PadLeft(6));
            }
            
            if (options.ShowScore)
            {
                sb.Append(" │ ");
                sb.Append($"{result.Statistics.MeanScore:F1}".PadLeft(6));
            }
            
            if (options.ShowDuration)
            {
                sb.Append(" │ ");
                sb.Append($"{result.DurationStats.Mean:F0}".PadLeft(8));
            }
            
            if (options.ShowTimeToFirstToken)
            {
                sb.Append(" │ ");
                var ttft = result.TimeToFirstTokenStats?.Mean;
                sb.Append(ttft.HasValue ? $"{ttft:F0}".PadLeft(6) : "N/A".PadLeft(6));
            }
            
            if (options.ShowTokens)
            {
                sb.Append(" │ ");
                var tokens = result.TotalTokenStats?.Mean ?? 0;
                sb.Append(tokens > 0 ? $"{tokens:F0}".PadLeft(7) : "N/A".PadLeft(7));
            }
            
            if (options.ShowCost)
            {
                sb.Append(" │ ");
                var cost = result.CostStats?.Mean ?? 0;
                sb.Append(cost > 0 ? $"{cost:F5}".PadLeft(9) : "N/A".PadLeft(9));
            }
            
            if (options.ShowToolSuccess)
            {
                sb.Append(" │ ");
                var toolSuccess = GetToolSuccessRate(result);
                sb.Append(toolSuccess.HasValue ? $"{toolSuccess:P0}".PadLeft(7) : "N/A".PadLeft(7));
            }
            
            // Metric values
            foreach (var metric in allMetrics)
            {
                sb.Append(" │ ");
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
        
        writer.WriteLine($"{indent}" + new string('═', 100));
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
        
        var range = result.DurationStats.Max - result.DurationStats.Min;
        var variance = result.DurationStats.Mean > 0 
            ? (range / result.DurationStats.Mean * 100) 
            : 0;
        var icon3 = options.UseColors ? "📏" : "[RANGE]";
        writer.WriteLine($"{indent}{icon3} Range:   {range:F0}ms ({variance:F1}% variance)");
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
}
