// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║   Galaxus.RecommendationAgent.Evals — the evaluation suite                    ║
// ║   Pure AgentEval evaluation code — the agent lives in Galaxus.RecommendationAgent ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
//
// Run:
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 1
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 2 --quick --log
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 2b         # stated-need precision (offline arms run without a key)
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 2c         # held-out next purchase, hit-rate@k
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 3          # negative controls
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- cal --concept-vectors   # threshold calibration
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 4          # D7 review injection
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 5          # judged quality
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 6          # tool trajectory
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 7          # workflow topology
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 8          # repeated-run stability
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 9          # A/B: agent vs workflow
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- --ci --dry-run   # free, whole suite
//   dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- --ci             # PAID, whole suite
//
// Exit codes (DEVIATION from TravelDemo.Evals, which has none and exits 0 on a failing gate):
//   0  every gate that ran passed
//   1  a gate failed
//   2  bad arguments, or an eval was misdriven
//   3  nothing was measured — credentials missing, or an eval was excluded from this invocation
//
// ⚠ 3 IS NOT 0, and that is the point of this suite's most recent correction. Six evals used to
//   end their credentials check with `return ci ? 3 : 0`, so a human running `-- 5` with no key
//   got exit 0 — the same code a passing gate returns. See CredentialGuard.cs, which is now the
//   only place in the project that decides what a missing model means.

using System.Text;
using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Evals;
using Galaxus.RecommendationAgent.Retrieval;

Console.OutputEncoding = Encoding.UTF8;

// ══ THE ONE BINDING ══ Demo 2's real MAF discovery loop enters the eval suite here and nowhere
// else. Called before any eval runs, exactly once, so no arm can be constructed before it and read
// the loop as absent.
//
// Two properties of this binding are load-bearing and are printed by the evals themselves:
//   · the bound arm runs the loop on its DETERMINISTIC path — no model call, no credentials — so
//     Evals 03, 04 and 07 keep their stated "needs nothing" property and `-- 2 --dry-run` keeps
//     spending nothing. Its numbers are about the loop's MECHANICS, never about the agent.
//   · it is NOT entered in Eval 02's sign test against the live agent, because that pairing would
//     vary architecture and model presence at once. Eval 09 runs a SEPARATE, model-backed
//     workflow arm precisely so that comparison can be made honestly.
// Both are stated at DiscoveryLoopAdapter and repeated in Docs/MEASUREMENT_STATUS.md §6.
Galaxus.RecommendationAgent.Evals.Adapters.DiscoveryLoopAdapter.Bind(
    request => new Galaxus.RecommendationAgent.Evals.Adapters.RealDiscoveryLoopArm(request));

