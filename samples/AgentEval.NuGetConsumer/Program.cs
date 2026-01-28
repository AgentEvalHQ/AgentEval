// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors
//
// ═══════════════════════════════════════════════════════════════════════════════
//    AgentEval NuGet Consumer Sample - Complete Feature Showcase
// ═══════════════════════════════════════════════════════════════════════════════
//
// This standalone project demonstrates AgentEval features as a NuGet consumer.
// Run in MOCK mode (no Azure credentials) or REAL mode (actual LLM calls).
//
// INTERACTIVE:
//   dotnet run --project samples/AgentEval.NuGetConsumer
//
// COMMAND-LINE (automated):
//   dotnet run --project samples/AgentEval.NuGetConsumer -- --mock --demo 0
//   dotnet run --project samples/AgentEval.NuGetConsumer -- --real --demo all
//   dotnet run --project samples/AgentEval.NuGetConsumer -- -m -d 3
//
// ═══════════════════════════════════════════════════════════════════════════════

using AgentEval.NuGetConsumer;

// Parse command-line arguments for non-interactive mode
var (cliMode, cliDemo) = ParseArguments(args);

if (cliMode.HasValue)
{
    // Command-line mode - run without prompts
    await RunAutomated(cliMode.Value, cliDemo ?? "all");
}
else
{
    // Interactive mode - show menus
    ShowWelcome();
    var useMock = SelectMode();
    SafeClear();
    ShowHeader(useMock);
    await ShowDemoMenu(useMock);
    ShowSummary(useMock);
}

return;

// ═══════════════════════════════════════════════════════════════════════════════
// Command-Line Parsing
// ═══════════════════════════════════════════════════════════════════════════════

static (bool? useMock, string? demo) ParseArguments(string[] args)
{
    bool? useMock = null;
    string? demo = null;
    
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i].ToLowerInvariant();
        
        switch (arg)
        {
            case "--mock" or "-m":
                useMock = true;
                break;
            case "--real" or "-r":
                useMock = false;
                break;
            case "--demo" or "-d":
                if (i + 1 < args.Length)
                {
                    demo = args[++i].ToLowerInvariant();
                }
                break;
            case "--help" or "-h" or "-?":
                PrintUsage();
                Environment.Exit(0);
                break;
        }
    }
    
    return (useMock, demo);
}

static void PrintUsage()
{
    Console.WriteLine("""
    AgentEval NuGet Consumer - Command-Line Usage
    
    USAGE:
      dotnet run --project samples/AgentEval.NuGetConsumer [OPTIONS]
    
    OPTIONS:
      --mock, -m       Run in mock mode (no Azure credentials needed)
      --real, -r       Run in real mode (requires Azure OpenAI credentials)
      --demo, -d NUM   Run specific demo(s):
                         0   = Complete Example
                         1   = Behavioral Policies
                         2   = Stochastic Model Comparison
                         3   = Run ALL demos
                         all = Run ALL demos
      --help, -h       Show this help
    
    EXAMPLES:
      # Interactive mode (prompts for choices)
      dotnet run --project samples/AgentEval.NuGetConsumer
    
      # Run all demos in mock mode
      dotnet run --project samples/AgentEval.NuGetConsumer -- --mock --demo all
    
      # Run specific demo in real mode
      dotnet run --project samples/AgentEval.NuGetConsumer -- --real --demo 0
    
    ENVIRONMENT VARIABLES (for real mode):
      AZURE_OPENAI_ENDPOINT     Azure OpenAI endpoint URL
      AZURE_OPENAI_API_KEY      Azure OpenAI API key
      AZURE_OPENAI_DEPLOYMENT   Primary model deployment name
    """);
}

// ═══════════════════════════════════════════════════════════════════════════════
// Automated Execution
// ═══════════════════════════════════════════════════════════════════════════════

