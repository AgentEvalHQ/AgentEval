// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Workflows;

namespace Galaxus.RecommendationAgent.Demos;

/// <summary>
/// Demo 02 — the interest-map discovery loop: five executors, five named routes, one conditional
/// loop-back edge, bounded at three rounds.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this demo is arguing, narrowly.</b> A single agent with a search tool can call it
/// twice. What it cannot do is write its second query against documents it has not retrieved
/// yet. So the loop does not add a CAPABILITY; it adds an OBLIGATION (a per-interest coverage
/// ledger) and an AUDIT TRAIL (a printed gap list and a stated stop reason). Whether the single
/// agent would have done it anyway is an empirical question — measure it, do not assert it, and
/// <c>samples/Galaxus.RecommendationAgent.Evals/Docs/MEASUREMENT_STATUS.md</c> records what this
/// repository's corpus can and cannot currently support on that question.
/// </para>
/// <para>
/// <b>What to watch, in order.</b> The DIRECT/LATENT split in the interest map; the products each
/// query discovers, named rather than counted; the deterministic pre-gate rejecting before a token
/// is spent; the coverage ledger, which is the artifact a human verifies; the loop-back arrow with
/// its round number; round 2's queries speaking the CATALOGUE's vocabulary instead of the
/// customer's; the SAME guardrail pipeline Demo 1 runs, with its ledger; and the D-3 vocabulary
/// panel, which prints whether or not it fired.
/// </para>
/// <para>
/// <b>The answer is screened by exactly Demo 1's bar.</b> <c>DiscoveryPresentation.Render</c> hands
/// the loop's selection to the shipped <c>GuardrailPipeline</c> and prints it with the shipped
/// <c>RecommendationPrinter</c>, so the comparison the whole demo rests on is not confounded by two
/// renderers and two guardrail suites. The guardrail ledger you see below the recommendations is
/// that pipeline's, not this file's.
/// </para>
/// <para>
/// ⏱️ Runtime: a handful of seconds live (up to five model calls for a two-round run), well
/// under a second offline. <c>--offline</c> costs nothing and needs no key.
/// </para>
/// </remarks>
public static class Demo02_InterestMapWorkflow
{
    /// <summary>The persona the demo opens on when <c>--user</c> is not given.</summary>
    public const string DefaultUserId = GalaxusDemoPrompts.NadiaUserId;

    /// <summary>Runs the demo with every default: Nadia, personalization on, live model, three rounds.</summary>
    public static Task RunAsync() => RunAsync(DefaultUserId, personalizationDisabled: false, offline: false);