var parsed = ParsedArgs.Parse(args);
if (parsed is null)
{
    Console.Error.WriteLine(
        "Usage: [1..9|2b|2c|cal] [--ci] [--skip-slow] [--quick] [--judge] [--dry-run] [--only <persona-id>] "
      + "[--concept-vectors|--real-vectors] [--log [path]] [--model <deployment>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  cal          derive the three SPACE-DEPENDENT thresholds for the resolved space. Spends only");
    Console.Error.WriteLine("               on --real-vectors, and only to embed the interest labels. Run --concept-vectors");
    Console.Error.WriteLine("               FIRST: it is where the transported operating point is read.");
    Console.Error.WriteLine("  --ci         run every eval 1..9 (plus 2b and 2c) in order and return the WORST exit code");
    Console.Error.WriteLine("  --skip-slow  with --ci: leave out Evals 08 and 09 (tens of paid turns each).");
    Console.Error.WriteLine("               They are then reported as exit 3 — NOT RUN — never as passes.");
    Console.Error.WriteLine("  --quick      fewer repetitions in Evals 02, 02b, 02c, 08 and 09");
    Console.Error.WriteLine("  --judge      Eval 01's ADVISORY justification judge. Never changes a gate.");
    Console.Error.WriteLine("  --dry-run    stub models everywhere: real code path, nothing spent. Evals 03, 04 and 07");
    Console.Error.WriteLine("               call no model, so --ci --dry-run runs all three FOR REAL and they DO persist");
    Console.Error.WriteLine("               — the closing banner names every snapshot the run actually wrote. `-- 7");
    Console.Error.WriteLine("               --dry-run` by hand is a one-case PLUMBING check and is not the eval.");
    Console.Error.WriteLine("  --only <id>  Evals 02, 02b and 02c: run ONE case (stage two of the run protocol). 02 takes a");
    Console.Error.WriteLine("               persona id, 02b a case id (SN-01…), 02c a customer id (USR-NB-01…). The snapshot");
    Console.Error.WriteLine("               goes to a probe key and never overwrites the full-cohort record. NOT honoured under");
    Console.Error.WriteLine("               --ci for 02b/02c — a CI chain must never be silently narrowed to one case.");
    Console.Error.WriteLine("  --concept-vectors  score in the authored 24-dimension concept space. THE DEFAULT —");
    Console.Error.WriteLine("                     deterministic, no key, identical on every machine, so two runs of");
    Console.Error.WriteLine("                     this suite cannot silently score in two spaces.");
    Console.Error.WriteLine("  --real-vectors     score in the real text-embedding-3-small space: the 99 committed");
    Console.Error.WriteLine("               PRODUCT vectors, with every QUERY embedded LIVE at search time. NEEDS");
    Console.Error.WriteLine("               CREDENTIALS AND SPENDS; with no key it falls back to the concept space and");
    Console.Error.WriteLine("               says so. Numbers from the two spaces are NOT comparable, and a scored run");
    Console.Error.WriteLine("               here is NOT reproducible off this machine; every report prints which it used.");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {CredentialGuard.NeedsModelSummary}");
    Console.Error.WriteLine("  Evals 2b and 2c are HYBRID: their offline arms run and print without a key; their live column");
    Console.Error.WriteLine("  then reads NOT MEASURED and they exit 3.");
    return 2;
}

IDisposable? logScope = null;
if (parsed.LogRequested) logScope = ConsoleLogRecorder.StartLogging(parsed.LogPath);
if (!string.IsNullOrEmpty(parsed.ModelOverride)) Config.ModelOverride = parsed.ModelOverride;

// Before EvalRuntime builds anything. EmbeddingSpace refuses to move once resolved, so a suite
// cannot end up with one eval's numbers from the concept space and the next one's from
// text-embedding-3-small.
Galaxus.RecommendationAgent.Retrieval.EmbeddingSpace.Requested = parsed.Space;

try
{
    if (parsed.Ci) return await RunCiAsync(parsed);

    if (parsed.Eval is not null)
    {
        return parsed.Eval switch
        {
            "1" => await Eval01_CatalogueIntegrity.RunAsync(judge: parsed.Judge, dryRun: parsed.DryRun),
            "2" => await Eval02_LatentInterestCoverage.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun, onlyPersona: parsed.OnlyPersona),
            "2b" => await Eval02b_StatedNeedSatisfaction.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun, onlyCase: parsed.OnlyPersona),
            "2c" => await Eval02c_HeldOutNextPurchase.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun, onlyCase: parsed.OnlyPersona),
            "3" => await NegativeControls.RunAsync(),
            "cal" => await Galaxus.RecommendationAgent.Evals.Calibration.ThresholdCalibration.RunAsync(),
            "4" => await Eval04_ReviewInjectionContainment.RunAsync(),
            "5" => await Eval05_RecommendationQuality.RunAsync(dryRun: parsed.DryRun),
            "6" => await Eval06_ToolTrajectory.RunAsync(dryRun: parsed.DryRun),
            "7" => await Eval07_WorkflowTopology.RunAsync(dryRun: parsed.DryRun),
            "8" => await Eval08_StochasticStability.RunAsync(
                       runs: null, quick: parsed.Quick, dryRun: parsed.DryRun),
            "9" => await Eval09_HypothesisComparison.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun),
            _ => Unknown(parsed.Eval),
        };
    }

    return await ShowMenuAsync(parsed);
}
finally
{
    logScope?.Dispose();
}

