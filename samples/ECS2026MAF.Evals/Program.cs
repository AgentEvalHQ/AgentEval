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
using ECS2026MAF.Evals;

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
            case '1': await Eval01_TravelAgentEvals.RunAsync();    break;
            case '2': await Eval02_TripPlannerEvals.RunAsync();    break;
            case 'q' or 'Q': return;
        }

        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey(intercept: true);
    }
}
