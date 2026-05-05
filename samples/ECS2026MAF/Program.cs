// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo
//
// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║               ECS2026MAF — Microsoft Agent Framework Demos                  ║
// ║   Pure MAF code — no AgentEval dependencies in this project                 ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
//
// Run:
//   dotnet run --project samples/ECS2026MAF

using System.Text;
using ECS2026MAF.Demos;

Console.OutputEncoding = Encoding.UTF8;

await ShowMenuAsync();

static async Task ShowMenuAsync()
{
    while (true)
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║                 ECS 2026 — Microsoft Agent Framework Demos                  ║
╠══════════════════════════════════════════════════════════════════════════════╣
║                                                                              ║
║   1  TravelAgent             Single agent with tools                         ║
║   2  TripPlanner Workflow    Multi-agent pipeline (4 agents + tools)         ║
║   3  Live Demo               Placeholder — built live during the talk        ║
║   4  Live Demo (Complete)    Completed reference — chat loop from scratch    ║
║                                                                              ║
║   Q  Quit                                                                    ║
║                                                                              ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        if (!ECS2026MAF.Config.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️  Azure OpenAI credentials not found.");
            Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT.\n");
            Console.ResetColor();
        }

        Console.Write("  Select: ");
        var key = Console.ReadKey(intercept: true).KeyChar;
        Console.WriteLine();

        switch (key)
        {
            case '1': await Demo01_TravelAgent.RunAsync();             break;
            case '2': await Demo02_TripPlannerWorkflow.RunAsync();     break;
            case '3': await Demo03_LiveDemo.RunAsync();                break;
            case '4': await Demo03_LiveDemoComplete.RunAsync();        break;
            case 'q' or 'Q': return;
        }

        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey(intercept: true);
    }
}