    /// <summary>
    /// Runs one loop end to end.
    /// </summary>
    /// <param name="userId">One of <see cref="Personas.AllPersonaIds"/>. Null selects <see cref="DefaultUserId"/>.</param>
    /// <param name="personalizationDisabled">The §F.6 opt-out: history is not read at all.</param>
    /// <param name="offline">Skip every model call and run the deterministic arm.</param>
    /// <param name="maxRounds">
    /// The round cap. Lower it to watch the round-cap termination fire — the guard is only a
    /// guard if it can be triggered.
    /// </param>
    /// <param name="modelCallTimeout">
    /// Wall-clock ceiling on ONE model call. Null uses the default. Lower it to watch the whole
    /// loop degrade to its deterministic arms and still answer.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task RunAsync(
        string? userId,
        bool personalizationDisabled,
        bool offline,
        int maxRounds = DiscoveryState.DefaultMaxDiscoveryRounds,
        TimeSpan? modelCallTimeout = null,
        CancellationToken cancellationToken = default)
    {
        PrintHeader();

        var id = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId.Trim();
        if (UserProfiles.Find(id) is null)
        {
            PrintUnknownPersona(id);
            return;
        }

        if (offline)
        {
            PrintOfflineBanner();
        }
        else
        {
            if (!Config.IsConfigured)
            {
                PrintMissingCredentials();
                return;
            }

            Config.PrintAzureTarget();
            Console.WriteLine();
        }

        PrintWhatToWatch();

        // Which SPACE every search below runs in, resolved and printed BEFORE the first one. The
        // loop asks EmbeddingSpace for the same source, so this line is a statement about the run
        // rather than a label beside it.
        EmbeddingSpace.Resolve(Catalogue.Default.All).PrintBanner();
        Console.WriteLine();

        var sink = new ConsoleDiscoveryProgressSink();

        try
        {
            var result = await GalaxusDiscoveryLoop.RunAsync(
                id,
                new DiscoveryLoopOptions(
                    Offline: offline,
                    PersonalizationDisabled: personalizationDisabled,
                    MaxRounds: maxRounds,
                    Progress: sink,
                    ModelCallTimeout: modelCallTimeout),
                cancellationToken).ConfigureAwait(false);

            PrintTermination(result);
            PrintVocabularyTransfer(result.State);
            PrintInjectionLedger(result.State);
            PrintScreenedAnswer(result.State);
            PrintRoutes(result);
            PrintDegradations(result.State);
            PrintExecutorFailures(result);
        }
        catch (Exception ex)
        {
            PrintFailure(ex);
        }
    }

    /// <summary>
    /// Prints the node failures and makes the PROCESS say so. A run with a thrown node is not a
    /// result, and until this existed it was indistinguishable from a healthy one at the exit code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate, not the panel. The panel above it already printed
    /// <c>⚠ [CoverageReviewer] executor FAILED</c> — and the process still exited <b>0</b>, so every
    /// automated reader saw green while the tray below the warning was produced by a workflow whose
    /// reviewer never ran. Printing louder does not fix that; changing the exit code does.
    /// </para>
    /// <para>
    /// ⚠ It is deliberately NOT a rethrow. The stream is fully drained and every panel is printed
    /// first, because the failure text and the partial state are the evidence a reader needs. The
    /// run is reported in full and then marked unusable.
    /// </para>
    /// <para>
    /// <b>The check can fail, and was demonstrated failing in both directions.</b> Removing one
    /// entry from <c>QueryVocabulary</c>'s B-9 localisation table makes <c>CoverageReviewer</c>
    /// throw: before this, <c>Agent -- 2 --offline</c> exited 0; after it, 1. On the shipped tree,
    /// with the table intact, it exits 0. A gate that only ever fires, or never fires, proves
    /// nothing either way.
    /// </para>
    /// </remarks>
    /// <param name="result">The finished run.</param>
    private static void PrintExecutorFailures(DiscoveryRunResult result)
    {
        if (!result.Failed) return;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ─── NODE FAILURES — this run is NOT a result ────────────────────────────");
        foreach (var failure in result.ExecutorFailures)
            Console.WriteLine($"    ❌ {failure}");

        Console.WriteLine(
            "    Nothing above this line may be quoted. A node threw, so the panels were built from"
            + Environment.NewLine
            + "    a state that node never contributed to. Exit code set to 1 — the earlier ⚠ note is"
            + Environment.NewLine
            + "    on the WARNING channel, which is for degradation a run survived, and this is not that.");
        Console.ResetColor();

        Environment.ExitCode = 1;
    }

    // ── Panels ────────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 02 — Interest-map discovery loop · 5 executors · bounded at 3 rounds   ║
