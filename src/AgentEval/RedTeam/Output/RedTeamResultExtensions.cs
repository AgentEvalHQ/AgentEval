// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Output;

/// <summary>
/// Extension methods for printing RedTeamResult with rich formatting.
/// </summary>
public static class RedTeamResultExtensions
{
    /// <summary>
    /// Print results to console with rich formatting.
    /// </summary>
    /// <param name="result">The RedTeam result to print.</param>
    /// <param name="options">Output formatting options.</param>
    public static void Print(this RedTeamResult result, RedTeamOutputOptions? options = null)
    {
        var formatter = new RedTeamOutputFormatter(options);
        formatter.Print(result);
    }

    /// <summary>
    /// Print results with specified verbosity level.
    /// </summary>
    /// <param name="result">The RedTeam result to print.</param>
    /// <param name="verbosity">Verbosity level for output.</param>
    public static void Print(this RedTeamResult result, VerbosityLevel verbosity)
    {
        result.Print(new RedTeamOutputOptions { Verbosity = verbosity });
    }

    /// <summary>
    /// Print minimal summary (for CI/CD).
    /// </summary>
    /// <param name="result">The RedTeam result to print.</param>
    public static void PrintSummary(this RedTeamResult result)
        => result.Print(RedTeamOutputOptions.CI);

    /// <summary>
    /// Print with full details (for debugging).
    /// </summary>
    /// <param name="result">The RedTeam result to print.</param>
    public static void PrintFull(this RedTeamResult result)
        => result.Print(RedTeamOutputOptions.Debug);

    /// <summary>
    /// Format result to a string with rich formatting.
    /// </summary>
    /// <param name="result">The RedTeam result to format.</param>
    /// <param name="options">Output formatting options.</param>
    /// <returns>Formatted string representation.</returns>
    public static string ToFormattedString(this RedTeamResult result, RedTeamOutputOptions? options = null)
    {
        using var writer = new StringWriter();
        var formatter = new RedTeamOutputFormatter(options, writer);
        formatter.Print(result);
        return writer.ToString();
    }
}
