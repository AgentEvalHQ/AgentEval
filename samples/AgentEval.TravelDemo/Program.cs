// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo
//
// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║               AgentEval.TravelDemo — Microsoft Agent Framework Demos                  ║
// ║   Pure MAF code — no AgentEval dependencies in this project                 ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
//
// Run:
//   dotnet run --project samples/AgentEval.TravelDemo

using System.Text;
using AgentEval.TravelDemo;
using AgentEval.TravelDemo.Demos;

Console.OutputEncoding = Encoding.UTF8;

// CLI flags (any order):
//   1 | 2 | 3 | 4        Demo selector (skips the interactive menu)
//   --log [path]         Tee Console.Out + Error to a log file (path optional → auto-named)
//   --model <deployment> Override AZURE_OPENAI_DEPLOYMENT for this run only
// Examples:
//   dotnet run -- 1
//   dotnet run -- 1 --log
//   dotnet run -- 1 --model gpt-4o-mini --log ./logs/run.log
var (demoArg, parsedArgs) = ParseArgs(args);
IDisposable? logScope = null;
if (parsedArgs.LogRequested) logScope = ConsoleLogRecorder.StartLogging(parsedArgs.LogPath);
if (!string.IsNullOrEmpty(parsedArgs.ModelOverride)) Config.ModelOverride = parsedArgs.ModelOverride;

try
{
    if (demoArg is not null)
    {
        switch (demoArg)
        {
            case "1": await Demo01_TravelAgent.RunAsync();         return;
            case "2": await Demo02_TripPlannerWorkflow.RunAsync(); return;
            case "3": await Demo03_LiveDemo.RunAsync();            return;
            case "4": await Demo03_LiveDemoComplete.RunAsync();    return;
            default:
                Console.Error.WriteLine($"Unknown demo selector '{demoArg}'. Valid: 1 | 2 | 3 | 4.");
                Environment.Exit(2);
                return;
        }
    }

    await ShowMenuAsync();
}
finally
{
    logScope?.Dispose();
}

static (string? Demo, ParsedArgs Parsed) ParseArgs(string[] args)
{
    string? demo = null;
    var parsed = new ParsedArgs();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--log":
                parsed.LogRequested = true;
                // Optional next token = log path. If it starts with '-' or is a demo digit, treat as absent.
                if (i + 1 < args.Length && args[i + 1] is { Length: > 0 } next
                    && !next.StartsWith('-') && next is not ("1" or "2" or "3" or "4"))
                    parsed.LogPath = args[++i];
                break;
            case "--model":
                if (i + 1 < args.Length) parsed.ModelOverride = args[++i];
                break;
            default:
                demo ??= args[i]; // first positional = demo selector
                break;
        }
    }
    return (demo, parsed);
}

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

        if (!AgentEval.TravelDemo.Config.IsConfigured)
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

// Parsed CLI flags. Local to this Program for argument routing.
sealed class ParsedArgs
{
    public bool    LogRequested  { get; set; }
    public string? LogPath       { get; set; }
    public string? ModelOverride { get; set; }
}