║   Map → discover → review coverage → (loop) → rank → present                  ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    /// <summary>
    /// The five beats, named before they happen.
    /// </summary>
    /// <remarks>
    /// Printed first because the loop's output is dense and the interesting moments are cheap to
    /// miss. A viewer who has been told what to look for reads a ledger; one who has not reads a
    /// wall of text and takes the final answer on trust — which is the opposite of the point.
    /// </remarks>
    private static void PrintWhatToWatch()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── What to watch ───────────────────────────────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("    1  The interest map's DIRECT / LATENT split. A LATENT interest is a reading of a");
        Console.WriteLine("       CONJUNCTION of signals — drop any one and the inference fails. That is the thing");
        Console.WriteLine("       a collaborative-filtering model cannot reach, because it is not in anyone else's");
        Console.WriteLine("       history.");
        Console.WriteLine("    2  Each search line NAMES the products it discovered, not just how many. A query");
        Console.WriteLine("       that returns → 0 is visible here; in a single-shot agent it is invisible.");
        Console.WriteLine("    3  ⛔ the deterministic pre-gate. It can REJECT for free; it can never APPROVE for");
        Console.WriteLine("       free. A cheap accept is exactly the rubber-stamp failure this design prevents.");
        Console.WriteLine("    4  ↩ the loop-back arrow, with the round number it is moving to. One conditional");
        Console.WriteLine("       edge, bounded at three, on a counter that lives on the MESSAGE.");
        Console.WriteLine("    5  Round 2's queries, and the ─── Vocabulary transfer ─── panel that scores them.");
        Console.WriteLine("       Round 1 searched in the CUSTOMER's words; round 2 searches in the CATALOGUE's,");
        Console.WriteLine("       because the records that came back told the reviewer what things are called.");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// The termination panel — the point of the demo that an interviewer will actually probe.
    /// </summary>
    /// <remarks>
    /// It names WHICH of the three stops fired and states the invariant that makes the set
    /// exhaustive, because "it terminates" is a claim and the printed reason is the evidence.
    /// </remarks>
    private static void PrintTermination(DiscoveryRunResult result)
    {
        var state = result.State;

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Termination ─────────────────────────────────────────────────────────");
        Console.ResetColor();

        var explanation = state.StopReason switch
        {
            DiscoveryStopReason.CoverageSufficient =>
                "the reviewer approved: every interest on the map has candidates worth opening.",
            DiscoveryStopReason.RoundLimitReached =>
                $"ROUND CAP — {state.DiscoveryRound} of {state.MaxRounds} rounds ran and gaps were still open. " +
                "The answer below is PARTIAL and says so.",
            DiscoveryStopReason.NoProgress =>
                "NO PROGRESS — the last round added 0 new product ids. Another identical round cannot change " +
                "the answer, so the loop stopped early rather than paying for it.",
            DiscoveryStopReason.GapsUnresolvable =>
                "GAPS UNRESOLVABLE — gaps remain but no materially different query is available. The reviewer " +
                "said so rather than inventing a query it did not believe in.",
            DiscoveryStopReason.GapsRemain =>
                "gaps remain — this value is not terminal and reaching it here would be a wiring fault.",
            _ => "no stop reason was recorded, which is itself a defect."
        };

        Console.ForegroundColor = state.CoverageApproved ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"    stop_reason   {state.StopReason}");
        Console.WriteLine($"    because       {explanation}");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    rounds        {state.DiscoveryRound} of {state.MaxRounds}   " +
                          $"last round added {(state.LastRoundNewProductCount < 0 ? "n/a" : state.LastRoundNewProductCount.ToString(CultureInfo.InvariantCulture))} new id(s)   " +
                          $"open gaps {state.OpenGaps.Count}");
        Console.WriteLine($"    loop-back     {(result.Looped ? "FIRED" : "did not fire")}   super-steps {result.SuperSteps}");
        Console.WriteLine("    invariant     the reviewer's two edges partition the space: DiscoveryLimitReached is");
        Console.WriteLine("                  DEFINED as !CoverageApproved && !NeedsMoreDiscovery, so there is no state");
        Console.WriteLine("                  in which the reviewer has no outgoing edge. It cannot hang.");
        Console.WriteLine("    prove it      dotnet run --project samples/Galaxus.RecommendationAgent -- 0");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// The vocabulary-transfer panel — the loop's central claim, scored on this run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Round 1's queries were written from the interest map, before a single catalogue record had
    /// been seen. Round 2+'s were written by the reviewer FROM the records that came back. The
    /// panel groups the run's own query log on that distinction and reports what each group
    /// discovered, so the claim is a measurement of this run rather than a sentence in a slide.
    /// </para>
    /// <para>
    /// ⚠ A run in which the loop never looped shows an EMPTY catalogue-vocabulary group. That is
    /// reported as a result, not hidden: a loop that exits on round 1 bought nothing, and the
    /// panel is the place that says so.
    /// </para>
    /// </remarks>
    private static void PrintVocabularyTransfer(DiscoveryState state)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Vocabulary transfer — the loop's central claim, on THIS run ─────────");
        Console.ResetColor();

        var fromMap = state.QueryLog.Where(q => !q.FromCatalogueVocabulary).ToList();
        var fromCatalogue = state.QueryLog.Where(q => q.FromCatalogueVocabulary).ToList();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"    from the CUSTOMER's vocabulary (mapper interests, written before any retrieval ran): " +
                          $"{fromMap.Count} quer(y|ies), {fromMap.Sum(q => q.NewProductIds.Count)} new product id(s)");
        foreach (var executed in fromMap)
            Console.WriteLine($"      r{executed.Round}  [{executed.Plan.InterestId}]  \"{Fit(executed.Query, 52)}\"  → {executed.Hits} hit(s), {executed.NewProductIds.Count} new");
        Console.ResetColor();

        if (fromCatalogue.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("    from the CATALOGUE's vocabulary: NONE. The loop exited before a second round, so");
            Console.WriteLine("    on this run it bought nothing that a single retrieval pass could not have bought.");
            Console.WriteLine("    That is a RESULT, not an omission — it is the rubber-stamp shape (design §D.3) and");
            Console.WriteLine("    it is exactly what the eval lane's rounds-taken distribution exists to catch.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"    from the CATALOGUE's vocabulary (reviewer gaps and mid-run proposals, written " +
                              $"after seeing real records): {fromCatalogue.Count} quer(y|ies), " +
                              $"{fromCatalogue.Sum(q => q.NewProductIds.Count)} new product id(s)");
            foreach (var executed in fromCatalogue)
            {
                var filter = executed.Plan.CategoryPathPrefix is { Length: > 0 } path ? $"  cat={path}" : string.Empty;
                var attributes = executed.Plan.Attributes is { Count: > 0 } a
                    ? "  " + string.Join(", ", a.Select(kv => $"{kv.Key}={kv.Value}"))
                    : string.Empty;

                Console.WriteLine($"      r{executed.Round}  [{executed.Plan.InterestId}·{executed.Plan.Origin}]  \"{Fit(executed.Query, 46)}\"  → {executed.Hits} hit(s), {executed.NewProductIds.Count} new{filter}{attributes}");
                foreach (var productId in executed.NewProductIds)
                    Console.WriteLine($"          ✚ {productId}  — could not have been found by round 1's query plan");
            }
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("    Those queries could not have been written before round 1 ran: their words, their");
            Console.WriteLine("    category paths and their attribute filters come from records that did not exist in");
            Console.WriteLine("    the prompt until retrieval put them there. That is the whole mechanism, and it is");
            Console.WriteLine("    narrower than \"the loop is smarter\" — it is about WHEN the query is written.");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The §0.5 / D-3 panel: what the structural vocabulary constraint refused.
    /// </summary>
    /// <remarks>
    /// Printed even when it is EMPTY, and labelled as such. A control that leaves no trace when
    /// it does not fire is indistinguishable from a control that is not wired in — which is
    /// exactly the failure this panel exists to make visible.
    /// </remarks>
    private static void PrintInjectionLedger(DiscoveryState state)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Query-vocabulary constraint (§0.5 / D-3) ────────────────────────────");
        Console.ResetColor();

        // The DENOMINATOR first. An empty drop ledger beside zero proposals means the control was
        // never exercised; an empty one beside four proposals means it ran and found nothing to
        // refuse. Those are different facts and only the second is about the control working.
        int accepted = state.Proposals.Count(p => p.Accepted);
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"    {state.Proposals.Count} mid-run interest(s) proposed from review text · {accepted} accepted · " +
                          $"{state.Proposals.Count - accepted} refused · {state.DroppedQueryTerms.Count} query term(s) refused");
        foreach (var proposal in state.Proposals.Where(p => !p.Accepted))
            Console.WriteLine($"      ✗ \"{Fit(proposal.Label, 44)}\" — {proposal.Refusal}");
        Console.ResetColor();

        if (state.DroppedQueryTerms.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("    No TERM was refused this run. Review text is the highest-risk input here and");
            Console.WriteLine("    marketplace sellers control it, so the control runs on every proposed term");
            Console.WriteLine("    whether or not it fires. An empty ledger is a RESULT, not a pass — and with");
            Console.WriteLine($"    {state.Proposals.Count} proposal(s) above it, it is a result about " +
                              (state.Proposals.Count == 0 ? "a control that was never tempted." : "a control that ran."));
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var dropped in state.DroppedQueryTerms)
                Console.WriteLine($"    🛡  {dropped}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("    Each of these was proposed by the reviewer and REFUSED before it could reach");
            Console.WriteLine("    retrieval, because its tokens are in neither the interest map nor the catalogue's");
            Console.WriteLine("    own category and attribute vocabulary. This is a code path, not a prompt rule.");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    The adversarial case that proves the control can fire, and the negative control that");
        Console.WriteLine("    proves the case can produce a red result, live in the eval lane:");
        Console.WriteLine("      dotnet run --project samples/Galaxus.RecommendationAgent.Evals -- 4");
        Console.ResetColor();

        Console.WriteLine();
    }

    /// <summary>
    /// What the customer was actually shown, after the shared guardrail pipeline.
    /// </summary>
    /// <remarks>
    /// The Ranker's selection and the customer's answer are DIFFERENT sets — the pipeline removes
    /// items after the Ranker has finished — and printing only one of them invites a reader to
    /// treat the loop's choice as the delivered answer. Both numbers, and the difference, are on
    /// screen.
    /// </remarks>
    private static void PrintScreenedAnswer(DiscoveryState state)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Selected → screened → shown ─────────────────────────────────────────");
        Console.ResetColor();

        int removedByPostChecks = state.DroppedSkus.Count;
        int removedByPipeline = Math.Max(0, state.Ranked.Count - state.Presented.Count);

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"    {state.Candidates.Count,3}  candidates discovered across {state.DiscoveryRound} round(s)");
        Console.WriteLine($"    {state.Ranked.Count,3}  selected by the Ranker, after {removedByPostChecks} deterministic post-check drop(s)");
        Console.WriteLine($"    {state.Presented.Count,3}  actually SHOWN, after the shared GuardrailPipeline removed {removedByPipeline} more");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    The guardrail ledger printed above the ⚖ line is that pipeline's — the SAME one Demo 1");
        Console.WriteLine("    runs, deliberately, so the two demos' answers are measured against one bar. Read its");
        Console.WriteLine("    ⚠ arm_inapplicable rows before quoting any number from it.");
        Console.ResetColor();

        Console.WriteLine();
    }

    private static void PrintRoutes(DiscoveryRunResult result)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─── Routes taken (trace) ────────────────────────────────────────────────");
        Console.WriteLine($"    {string.Join("  →  ", result.RoutesTaken)}");
        Console.WriteLine("    ⚠ MAF may evaluate an edge predicate more than once per super-step, so this is a");
        Console.WriteLine("      trace of selection, not a count. The round number lives on the message.");
        Console.WriteLine($"    executors: {string.Join(" · ", result.ExecutorIds)}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintDegradations(DiscoveryState state)
    {
        if (state.DegradedNotes.Count == 0) return;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ─── Degraded this run (warnings, not failures) ──────────────────────────");
        foreach (var note in state.DegradedNotes) Console.WriteLine($"    ⚠  {note}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintOfflineBanner()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"  ┌──────────────────────────────────────────────────────────────────────────┐
  │  OFFLINE — no model call was made.                                       │
  │  The five stages ran with deterministic stand-ins. This exercises the     │
  │  LOOP'S MECHANICS — its query plan, its ledger, its three terminations    │
  │  and its D-3 vocabulary constraint — at zero cost. It is a BASELINE, not  │
  │  a simulation of the agent: do not read an offline number as a claim      │
  │  about what the model would have done.                                   │
  └──────────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintUnknownPersona(string userId)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Unknown customer '{userId}'.");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("     Known personas:");
        foreach (var id in Personas.AllPersonaIds)
            Console.WriteLine($"       {id}  {UserProfiles.Require(id).DisplayName}");
        Console.WriteLine("\n     No fallback is applied on purpose: running the wrong persona's history produces a");
        Console.WriteLine("     plausible, wrong demo.");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping the live loop — Azure OpenAI credentials required.

     Set the following environment variables and try again:
       AZURE_OPENAI_ENDPOINT
       AZURE_OPENAI_API_KEY
       AZURE_OPENAI_DEPLOYMENT          (optional, defaults to gpt-5-mini)

     Or run the whole loop deterministically, with no key at all:
       dotnet run --project samples/Galaxus.RecommendationAgent -- 2 --offline
");
        Console.ResetColor();
    }

    private static void PrintFailure(Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ The discovery loop could not start — {ex.GetType().Name}");
        Console.WriteLine($"     Message: {ex.Message}");
        Console.WriteLine("     Note: this is a COMPOSITION failure. The loop itself never throws once it is");
        Console.WriteLine("     running — a failing node degrades to its deterministic arm and says so.");
        if (ex.InnerException is not null)
            Console.WriteLine($"     Inner:   {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        Console.WriteLine($"\n     Stack trace:\n{ex.StackTrace}");
        Console.ResetColor();
    }

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";
}
