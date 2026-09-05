// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║        Galaxus.RecommendationAgent — Microsoft Agent Framework demo          ║
// ║   Pure MAF + Azure OpenAI. No AgentEval dependency, exactly like TravelDemo. ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
//
// Run:
//   dotnet run --project samples/Galaxus.RecommendationAgent
//   dotnet run --project samples/Galaxus.RecommendationAgent -- 1                     Demo 01, the single agent
//   dotnet run --project samples/Galaxus.RecommendationAgent -- 2                     Demo 02, the discovery loop
//   dotnet run --project samples/Galaxus.RecommendationAgent -- 2 --offline
//   dotnet run --project samples/Galaxus.RecommendationAgent -- 2 --no-personalization
//   dotnet run --project samples/Galaxus.RecommendationAgent -- 1 --user USR-MI-02
//   dotnet run --project samples/Galaxus.RecommendationAgent -- 0                     prove the terminations
//   dotnet run --project samples/Galaxus.RecommendationAgent -- --rebuild-embeddings

using System.Text;
using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Retrieval;

// FIRST executable line, before anything writes to the console: without it every box-drawing
// character in this sample is mojibake on a default Windows terminal.
Console.OutputEncoding = Encoding.UTF8;

// CLI flags (any order):
//   0 .. 9                  Menu entry (skips the interactive menu). 1 and 3-7 run Demo 01, 2 and
//                           8-9 run Demo 02, and 0 runs Demo 02's termination proof. The two
//                           headline selectors are 1 and 2 — the design's §F demo script types
//                           exactly `-- 1` and then `-- 2` — and the rest are conveniences that
//                           only differ from those two by a persona or a toggle. See ShowMenuAsync.
//   --user <USR-XX-NN>      Persona to run. Overrides the digit's persona.
//   --no-personalization    §F.6 opt-out: history is not read, the turn runs on stated need.
//   --offline               Skip the model entirely; run the deterministic baseline arm.
//   --rebuild-embeddings    Regenerate Data/*.embeddings.json from a LIVE embedding model.
//   --model <deployment>    Override AZURE_OPENAI_DEPLOYMENT for this run only.
//   --embedding-model <d>   Override AZURE_OPENAI_EMBEDDING_DEPLOYMENT for this run only.
//   --model-timeout <secs>  Demo 02 only: wall-clock ceiling on ONE model call.
//   --log [path]            Tee Console.Out + Error to a log file (path optional).
//   --help | -h | -?        Print this list and exit.
var parsed = ParseArgs(args);

if (parsed is null)
{
    // An unknown flag is a TYPO, and a typo that is silently swallowed here spends money: the
    // old parser folded '--offlien' into the positional selector, so `-- 1 --offlien` ran a
    // live, paid turn while the operator believed they had asked for the offline arm.
    Console.Error.WriteLine("Unknown or incomplete argument. Run with --help for the flag list.");
    Environment.ExitCode = 2;
    return;
}

if (parsed.HelpRequested)
{
    PrintUsage();
    return;
}

IDisposable? logScope = null;
if (parsed.LogRequested) logScope = ConsoleLogRecorder.StartLogging(parsed.LogPath);
if (!string.IsNullOrEmpty(parsed.ModelOverride)) Config.ModelOverride = parsed.ModelOverride;
if (!string.IsNullOrEmpty(parsed.EmbeddingModelOverride)) Config.EmbeddingModelOverride = parsed.EmbeddingModelOverride;

// Before ANY retriever is built. EmbeddingSpace throws if this moves after something resolved,
// which is the guard against one run reporting numbers from two incomparable vector spaces.
EmbeddingSpace.Requested = parsed.Space;

try
{
    if (parsed.RebuildEmbeddings)
    {
        await RebuildEmbeddingsAsync();
        return;
    }

    if (parsed.Selector is not null)
    {
        if (!TryResolveSelector(parsed, out var choice))
        {
            Console.Error.WriteLine($"Unknown selector '{parsed.Selector}'. Valid: 0-9. Run with --help.");

            // ExitCode rather than Environment.Exit: Exit() tears the process down without
            // running the finally below, so the log scope never flushes and closes.
            Environment.ExitCode = 2;
            return;
        }

        await RunChoiceAsync(choice, parsed.ModelTimeoutSeconds);
        return;
    }

    await ShowMenuAsync(parsed);
}
finally
{
    logScope?.Dispose();
}

