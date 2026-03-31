// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;

namespace AgentEval.Samples;

/// <summary>
/// Main program — two-level interactive menu to browse and run samples by group.
/// </summary>
public static class Program
{
    private static bool _interactive = true;

    // ──────────────────────────────────────────────────────────
    //  Sample catalogue — one record per group, samples in order
    // ──────────────────────────────────────────────────────────

    private record SampleEntry(string Name, string Description, Func<Task> Run);
    private record SampleGroup(char Key, string Name, string Note, IReadOnlyList<SampleEntry> Samples);

    private static readonly IReadOnlyList<SampleGroup> Groups =
    [
        new('A', "Getting Started", "★ no credentials needed",
        [
            new("Hello World",               "Minimal AgentEval test — TestCase, TestResult, pass/fail",               HelloWorld.RunAsync),
            new("Agent + One Tool",          "Tool tracking and fluent assertions (HaveCalledTool, WithoutError)",      AgentWithOneTool.RunAsync),
            new("Agent + Multiple Tools",    "Tool ordering, BeforeTool / AfterTool, visual timeline",                 AgentWithMultipleTools.RunAsync),
            new("Performance Metrics",       "Latency, cost, TTFT, token budget assertions",                           PerformanceMetrics.RunAsync),
            new("Light Path (MEAI)",        "AgentEval as MEAI IEvaluator — plug into MAF's evaluation pipeline",     LightPathMAFIntegration.RunAsync),
        ]),

        new('B', "Metrics & Quality", "",
        [
            new("Comprehensive RAG",         "Build & evaluate a full RAG system — 8 metrics + IR metrics",           ComprehensiveRAG.RunAsync),
            new("Quality & Safety Metrics",  "Groundedness, Coherence, Fluency — beyond RAG accuracy",               QualitySafetyMetrics.RunAsync),
            new("Judge Calibration",         "Multi-model consensus voting (Median, Mean, Weighted)",                 JudgeCalibration.RunAsync),
            new("Responsible AI",            "Toxicity, bias, misinformation with counterfactual testing",            ResponsibleAI.RunAsync),
            new("Calibrated Evaluator",      "Drop-in IEvaluator with per-criterion majority voting",                CalibratedEvaluatorDemo.RunAsync),
        ]),

        new('C', "Workflows & Conversations", "",
        [
            new("Conversation Evaluation",   "Multi-turn testing with ConversationRunner and fluent builder API",     ConversationEvaluation.RunAsync),
            new("Real MAF Workflow",         "WorkflowBuilder + InProcessExecution: 4-agent pipeline",               WorkflowEvaluationReal.RunAsync),
            new("Workflow + Tools",          "TripPlanner pipeline: 4 agents with tool call tracking",               WorkflowWithTools.RunAsync),
        ]),

        new('D', "Performance & Statistics", "",
        [
            new("Performance Profiling",     "Real latency: p50 / p90 / p99 percentiles, tool accuracy",             PerformanceProfiling.RunAsync),
            new("Stochastic Evaluation",     "Run N times — assert on pass rate, not single pass/fail",              StochasticEvaluation.RunAsync),
            new("Model Comparison",          "Compare and rank 3 models on quality, speed, cost, reliability",       ModelComparison.RunAsync),
            new("Stochastic + Comparison",   "Stochastic rigor applied to side-by-side model comparison",           CombinedStochasticComparison.RunAsync),
            new("Streaming vs Async",        "TTFT vs throughput — compare streaming and non-streaming modes",       StreamingVsAsyncPerformance.RunAsync),
        ]),

        new('E', "Safety & Security", "",
        [
            new("Policy & Safety",           "Enterprise guardrails — NeverCallTool, PII detection, MustConfirmBefore", PolicySafetyEvaluation.RunAsync),
            new("Red Team Basic",            "One-liner security scan — 9 attack types, OWASP probes",               RedTeamBasic.RunAsync),
            new("Red Team Advanced",         "Custom attack pipeline, OWASP compliance, PDF export, baselines",      RedTeamAdvanced.RunAsync),
        ]),

        new('F', "Data & Infrastructure", "",
        [
            new("Snapshot Testing",          "Regression detection — JSON diff, field scrubbing, semantic tolerance", SnapshotTesting.RunAsync),
            new("Datasets & Export",         "Batch evaluation: YAML datasets → JUnit / Markdown / JSON / TRX",      DatasetsAndExport.RunAsync),
            new("Trace Record & Replay",     "Capture executions, save to JSON, replay deterministically",            TraceRecordReplay.RunAsync),
            new("Benchmark System",          "JSONL-loaded tool-accuracy benchmarks (BFCL, GAIA-style)",             BenchmarkSystem.RunAsync),
            new("Dataset Loaders",           "Multi-format auto-detection: JSONL, JSON, YAML, CSV (offline)",        DatasetLoaders.RunAsync),
            new("Extensibility",             "DI registries — custom metrics, exporters, loaders, attacks",          Extensibility.RunAsync),
            new("Cross-Framework",           "Universal IChatClient.AsEvaluableAgent() for any AI provider",         CrossFrameworkEvaluation.RunAsync),
        ]),

        new('G', "Memory Evaluation", "",
        [
            new("Memory Basics",             "Test if agents remember facts — MemoryJudge, fluent assertions",        MemoryBasics.RunAsync),
            new("Memory Benchmark",          "Comprehensive memory scoring — Quick / Standard / Full with grades",    MemoryBenchmarkDemo.RunAsync),
            new("Memory Scenarios",          "ReachBackEvaluator (recall depth), ReducerEvaluator (compression)",    MemoryScenariosDemo.RunAsync),
            new("Memory DI",                 "Production DI wiring — AddAgentEvalMemory(), CanRememberAsync()",      MemoryDI.RunAsync),
            new("Cross-Session Memory",      "Fact persistence across session resets — compare with / without",      MemoryCrossSession.RunAsync),
            new("Benchmark Reporting",       "Run benchmarks, save baselines, compare configs, HTML report",        MemoryBenchmarkReporting.RunAsync),
            new("LongMemEval Benchmark",     "Cross-platform memory eval — 120K token haystacks (ICLR 2025, MIT)",  LongMemEvalBenchmark.RunAsync),
            new("Run Single Benchmark",     "Pick Quick/Standard/Full, run it, save baseline, view report",       RunSingleBenchmark.RunAsync),
        ]),
    ];

