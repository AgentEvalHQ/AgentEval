// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo
//
// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║           ECS2026MAF.Evals — AgentEval Assertions & Metrics                 ║
// ║   Pure AgentEval evaluation code — agents live in ECS2026MAF               ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
//
// Run:
//   dotnet run --project samples/ECS2026MAF.Evals

using System.Text;

Console.OutputEncoding = Encoding.UTF8;

await ShowMenuAsync();

static async Task ShowMenuAsync()
{
    while (true)
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║                ECS 2026 — AgentEval Evaluation Demos                        ║
╠══════════════════════════════════════════════════════════════════════════════╣
║                                                                              ║
║   1  TravelAgent Evals       Behavioral policies + tool assertions           ║
║   2  TripPlanner Evals       Workflow structure + tool-level assertions      ║
║   3  Hypothesis Comparison   Load snapshots — side-by-side (no LLM calls)   ║
║   4  Stochastic Agent        5-run reliability test · single agent           ║
║   5  Stochastic Workflow     5-run reliability test · 4-agent pipeline       ║
║                                                                              ║
║   Q  Quit                                                                    ║
║                                                                              ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        if (!Config.IsConfigured)
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
            case '1': await Eval01_TravelAgentEvals.RunAsync();        break;
            case '2': await Eval02_TripPlannerEvals.RunAsync();        break;
            case '3': await Eval03_HypothesisComparison.RunAsync();    break;
            case '4': await Eval04_StochasticAgent.RunAsync();         break;
            case '5': await Eval05_StochasticWorkflow.RunAsync();      break;
            case 'q' or 'Q': return;
        }

        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey(intercept: true);
    }
}