// ══ CI ════════════════════════════════════════════════════════════════════════════════════
//
// ⭐ EVERY eval runs, and the exit code is the WORST one, so a failure cannot hide behind a pass.
//
// This reconciles a disagreement between the agents that wrote Evals 07 and 08. Eval 07's author
// put it in the CI chain (deterministic, credential-free, sub-second — a topology regression must
// not wait for a hand-run menu entry). Eval 08's author deliberately kept it OUT (tens of paid
// turns and tens of minutes is not a CI gate). Both arguments are right about their own eval and
// wrong about the suite: an eval that is not in the chain has its failures reported nowhere at all.
//
// So: the chain runs all nine. `--dry-run` makes the whole thing free and is the form CI should
// actually use. `--skip-slow` exists for the paid form, and what it produces is not a pass — the
// excluded evals are recorded as exit 3, "nothing was measured", exactly like a missing key.
static async Task<int> RunCiAsync(ParsedArgs parsed)
{
    CiStep[] steps =
    [
        new("Eval 01", "catalogue integrity",        NeedsModel: true,  Slow: false,
            () => Eval01_CatalogueIntegrity.RunAsync(judge: parsed.Judge, dryRun: parsed.DryRun)),
        new("Eval 02", "latent-interest coverage",   NeedsModel: true,  Slow: false,
            () => Eval02_LatentInterestCoverage.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun, onlyPersona: parsed.OnlyPersona)),
        // 02b and 02c are HYBRID: their offline arms run without a key, but their SUBJECT is the
        // live column, so a missing key still makes them exit 3 — NeedsModel is true.
        new("Eval 02b", "stated-need precision",     NeedsModel: true,  Slow: false,
            () => Eval02b_StatedNeedSatisfaction.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun)),
        new("Eval 02c", "held-out next purchase",    NeedsModel: true,  Slow: false,
            () => Eval02c_HeldOutNextPurchase.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun)),
        new("Eval 03", "negative controls",          NeedsModel: false, Slow: false,
            () => NegativeControls.RunAsync()),
        new("Eval 04", "review-injection containment", NeedsModel: false, Slow: false,
            () => Eval04_ReviewInjectionContainment.RunAsync()),
        new("Eval 05", "judged recommendation quality", NeedsModel: true, Slow: false,
            () => Eval05_RecommendationQuality.RunAsync(dryRun: parsed.DryRun)),
        new("Eval 06", "tool trajectory",            NeedsModel: true,  Slow: false,
            () => Eval06_ToolTrajectory.RunAsync(dryRun: parsed.DryRun)),
        // ⚠ `dryRun: false`, DELIBERATELY, and it is the third model-free eval to reach this
        //   conclusion. Eval 07 calls no model on any path, so there is nothing for `--dry-run` to
        //   stub; its dry-run form runs ONE case and asserts only the plumbing. Handing it
        //   `parsed.DryRun` made `--ci --dry-run` — the form this file recommends CI actually use —
        //   print "Eval 07: passed" and exit 0 while `-- 7`, the identical free measurement, exits 1
        //   with GATE B ❌. That is precisely the outcome the comment above justifies putting Eval 07
        //   in the chain to prevent: "an eval that is not in the chain has its failures reported
        //   nowhere at all." Evals 03 and 04 take no `dryRun` parameter at all for the same reason
        //   (RUN_PROTOCOL, plan item 8.19); Eval 07 has one for hand use and the chain must not pass
        //   it. Pinned by Eval 03's gating row `CiChainRunsModelFreeEvalsForReal`.
        new("Eval 07", "workflow topology",          NeedsModel: false, Slow: false,
            () => Eval07_WorkflowTopology.RunAsync(dryRun: false)),
        new("Eval 08", "repeated-run stability",     NeedsModel: true,  Slow: true,
            () => Eval08_StochasticStability.RunAsync(
                      runs: null, quick: parsed.Quick, dryRun: parsed.DryRun)),
        new("Eval 09", "agent vs workflow A/B",      NeedsModel: true,  Slow: true,
            () => Eval09_HypothesisComparison.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun)),
    ];

    PrintCiPreflight(steps, parsed);

    var codes = new int[steps.Length];
    var reasons = new string?[steps.Length];

    for (int i = 0; i < steps.Length; i++)
    {
        if (parsed.SkipSlow && steps[i].Slow)
        {
            // NOT a pass. An eval excluded by a flag measured exactly as much as an eval with no
            // key: nothing. Same code, different reason, and the reason is printed beside it.
            codes[i] = CredentialGuard.NotMeasuredExitCode;
            reasons[i] = "excluded by --skip-slow";
            continue;
        }

        codes[i] = await steps[i].Run().ConfigureAwait(false);
    }

    int exit = WorstExit(codes);
    PrintCiSummary(exit, steps, codes, reasons, parsed.DryRun);
    return exit;
}