// ── Argument parsing ──────────────────────────────────────────────────────────

// Returns null when an argument is not understood — the caller then exits 2.
//
// ⚠ Mirrors Galaxus.RecommendationAgent.Evals' parser deliberately. The previous version ended in
// `parsed.Selector ??= args[i]`, which swallowed an unknown flag once a selector had been seen:
// `-- 1 --offlien` ran a LIVE, paid turn while reading as a request for the offline arm. A flag
// with a leading '-' that this switch does not name is a typo, and a missing value for a flag
// that needs one is a typo too. Both now refuse rather than no-op.
static ParsedArgs? ParseArgs(string[] args)
{
    var parsed = new ParsedArgs();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--help" or "-h" or "-?":
                parsed.HelpRequested = true;
                break;

            case "--log":
                parsed.LogRequested = true;
                // An optional next token is the log path. A leading '-' or a bare menu digit
                // means the path was omitted, not that the user wants a file called "1".
                if (i + 1 < args.Length && args[i + 1] is { Length: > 0 } next
                    && !next.StartsWith('-') && !IsSelector(next))
                    parsed.LogPath = args[++i];
                break;

            case "--user":
                if (i + 1 >= args.Length) return null;
                parsed.UserId = args[++i];
                break;

            case "--model":
                if (i + 1 >= args.Length) return null;
                parsed.ModelOverride = args[++i];
                break;

            case "--embedding-model":
                if (i + 1 >= args.Length) return null;
                parsed.EmbeddingModelOverride = args[++i];
                break;

            case "--model-timeout":
                // Demo 02 only. A stalled deployment must DEGRADE, not queue — lower this to
                // watch every model-backed stage fall back and the loop still answer.
                if (i + 1 >= args.Length) return null;
                if (!int.TryParse(args[++i], System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    || seconds <= 0)
                    return null;
                parsed.ModelTimeoutSeconds = seconds;
                break;

            case "--no-personalization" or "--no-personalisation":
                parsed.NoPersonalization = true;
                break;

            case "--offline":
                parsed.Offline = true;
                break;

            case "--rebuild-embeddings":
                parsed.RebuildEmbeddings = true;
                break;

            // ── Which embedding SPACE retrieves. Parsed here and applied below, before any
            //    retriever exists, because EmbeddingSpace refuses to change once resolved.
            case "--concept-vectors":
                if (parsed.Space is EmbeddingSpaceChoice.RealVectors) return null;
                parsed.Space = EmbeddingSpaceChoice.ConceptVectors;
                break;

            case "--real-vectors":
                if (parsed.Space is EmbeddingSpaceChoice.ConceptVectors) return null;
                parsed.Space = EmbeddingSpaceChoice.RealVectors;
                break;

            default:
                // A leading '-' that reached here is an unrecognised flag, not a selector. And a
                // SECOND positional is a typo, not a second selector.
                if (args[i].StartsWith('-')) return null;
                if (parsed.Selector is not null) return null;
                parsed.Selector = args[i];
                break;
        }
    }

    return parsed;
}

static bool IsSelector(string token) =>
    token is "0" or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9";

