// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using System.Text;

namespace AgentEval.Samples;

/// <summary>
/// Main program - interactive menu to run samples.
/// </summary>
public static class Program
{
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
                    await Sample05_RAGEvaluation.RunAsync();
                    break;
                case 6:
                    await Sample06_Benchmarks.RunAsync();
                    break;
                case 7:
                    await Sample07_SnapshotTesting.RunAsync();
                    break;
                case 8:
                    await Sample08_ConversationTesting.RunAsync();
                    break;
                case 9:
                    await Sample09_WorkflowTesting.RunAsync();
                    break;
                case 10:
                    await Sample10_DatasetsAndExport.RunAsync();
                    break;
                case 11:
                    await Sample11_BecauseAssertions.RunAsync();
                    break;
                case 12:
                    await Sample12_PolicySafetyTesting.RunAsync();
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
║              The .NET-native AI Agent Testing Framework                       ║
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
        Console.WriteLine("│  5. 📊 RAG Evaluation     - Faithfulness metric demo        │");
        Console.WriteLine("│  6. 📈 Benchmarks         - Performance & agentic benchmarks│");
        Console.WriteLine("│  7. 📸 Snapshot Testing   - Regression detection            │");
        Console.WriteLine("│  8. 💬 Conversations      - Multi-turn testing              │");
        Console.WriteLine("│  9. 🔀 Workflow Testing   - Multi-agent orchestration       │");
        Console.WriteLine("│ 10. 📂 Datasets & Export  - Batch testing, JUnit export     │");
        Console.WriteLine("│ 11. 📝 Because Assertions - Self-documenting tests          │");
        Console.WriteLine("│ 12. 🛡️ Policy & Safety    - Enterprise guardrails           │");
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("├─────────────────────────────────────────────────────────────┤");
        Console.WriteLine("│  q. Quit                                                    │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        
        Console.Write("\nEnter your choice: ");
    }
}
