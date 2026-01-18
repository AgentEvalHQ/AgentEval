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

// Run all demos
await Demos.RunToolAssertionsDemo(useMock);
await Demos.RunPerformanceAssertionsDemo(useMock);
await Demos.RunBehavioralPoliciesDemo(useMock);
await Demos.RunResponseAssertionsDemo(useMock);

// Stochastic testing only in real mode (requires actual LLM variability)
if (!useMock)
{
    await Demos.RunStochasticTestingDemo();
}
else
{
    Demos.ShowStochasticExplanation();
}

ShowSummary(useMock);

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
    ║                         ✅ ALL FEATURES DEMONSTRATED!                          ║
    ╠════════════════════════════════════════════════════════════════════════════════╣
    ║                                                                                ║
    ║   ✅ Tool Chain Assertions    - HaveCalledTool, WithArgument, BeforeTool       ║
    ║   ✅ Performance Assertions   - Duration, TTFT, Cost, Token limits             ║
    ║   ✅ Behavioral Policies      - NeverCallTool, MustConfirmBefore               ║
    ║   ✅ Response Assertions      - Contain, NotContain, Length validation         ║
    """);
    
    if (!useMock)
    {
        Console.WriteLine("║   ✅ Stochastic Testing       - Real statistical analysis over 5 runs         ║");
    }
    else
    {
        Console.WriteLine("║   ℹ️  Stochastic Testing       - Run in REAL mode to see actual stats          ║");
    }
    
    Console.WriteLine("""
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
