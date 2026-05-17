// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo
//
// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║           AgentEval.TravelDemo.Evals — AgentEval Assertions & Metrics                 ║
// ║   Pure AgentEval evaluation code — agents live in AgentEval.TravelDemo               ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
//
// Run:
//   dotnet run --project samples/AgentEval.TravelDemo.Evals

using System.Text;
using AgentEval.TravelDemo;

Console.OutputEncoding = Encoding.UTF8;

// CLI flags (any order):
//   1..5                  Eval selector (skips the interactive menu)
//   --log [path]          Tee Console.Out + Error to a log file (path optional → auto-named)
//   --model <deployment>  Override AZURE_OPENAI_DEPLOYMENT for this run only
// Examples:
//   dotnet run -- 1
//   dotnet run -- 1 --log
//   dotnet run -- 1 --model gpt-4o-mini --log ./logs/eval01.log
var (evalArg, parsedArgs) = ParseArgs(args);
IDisposable? logScope = null;
if (parsedArgs.LogRequested) logScope = ConsoleLogRecorder.StartLogging(parsedArgs.LogPath);
if (!string.IsNullOrEmpty(parsedArgs.ModelOverride)) Config.ModelOverride = parsedArgs.ModelOverride;

try
{
    if (evalArg is not null)
    {
        switch (evalArg)
        {
            case "1": await Eval01_TravelAgentEvals.RunAsync(); return;
            case "2": await Eval02_TripPlannerEvals.RunAsync(); return;
            case "3": await Eval03_HypothesisComparison.RunAsync(); return;
            case "4": await Eval04_StochasticAgent.RunAsync(); return;
            case "5": await Eval05_StochasticWorkflow.RunAsync(); return;
            default:
                Console.Error.WriteLine($"Unknown eval id: {evalArg}. Valid: 1..5.");
                return;
        }
    }

    await ShowMenuAsync();
}
finally
{
    logScope?.Dispose();
}

static (string? Eval, ParsedArgs Parsed) ParseArgs(string[] args)
{
    string? eval = null;
    var parsed = new ParsedArgs();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--log":
                parsed.LogRequested = true;
                if (i + 1 < args.Length && args[i + 1] is { Length: > 0 } next
                    && !next.StartsWith('-') && next is not ("1" or "2" or "3" or "4" or "5"))
                    parsed.LogPath = args[++i];
                break;
            case "--model":
                if (i + 1 < args.Length) parsed.ModelOverride = args[++i];
                break;
            default:
                eval ??= args[i]; // first positional = eval selector
                break;
        }
    }
    return (eval, parsed);
}

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

// Parsed CLI flags. Local to this Program for argument routing.
sealed class ParsedArgs
{
    public bool    LogRequested  { get; set; }
    public string? LogPath       { get; set; }
    public string? ModelOverride { get; set; }
}