static async Task RunAutomated(bool useMock, string demoChoice)
{
    // Validate real mode has credentials
    if (!useMock && !Config.IsConfigured)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("ERROR: --real mode requires Azure OpenAI credentials.");
        Console.WriteLine("Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT");
        Console.ResetColor();
        Environment.Exit(1);
    }
    
    var mode = useMock ? "MOCK" : "REAL";
    Console.WriteLine($"Running in {mode} mode, demo: {demoChoice}\n");
    
    try
    {
        switch (demoChoice)
        {
            case "0":
                await Demos.RunCompleteExample(useMock);
                break;
            case "1":
                await Demos.RunBehavioralPoliciesDemo(useMock);
                break;
            case "2":
                if (!useMock)
                    await Demos.RunStochasticEvaluationDemo();
                else
                    Demos.ShowStochasticExplanation();
                break;
            case "3" or "all":
                await Demos.RunCompleteExample(useMock);
                Console.WriteLine("\n" + new string('═', 80) + "\n");
                await Demos.RunBehavioralPoliciesDemo(useMock);
                Console.WriteLine("\n" + new string('═', 80) + "\n");
                if (!useMock)
                    await Demos.RunStochasticEvaluationDemo();
                else
                    Demos.ShowStochasticExplanation();
                break;
            default:
                Console.WriteLine($"Unknown demo: {demoChoice}. Use 0, 1, 2, 3, or 'all'");
                Environment.Exit(1);
                break;
        }
        
        Console.WriteLine("\n✅ Demo completed successfully!");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n❌ Error: {ex.Message}");
        Console.ResetColor();
        Environment.Exit(1);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Demo Menu System
// ═══════════════════════════════════════════════════════════════════════════════

static async Task ShowDemoMenu(bool useMock)
{
    while (true)
    {
        Console.WriteLine("""
        ═══════════════════════════════════════════════════════════════════════════════
          Select Demo to Run:
        ═══════════════════════════════════════════════════════════════════════════════
        
        """);
        
        Console.WriteLine("  🎯 [0] COMPLETE EXAMPLE - All AgentEval features in one comprehensive demo");
        Console.WriteLine("  🛡️  [1] BEHAVIORAL POLICIES - LLM-as-a-judge evaluation + safety guardrails");
        Console.WriteLine("  📊 [2] STOCHASTIC MODEL COMPARISON - Statistical analysis across models");
        Console.WriteLine("  🏃 [3] Run ALL Demos");
        Console.WriteLine("  ❌ [Q] Quit");
        Console.WriteLine();
        
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("      💡 Basic demos (Tool Chain, Performance, Response) available in AgentEval.Samples");
        Console.ResetColor();
        Console.WriteLine();
        
        Console.Write("  Enter choice [0-3/Q]: ");
        
        var choice = Console.ReadKey(intercept: true).KeyChar.ToString().ToUpper();
        Console.WriteLine(choice);
        Console.WriteLine();
        
        try
        {
            switch (choice)
            {
                case "0":
                    await Demos.RunCompleteExample(useMock);
                    break;
                case "1":
                    await Demos.RunBehavioralPoliciesDemo(useMock);
                    break;
                case "2":
                    if (!useMock)
                    {
                        await Demos.RunStochasticEvaluationDemo();
                    }
                    else
                    {
                        Demos.ShowStochasticExplanation();
                    }
                    break;
                case "3":
                    await Demos.RunCompleteExample(useMock);
                    await Demos.RunBehavioralPoliciesDemo(useMock);
                    if (!useMock)
                    {
                        await Demos.RunStochasticEvaluationDemo();
                    }
                    else
                    {
                        Demos.ShowStochasticExplanation();
                    }
                    ShowSummary(useMock);
                    return; // Exit menu after running all
                case "Q":
                    return;
                default:
                    Console.WriteLine("  Invalid choice. Please try again.");
                    await Task.Delay(1000);
                    continue;
            }
            
            Console.WriteLine("\n  Press any key to return to menu...");
            Console.ReadKey(intercept: true);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ Error: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine("\n  Press any key to continue...");
            Console.ReadKey(intercept: true);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// UI Methods
// ═══════════════════════════════════════════════════════════════════════════════

static void ShowWelcome()
{
    SafeClear();
    Console.WriteLine("""
    
    ╔════════════════════════════════════════════════════════════════════════════════╗
    ║                                                                                ║
    ║    █████╗  ██████╗ ███████╗███╗   ██╗████████╗███████╗██╗   ██╗ █████╗ ██╗     ║
    ║   ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝██╔════╝██║   ██║██╔══██╗██║     ║
    ║   ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   █████╗  ██║   ██║███████║██║     ║
    ║   ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ██╔══╝  ╚██╗ ██╔╝██╔══██║██║     ║
    ║   ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ███████╗ ╚████╔╝ ██║  ██║███████╗║
    ║   ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   ╚══════╝  ╚═══╝  ╚═╝  ╚═╝╚══════╝║
    ║                                                                                ║
    ║                    NuGet Consumer Sample - Feature Showcase                    ║
    ║                                                                                ║
    ╚════════════════════════════════════════════════════════════════════════════════╝

    """);
}

static bool SelectMode()
{
    var hasCredentials = Config.IsConfigured;
    
    Console.WriteLine("  Select mode:\n");
    Console.WriteLine("    [1] 🎭 MOCK MODE - No Azure credentials needed (instant, offline)");
    
    if (hasCredentials)
    {
        Console.WriteLine("    [2] 🚀 REAL MODE - Use Azure OpenAI (actual LLM calls)");
        Console.WriteLine($"\n        Endpoint: {Config.Endpoint}");
        Console.WriteLine($"        Model: {Config.Model}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    [2] 🚀 REAL MODE - Not available (credentials not configured)");
        Console.ResetColor();
    }
    
    Console.Write("\n  Enter choice [1/2]: ");
    
    // Handle both interactive and redirected input
    if (Console.IsInputRedirected)
    {
        var line = Console.ReadLine();
        if (line == "2" && hasCredentials)
        {
            Console.WriteLine("Real Mode");
            return false;
        }
        Console.WriteLine("Mock Mode (default)");
        return true;
    }
    
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.KeyChar == '1')
        {
            Console.WriteLine("1 - Mock Mode");
            return true;
        }
        if (key.KeyChar == '2' && hasCredentials)
        {
            Console.WriteLine("2 - Real Mode");
            return false;
        }
        // Default to mock on Enter
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine("1 - Mock Mode (default)");
            return true;
        }
    }
}

static void ShowHeader(bool useMock)
{
    var mode = useMock ? "🎭 MOCK MODE" : "🚀 REAL MODE";
    Console.WriteLine($"""
    
    ════════════════════════════════════════════════════════════════════════════════
      AgentEval Feature Showcase - {mode}
    ════════════════════════════════════════════════════════════════════════════════
    
    """);
}

static void ShowSummary(bool useMock)
{
    Console.WriteLine("""

    ╔════════════════════════════════════════════════════════════════════════════════╗
    ║                         ✅ ADVANCED AGENTEVAL FEATURES!                       ║
    ╠════════════════════════════════════════════════════════════════════════════════╣
    ║                                                                                ║
    ║   🎯 Complete Example         - TestCase, EvaluationOptions, StreamingOptions        ║
    ║   🧑‍⚖️  LLM-as-a-Judge           - Behavioral policies + evaluation criteria    ║
    """);
    
    if (!useMock)
    {
        Console.WriteLine("║   📊 Model Comparison         - Real statistical analysis across models       ║");
    }
    else
    {
        Console.WriteLine("║   ℹ️  Model Comparison         - Run in REAL mode for live comparison         ║");
    }
    
    Console.WriteLine("""
    ║                                                                                ║
    ║   💡 For basic demos (Tool Chain, Performance, Response):                     ║
    ║      See AgentEval.Samples project with 18 targeted examples                  ║
    ║                                                                                ║
    ╠════════════════════════════════════════════════════════════════════════════════╣
    ║   📦 Install: dotnet add package AgentEval --prerelease                        ║
    ║   📖 Docs:    https://github.com/joslat/AgentEval                              ║
    ╚════════════════════════════════════════════════════════════════════════════════╝

    """);
}

static void SafeClear()
{
    // Console.Clear() throws when input is redirected (piped)
    try
    {
        if (!Console.IsInputRedirected)
        {
            Console.Clear();
        }
    }
    catch (IOException)
    {
        // Ignore - running in non-interactive mode
    }
}