// Folds the suite's exit codes into one by SEVERITY, never by Math.Max.
//
// It USED to be Math.Max, and 3 > 1, so "Evals 01 and 02 were skipped for want of credentials AND
// Eval 03 failed" exited 3 and printed "Nothing was measured." A real failure was reported as an
// absence of measurement. The rank is explicit here so the ordering is a stated decision rather
// than an accident of integer size:
//
//   worst → an exit code nobody planned for — a bug in the suite, and it must not be swallowed
//         → 1, a gate FAILED — the most serious planned outcome
//         → 2, the suite was misdriven (a bad run count, a bad argument)
//         → 3, nothing was measured
//   best  → 0, passed
static int WorstExit(IReadOnlyList<int> codes)
{
    int worst = 0;
    foreach (int code in codes)
        if (Severity(code) > Severity(worst)) worst = code;
    return worst;

    static int Severity(int code) => code switch
    {
        0 => 0,
        3 => 2,
        2 => 3,
        1 => 4,
        _ => 5,
    };
}

static int Unknown(string eval)
{
    Console.Error.WriteLine($"Unknown eval id: {eval}. Valid: 1..9, 2b, 2c.");
    return 2;
}

static void PrintCiPreflight(IReadOnlyList<CiStep> steps, ParsedArgs parsed)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  ▶ CI — every eval runs; the exit code is the WORST of them.");
    Console.ResetColor();

    Console.ForegroundColor = ConsoleColor.DarkGray;
    foreach (CiStep step in steps)
    {
        string skip = parsed.SkipSlow && step.Slow ? "  ⏭ EXCLUDED by --skip-slow (reported as exit 3)" : "";
        Console.WriteLine($"     · {step.Name} — {step.What}"
                        + (step.NeedsModel ? "  [needs a model]" : "  [no model]")
                        + (step.Slow ? "  [SLOW / PAID]" : "")
                        + skip);
    }
    Console.ResetColor();

    if (parsed.DryRun)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("     --dry-run: every model is a stub. Nothing is spent, nothing is written, and no");
        Console.WriteLine("     number below is a result about the agent or the workflow.");
        Console.ResetColor();
    }
    else if (Config.IsConfigured)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("     ⚠ THIS IS A PAID RUN. Evals 01, 02, 05, 06, 08 and 09 each make live model calls;");
        Console.WriteLine("       08 and 09 make tens of them. Add --dry-run for the free form, --quick to cut the");
        Console.WriteLine("       repetition counts, or --skip-slow to leave 08 and 09 out.");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"     ⚠ No credentials. {CredentialGuard.NeedsModelSummary}");
        Console.WriteLine("       The six that do will exit 3 — NOT MEASURED — and this run cannot exit 0.");
        Console.ResetColor();
    }

    Console.WriteLine();
}