// Maps a menu digit to a demo, a persona and its toggles. An explicit flag always wins over the
// digit's default, so `-- 2 --offline` runs the discovery loop through the deterministic arm and
// `-- 3 --offline` runs Marco's gift trap through Demo 01's baseline arm.
//
// 1 and 3-7 are Demo 01 (the single agent). 2, 8 and 9 are Demo 02 (the discovery loop). The DEMO
// is part of the resolved choice rather than inferred at the call site, so a new selector cannot
// silently route to the wrong demo.
//
// ⚠ 1 = Demo 01 and 2 = Demo 02 is a CONTRACT, not a convenience. The design's §F demo script is
// nine minutes of typing `-- 1`, talking, then typing `-- 2`; a numbering in which `-- 2` ran a
// different persona of the SAME demo would break the script in the room. Marco, Sofia and Luca
// moved to 3, 4 and 5 for that reason. Nothing outside this file referenced the old digits.
static bool TryResolveSelector(ParsedArgs parsed, out (int Demo, string UserId, bool NoPersonalization, bool Offline) choice)
{
    var (demo, defaultUser, defaultNoPersonalization, defaultOffline) = parsed.Selector switch
    {
        // 0 is Demo 02's termination proof: it forces each stop condition and discriminates it
        // from the other two. Offline, deterministic, no cost — and the persona is fixed because
        // the probes assert on the loop, not on the customer.
        "0" => (3, Galaxus.RecommendationAgent.Workflows.DiscoveryTerminationProbe.ProbeUserId, false, true),

        // ── The two headline selectors, in the order the demo script types them. ──────
        // Demo 02 defaults to MARCO, not Nadia, because after the corpus extension the reviewer
        // approves Nadia's coverage in round 1 — the loop-back edge never fires, so the demo's
        // visual payload is invisible. Marco's coverage leaves gaps and the loop runs 3 rounds.
        // ⚠ This picks the persona on which the MECHANISM is exercised. It is NOT evidence that
        //   the loop produces better recommendations — at equal k it does not, and Eval 02 says
        //   so. For the same-customer comparison against Demo 01, run `-- 2 --user USR-NB-01`;
        //   Nadia's run is the loop correctly declining to spend a second round.
        "1" => (1, GalaxusDemoPrompts.NadiaUserId, false, false),
        "2" => (2, GalaxusDemoPrompts.MarcoUserId, false, false),

        // ── Demo 01, the other personas and toggles. ─────────────────────────────────
        "3" => (1, GalaxusDemoPrompts.MarcoUserId, false, false),
        "4" => (1, GalaxusDemoPrompts.SofiaUserId, false, false),
        "5" => (1, GalaxusDemoPrompts.LucaUserId,  false, false),
        "6" => (1, GalaxusDemoPrompts.NadiaUserId, true,  false),
        "7" => (1, GalaxusDemoPrompts.NadiaUserId, false, true),

        // ── Demo 02's toggles. Both are `-- 2` plus a flag; the digits are shorthand. ─
        "8" => (2, GalaxusDemoPrompts.NadiaUserId, false, true),
        "9" => (2, GalaxusDemoPrompts.NadiaUserId, true,  true),
        _   => (0, string.Empty, false, false),
    };

    if (defaultUser.Length == 0)
    {
        choice = default;
        return false;
    }

    choice = (
        demo,
        string.IsNullOrWhiteSpace(parsed.UserId) ? defaultUser : parsed.UserId.Trim(),
        parsed.NoPersonalization || defaultNoPersonalization,
        parsed.Offline || defaultOffline);

    return true;
}

