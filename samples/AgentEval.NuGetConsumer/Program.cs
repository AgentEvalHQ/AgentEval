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
// RUN: dotnet run --project samples/AgentEval.NuGetConsumer
//
// ═══════════════════════════════════════════════════════════════════════════════

using AgentEval.NuGetConsumer;

// Show welcome and select mode
ShowWelcome();
var useMock = SelectMode();

SafeClear();
ShowHeader(useMock);

// Interactive menu for demo selection
await ShowDemoMenu(useMock);

ShowSummary(useMock);

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
                        await Demos.RunStochasticTestingDemo();
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
                        await Demos.RunStochasticTestingDemo();
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
    ║   🎯 Complete Example         - ALL TestCase, EvaluationOptions, StreamingOptions    ║
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