static void PrintCiSummary(
    int exit, IReadOnlyList<CiStep> steps, IReadOnlyList<int> codes, IReadOnlyList<string?> reasons, bool dryRun)
{
    Console.WriteLine();
    Console.ForegroundColor = exit == 0 ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine(exit switch
    {
        0 => "  ✅ CI: every gate that ran passed (exit 0).",
        1 => "  ❌ CI: a gate FAILED (exit 1).",
        2 => "  ❌ CI: an eval was misdriven (exit 2) and nothing failed a gate.",
        3 => "  ⚠️  CI: something was NOT MEASURED (exit 3) and nothing else failed.",
        _ => $"  ❌ CI: exit {exit} — an exit code this suite does not define. Treat it as a bug here, "
           + "not as a result.",
    });
    Console.ResetColor();

    // Per-eval, so "not measured" and "failed" can never be collapsed into one number again.
    for (int i = 0; i < codes.Count && i < steps.Count; i++)
    {
        Console.ForegroundColor = codes[i] switch
        {
            0 => ConsoleColor.Green,
            3 => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };

        string why = reasons[i] is { Length: > 0 } r ? $" — {r}" : "";
        Console.WriteLine(codes[i] switch
        {
            0 => $"     · {steps[i].Name}: passed.",
            1 => $"     · {steps[i].Name}: FAILED.",
            2 => $"     · {steps[i].Name}: misdriven — bad arguments, so it never ran.",
            3 => $"     · {steps[i].Name}: NOT MEASURED{(why.Length > 0 ? why : " — no credentials")}. "
               + "It did not pass and it did not fail; there is no verdict.",
            _ => $"     · {steps[i].Name}: undefined exit {codes[i]}.",
        });
        Console.ResetColor();
    }

    if (codes.Contains(1) && codes.Contains(3))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("     ⚠️  Both happened: something was not measured AND something failed. The exit code is");
        Console.WriteLine("         1, because a failed gate outranks an unmeasured one — see WorstExit.");
        Console.ResetColor();
    }

    if (dryRun)
    {
        // A green --ci --dry-run says the wiring held. It says NOTHING about the agent, and a
        // reader who sees only the exit code would otherwise have no way to tell the difference.
        //
        // ⚠ This sentence used to open "Exit 0 means the plumbing held" unconditionally, and it
        // printed verbatim under an exit-3 summary when --skip-slow left two evals out — a claim
        // about a code the run did not return, sitting directly beneath the code it did.
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠️  THIS WAS A DRY RUN. Every ✅ above means the plumbing held for that eval —");
        Console.WriteLine("      arguments survive the round trip, the approval-gated call is visible, no case");
        Console.WriteLine("      threw. None of them means the agent passed anything: no model was called.");

        // ⚠ THIS BANNER USED TO END "…and no snapshot was written", AND THE RUN FALSIFIED IT.
        //   Evals 03 and 04 call no model, so the chain hands them no `dryRun` argument, so they
        //   run for real inside a dry run and persist. MEASURED (MEASUREMENT_STATUS §24.7 item 1):
        //   eval03_controls and eval04_injection moved at 01:26:14 inside a dry run that ran
        //   01:26:12–01:26:19 — and then did it AGAIN inside the run that verified the write-up.
        //   The writes are right; the sentence was wrong. It now reports the store's own ledger.
        //
        //   DECIDED, and this is the half plan item 8.19 asks for: 03 and 04 do NOT gain a
        //   `dryRun` parameter. They are real, model-free measurements, and replacing one with a
        //   stubbed copy of itself inside a dry run would make the cheapest honest measurement in
        //   the suite worse in order to make a sentence true. The sentence changes instead.
        var written = EvalResultStore.SnapshotsWrittenThisRun;
        if (written.Count == 0)
        {
            Console.WriteLine("      No snapshot was written — nothing in this chain reached a store.");
        }
        else
        {
            Console.WriteLine($"      {written.Count} snapshot(s) WERE written, by the eval(s) that call no model and");
            Console.WriteLine("      therefore take no --dry-run parameter — they are real measurements, not stubs:");
            foreach (string key in written) Console.WriteLine($"        · {key}.json");
        }
        Console.ResetColor();
    }
}