// The one place a resolved choice becomes a run.
static async Task RunChoiceAsync(
    (int Demo, string UserId, bool NoPersonalization, bool Offline) choice,
    int? modelTimeoutSeconds = null)
{
    if (choice.Demo == 3)
    {
        var probes = await Galaxus.RecommendationAgent.Workflows.DiscoveryTerminationProbe.RunAllAsync();
        Galaxus.RecommendationAgent.Workflows.DiscoveryTerminationProbe.Print(probes);
        if (probes.Any(p => !p.Passed)) Environment.ExitCode = 1;
        return;
    }

    if (choice.Demo == 2)
    {
        await Demo02_InterestMapWorkflow.RunAsync(
            choice.UserId, choice.NoPersonalization, choice.Offline,
            Galaxus.RecommendationAgent.Workflows.DiscoveryState.DefaultMaxDiscoveryRounds,
            modelTimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
        return;
    }

    await Demo01_RecommendationAgent.RunAsync(choice.UserId, choice.NoPersonalization, choice.Offline);
}

// ── Menu ──────────────────────────────────────────────────────────────────────

// ── Console helpers that survive a non-terminal ──────────────────────────────────────────
//
// Console.Clear() throws IOException("The handle is invalid.") as soon as stdout is redirected —
// every CI run, every `... | tee`, every `> file`. Console.ReadKey needs a real console too, and
// the EOF → 'q' mapping is load-bearing: without it the `while (true)` menu spins forever on
// redirected input instead of exiting.
static void ClearIfInteractive()
{
    if (Console.IsOutputRedirected) return;
    try { Console.Clear(); }
    catch (IOException) { /* not a console after all — the menu still prints */ }
}

static char ReadMenuKey()
{
    if (!Console.IsInputRedirected)
    {
        try { return Console.ReadKey(intercept: true).KeyChar; }
        catch (InvalidOperationException) { /* fall through to the redirected path */ }
    }

    int read = Console.Read();
    return read >= 0 ? (char)read : 'q';   // EOF means "no more input" — quit, never spin.
}

static async Task ShowMenuAsync(ParsedArgs parsed)
{
    while (true)
    {
        ClearIfInteractive();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║          Galaxus — Robin, the recommendation agent (MAF demo)                ║
╠══════════════════════════════════════════════════════════════════════════════╣
║                                                                              ║
║   ── The demo script, in the order it is told ──────────────────────────     ║
║   1  Demo 01 · Nadia, ONE AGENT    Latent interest. It misses, and no ledger ║
║                                    exists that would have told you so.       ║
║   2  Demo 02 · Nadia, THE LOOP     5 executors, 1 loop-back edge, cap 3.     ║
║                                    Add --offline for the zero-cost arm.      ║
║                                                                              ║
║   ── Demo 01 · the other personas and toggles ──────────────────────────     ║
║   3  Marco  · gift trap            Two gift purchases, suppressed in code    ║
║   4  Sofia  · replenish + gap      Owns the beans, owns no grinder           ║
║   5  Luca   · thin signal          The abstention gate, before any spend     ║
║   6  Nadia, personalization OFF    §F.6 — the tool refuses, not the prompt   ║
║   7  Nadia, OFFLINE baseline       No model call. The arm to compare against ║
║                                                                              ║
║   ── Demo 02 · toggles and the termination proof ───────────────────────     ║
║   8  Nadia, LOOP offline           The loop's mechanics at zero cost         ║
║   9  Nadia, LOOP offline, no pers. §F.6 through the loop                     ║
║   0  PROVE the three terminations  Forces each stop, discriminates each one  ║
║                                                                              ║
║   R  Rebuild the embedding assets  (needs a live embedding deployment)       ║
║   Q  Quit                                                                    ║
║                                                                              ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        if (!Config.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️  Azure OpenAI credentials not found — entries 1-6 need them.");
            Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT.");
            Console.WriteLine("     Entries 0, 7, 8 and 9 (offline) run with no key at all.\n");
            Console.ResetColor();
        }

        Console.Write("  Select: ");
        var key = ReadMenuKey();
        Console.WriteLine();

        if (key is 'q' or 'Q') return;

        if (key is 'r' or 'R')
        {
            await RebuildEmbeddingsAsync();
        }
        else
        {
            var selection = parsed with { Selector = key.ToString() };
            if (!TryResolveSelector(selection, out var choice))
            {
                continue;
            }

            await RunChoiceAsync(choice, parsed.ModelTimeoutSeconds);
        }

        Console.WriteLine("\nPress any key to return to the menu...");
        _ = ReadMenuKey();
    }
}

// ── --rebuild-embeddings ──────────────────────────────────────────────────────

// Regenerates the two committed embedding assets from a LIVE embedding deployment.
//
// The builder refuses an offline source unless told otherwise, and it is not told otherwise
// here: writing authored concept vectors into a file named catalogue.embeddings.json would
// invite the next reader to believe it holds real text-embedding-3-small output. A refusal
// that says why beats a file that lies.
static async Task RebuildEmbeddingsAsync()
{
    var catalogue = Catalogue.Default;

    if (!AzureEmbeddingSource.TryCreate(out var source, out var reason))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  ⚠️  Cannot rebuild the embedding assets: {reason}");
        Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY and (optionally)");
        Console.WriteLine("     AZURE_OPENAI_EMBEDDING_DEPLOYMENT, then run --rebuild-embeddings again.");
        Console.WriteLine("     The demo itself needs none of this: its default retrieval path is offline.");
        Console.ResetColor();
        return;
    }

    using (source)
    {
        var report = await EmbeddingCacheBuilder.RunAsync(catalogue.All, source!);
        if (report is null) return;   // the builder printed its own refusal

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠️  Restore the two <EmbeddedResource> lines in Galaxus.RecommendationAgent.csproj");
        Console.WriteLine("     in the SAME commit that adds these files, or they ship unreferenced.");
        Console.ResetColor();
    }
}

// ── Usage ─────────────────────────────────────────────────────────────────────

