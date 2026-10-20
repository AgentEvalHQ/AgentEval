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
//   --model <model>      Override the model (Azure: deployment) for this run only, whichever
//                        provider AI_INFERENCE_PROVIDER selects (azure | bitdeer | openai | foundry | openai-compatible)
//   --show-model         Show the provider, model and endpoint on the console. HIDDEN by default — the demo
//                        is presented on stage. TRAVELDEMO_SHOW_MODEL=1 does the same.
//   --hide-model         Keep them hidden even when TRAVELDEMO_SHOW_MODEL=1 is set (wins over --show-model too).
//   --help | -h          Print this usage
// Examples:
//   dotnet run -- 1
//   dotnet run -- 1 --log
//   dotnet run -- 1 --model gpt-4o-mini --log ./logs/run.log
//   dotnet run -- 1 --show-model
var (demoArg, parsedArgs) = ParseArgs(args);
if (parsedArgs.Help)
{
    PrintUsage();
    return;
}
if (parsedArgs.ModelValueMissing)
{
    // Refused rather than guessed: running on the default model would not be what --model asked for.
    Console.Error.WriteLine("--model needs a model name, e.g. --model gpt-4o-mini (run with --help for usage).");
    Environment.ExitCode = 2;
    return;
}
IDisposable? logScope = null;
if (parsedArgs.LogRequested) logScope = ConsoleLogRecorder.StartLogging(parsedArgs.LogPath);
if (!string.IsNullOrEmpty(parsedArgs.ModelOverride)) Config.ModelOverride = parsedArgs.ModelOverride;
// --hide-model wins over --show-model and over TRAVELDEMO_SHOW_MODEL; neither flag leaves the variable in charge.
if (parsedArgs.HideModel) Config.ShowModelRequested = false;
else if (parsedArgs.ShowModel) Config.ShowModelRequested = true;

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
catch (Exception ex)
{
    // Last resort: nothing may reach the runtime's own crash printer, which writes the provider's raw error text
    // (host, model) and runs after --log has been closed. Redacted here, inside the log scope, with exit code 1.
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n  ❌ Unhandled {ex.GetType().Name}: {Config.Redact(ex, includeStackTrace: true)}");
    Console.ResetColor();
    Environment.ExitCode = 1;
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
                // The next token is the model unless it is another flag or a demo selector: `--model --hide-model`
                // used to take "--hide-model" as the model name and drop the flag.
                if (i + 1 < args.Length && args[i + 1] is { Length: > 0 } model
                    && !model.StartsWith('-') && model is not ("1" or "2" or "3" or "4"))
                    parsed.ModelOverride = args[++i];
                else
                    parsed.ModelValueMissing = true;
                break;
            case "--show-model":
                parsed.ShowModel = true;
                break;
            case "--hide-model":
                parsed.HideModel = true;
                break;
            case "--help" or "-h":
                parsed.Help = true;
                break;
            default:
                demo ??= args[i]; // first positional = demo selector
                break;
        }
    }
    return (demo, parsed);
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: AgentEval.TravelDemo [1|2|3|4] [--log [path]] [--model <model>] [--show-model | --hide-model]

          1 | 2 | 3 | 4     Demo selector (skips the interactive menu)
          --log [path]      Tee the console to a log file (path optional → auto-named)
          --model <model>   Override the model (Azure: deployment) for this run only
          --show-model      Show the provider, model and endpoint on the console (hidden by default);
                            TRAVELDEMO_SHOW_MODEL=1 does the same
          --hide-model      Keep them hidden even when TRAVELDEMO_SHOW_MODEL=1 is set (wins over --show-model)
          --help | -h       Print this usage

        The model provider comes from AI_INFERENCE_PROVIDER and that provider's variables (see Config.cs).
        """);
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
            Console.WriteLine("  ⚠️  No model configured.");
            AgentEval.TravelDemo.Config.PrintMissingDetails();
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
    public bool    ModelValueMissing { get; set; }
    public bool    ShowModel     { get; set; }
    public bool    HideModel     { get; set; }
    public bool    Help          { get; set; }
}
