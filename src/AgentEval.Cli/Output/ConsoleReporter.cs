// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha during the v1.1 CLI consolidation.

using System.Reflection;
using AgentEval.Models;

namespace AgentEval.Cli.Output;

/// <summary>
/// Human-friendly console output for evaluation progress and summary.
/// All messages go to <see cref="Console.Error"/> (stderr) so that
/// export data on stdout can be piped cleanly (Unix convention).
/// </summary>
internal static class ConsoleReporter
{
    /// <summary>
    /// Writes the evaluation header before test execution begins.
    /// </summary>
    public static void WriteHeader(string model, string dataset, int testCount)
    {
        var version = typeof(ConsoleReporter).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  AgentEval CLI v{version}");
        Console.Error.WriteLine($"  Model:   {model}");
        Console.Error.WriteLine($"  Dataset: {dataset} ({testCount} test{(testCount != 1 ? "s" : "")})");
        Console.Error.WriteLine();
    }

    /// <summary>
    /// Writes the evaluation summary after all tests complete.
    /// </summary>
    public static void WriteSummary(TestSummary summary)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  ─────────────────────────────────");
        Console.Error.WriteLine($"  Results: {summary.PassedCount}/{summary.TotalCount} passed" +
            $"  Score: {summary.AverageScore:F1}");

        if (summary.TotalDuration > TimeSpan.Zero)
            Console.Error.WriteLine($"  Duration: {summary.TotalDuration.TotalSeconds:F1}s" +
                $"  Cost: ${summary.TotalCost:F4}");

        if (summary.AllPassed)
            Console.Error.WriteLine("  All tests passed.");
        else
            Console.Error.WriteLine($"  {summary.FailedCount} test(s) failed.");

        Console.Error.WriteLine();
    }
}