    // ──────────────────────────────────────────────────────────
    //  Entry point
    // ──────────────────────────────────────────────────────────

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        PrintBanner();

        if (!AIConfig.IsConfigured)
            AIConfig.PrintMissingCredentialsWarning();

        // Legacy CLI: dotnet run -- <1-32>  (direct sample number, flattened in group order)
        if (args.Length > 0 && int.TryParse(args[0], out var legacyNumber))
        {
            _interactive = false;
            await RunLegacyNumber(legacyNumber);
            return;
        }

        // Two-level interactive menu
        while (true)
        {
            var group = PromptForGroup();
            if (group is null) break;           // 'Q' → exit

            while (true)
            {
                var choice = PromptForSample(group);

                if (choice == "B") break;
                if (choice == "Q") goto done;
                if (choice == "A") { foreach (var s in group.Samples) await RunEntry(s); continue; }

                if (int.TryParse(choice, out var idx) && idx >= 1 && idx <= group.Samples.Count)
                    await RunEntry(group.Samples[idx - 1]);
            }
        }
        done:

        Console.WriteLine("\n👋 Goodbye!\n");
    }

    // ──────────────────────────────────────────────────────────
    //  Prompt helpers
    // ──────────────────────────────────────────────────────────

    private static SampleGroup? PromptForGroup()
    {
        while (true)
        {
            PrintGroupMenu();
            var raw = Console.ReadLine()?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(raw) || raw == "Q") return null;

            var group = Groups.FirstOrDefault(g => g.Key.ToString() == raw);
            if (group is not null) return group;

            Console.WriteLine("  Enter a letter A–G or Q to quit.\n");
        }
    }

    // Returns "B" (back), "Q" (quit), "A" (run all), or a digit string for a sample index.
    private static string PromptForSample(SampleGroup group)
    {
        while (true)
        {
            PrintSampleMenu(group);
            var raw = Console.ReadLine()?.Trim().ToUpperInvariant() ?? "";

            if (raw == "" || raw == "B") return "B";
            if (raw == "Q") return "Q";
            if (raw == "A") return "A";

            if (int.TryParse(raw, out var idx) && idx >= 1 && idx <= group.Samples.Count)
                return raw;

            Console.WriteLine($"  Enter 1–{group.Samples.Count}, A to run all, B to go back, or Q to quit.\n");
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Menu rendering
    // ──────────────────────────────────────────────────────────

    private static void PrintGroupMenu()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ┌─────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │                     SELECT A GROUP                              │");
        Console.WriteLine("  ├─────────────────────────────────────────────────────────────────┤");
        Console.ResetColor();

        foreach (var g in Groups)
        {
            var note = string.IsNullOrEmpty(g.Note) ? "" : $"  {g.Note}";
            var count = $"({g.Samples.Count} samples)";
            Console.WriteLine($"  │  [{g.Key}] {g.Name,-32} {count,-12}{note,-24}│");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ├─────────────────────────────────────────────────────────────────┤");
        Console.WriteLine("  │  [Q] Quit                                                       │");
        Console.WriteLine("  └─────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.Write("\n  Group: ");
    }

    private static void PrintSampleMenu(SampleGroup group)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        var header = group.Name + (string.IsNullOrEmpty(group.Note) ? "" : "  " + group.Note);
        Console.WriteLine($"  ┌─────────────────────────────────────────────────────────────────┐");
        Console.WriteLine($"  │  {header,-65}│");
        Console.WriteLine($"  ├─────────────────────────────────────────────────────────────────┤");
        Console.ResetColor();

        for (var i = 0; i < group.Samples.Count; i++)
        {
            var s = group.Samples[i];
            Console.WriteLine($"  │  [{i + 1,2}] {s.Name,-28} {s.Description,-33}│");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ├─────────────────────────────────────────────────────────────────┤");
        Console.WriteLine("  │  [A] Run all in this group   [B] Back   [Q] Quit               │");
        Console.WriteLine("  └─────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.Write("\n  Sample: ");
    }

    // ──────────────────────────────────────────────────────────
    //  Runner
    // ──────────────────────────────────────────────────────────

    private static async Task RunEntry(SampleEntry entry)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ▶ Running: {entry.Name}");
        Console.ResetColor();
        Console.WriteLine();

        try
        {
            await entry.Run();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ❌ Error: {ex.Message}");
            Console.ResetColor();
        }

        Console.WriteLine("\n  Press any key to continue...");
        if (_interactive) Console.ReadKey(true);
    }

    // ──────────────────────────────────────────────────────────
    //  Legacy: dotnet run -- <1-32>
    // ──────────────────────────────────────────────────────────

    private static async Task RunLegacyNumber(int n)
    {
        var all = Groups.SelectMany(g => g.Samples).ToList();
        if (n < 1 || n > all.Count)
        {
            Console.WriteLine($"  ❌ Sample {n} not found. Valid range: 1–{all.Count}");
            return;
        }
        await RunEntry(all[n - 1]);
    }

    // ──────────────────────────────────────────────────────────
    //  Banner
    // ──────────────────────────────────────────────────────────

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║     █████╗  ██████╗ ███████╗███╗   ██╗████████╗███████╗██╗   ██╗ █████╗ ██╗   ║
║    ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝██╔════╝██║   ██║██╔══██╗██║   ║
║    ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   █████╗  ██║   ██║███████║██║   ║
║    ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ██╔══╝  ╚██╗ ██╔╝██╔══██║██║   ║
║    ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ███████╗ ╚████╔╝ ██║  ██║███████╗
║    ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   ╚══════╝  ╚═══╝  ╚═╝  ╚═╝╚══════╝
║                                                                               ║
║              The .NET Evaluation Toolkit for AI Agents                        ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