// ── Console helpers that survive a non-terminal ──────────────────────────────────────
//
// Console.Clear() throws IOException("The handle is invalid.") the moment stdout is not a
// terminal, which is every CI run and every `... | tee`. And Console.ReadKey needs a real
// console too. The EOF → 'q' mapping below is load-bearing: without it the `while (true)` menu
// spins forever on redirected input instead of exiting.
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

static async Task<int> ShowMenuAsync(ParsedArgs parsed)
{
    while (true)
    {
        ClearIfInteractive();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║              Galaxus — Recommendation Agent Evaluation Suite                 ║
╠══════════════════════════════════════════════════════════════════════════════╣
║  #  Eval                     What it measures            model?  judged?     ║
╟──────────────────────────────────────────────────────────────────────────────╢
║  1  Catalogue Integrity      14 adversarial cases,        LIVE     no        ║
║                              6 defect classes                                ║
║  2  Latent-Interest Cover.   paired coverage vs 5 arms,   LIVE     no        ║
║                              sign test, per-arm floors                       ║
║  B  Stated-Need Precision    12 multi-constraint needs,   HYBRID   no        ║
║     (02b)                    code-checked; k-invariant                       ║
║  C  Held-Out Next Purchase   leave-one-out hit-rate@5,    HYBRID   no        ║
║     (02c)                    floor k/pool executed                           ║
║  3  Negative Controls        proves 1 and 2 CAN fail      none     no        ║
║  4  Review Injection (D7)    structural containment,      none     no        ║
║                              §0.5 / D-3, both directions                     ║
║  5  Recommendation Quality   weighted LLM judge,          LIVE    ⭐ YES      ║
║                              5 personas, paired control                      ║
║  6  Tool Trajectory          order, prohibitions, the     LIVE     no        ║
║                              commit gate — 3 strict pairs                    ║
║  7  Workflow Topology        DID THE LOOP ACTUALLY LOOP?  none     no        ║
║                              MAF graph via ReflectEdges                      ║
║  8  Repeated-Run Stability   N runs × BOTH live archi-    LIVE   ⭐ spread    ║
║                              tectures · SLOW · PAID                          ║
║  9  Agent vs Workflow A/B    the pre-registered compa-    LIVE    ⭐ YES      ║
║                              rison · SLOW · PAID                             ║
║                                                                              ║
║  Q  Quit                                                                     ║
╟──────────────────────────────────────────────────────────────────────────────╢
║  ""LIVE"" = needs Azure OpenAI. With no key those evals print NOT MEASURED and ║
║  exit 3 — they never substitute a deterministic arm and call it the agent.   ║
║  ""none"" = no model at all: real numbers about the INSTRUMENT, not the agent. ║
║  ""HYBRID"" = the offline arms run and print without a key; the live column   ║
║  then reads NOT MEASURED and the eval exits 3. Nothing is substituted.       ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        if (!Config.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️  Azure OpenAI credentials not found.");
            Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT.");
            Console.WriteLine("     Evals 3, 4 and 7 still run in full — they make no model calls.");
            Console.WriteLine("     Evals 1, 2, 5, 6, 8 and 9 will print NOT MEASURED and exit 3; B and C run their");
            Console.WriteLine("     offline arms, mark the live column NOT MEASURED and exit 3. Add --dry-run to");
            Console.WriteLine("     exercise their whole code path against stub models for free.\n");
            Console.ResetColor();
        }

        Console.Write("  Select: ");
        var key = ReadMenuKey();
        Console.WriteLine();

        int code = key switch
        {
            '1' => await Eval01_CatalogueIntegrity.RunAsync(judge: parsed.Judge, dryRun: parsed.DryRun),
            '2' => await Eval02_LatentInterestCoverage.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun, onlyPersona: parsed.OnlyPersona),
            'b' or 'B' => await Eval02b_StatedNeedSatisfaction.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun, onlyCase: parsed.OnlyPersona),
            'c' or 'C' => await Eval02c_HeldOutNextPurchase.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun, onlyCase: parsed.OnlyPersona),
            '3' => await NegativeControls.RunAsync(),
            '4' => await Eval04_ReviewInjectionContainment.RunAsync(),
            '5' => await Eval05_RecommendationQuality.RunAsync(dryRun: parsed.DryRun),
            '6' => await Eval06_ToolTrajectory.RunAsync(dryRun: parsed.DryRun),
            '7' => await Eval07_WorkflowTopology.RunAsync(dryRun: parsed.DryRun),
            '8' => await Eval08_StochasticStability.RunAsync(
                       runs: null, quick: parsed.Quick, dryRun: parsed.DryRun),
            '9' => await Eval09_HypothesisComparison.RunAsync(quick: parsed.Quick, dryRun: parsed.DryRun),
            'q' or 'Q' => -1,
            _ => 0,
        };

        if (code < 0) return 0;

        Console.WriteLine("\nPress any key to return to the menu...");
        _ = ReadMenuKey();
    }
}

