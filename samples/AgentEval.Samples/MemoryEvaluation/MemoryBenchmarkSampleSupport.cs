// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using AgentEval.Samples.Benchmarks;

namespace AgentEval.Samples;

/// <summary>
/// What G7 (Benchmark Reporting) and G9 (Run Single Benchmark) share: which memory preset a run uses, whether the
/// sample may prompt or serve a report, and the verdict a memory benchmark result records.
/// </summary>
internal static class MemoryBenchmarkSampleSupport
{
    /// <summary>
    /// True only when a person is at the console: the menu runner, input not redirected and
    /// <c>AGENTEVAL_SAMPLES_NONINTERACTIVE</c> unset. <c>dotnet run -- &lt;n&gt;</c> and a sweep never wait on a prompt
    /// or on a report server: G9 used to print its menu, read nothing and exit 0 having run nothing.
    /// </summary>
    public static bool CanPrompt => Program.IsInteractive && !BenchmarkSampleHelpers.IsNonInteractive();

    /// <summary>
    /// The preset to run: <c>--preset</c> / <c>AGENTEVAL_SAMPLES_PRESET</c> when given (the H-group tier names map as
    /// H10 maps them: smoke → Quick, standard → Standard, audit-grade → Full), else the menu when someone is at the
    /// console, else Quick. Null when the menu was cancelled.
    /// </summary>
    public static MemoryBenchmark? ResolveBenchmark()
    {
        var requested = Environment.GetEnvironmentVariable("AGENTEVAL_SAMPLES_PRESET");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var benchmark = Parse(requested);
            if (benchmark is null)
            {
                Console.WriteLine($"   Preset '{requested.Trim()}' is not quick, standard or full: running Quick.");
                benchmark = MemoryBenchmark.Quick;
            }
            return benchmark;
        }

        return CanPrompt ? PromptForBenchmark() : MemoryBenchmark.Quick;
    }

    private static MemoryBenchmark? Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "quick" or "smoke" or "s" or "1" => MemoryBenchmark.Quick,
        "standard" or "std" or "t" or "2" => MemoryBenchmark.Standard,
        "full" or "audit" or "audit-grade" or "auditgrade" or "a" or "3" => MemoryBenchmark.Full,
        _ => null,
    };

    private static MemoryBenchmark? PromptForBenchmark()
    {
        Console.WriteLine("Select a benchmark preset:\n");
        Console.WriteLine("   [1] Quick      3 categories   Basic Retention, Temporal, Noise");
        Console.WriteLine("   [2] Standard   8 categories   + Reach-Back, FactUpdate, MultiTopic, Abstention, Preference");
        Console.WriteLine("   [3] Full      12 categories   + CrossSession, Reducer, Conflict, MultiSession");
        // [4] Overflow disabled — requires agent with reducer/memory system to be meaningful.
        // See hardening plan Part 14 for details. Re-enable when ISessionResettableAgent reducers are available.
        Console.WriteLine("   [Q] Cancel\n");
        Console.Write("   Choice: ");

        var key = Console.ReadLine()?.Trim();
        return key is null ? null : Parse(key);
    }

    /// <summary>
    /// Records what a run measured. A category whose run threw is an error; one that measured nothing or is not
    /// supported, and a question the judge did not score, are not measured: never a pass and never a 0. A complete
    /// run under the benchmark's 70% pass mark warns.
    /// </summary>
    public static void RecordVerdict(string label, MemoryBenchmarkResult result)
    {
        foreach (var category in result.CategoryResults.Where(c => c.Skipped))
        {
            var reason = $"{label}: {category.CategoryName} not measured — {category.SkipReason ?? "skipped"}";
            // MemoryBenchmarkRunner marks a category whose run threw "Error: <message>"; one where the judge scored
            // nothing is Errored too, and is not measured rather than an error.
            if (category.Errored && category.SkipReason?.StartsWith("Error:", StringComparison.Ordinal) == true)
                SampleOutcome.Error(reason);
            else
                SampleOutcome.Skip(reason);
        }

        if (result.UnmeasuredQueries > 0)
        {
            SampleOutcome.Skip(
                $"{label}: {result.UnmeasuredQueries} question(s) the judge did not score are left out of their category scores");
        }

        if (result.IsComplete && result.SkippedCategories.Count == 0 && !result.Passed)
            SampleOutcome.Warn($"{label} scored {result.OverallScore:F1}% on {result.BenchmarkName}, below the 70% pass mark");
    }

    /// <summary>Prints each category, the skipped and crashed ones with their reason, and what the judge left unscored.</summary>
    public static void PrintCategories(MemoryBenchmarkResult result)
    {
        foreach (var category in result.CategoryResults)
        {
            if (category.Skipped)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                var what = category.Errored ? "NOT MEASURED" : "SKIPPED";
                Console.WriteLine($"     {category.CategoryName,-24} {what}  ({category.SkipReason ?? "no reason given"})");
                Console.ResetColor();
                continue;
            }

            Console.ForegroundColor = category.Score >= 80 ? ConsoleColor.Green :
                                      category.Score >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.Write($"     {category.CategoryName,-24} {category.Score,6:F1}%");
            Console.ResetColor();
            if (category.UnmeasuredQueries > 0)
                Console.Write($"  ({category.UnmeasuredQueries} question(s) unscored, left out)");
            Console.WriteLine();
        }

        if (!result.IsComplete)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("     Incomplete run: a category that measured nothing counts as 0 in the overall score, so it is a lower bound.");
            Console.ResetColor();
        }
    }

    /// <summary>Serves the HTML report when someone is at the console; otherwise prints where it is and never blocks.</summary>
    public static void ServeOrPrintReport(JsonFileBaselineStore store, string agentName)
    {
        if (!CanPrompt)
        {
            Console.WriteLine($"   Report: {Path.Combine(store.GetReportDirectory(agentName), "report.html")}");
            Console.WriteLine("   Not served: this run is non-interactive. Run the sample from the menu to open it in a browser.\n");
            return;
        }

        var serverProcess = store.OpenReport(agentName);
        if (serverProcess != null)
        {
            Console.WriteLine("   Press Enter to stop the server and exit...");
            Console.ReadLine();
            serverProcess.Kill();
            serverProcess.Dispose();
        }
    }
}
