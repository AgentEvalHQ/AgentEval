// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;

namespace AgentEval.Samples;

/// <summary>
/// Main program - interactive menu to run samples.
/// </summary>
public static class Program
{
    private static bool _interactive = true;

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        PrintBanner();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
        }

        if (args.Length > 0 && int.TryParse(args[0], out var sampleNumber))
        {
            _interactive = false;
            await RunSample(sampleNumber);
            return;
        }

        while (true)
        {
            PrintMenu();
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input) || input.Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("\n👋 Goodbye!\n");
                break;
            }

            if (int.TryParse(input, out var choice))
            {
                await RunSample(choice);
            }
            else
            {
                Console.WriteLine("❌ Invalid choice. Enter a number or 'q' to quit.\n");
            }
        }
    }

    private static async Task RunSample(int sampleNumber)
    {
        Console.WriteLine();
        
        try
        {
            switch (sampleNumber)
            {
                case 1:
                    await Sample01_HelloWorld.RunAsync();
                    break;
                case 2:
                    await Sample02_AgentWithOneTool.RunAsync();
                    break;
                case 3:
                    await Sample03_AgentWithMultipleTools.RunAsync();
                    break;
                case 4:
                    await Sample04_PerformanceMetrics.RunAsync();
                    break;
                case 5:
                    await Sample05_ComprehensiveRAG.RunAsync();
                    break;
                case 6:
                    await Sample06_Benchmarks.RunAsync();
                    break;
                case 7:
                    await Sample07_SnapshotTesting.RunAsync();
                    break;
                case 8:
                    await Sample08_ConversationEvaluation.RunAsync();
                    break;
                case 9:
                    await Sample09_WorkflowEvaluationReal.RunAsync();
                    break;
                case 10:
                    await Sample10_WorkflowWithTools.RunAsync();
                    break;
                case 11:
                    await Sample11_DatasetsAndExport.RunAsync();
                    break;
                case 12:
                    await Sample12_PolicySafetyEvaluation.RunAsync();
                    break;
                case 13:
                    await Sample13_TraceRecordReplay.RunAsync();
                    break;
                case 14:
                    await Sample14_StochasticEvaluation.RunAsync();
                    break;
                case 15:
                    await Sample15_ModelComparison.RunAsync();
                    break;
                case 16:
                    await Sample16_CombinedStochasticComparison.RunAsync();
                    break;
                case 17:
                    await Sample17_QualitySafetyMetrics.RunAsync();
                    break;
                case 18:
                    await Sample18_JudgeCalibration.RunAsync();
                    break;
                case 19:
                    await Sample19_StreamingVsAsyncPerformance.RunAsync();
                    break;
                case 20:
                    await Sample20_RedTeamBasic.RunAsync();
                    break;
                case 21:
                    await Sample21_RedTeamAdvanced.RunAsync();
                    break;
                case 22:
                    await Sample22_ResponsibleAI.RunAsync();
                    break;
                case 23:
                    await Sample23_BenchmarkSystem.RunAsync();
                    break;
                case 24:
                    await Sample24_CalibratedEvaluator.RunAsync();
                    break;
                default:
                    Console.WriteLine($"❌ Sample {sampleNumber} not found.\n");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n❌ Error: {ex.Message}");
            Console.ResetColor();
        }

        Console.WriteLine("\nPress any key to continue...");
        if (_interactive)
            Console.ReadKey(true);
    }

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

    private static void PrintMenu()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│                    📚 SAMPLES MENU                          │");
        Console.WriteLine("├─────────────────────────────────────────────────────────────┤");
        Console.ResetColor();
        
        Console.WriteLine("│  1. 🌍 Hello World        - Minimal AgentEval test          │");
        Console.WriteLine("│  2. 🔧 Agent + One Tool   - Tool tracking assertions        │");
        Console.WriteLine("│  3. 🔧 Agent + Multi Tool - Tool ordering & timeline        │");
        Console.WriteLine("│  4. ⚡ Performance        - Latency, cost, TTFT metrics     │");
        Console.WriteLine("│  5. 📊 Comprehensive RAG  - Build & evaluate RAG system    │");
        Console.WriteLine("│  6. 📈 Benchmarks         - Performance & agentic benchmarks│");
        Console.WriteLine("│  7. 📸 Snapshot Testing   - Regression detection            │");
        Console.WriteLine("│  8. 💬 Conversations      - Multi-turn evaluation            │");
        Console.WriteLine("│  9. 🔀 Workflow Evaluation - Real MAF workflow pipeline       │");
        Console.WriteLine("│ 10. � Workflow + Tools   - TripPlanner with tool tracking     │");
        Console.WriteLine("│ 11. �📂 Datasets & Export  - Batch evaluation, JUnit export   │");
        Console.WriteLine("│ 12. 🛡️ Policy & Safety    - Enterprise guardrails           │");
        Console.WriteLine("│ 13. 🔄 Trace Record/Replay - Deterministic evaluation        │");
        Console.WriteLine("│ 14. 🎲 Stochastic Evaluation - Multi-run reliability           │");
        Console.WriteLine("│ 15. ⚖️ Model Comparison   - Compare & rank models           │");
        Console.WriteLine("│ 16. 🔀 Combined Test      - Stochastic + Model Comparison   │");
        Console.WriteLine("│ 17. 🛡️ Quality & Safety   - Groundedness, Coherence, Fluency│");
        Console.WriteLine("│ 18. ⚖️ Judge Calibration  - Multi-model consensus voting   │");
        Console.WriteLine("│ 19. ⚡ Streaming vs Async - Performance comparison          │");
        Console.WriteLine("│ 20. 🛡️ Red Team Basic    - Security vulnerability scan       │");
        Console.WriteLine("│ 21. 🛡️ Red Team Advanced - Pipeline, reports, compliance    │");
        Console.WriteLine("│ 22. 🛡️ ResponsibleAI     - Content safety, bias, misinfo   │");
        Console.WriteLine("│ 23. 📊 Benchmark System  - Performance, agentic & standard  │");
        Console.WriteLine("│ 24. ⚖️ Calibrated Eval   - Multi-model harness evaluation    │");
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("├─────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│  q. Quit                                                    │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        
        Console.Write("\nEnter your choice: ");
    }
}