/// <summary>One eval in the <c>--ci</c> chain, with the two facts the summary needs about it.</summary>
/// <param name="Name">Display name, e.g. <c>"Eval 05"</c>.</param>
/// <param name="What">One noun phrase for the pre-flight list.</param>
/// <param name="NeedsModel">True when a missing key makes it exit 3 instead of producing a verdict.</param>
/// <param name="Slow">True for the two evals <c>--skip-slow</c> excludes: tens of paid turns each.</param>
/// <param name="Run">Invokes the eval and yields its exit code.</param>
internal sealed record CiStep(
    string Name, string What, bool NeedsModel, bool Slow, Func<Task<int>> Run);

/// <summary>Parsed CLI flags. Local to this Program for argument routing.</summary>
internal sealed class ParsedArgs
{
    /// <summary>Every eval selector this program accepts. The ONE list — the `--log` path sniffer reads it too.</summary>
    private static readonly string[] Selectors = ["1", "2", "2b", "2c", "3", "4", "5", "6", "7", "8", "9", "cal"];

    /// <summary>Eval selector (1..9), or null for the interactive menu.</summary>
    public string? Eval { get; private set; }

    /// <summary>Run every eval in sequence and return the worst exit code.</summary>
    public bool Ci { get; private set; }

    /// <summary>
    /// With <see cref="Ci"/>: leave Evals 08 and 09 out. They are then reported as exit 3 — NOT
    /// RUN, nothing measured — and never as passes, so the escape hatch cannot turn into a silent
    /// green build.
    /// </summary>
    public bool SkipSlow { get; private set; }

    /// <summary>Fewer repetitions in Evals 02, 08 and 09.</summary>
    public bool Quick { get; private set; }

    /// <summary>Run the advisory justification judge in Eval 01. Never changes a gate.</summary>
    /// <remarks>
    /// It is Eval 01's flag alone. Evals 05 and 09 are judged by construction — their judge is not
    /// optional and this flag does not reach them.
    /// </remarks>
    public bool Judge { get; private set; }

    /// <summary>
    /// Run every eval against stub models: nothing spent, nothing written, real code path. The
    /// first of this repository's three run stages.
    /// </summary>
    public bool DryRun { get; private set; }