static void PrintUsage()
{
    Console.WriteLine(@"
Galaxus.RecommendationAgent — Demo 01 (Robin, the single agent) and Demo 02 (the
bounded discovery loop), plus the loop's termination proof.

  dotnet run --project samples/Galaxus.RecommendationAgent [-- <selector>] [flags]

The two headline selectors, in the order the demo script types them:
  1   Demo 01 — Nadia Brunner (USR-NB-01) through the SINGLE AGENT
  2   Demo 02 — Marco Iten (USR-MI-02) through the DISCOVERY LOOP
      Marco's coverage leaves gaps, so the loop-back edge fires and the loop runs 3
      rounds — the mechanism this demo exists to show. It is NOT a claim that the loop
      recommends better: at equal k it does not, and Eval 02 reports that. For the
      same-customer comparison against Demo 01, run: -- 2 --user USR-NB-01 (Nadia's
      coverage is sufficient in round 1, so the loop declines to spend a second one).
      Add --offline to either one for the deterministic, zero-cost arm.

Selectors 3-7 are Demo 01's other personas and toggles:
  3   Marco Iten      USR-MI-02   the gift trap
  4   Sofia Keller    USR-SK-03   replenishment lane + capability gap
  5   Luca Ferrari    USR-LF-04   thin signal, the abstention gate fires
  6   Nadia with personalization OFF
  7   Nadia through the OFFLINE baseline arm (no model call)

Selectors 8-9 are Demo 02's toggles — each is `-- 2` plus a flag, kept as a digit
so the offline loop is one keystroke from the menu:
  8   Nadia through the loop, OFFLINE   the loop's mechanics with no model call
  9   Nadia through the loop, OFFLINE, personalization OFF

Selector 0 proves the loop's three terminations (offline, no cost, exit code 1 on failure):
  0   Forces the round cap, no-progress and gaps-unresolvable in turn, and shows why each
      outcome could NOT have been produced by the other two. Also checks the loop-back edge
      and the D-3 vocabulary constraint in BOTH directions.

Flags:
  --user <USR-XX-NN>     Run this persona instead of the selector's default.
  --no-personalization   §F.6 opt-out. GetPurchaseHistory and GetInterestMap refuse;
                         the turn runs on what the customer says in this conversation.
  --offline              Skip the model. The deterministic retrieval + guardrail path
                         selects the products, and the panel says so. This is the
                         baseline arm — the thing a claim about the agent needs.
  --rebuild-embeddings   Regenerate Data/*.embeddings.json from a live embedding model.
  --concept-vectors      Retrieve in the authored 24-dimension concept space. THE DEFAULT.
                         It embeds ANY text with no key, so a query composed at run time
                         still reaches the dense leg.
  --real-vectors         Retrieve in the committed text-embedding-3-small space instead
                         (Data/*.embeddings.json, still no key, nothing spent). Real
                         vectors — but the QUERY side of a cache holds only text somebody
                         anticipated, and a run-time-composed need is a miss, which turns
                         that search LEXICAL-ONLY. Both spaces are printed in the banner.
  --model <deployment>   Override AZURE_OPENAI_DEPLOYMENT for this run only.
  --embedding-model <d>  Override AZURE_OPENAI_EMBEDDING_DEPLOYMENT for this run only.
  --model-timeout <secs> Demo 02 only. Wall-clock ceiling on ONE model call (default 60).
                         A stalled deployment must DEGRADE, not queue: lower this to watch
                         every model-backed stage fall back and the loop still answer.
  --log [path]           Tee stdout and stderr to a log file.
  --help                 This text.

With no selector an interactive menu is shown.
");
}

// Parsed CLI flags. Local to this Program for argument routing.
sealed record ParsedArgs
{
    public string? Selector { get; set; }
    public string? UserId { get; set; }
    public bool NoPersonalization { get; set; }
    public bool Offline { get; set; }
    public bool RebuildEmbeddings { get; set; }
    public bool HelpRequested { get; set; }
    public bool LogRequested { get; set; }
    public string? LogPath { get; set; }
    public string? ModelOverride { get; set; }
    public string? EmbeddingModelOverride { get; set; }
    public int? ModelTimeoutSeconds { get; set; }

    /// <summary>Which embedding space to retrieve in. Auto lets <c>EmbeddingSpace</c> decide.</summary>
    public EmbeddingSpaceChoice Space { get; set; } = EmbeddingSpaceChoice.Auto;
}
