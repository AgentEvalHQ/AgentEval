// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;

namespace AgentEval.Output;

/// <summary>
/// Extension methods for StochasticResult to enable fluent table output.
/// </summary>
public static class StochasticResultExtensions
{
    /// <summary>
    /// Prints the stochastic result as a formatted table to the console.
    /// </summary>
    /// <param name="result">The stochastic result to print.</param>
    /// <param name="title">Optional title for the table.</param>
    /// <param name="options">Output formatting options.</param>
    /// <returns>The same result for fluent chaining.</returns>
    public static StochasticResult PrintTable(
        this StochasticResult result, 
        string? title = null, 
        OutputOptions? options = null)
    {
        TableFormatter.PrintTable(result, title, options);
        return result;
    }

    /// <summary>
    /// Prints the stochastic result summary (pass/fail) to the console.
    /// </summary>
    /// <param name="result">The stochastic result to print.</param>
    /// <param name="options">Output formatting options.</param>
    /// <returns>The same result for fluent chaining.</returns>
    public static StochasticResult PrintSummary(
        this StochasticResult result,
        OutputOptions? options = null)
    {
        options ??= OutputOptions.Default;
        options.Writer.WriteLine($"{options.Indent}{result.Summary}");
        return result;
    }

    /// <summary>
    /// Prints the performance summary (fastest/slowest runs) to the console.
    /// </summary>
    /// <param name="result">The stochastic result to print.</param>
    /// <param name="options">Output formatting options.</param>
    /// <returns>The same result for fluent chaining.</returns>
    public static StochasticResult PrintPerformanceSummary(
        this StochasticResult result,
        OutputOptions? options = null)
    {
        TableFormatter.PrintPerformanceSummary(result, options);
        return result;
    }

    /// <summary>
    /// Prints tool usage summary for a specific tool.
    /// </summary>
    /// <param name="result">The stochastic result.</param>
    /// <param name="toolName">Name of the tool to summarize.</param>
    /// <param name="options">Output formatting options.</param>
    /// <returns>The same result for fluent chaining.</returns>
    public static StochasticResult PrintToolSummary(
        this StochasticResult result,
        string toolName,
        OutputOptions? options = null)
    {
        var summary = result.GetToolUsageSummary(toolName);
        TableFormatter.PrintToolSummary(summary, options);
        return result;
    }

    /// <summary>
    /// Converts the stochastic result to a formatted table string.
    /// </summary>
    /// <param name="result">The stochastic result.</param>
    /// <param name="title">Optional title for the table.</param>
    /// <param name="options">Output formatting options.</param>
    /// <returns>The formatted table as a string.</returns>
    public static string ToTableString(
        this StochasticResult result,
        string? title = null,
        OutputOptions? options = null)
    {
        return TableFormatter.ToTableString(result, title, options);
    }
}

/// <summary>
/// Extension methods for model comparison results.
/// </summary>
public static class ComparisonResultExtensions
{
    /// <summary>
    /// Prints a comparison table for multiple model results.
    /// </summary>
    /// <param name="results">List of model results to compare.</param>
    /// <param name="options">Output formatting options.</param>
    /// <returns>The same results for fluent chaining.</returns>
    public static IReadOnlyList<(string ModelName, StochasticResult Result)> PrintComparisonTable(
        this IReadOnlyList<(string ModelName, StochasticResult Result)> results,
        OutputOptions? options = null)
    {
        TableFormatter.PrintComparisonTable(results, options);
        return results;
    }

    /// <summary>
    /// Converts comparison results to a formatted table string.
    /// </summary>
    public static string ToComparisonTableString(
        this IReadOnlyList<(string ModelName, StochasticResult Result)> results,
        OutputOptions? options = null)
    {
        return TableFormatter.ToComparisonTableString(results, options);
    }
}