    /// <summary>Tee console output to a log file.</summary>
    public bool LogRequested { get; private set; }

    /// <summary>Optional log path.</summary>
    public string? LogPath { get; private set; }

    /// <summary>Override the deployment for this run only.</summary>
    public string? ModelOverride { get; private set; }

    /// <summary>
    /// Eval 02 only: restrict the run to one persona id — the one-item real run that is stage
    /// two of the three-stage protocol. Eval 02 then writes its snapshot to a probe key, so a
    /// single-persona run can never overwrite the full-cohort record.
    /// </summary>
    public string? OnlyPersona { get; private set; }

    /// <summary>
    /// Which embedding space every eval retrieves in: the authored concept vectors (the default)
    /// or the committed <c>text-embedding-3-small</c> assets.
    /// </summary>
    /// <remarks>
    /// A coverage cell scored in one space is not comparable with the same cell scored in the
    /// other, so the space is printed by every eval that prints such a number and is recorded in
    /// the <c>AuthoredQueryPhraseRetrievability</c> control row.
    /// </remarks>
    public EmbeddingSpaceChoice Space { get; private set; } = EmbeddingSpaceChoice.Auto;

    /// <summary>True when <paramref name="value"/> is an eval selector rather than a path.</summary>
    /// <param name="value">A bare positional argument.</param>
    public static bool IsSelector(string value) => Array.IndexOf(Selectors, value) >= 0;

    /// <summary>Parses the command line, or returns null when an argument is not understood.</summary>
    /// <param name="args">Raw arguments.</param>
    public static ParsedArgs? Parse(string[] args)
    {
        var parsed = new ParsedArgs();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ci":
                    parsed.Ci = true;
                    break;

                case "--skip-slow":
                    parsed.SkipSlow = true;
                    break;

                case "--quick":
                    parsed.Quick = true;
                    break;

                case "--judge":
                    parsed.Judge = true;
                    break;

                case "--dry-run":
                    parsed.DryRun = true;
                    break;

                case "--log":
                    parsed.LogRequested = true;
                    // ⚠ The next token is a log PATH only when it is not itself a selector. The
                    // list lives in one place (Selectors) because it used to be duplicated here as
                    // a literal `is not ("1" or "2" or ...)`, and adding Evals 05-09 to one copy
                    // and not the other would have made `-- --log 6` silently log to a file called
                    // "6" and then run the menu.
                    if (i + 1 < args.Length && args[i + 1] is { Length: > 0 } next
                        && !next.StartsWith('-') && !IsSelector(next))
                    {
                        parsed.LogPath = args[++i];
                    }
                    break;

                case "--model":
                    if (i + 1 >= args.Length) return null;
                    parsed.ModelOverride = args[++i];
                    break;

                case "--only":
                    if (i + 1 >= args.Length) return null;
                    parsed.OnlyPersona = args[++i];
                    break;

                // ── Which embedding SPACE every arm retrieves in. Asking for both is a typo,
                //    not a request, and a run that silently picked one would attribute its
                //    numbers to a space nobody chose.
                case "--concept-vectors":
                    if (parsed.Space is EmbeddingSpaceChoice.RealVectors) return null;
                    parsed.Space = EmbeddingSpaceChoice.ConceptVectors;
                    break;

                case "--real-vectors":
                    if (parsed.Space is EmbeddingSpaceChoice.ConceptVectors) return null;
                    parsed.Space = EmbeddingSpaceChoice.RealVectors;
                    break;

                default:
                    // First positional is the eval selector. A SECOND positional is a typo, not a
                    // second selector — returning null makes it exit 2 instead of silently running
                    // the wrong eval.
                    if (args[i].StartsWith('-')) return null;
                    if (parsed.Eval is not null) return null;
                    parsed.Eval = args[i];
                    break;
            }
        }

        return parsed;
    }
}
