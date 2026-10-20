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
//   --model <model>       Override the model (Azure: deployment) for this run only — agent AND judge,
//                         whichever provider AI_INFERENCE_PROVIDER selects
//   --show-model          Show the provider, model and endpoint on the console. HIDDEN by default — the demo
//                         is presented on stage. TRAVELDEMO_SHOW_MODEL=1 does the same. Only the console changes:
//                         the snapshots Eval 3 compares record the provider and the model either way.
//   --hide-model          Keep them hidden even when TRAVELDEMO_SHOW_MODEL=1 is set (wins over --show-model too).
//   --help | -h           Print this usage
// Examples:
//   dotnet run -- 1
//   dotnet run -- 1 --log
//   dotnet run -- 1 --model gpt-4o-mini --log ./logs/eval01.log
//   dotnet run -- 1 --show-model
// --selftest: the deterministic eval's invariants, offline. It must NOT touch Config, which
// reads credentials (AgentEval.TravelDemo/Config.cs, Config.Settings) — an interview demo that needs a key is a
// demo that fails in the room, and this check is about the projection, not about a live model.
if (args.Contains("--selftest", StringComparer.Ordinal))
{
    var bookFlight = await BookFlightSelfTest.RunAsync();
    var plumbing   = TravelDemoSelfTest.Run();
    Environment.Exit(bookFlight != 0 || plumbing != 0 ? 1 : 0);
}

var (evalArg, parsedArgs) = ParseArgs(args);
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
    // TravelDemo resolves its provider with its own copy of the rules (it does not reference AgentEval).
    // Stop here, before any model call, if that copy has drifted from AgentEval's resolver.
    if (!ProviderDriftGuard.Check())
    {
        Environment.ExitCode = 2;
        return;
    }

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
                Environment.ExitCode = 2;
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
                // The next token is the model unless it is another flag or an eval selector: `--model --hide-model`
                // used to take "--hide-model" as the model name and drop the flag.
                if (i + 1 < args.Length && args[i + 1] is { Length: > 0 } model
                    && !model.StartsWith('-') && model is not ("1" or "2" or "3" or "4" or "5"))
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
                eval ??= args[i]; // first positional = eval selector
                break;
        }
    }
    return (eval, parsed);
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: AgentEval.TravelDemo.Evals [1..5] [--log [path]] [--model <model>] [--show-model | --hide-model]
               AgentEval.TravelDemo.Evals --selftest

          1..5              Eval selector (skips the interactive menu)
          --log [path]      Tee the console to a log file (path optional → auto-named)
          --model <model>   Override the model (Azure: deployment) for this run only — agent AND judge
          --show-model      Show the provider, model and endpoint on the console (hidden by default);
                            TRAVELDEMO_SHOW_MODEL=1 does the same
          --hide-model      Keep them hidden even when TRAVELDEMO_SHOW_MODEL=1 is set (wins over --show-model)
          --selftest        Offline invariants: the deterministic eval, provider resolution vs AgentEval,
                            redaction, run accounting and the console output (no model, no network)
          --help | -h       Print this usage

        The model provider comes from AI_INFERENCE_PROVIDER and that provider's variables
        (see samples/AgentEval.TravelDemo/Config.cs).
        """);
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
            Console.WriteLine("  ⚠️  No model configured — evals 1, 2, 4 and 5 need one (3 reads saved snapshots).");
            Config.PrintMissingDetails();
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
    public bool    ModelValueMissing { get; set; }
    public bool    ShowModel     { get; set; }
    public bool    HideModel     { get; set; }
    public bool    Help          { get; set; }
}
