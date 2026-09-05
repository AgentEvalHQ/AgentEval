// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using AgentEval.MAF;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Evals.Adapters;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 02 — Latent-Interest Coverage. Paired, deterministic, and honest about what it cannot
/// measure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read this before reading any number this eval prints.</b> The design pre-registers an A/B
/// between a single agent (Arm A) and a discovery workflow (Arm B), decided by an exact two-sided
/// sign test needing 10 wins of 12 paired personas. ONE of those three things still does not exist
/// in this repository — the twelve personas were authored (§4 of MEASUREMENT_STATUS.md) and the
/// rule is now reachable, but Arm B is still not a comparable entrant:
/// </para>
/// <list type="number">
///   <item><description><b>Arm B runs, but not as a comparable entrant.</b> Demo 2's discovery
///   workflow reaches this eval through <see cref="Adapters.DiscoveryLoopAdapter"/>, which
///   <c>Program.cs</c> binds, and it is on its DETERMINISTIC path — zero model calls. So pairing it
///   against the live agent would vary architecture and model presence in one comparison, and it is
///   deliberately NOT entered in the sign test; it is a reference row. What it CAN settle is the
///   comparison against <see cref="Broken05_RubberStampReviewer"/> — a real loop whose reviewer
///   approves on round 1 — and it settles that one in the ROUNDS distribution, not in coverage:
///   measured P(rounds = 1) is <b>0.417</b> for the real loop and 1.000 for the rubber stamp. ⚠ That
///   separation used to be COMPLETE (0.000 against 1.000) on the three-persona corpus and is not any
///   more: on twelve personas the real loop stops at round 1 for five of them. Read
///   <c>Docs/MEASUREMENT_STATUS.md</c> §2.5 and §6 before quoting any cell of Arm B's
///   row.</description></item>
///   <item><description><b>n = 12, and it was 3.</b> The corpus now authors fourteen personas, of
///   which twelve are scored (<see cref="CoveragePersonas"/> names the two exclusions and why), so
///   the pre-registered rule is REACHABLE — at n = 12 the smallest attainable two-sided p is
///   0.0005. That is a statement about the analysis set, not about any particular comparison: the
///   exact sign test discards tied pairs, so a comparison whose arms tie still reports the n it
///   attained and the p that n can reach. Read the per-comparison line, never the
///   ceiling.</description></item>
/// </list>
/// <para>
/// ⚠ <b>The comparison is made at ONE DECLARED k, and only between cells at equal k.</b> MEASURED
/// on the 2026-09-04 live run (before this rule existed): the live agent presented 0–4 items per
/// persona (mean 3.1), every scripted control presented exactly 5, Demo 2's deterministic arm 7–12,
/// and the sign test paired their raw latent coverage — a RECALL, monotone in k. "Single shot 0.701
/// vs live 0.609" was therefore a 5-item answer against a 3-item one, and it was read as a statement
/// about architecture. Now: the canonical utterance declares a budget
/// (<see cref="CoverageArms.DeclaredK"/>), every arm is cut to its top k in its own stated order
/// before it is paired, a precision@k channel with its own floor sits beside recall, and any pair
/// whose two sides presented different counts is reported NOT COMPARABLE — never as a win, a loss
/// or a tie. A persisted run whose live arm was never told a budget is re-read at the live arm's
/// OWN k, with every control cut down to match; that re-read is the only fair reading of such a
/// run and it is printed beside the declared-k table.
/// </para>
/// <para>
/// <b>What this eval DOES do, and it is not nothing.</b> It measures the live agent against four
/// deterministic arms on a metric with a floor computed from the corpus rather than quoted from a
/// document — and the floor is derived at each arm's OWN presentation count, so an arm cannot buy
/// coverage by presenting more:
/// </para>
/// <list type="bullet">
///   <item><description><b>Control — single shot</b> (<see cref="Broken03_SingleShotWorkflow"/>):
///   one retrieval pass, no second look. The control that can take the win away.</description></item>
///   <item><description><b>Baseline — popularity</b> (<see cref="Broken04_PopularityAgent"/>): the
///   bestseller list, ignoring the customer. Its coverage is MEASURED, not asserted at 0.00 — this
///   catalogue's bestseller list is derived, not authored to carry zero latent tokens, so the
///   design's 0.00 does not transfer.</description></item>
///   <item><description><b>Baseline — tag join</b> (<see cref="Baseline_TagJoin"/>): design §0.5 /
///   D-4's missing baseline, finally run. Two lines of SQL, zero model calls. If it scores near
///   1.0, the headline metric is measuring a tag join rather than an inference, and that is the
///   finding.</description></item>
///   <item><description><b>Discovery Workflow (Demo 2) — deterministic arm</b>
///   (<see cref="Adapters.RealDiscoveryLoopArm"/>): the shipped MAF loop, on its deterministic
///   path. A reference row and a rounds-distribution comparator, never the design's headline
///   A/B.</description></item>
/// </list>
/// <para>
/// ⏱️ Runtime: roughly 5-12 minutes at 3 repetitions (36 live turns), 2-4 minutes with
/// <c>--quick</c>. The deterministic arms cost nothing and take milliseconds.
/// </para>
/// </remarks>
public static class Eval02_LatentInterestCoverage
{
    /// <summary>Repetitions per persona for the live arm.</summary>
    public const int Reps = 3;

    /// <summary>Repetitions per persona for the live arm under <c>--quick</c>.</summary>
    public const int QuickReps = 1;

    /// <summary>
    /// Snapshot key used when <c>--only</c> restricts the run to one persona — stage two of the
    /// three-stage run protocol. A one-persona probe must never overwrite the full-cohort record.
    /// </summary>
    public const string ProbeSnapshotKey = EvalResultStore.CoverageKey + "_probe";

    // ── Arm labels. The single source is CoverageArms; these aliases exist so the call sites
    //    below read as prose. Adding an ARM does not require touching this file at all — see
    //    CoverageArms' remarks for why the registry replaced three parallel copies of this list.

    /// <summary>Arm label for the live agent.</summary>
    public const string ArmLive = CoverageArms.Live;

    /// <summary>Arm label for the loop-disabled control.</summary>
    public const string ArmSingleShot = CoverageArms.SingleShot;

    /// <summary>Arm label for the popularity baseline.</summary>
    public const string ArmPopularity = CoverageArms.Popularity;

    /// <summary>Arm label for the oracle-adjacent tag-join baseline.</summary>
    public const string ArmTagJoin = CoverageArms.TagJoin;

    /// <summary>Runs the eval.</summary>
    /// <param name="quick">One repetition instead of three.</param>
    /// <param name="dryRun">
    /// Replace the live arm with a deliberately implausible stub model. Spends nothing, exercises the
    /// persona loop, the graders, the sign test, the printer and the gate, and writes no snapshot.
    /// </param>
    /// <param name="onlyPersona">
    /// Restrict the run to one persona id — the one-item real run that is stage two of the
    /// three-stage protocol. The snapshot then goes to <see cref="ProbeSnapshotKey"/>, never to the
    /// full-cohort key.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 0 when both gates pass, 1 when either fails, 2 when the persona filter matches nothing, 3
    /// when credentials are missing and nothing was measured. ⚠ The <c>ci</c> parameter is GONE —
    /// see <see cref="CredentialGuard"/>.
    /// </returns>
    public static async Task<int> RunAsync(
        bool quick = false, bool dryRun = false, string? onlyPersona = null, CancellationToken ct = default)
    {
        PrintHeader();
        PrintPreRegistration();

        // ⚠ B-12. Before the numbers, not after them: the legend says which arm is an ORACLE that
        // reads the gold, which is the CONTROL that can take the win away, and which arm carries a
        // "do NOT read this as the headline" caveat. Those notes were written on every arm and read
        // by nothing, so the caveat existed only in source — and a caveat that does not print is not
        // a caveat.
        EvalPrinter.PrintArmLegend(CoverageArms.All);

        IReadOnlyList<CoveragePersona> personas = onlyPersona is null
            ? CoveragePersonas.All
            : [.. CoveragePersonas.All.Where(p => string.Equals(p.Id, onlyPersona, StringComparison.OrdinalIgnoreCase))];

        if (personas.Count == 0)
        {
            EvalPrinter.PrintRefusal(
                $"--only {onlyPersona} matches no scored persona.",
                "Scored ids: " + string.Join(", ", CoveragePersonas.All.Select(p => p.Id)) + ".");
            return 2;
        }

        // ⚠️ HONESTY GATE. FIVE of this eval's six arms need no model at all, so with no key it
        // would happily print a full coverage table, a forced-choice panel and a sign test — with
        // the one column that is supposed to be the agent simply missing, and nothing in the table
        // saying so. It refuses instead.
        if (CredentialGuard.Blocks(
                "Eval 02", "Latent-interest coverage for the live agent", dryRun,
                "The single-shot control, the popularity floor, the tag-join oracle, the rubber-stamp",
                "loop and Demo 2's offline arm would ALL run without a key. Their numbers are about",
                "those arms. Printing them under this eval's heading would be reporting a baseline as",
                "the agent, and the sign test would have nothing to test against.")
            is { } noCredentials)
        {
            return noCredentials;
        }

        if (dryRun)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🧪 DRY RUN — the live arm is a stub model. Nothing spent, no snapshot written.");
            Console.WriteLine("     Its coverage number is NOT a result: the stub presents the same two products");
            Console.WriteLine("     for every persona. What this run proves is that the persona loop, the gold");
            Console.WriteLine("     derivation, the graders, the sign test and the gate all execute.");
            Console.ResetColor();
            Console.WriteLine();
        }
        else
        {
            Config.PrintAzureTarget();
            Console.WriteLine();
        }

        if (onlyPersona is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  🔬 ONE-PERSONA PROBE — {personas[0].Id} only. Stage two of the three-stage protocol.");
            Console.WriteLine($"     n = 1: no sign test can reach a result, and the snapshot goes to '{ProbeSnapshotKey}',");
            Console.WriteLine("     never to the full-cohort key.");
            Console.ResetColor();
            Console.WriteLine();
        }

        var retriever = await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);

        int reps = dryRun ? 1 : quick ? QuickReps : Reps;
        int declaredK = CoverageArms.DeclaredK;
        var harness = new MAFEvaluationHarness(verbose: false);
        var options = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            EvaluateResponse = false,
            Verbose = false,
            ModelName = dryRun ? "(stub — dry run)" : Config.Model,
        };

        // The dry-run stub ASKS before presenting on Jonas (USR-JV-08) and nowhere else, so the
        // harness's second turn is exercised on exactly the persona whose instructed silence
        // failed GATE 1 "alone" on the 2026-09-04 live run.
        var liveAgent = dryRun
            ? RecommendationAgentFactory.Create(StubChatClient.AskThenPresentAgent(Personas.JonasUserId))
            : RecommendationAgentFactory.Create();

        // The ONE place an arm is constructed. Every arm — live, control, baseline, oracle, loop —
        // comes out of CoverageArms with this context, so a new arm is a new row in the registry
        // and nothing here changes.
        //
        // The live arm is wrapped in ClarifyingTurnAdapter: every scored persona has gold, so a
        // presentation is REQUIRED, and a first turn that stops to ask two clarifying questions
        // (RecommendationInstructions step 3) is answered from the persona's own profile and run
        // once more on the same session. Silence is scored only after that.
        var armContext = new CoverageArmContext(
            retriever,
            LiveAgentFactory: () => new ClarifyingTurnAdapter(new ApprovalAwareAgentAdapter(liveAgent)),   // fresh session per rep
            DryRun: dryRun,
            DeclaredK: declaredK);

        // TWO reports over the same turns. `ownK` scores each arm at whatever it presented — the
        // floors GATE 1 reads, the loop-health telemetry, the snapshot's continuity with earlier
        // runs. `atK` scores each arm cut to the DECLARED budget — the only cells that may be
        // paired. One turn, two readings; neither is derived from the other after the fact.
        var ownK = new PairedCoverageReport();
        var atK = new PairedCoverageReport();
        var floors = new Dictionary<string, double>(StringComparer.Ordinal);
        var recallFloorsAtK = new Dictionary<string, double>(StringComparer.Ordinal);
        var precisionFloors = new Dictionary<string, double>(StringComparer.Ordinal);
        var notes = new List<string>();
        var roundsByArm = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int armsThatThrew = 0;

        // Every live cell whose first turn presented nothing, with what the second turn did. The
        // second turn is a HARNESS event and is reported as one, cell by cell, never folded away.
        var secondTurns = new List<(string Cell, ClarifyingTurnOutcome Outcome)>();

        // Declared-absent arms are announced ONCE, before any number is printed, so a reader meets
        // the missing comparison before meeting the comparisons that did run.
        foreach (CoverageArm absent in CoverageArms.Absent)
            notes.Add($"Arm '{absent.Label}' is DECLARED ABSENT and was not run. {absent.AbsenceReason}");

        // Every persona's gold, derived ONCE and up front — over the WHOLE analysis set even under
        // --only, because the cross-persona forced choice grades one answer against every
        // customer's gold, and a probe that narrowed the rival set would flatter itself.
        var goldByPersona = CoveragePersonas.All.ToDictionary(
            p => p.Id, p => InterestMapGold.Derive(p.Id), StringComparer.Ordinal);

        int scorablePersonas = goldByPersona.Count(kv => !kv.Value.LatentIsEmpty);
        double forcedChoiceFloor = InterestCoverageGrader.ForcedChoiceFloor(scorablePersonas);

        foreach (CoveragePersona persona in personas)
        {
            GoldInterestMap gold = goldByPersona[persona.Id];
            var (poolSize, randomLatent, randomManifest) =
                ChanceFloors.RandomDrawFloor(gold, declaredK);
            var (_, relevantCarriers, randomPrecision) = ChanceFloors.RandomPrecisionFloor(gold);

            PrintPersonaHeader(persona, gold, poolSize, randomLatent, randomManifest, declaredK, relevantCarriers, randomPrecision);

            if (gold.LatentIsEmpty)
            {
                // The floor is recorded only for a persona that is actually scored. Writing NaN
                // into the snapshot for a skipped persona puts a number in the record for a
                // measurement that was never made.
                notes.Add($"{persona.Id} produced an EMPTY latent-gold set and was skipped. "
                        + "An empty denominator is excluded from the mean, never scored as 0 or 1.");
                continue;
            }

            floors[persona.Id] = randomLatent;
            recallFloorsAtK[persona.Id] = randomLatent;
            precisionFloors[persona.Id] = randomPrecision;

            if (gold.Latent.Count == 1)
            {
                notes.Add($"⚠️ {persona.Id}'s latent gold is ONE token. Its 'coverage' is a single Bernoulli trial, "
                        + "and 0.000 / 1.000 are the only two values it can take. Reported, not excluded — but no "
                        + "difference between arms on this persona is a measurement of anything.");
            }

            var unreachable = InterestCoverageGrader.UnreachableLatentTokens(gold);
            if (unreachable.Count > 0)
            {
                notes.Add($"{persona.Id}: {unreachable.Count} of {gold.Latent.Count} latent tokens are carried by NO "
                        + $"product outside the owned categories ({string.Join(", ", unreachable)}). They cap every "
                        + "arm below 1.0 for a reason that has nothing to do with the agent. Left in the denominator "
                        + "and reported rather than quietly removed.");
            }

            // ── Every runnable arm, from the registry, in report order. ──────────────────
            //
            // A stochastic arm is repeated and its reps average into ONE observation; a
            // deterministic arm runs once, because one run IS its whole distribution. Which is
            // which is a property of the arm (CoverageArm.IsRepeated), not a branch here — that is
            // what stops a new arm from needing a new code path.
            foreach (CoverageArm arm in CoverageArms.Runnable)
            {
                int armReps = arm.IsRepeated ? reps : 1;
                var ownScores = new List<CoverageScore>(armReps);
                var cutScores = new List<CoverageScore>(armReps);

                for (int rep = 1; rep <= armReps; rep++)
                {
                    IEvaluableAgent agent = arm.Create(armContext);
                    string repLabel = arm.IsRepeated ? $"rep {rep}/{armReps}" : "deterministic";

                    var scored = await ScoreArmAsync(
                        persona, goldByPersona, agent, harness, options, ownK, arm.Label, repLabel, declaredK, ct)
                        .ConfigureAwait(false);

                    // A loop arm says how many rounds it took. Design §D.3's guard (b): a degenerate
                    // reviewer shows P(rounds = 1) ≈ 1, and that is invisible in a coverage number.
                    if (agent is IDiscoveryLoopArm { LastRun: { } telemetry })
                    {
                        if (!roundsByArm.TryGetValue(arm.Label, out var taken))
                            roundsByArm[arm.Label] = taken = [];
                        taken.Add(telemetry.RoundsTaken);
                    }

                    // The live arm's second turn, when its first turn presented nothing. Printed on
                    // the cell so "k = 3" and "k = 3 after the customer answered" never read alike.
                    if (agent is ClarifyingTurnAdapter { LastOutcome: { } turn }
                        && (turn.SecondTurnRan || turn.PresentedAfterFirstTurn == 0))
                    {
                        secondTurns.Add(($"{persona.Id} · {repLabel}", turn));
                        Console.ForegroundColor = turn.SilentAfterSecondTurn || turn.SecondTurnThrew ? ConsoleColor.Yellow : ConsoleColor.DarkCyan;
                        Console.WriteLine($"      ↩ second turn · {turn.Describe()}");
                        Console.ResetColor();
                    }

                    if (scored is null)
                    {
                        armsThatThrew++;
                        notes.Add($"{persona.Id} · {arm.Label} · {repLabel} THREW and was EXCLUDED from the mean. "
                                + "An errored turn presents nothing, and 0/n is not a measurement of an agent — it "
                                + "is the absence of one.");
                        continue;
                    }

                    ownScores.Add(scored.Value.Own);
                    cutScores.Add(scored.Value.Cut);
                    ownK.RecordPresented(persona.Id, arm.Label, scored.Value.Presented);
                }

                if (ownScores.Count > 0)
                {
                    ownK.Record(persona.Id, arm.Label, CoverageScore.Mean(ownScores));
                    atK.Record(persona.Id, arm.Label, CoverageScore.Mean(cutScores));
                }
                else
                {
                    notes.Add($"{persona.Id} · {arm.Label}: EVERY run threw, so this persona contributes NO "
                            + "observation for this arm at all. It is missing from the pairing rather than scored zero.");
                }
            }
        }

        // ── PANEL 1: the comparison, at the declared budget. ─────────────────────────────
        //
        // ⚠ The stub marker goes in the panel TITLE, not only in the banner further up. The live
        // column keeps the label "Single Agent (Robin)" because that string is the report's KEY —
        // the floors dictionary, the sign-test pairs and both gates look the arm up by it — so the
        // place to say "that column is a stub" is the frame the column sits inside.
        EvalPrinter.PrintDeclaredKCoverage(atK, declaredK, recallFloorsAtK, precisionFloors,
            dryRun
                ? $"Eval 02 — AT DECLARED k = {declaredK} — DRY RUN: the 'Single Agent' COLUMN IS A STUB (n = {atK.Personas.Count})"
                : $"Eval 02 — AT DECLARED k = {declaredK} (paired, n = {atK.Personas.Count}, {reps} rep(s) on the live arm)");

        // ── PANEL 2: the re-read at the live arm's OWN k. ────────────────────────────────
        //
        // In a live run the live cells are this run's, rep by rep. In a dry run the stub's cells
        // mean nothing, so the panel re-reads the PERSISTED live run instead — the one reading of
        // that run that pairs like with like, and it costs nothing.
        var deterministicArms = CoverageArms.Runnable.Where(a => !a.IsRepeated).Select(a => a.Label).ToList();
        PairedCoverageReport? rereadReport = null;
        IReadOnlyList<OwnKRereadRow> rereadRows = [];
        string rereadProvenance;
        CoverageSnapshot? persisted = dryRun ? EvalResultStore.LoadCoverage(EvalResultStore.CoverageKey) : null;

        if (!dryRun)
        {
            (rereadReport, rereadRows, rereadProvenance) = OwnKReread.FromThisRun(ownK, ArmLive, deterministicArms, goldByPersona);
        }
        else if (persisted is not null)
        {
            (rereadReport, rereadRows, rereadProvenance) = OwnKReread.FromSnapshot(ownK, persisted, ArmLive, deterministicArms, goldByPersona);
            notes.Add($"RE-READ SOURCE: the live cells on the own-k panel are the PERSISTED run of {persisted.RunAt:u} "
                    + $"(DeclaredK = {persisted.DeclaredK}; utterance {(persisted.Utterance.Length == 0 ? "NOT recorded — pre-dates the declared budget" : "recorded")}). "
                    + "The stub's cells were NOT used there. The declared-k panel's live column IS the stub.");
        }
        else
        {
            rereadProvenance = "no persisted live run on disk and this is a dry run — nothing to re-read";
            notes.Add("RE-READ SKIPPED: no persisted Eval 02 snapshot exists, so there is no live run to re-read at its own k.");
        }

        EvalPrinter.PrintOwnKReread(rereadRows, ArmLive, deterministicArms, rereadProvenance);

        // ── PANEL 3: every arm at its OWN k — the floors GATE 1 reads. Not a comparison. ──
        EvalPrinter.PrintPairedCoverage(ownK, floors,
            dryRun
                ? $"Eval 02 — OWN k (GATE 1 floors; NOT for pairing) — DRY RUN, 'Single Agent' IS A STUB (n = {ownK.Personas.Count})"
                : $"Eval 02 — OWN k (GATE 1 floors; NOT for pairing) (n = {ownK.Personas.Count}, {reps} rep(s) on the live arm)");

        EvalPrinter.PrintForcedChoice(atK, forcedChoiceFloor, scorablePersonas);

        // ── Sign tests — EQUAL-k pairs only, on both panels. ─────────────────────────────
        //
        // ⚠ The pairs come from the registry (CoverageArm.EntersSignTest), not from a hand-built
        // list. Two reasons, both measured rather than theoretical. First, the tag-join ORACLE is
        // deliberately NOT entered — it reads the gold, and the printer paints a leading challenger
        // GREEN, so entering it rendered "the oracle beat the agent" as a positive result. Second,
        // the control gate below used to read signTests[0], a POSITIONAL index: inserting an arm
        // ahead of the control would have silently re-pointed a gate at a different comparison.
        var atKRecall = CoverageArms.SignTestPairs
            .Select(pair => atK.SignTestAtEqualK(pair.Reference, pair.Challenger, CoverageMetric.Recall))
            .ToList();
        var atKPrecision = CoverageArms.SignTestPairs
            .Select(pair => atK.SignTestAtEqualK(pair.Reference, pair.Challenger, CoverageMetric.PrecisionAtK))
            .ToList();

        // ⚠ The colour map is the REGISTRY's, not the panel's. Before this the sign-test rows were
        // painted green whenever the CHALLENGER led — and the primary control is a challenger, so
        // "one retrieval pass matched the shipped agent" rendered in the colour of good news (B-12).
        var kindByArm = CoverageArms.All.ToDictionary(a => a.Label, a => a.Kind, StringComparer.Ordinal);

        EvalPrinter.PrintSignTest([.. atKRecall, .. atKPrecision],
            $"Paired sign test AT THE DECLARED k = {declaredK} — equal-k pairs only, reported, never gated",
            kindByArm);

        // ── The design's pre-registered rule, EVALUATED. ─────────────────────────────────
        //
        // ⚠ B-2. This block used to be a sentence in the pre-registration banner with nothing
        // behind it — no WinsRequired, no comparison, no verdict — printed above a panel that had
        // once shown a green 12/0/0 for a DIFFERENT pair. The rule names the discovery WORKFLOW
        // against the single agent, and since the k = 5 re-cut a pair at unequal k is not
        // comparable at all, so the verdict has to be able to say NOT EVALUATED out loud rather
        // than vanishing. It is rendered for that pair specifically, whatever the panel shows.
        var preRegistered = PreRegisteredRule.Evaluate(
            ArmLive, CoverageArms.DiscoveryWorkflow, atKRecall, $"the declared-k panel (k = {declaredK}, recall)");
        EvalPrinter.PrintPreRegisteredRule(preRegistered);

        var rereadRecall = new List<SignTestOutcome>();
        var rereadPrecision = new List<SignTestOutcome>();
        if (rereadReport is not null)
        {
            rereadRecall = CoverageArms.SignTestPairs
                .Select(pair => rereadReport.SignTestAtEqualK(pair.Reference, pair.Challenger, CoverageMetric.Recall))
                .ToList();
            rereadPrecision = CoverageArms.SignTestPairs
                .Select(pair => rereadReport.SignTestAtEqualK(pair.Reference, pair.Challenger, CoverageMetric.PrecisionAtK))
                .ToList();

            EvalPrinter.PrintSignTest([.. rereadRecall, .. rereadPrecision],
                "Paired sign test AT THE LIVE ARM'S OWN k — controls cut to match, reported, never gated",
                kindByArm);
        }

        EvalPrinter.PrintCostComparison(ownK);

        // ── Gates. Deliberately NOT gated on "the agent won". ────────────────────────────
        double liveMean = ownK.MeanLatent(ArmLive);
        double meanFloor = floors.Values.Where(f => !double.IsNaN(f)).DefaultIfEmpty(double.NaN).Average();

        // ⚠ EVERY persona against its OWN floor, at its OWN k — not mean against mean. MEASURED
        // on the three-persona corpus this suite used to have: a constant arm presenting one
        // descaler to everybody scored 0.000 / 1.000 / 1.000, and its mean of 0.667 cleared a mean
        // floor of 0.462 while it was BELOW the floor on two personas of the three. One persona can
        // carry a mean; it cannot carry this.
        bool aboveFloor = ownK.EveryPersonaAboveOwnFloor(ArmLive);
        var below = ownK.PersonasBelowOwnFloor(ArmLive);

        // ⚠ GATE 2 reads EVERY equal-k recall comparison against the primary control — the
        // declared-k one (fair for arms that were GIVEN the budget) and the own-k re-read (fair
        // for a live arm that was not). The control leading on EITHER fails the gate; neither
        // decidable is UNDECIDABLE and fails closed. An absent control is not a passed one, and
        // a comparison with zero comparable pairs is not a passed one either.
        CoverageArm? primaryControl = CoverageArms.PrimaryControl;
        var gate2Reads = new List<(string Panel, SignTestOutcome Outcome)>();
        if (primaryControl is not null)
        {
            gate2Reads.AddRange(atKRecall
                .Where(t => string.Equals(t.ArmB, primaryControl.Label, StringComparison.Ordinal))
                .Select(t => ($"declared k={declaredK}", t)));
            gate2Reads.AddRange(rereadRecall
                .Where(t => string.Equals(t.ArmB, primaryControl.Label, StringComparison.Ordinal))
                .Select(t => ("own k, control re-cut", t)));
        }

        bool gate2Decidable = gate2Reads.Any(r => !r.Outcome.Undecidable);
        bool controlLeadsAnywhere = gate2Reads.Any(r => !r.Outcome.Undecidable && r.Outcome.ChallengerLeads);
        bool controlSane = primaryControl is not null && gate2Decidable && !controlLeadsAnywhere;

        // ⚠ B-11. The gate's RESULT is a bool; what the printer needs is the observed STATE, because
        // "the control did not lead", "there was nothing to lead on" and "no control was run" are
        // three different sentences and the printer used to render the first one for all three.
        EvalPrinter.CoverageGate2State gate2State =
            primaryControl is null ? EvalPrinter.CoverageGate2State.NoControlRun
            : !gate2Decidable ? EvalPrinter.CoverageGate2State.NoComparablePair
            : controlLeadsAnywhere ? EvalPrinter.CoverageGate2State.ControlLed
            : EvalPrinter.CoverageGate2State.ControlDidNotLead;

        string gate2Detail = primaryControl is null
            ? "no primary control arm was run."
            : string.Join(" · ", gate2Reads.Select(r =>
                  r.Panel
                + (r.Outcome.Undecidable
                    ? $": UNDECIDABLE ({r.Outcome.Excluded.Count} not comparable)"
                    : $": W/L/T {r.Outcome.Wins}/{r.Outcome.Losses}/{r.Outcome.Ties}, p = {r.Outcome.PValue:F4}, "
                      + $"{r.Outcome.Excluded.Count} not comparable → "
                      + (r.Outcome.ChallengerLeads ? "CONTROL LEADS" : "control does not lead"))));

        if (primaryControl is null)
        {
            notes.Add("GATE 2 is UNDECIDABLE: no primary control arm was run, so nothing could have taken the "
                    + "win away. Failing closed — an absent control is not a passed one.");
        }
        else if (!gate2Decidable)
        {
            notes.Add("GATE 2 is UNDECIDABLE: the primary control had NO equal-k pair with the live arm on either "
                    + "panel. Every persona was refused as NOT COMPARABLE (different k, or a silent side). Failing "
                    + "closed — a comparison that could not be made is not a comparison the agent won.");
        }

        foreach (var (label, taken) in roundsByArm.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            int degenerate = taken.Count(r => r <= 1);
            notes.Add($"LOOP HEALTH · {label}: rounds taken "
                    + string.Join(", ", taken.GroupBy(r => r).OrderBy(g => g.Key)
                          .Select(g => $"{g.Count()}×{g.Key}"))
                    + $" · P(rounds = 1) = {(taken.Count == 0 ? "n/a" : Format(degenerate / (double)taken.Count))}. "
                    + "Design §D.3 guard (b): a reviewer that rubber-stamps round 1 shows P(rounds = 1) ≈ 1 and is "
                    + "INVISIBLE in a coverage number, so the distribution is printed beside it.");
        }

        if (secondTurns.Count > 0)
        {
            int answered = secondTurns.Count(t => t.Outcome.SecondTurnRan && !t.Outcome.SecondTurnThrew);
            int recovered = secondTurns.Count(t => t.Outcome.SecondTurnRan && t.Outcome.PresentedAfterSecondTurn > 0);
            int stillSilent = secondTurns.Count(t => t.Outcome.SilentAfterSecondTurn);
            notes.Add($"SECOND TURN · {secondTurns.Count} live cell(s) presented NOTHING on turn 1 — the instructed "
                    + "thin-signal behaviour is to stop and ask two clarifying questions. The harness answered from the "
                    + $"persona's own profile (question-blind, no SKU, no category, no gold) and ran one more turn on the same "
                    + $"session: {answered} answered, {recovered} presented after the answer, {stillSilent} still silent — and "
                    + "ONLY that last silence is scored as silence. Cells: "
                    + string.Join("; ", secondTurns.Select(t => $"{t.Cell}: {t.Outcome.Describe()}"))
                    + ". A turn-1 silence on these cells is a HARNESS fact, not an agent fact.");
        }
        else
        {
            notes.Add("SECOND TURN · no live cell presented nothing on turn 1, so the harness's second turn never ran. "
                    + "The adapter was armed on every live cell.");
        }

        notes.Add("GATE 1 is per-persona, at each arm's OWN presentation count. The mean floor "
                + $"({Format(meanFloor)}) and the mean live score ({Format(liveMean)}) are printed for context and "
                + "are NOT what the gate reads: a mean-to-mean test is passed by an arm that clears the mean while "
                + "sitting below the floor on most of the personas that produced it."
                + (below.Count > 0 ? $" Below its own floor here: {string.Join(", ", below)}." : ""));

        // ── Which comparison is FAIR, said plainly. ───────────────────────────────────────
        notes.Add($"COMPARABILITY. The declared-k panel is fair only between arms that were GIVEN the budget "
                + $"(k = {declaredK}) and FILLED it; the utterance now declares it, so every live turn of THIS run "
                + "was told. The own-k re-read is fair for any live arm, told or not, because the controls are cut "
                + "to whatever it presented. A raw cell from an arm that was not told a budget, paired against a "
                + "5-item control, is NOT a comparison and no longer appears anywhere on this report.");

        if (persisted is not null && persisted.DeclaredK == 0)
        {
            notes.Add($"⚠️ The persisted live run ({persisted.RunAt:u}) was made under an utterance that declared NO "
                    + "budget and its per-rep item lists were not persisted. Its live cells therefore appear ONLY on "
                    + "the own-k re-read, at the ROUNDED rep-mean k the snapshot recorded, and its precision at that k "
                    + "is NOT RECORDED. They cannot be read at the declared k at all. A live re-run under the "
                    + "declared-budget utterance is what fills the declared-k panel's live column with a result.");
        }

        AddLeaderNotes(notes, atKRecall, atKPrecision, rereadRecall, rereadPrecision, declaredK, dryRun);

        double tagJoinMean = ownK.MeanLatent(ArmTagJoin);
        double singleShotMean = ownK.MeanLatent(ArmSingleShot);

        if (!double.IsNaN(tagJoinMean) && !double.IsNaN(singleShotMean))
        {
            bool identical = Math.Abs(tagJoinMean - singleShotMean) < 1e-9;
            notes.Add((identical
                    ? $"⚠️ The tag-join ORACLE and the one-pass single-shot control scored the SAME number "
                    + $"({tagJoinMean:F3}) — not 'the oracle scored at least as much', EQUAL. "
                    : $"⚠️ The tag-join ORACLE scored {tagJoinMean:F3} against the single-shot control's "
                    + $"{singleShotMean:F3}. ")
                + "An arm that calls InterestMapGold.Derive and an arm that never sees the gold are being separated "
                + "by this metric to that extent and no further. Design §0.5 / D-4 is CONFIRMED on this corpus.");
        }

        // Which arms are INDISTINGUISHABLE from the oracle, cell for cell — computed rather than
        // named, so an arm added later is checked the same way. Two arms with equal means could
        // still differ persona by persona; equal CELLS is the stronger statement and the one that
        // says the metric has no room left.
        var oracleTwins = atK.Arms
            .Where(a => !string.Equals(a, ArmTagJoin, StringComparison.Ordinal))
            .Where(a => atK.Personas.All(p =>
            {
                var mine = atK.ScoreOf(p, a);
                var oracle = atK.ScoreOf(p, ArmTagJoin);
                return mine is { IsScorable: true } && oracle is { IsScorable: true }
                    && Math.Abs(mine.Value.Latent - oracle.Value.Latent) < 1e-9;
            }))
            .ToList();

        if (oracleTwins.Count > 0)
        {
            notes.Add($"⚠️ {oracleTwins.Count} arm(s) are INDISTINGUISHABLE from the tag-join ORACLE cell for cell at k = {declaredK}: "
                    + string.Join(", ", oracleTwins) + ". Not 'close to' — identical on every persona. Whatever "
                    + "separates these architectures, this metric does not see it, and a difference reported between "
                    + "any two of them would be noise with a decimal point on it.");
        }

        if (!double.IsNaN(tagJoinMean) && !double.IsNaN(liveMean) && tagJoinMean >= liveMean)
        {
            notes.Add($"⚠️ The tag-join baseline scored {tagJoinMean:F3} against the agent's {liveMean:F3} (own k), with zero "
                    + "model calls. Latent coverage as defined here is substantially a tag join, and it does not "
                    + "license a claim about inference. The comparison that still means something is "
                    + "agent-versus-single-pass, not agent-versus-oracle.");
        }

        double popularityMean = ownK.MeanLatent(ArmPopularity);
        notes.Add($"Popularity coverage MEASURED at {Format(popularityMean)}. The design pre-registers 0.00, but that "
                + "figure belongs to a bestseller list authored to carry no latent tokens; this catalogue's list is "
                + "derived from rating counts, so the design's number does not transfer and the measured one is used.");

        notes.Add($"Cross-persona forced choice at k = {declaredK}, chance = {Format(forcedChoiceFloor)} (1/{scorablePersonas}, exact and "
                + "unsaturable): "
                + string.Join("; ", atK.Arms.Select(a =>
                      $"{EvalPrinter.ShortArm(a)} {Format(atK.ForcedChoiceRate(a))}"))
                + ". An arm at chance here has produced answers that fit any of these customers equally well, "
                + "whatever its coverage says.");

        // The DENOMINATOR is the personas GATE 1 actually read — the scorable ones for the live arm
        // — not every persona in the table. A gate that reports "12 of 12" while it only read three
        // is the diluted-denominator shape, and it fails in the flattering direction.
        int gate1Read = ownK.Personas.Count(p => ownK.ScoreOf(p, ArmLive) is { IsScorable: true });

        EvalPrinter.PrintCoverageGate(aboveFloor, below, gate1Read, gate2State, notes, gate2Detail);

        if (dryRun)
        {
            // A stub result under the real key would be read later as a measurement, and nothing
            // about the JSON would say "stub".
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot NOT written — a dry run must not leave a result behind.");
            Console.WriteLine("     GATE 1 above is expected to FAIL: a stub that presents the same two products");
            Console.WriteLine("     for every persona cannot beat a random draw. That is the stub being a stub.");
            Console.WriteLine("     GATE 2 above read the PERSISTED live run's re-read where one exists — not the stub.");
            Console.ResetColor();

            // ⚠ This branch used to end in `return 0;`, so Eval 02's dry run — stage one of this
            // repository's three-stage run protocol — could not fail. A stage that cannot fail is
            // not a stage. It now asserts the same class of property Eval 01's dry run does.
            bool plumbingHeld = DryRunPlumbingHeld(ownK, atK, goldByPersona, floors, precisionFloors, armsThatThrew,
                                                   declaredK, atKRecall, persisted, rereadRows, preRegistered);
            bool secondTurnWired = SecondTurnPlumbingHeld(secondTurns);
            return plumbingHeld && secondTurnWired ? 0 : 1;
        }

        string snapshotKey = onlyPersona is null ? EvalResultStore.CoverageKey : ProbeSnapshotKey;
        EvalResultStore.SaveCoverage(snapshotKey, ownK.ToSnapshot(floors, declaredK, GalaxusEvalPrompt.CoverageCanonical, atK));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Snapshot saved → {EvalResultStore.StorageLocation} ({snapshotKey})");
        Console.ResetColor();

        return aboveFloor && controlSane ? 0 : 1;
    }

    /// <summary>
    /// Says, per comparison and per panel, who leads — with the n it leads on and the pairs it
    /// could not use — so the reader is never left to reconstruct the answer from two tables.
    /// </summary>
    private static void AddLeaderNotes(
        List<string> notes,
        IReadOnlyList<SignTestOutcome> atKRecall, IReadOnlyList<SignTestOutcome> atKPrecision,
        IReadOnlyList<SignTestOutcome> rereadRecall, IReadOnlyList<SignTestOutcome> rereadPrecision,
        int declaredK, bool dryRun)
    {
        static string Verdict(SignTestOutcome o) =>
            o.Undecidable
                ? $"UNDECIDABLE (0 comparable, {o.Excluded.Count} refused)"
                : $"{(o.ChallengerLeads ? "challenger LEADS" : o.Wins == o.Losses ? "no direction" : "reference leads")} "
                  + $"W/L/T {o.Wins}/{o.Losses}/{o.Ties}, p = {o.PValue:F4}, mean Δ {o.MeanDelta:+0.000;-0.000;0.000}"
                  + (o.Excluded.Count > 0 ? $", {o.Excluded.Count} refused" : "");

        foreach (var o in atKRecall)
            notes.Add($"AT k = {declaredK} · recall · {EvalPrinter.ShortArm(o.ArmB)} vs {EvalPrinter.ShortArm(o.ArmA)}: {Verdict(o)}."
                    + (dryRun ? " (live side = STUB; this row proves the equal-k rule, not a result)" : ""));
        foreach (var o in atKPrecision)
            notes.Add($"AT k = {declaredK} · precision@{declaredK} · {EvalPrinter.ShortArm(o.ArmB)} vs {EvalPrinter.ShortArm(o.ArmA)}: {Verdict(o)}."
                    + (dryRun ? " (live side = STUB)" : ""));
        foreach (var o in rereadRecall)
            notes.Add($"OWN-k RE-READ · recall · {EvalPrinter.ShortArm(o.ArmB)}@k_live vs {EvalPrinter.ShortArm(o.ArmA)}: {Verdict(o)}.");
        foreach (var o in rereadPrecision)
            notes.Add($"OWN-k RE-READ · precision@k_live · {EvalPrinter.ShortArm(o.ArmB)}@k_live vs {EvalPrinter.ShortArm(o.ArmA)}: {Verdict(o)}."
                    + (o.Undecidable && o.Excluded.Any(e => e.Contains("undefined", StringComparison.Ordinal))
                        ? " The persisted run recorded no item lists, so the live arm's precision is NOT RECORDED there."
                        : ""));
    }

    /// <summary>
    /// Whether the dry run proved the harness's SECOND TURN is wired on the live arm: on the
    /// persona the ask-first stub targets, turn 1 presented nothing, the reply reached the same
    /// session, and the merged trace carried turn 2's presentations into the graded score.
    /// </summary>
    /// <remarks>
    /// Printed as its own line under the other plumbing checks, and it CAN fail: a reply sent to
    /// a fresh session, or two turns whose raw messages were not merged, would leave the stub's
    /// turn-2 presentation out of the graded trace and Jonas's cell would read k = 0 exactly as it
    /// did before the adapter existed.
    /// </remarks>
    /// <param name="secondTurns">The second-turn outcomes the run recorded.</param>
    private static bool SecondTurnPlumbingHeld(IReadOnlyList<(string Cell, ClarifyingTurnOutcome Outcome)> secondTurns)
    {
        var wired = secondTurns
            .Where(t => t.Outcome is { SecondTurnRan: true, SecondTurnThrew: false, PresentedAfterFirstTurn: 0, PresentedAfterSecondTurn: > 0 })
            .ToList();
        bool ok = wired.Count > 0;

        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(ok
            ? $"  ✅ the harness's SECOND TURN is wired on the live arm: {string.Join(", ", wired.Select(t => $"{t.Cell} (k {t.Outcome.PresentedAfterFirstTurn}→{t.Outcome.PresentedAfterSecondTurn})"))} — "
              + "turn 1 asked and presented nothing, the reply reached the same session, and the merged trace carried turn 2's presentations."
            : "  ❌ the harness's SECOND TURN did NOT fire on any live cell, or fired and carried no turn-2 presentation into the "
              + "graded trace. Jonas's instructed silence would still be graded as the agent's.");
        Console.ResetColor();

        return ok;
    }

    /// <summary>One rep's two readings and the raw list they were read from.</summary>
    private readonly record struct ScoredRep(CoverageScore Own, CoverageScore Cut, IReadOnlyList<PresentedCall> Presented);

    /// <summary>
    /// Runs and grades one arm for one persona — at its OWN k and at the DECLARED k. Returns null
    /// when the turn THREW; the caller then excludes it rather than folding it into a mean.
    /// </summary>
    /// <remarks>
    /// ⚠ This method used to compute and return the score BEFORE looking at
    /// <c>result.HasError</c>. An errored turn produces an empty trace, an empty trace serves no
    /// token, and 0/n is a perfectly well-formed 0.000 — which was then averaged into
    /// <c>CoverageScore.Mean</c> as if it were an observation of the agent. It is not an
    /// observation of the agent; it is the absence of one, and the two must never average together.
    /// </remarks>
    private static async Task<ScoredRep?> ScoreArmAsync(
        CoveragePersona persona,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona,
        IEvaluableAgent agent,
        MAFEvaluationHarness harness,
        EvaluationOptions options,
        PairedCoverageReport costReport,
        string armLabel,
        string repLabel,
        int declaredK,
        CancellationToken ct)
    {
        var testCase = new TestCase
        {
            Name = $"{persona.Id} · {armLabel} · {repLabel}",
            Input = persona.Prompt,
            PassingScore = 0,     // no criteria, no judge — the verdict is the coverage grade
        };

        TestResult result;
        using (EvalRuntime.BeginTurn())
        {
            result = await harness.RunEvaluationAsync(agent, testCase, options, ct).ConfigureAwait(false);
        }

        costReport.RecordCost(armLabel, result.Performance);

        // The error check comes FIRST. Nothing below it may produce a number.
        if (result.HasError)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      {armLabel,-26} {repLabel,-16} ❌ the turn threw: {result.Error?.Message}");
            Console.WriteLine("                                                    EXCLUDED — not scored 0.000.");
            Console.ResetColor();
            return null;
        }

        var presented = PresentedCall.FromToolUsage(result.ToolUsage);
        CoverageScore own = InterestCoverageGrader.GradeWithControls(persona.Id, goldByPersona, presented);
        CoverageScore cut = InterestCoverageGrader.GradeAtDeclaredK(persona.Id, goldByPersona, presented, declaredK);

        Console.ForegroundColor = own.IsScorable && own.Latent > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
        Console.WriteLine($"      {armLabel,-26} {repLabel,-16} own k={own.PresentedCount,2}: latent {Format(own.Latent)} "
                        + $"({own.LatentServed}/{own.LatentTotal}) vs floor {Format(own.LatentFloor)}"
                        + $"  │  @k={declaredK}: recall {Format(cut.Latent)} prec {Format(cut.PrecisionAtK)} "
                        + $"({cut.RelevantCount}/{declaredK}) vs {Format(cut.PrecisionFloor)}"
                        + (cut.OverFilledBudget ? $" ✂{cut.PresentedBeforeCut}→{cut.PresentedCount}" : "")
                        + (cut.UnderFilledBudget ? $" ↓{cut.PresentedBeforeCut}" : "")
                        + (own.IsSilent ? "  SILENT" : "")
                        + $"  forced-choice {Format(cut.ForcedChoice)}"
                        + (own.PhantomCount > 0 ? $"  ⚠ phantom {own.PhantomCount}" : ""));
        Console.ResetColor();

        return new ScoredRep(own, cut, presented);
    }

    /// <summary>
    /// Whether the dry run proved the PLUMBING — the only thing a stub can prove, and the reason
    /// stage one of the three-stage run protocol exists.
    /// </summary>
    /// <remarks>
    /// <para>Nine properties, each of which has to hold before any live number is trustworthy:</para>
    /// <list type="number">
    ///   <item><description><b>Gold derivation produced something.</b> At least one persona has a
    ///   non-empty latent-gold set. If R2 returns nothing for everybody, every score below is NaN
    ///   and the run reports an empty comparison as a clean one.</description></item>
    ///   <item><description><b>Every scored persona has a DEFINED recall floor.</b> A score with no
    ///   floor beside it is a decoration.</description></item>
    ///   <item><description><b>Every scored persona has a DEFINED precision floor.</b> Same reason,
    ///   other channel.</description></item>
    ///   <item><description><b>The live arm produced a real mean.</b> NaN means the persona loop,
    ///   the adapter or the extraction path is broken, not that the stub was modest.</description></item>
    ///   <item><description><b>Every deterministic arm presented at least one item.</b> An arm that
    ///   silently presents nothing sails through gate 2 by being broken.</description></item>
    ///   <item><description><b>No arm threw.</b></description></item>
    ///   <item><description><b>The declared-k cut RAN.</b> No cell scored more than k items, and at
    ///   least one arm was actually cut (Demo 2 presents 7–12, so on this corpus something must
    ///   be). A cut that never fires is a budget that was never applied.</description></item>
    ///   <item><description><b>The equal-k rule REFUSED something.</b> The stub presents two items
    ///   against controls at five, so the declared-k sign test must report those pairs NOT
    ///   COMPARABLE rather than counting them. A refusal that never fires is a rule that is not
    ///   wired.</description></item>
    ///   <item><description><b>The persisted re-read produced rows</b> when a snapshot exists —
    ///   otherwise the zero-cost reading of the paid run is silently absent.</description></item>
    ///   <item><description><b>Every registered arm carries a NOTE</b> (§8, B-12). The legend
    ///   prints one row per arm and the note is the row's payload; an arm registered without one
    ///   would print a blank caveat, which is how "do NOT read this as the headline" came to live
    ///   in source and nowhere else.</description></item>
    ///   <item><description><b>The pre-registered rule rendered a VERDICT</b> (§8, B-2). Not a
    ///   particular verdict — any of the three, with a reason attached. A rule text with no
    ///   evaluator behind it is what this check exists to make impossible to ship again.</description></item>
    /// </list>
    /// </remarks>
    private static bool DryRunPlumbingHeld(
        PairedCoverageReport ownK,
        PairedCoverageReport atK,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona,
        IReadOnlyDictionary<string, double> floors,
        IReadOnlyDictionary<string, double> precisionFloors,
        int armsThatThrew,
        int declaredK,
        IReadOnlyList<SignTestOutcome> atKRecall,
        CoverageSnapshot? persisted,
        IReadOnlyList<OwnKRereadRow> rereadRows,
        PreRegisteredRuleOutcome preRegistered)
    {
        var scored = goldByPersona.Where(kv => !kv.Value.LatentIsEmpty).Select(kv => kv.Key)
            .Where(id => ownK.Personas.Contains(id, StringComparer.Ordinal)).ToList();

        bool goldDerived = scored.Count > 0;
        bool floorsDefined = goldDerived
            && scored.All(id => floors.TryGetValue(id, out var f) && !double.IsNaN(f));
        bool precisionFloorsDefined = goldDerived
            && scored.All(id => precisionFloors.TryGetValue(id, out var f) && !double.IsNaN(f));
        bool liveMeasured = !double.IsNaN(ownK.MeanLatent(ArmLive));

        // Every runnable arm except the live one — from the registry, so an arm added tomorrow is
        // checked for silence tomorrow rather than the next time someone remembers this list.
        var silentArms = new List<string>();
        foreach (CoverageArm arm in CoverageArms.Runnable.Where(a => !a.IsRepeated))
        {
            bool presentedSomething = ownK.Personas
                .Select(p => ownK.ScoreOf(p, arm.Label))
                .Any(s => s is { PresentedCount: > 0 });

            if (!presentedSomething) silentArms.Add(arm.Label);
        }

        bool noneThrew = armsThatThrew == 0;

        var cutCells = atK.Personas.SelectMany(p => atK.Arms.Select(a => atK.ScoreOf(p, a))).Where(s => s is not null).Select(s => s!.Value).ToList();
        bool cutBounded = cutCells.Count > 0 && cutCells.All(s => s.PresentedCount <= declaredK && s.DeclaredK == declaredK);
        bool cutFired = cutCells.Any(s => s.OverFilledBudget);

        bool refusalFired = atKRecall.Count > 0 && atKRecall.All(o => o.Excluded.Count > 0);

        bool rereadRan = persisted is null || rereadRows.Count > 0;

        // ⚠ B-12. The legend's payload is the NOTE. An arm with an empty one prints a row that says
        // nothing, and the "do NOT read this arm's number as the headline" caveat is exactly the
        // note most likely to be left off a new arm.
        var notelessArms = CoverageArms.All.Where(a => a.Note.Length == 0).Select(a => a.Label).ToList();
        bool everyArmHasANote = notelessArms.Count == 0;

        // ⚠ B-2. Any of the three verdicts counts; a MISSING one does not. The rule must also carry
        // a reason — "NOT EVALUATED" with no sentence after it is the dormant text again in a
        // different font.
        bool ruleRendered = preRegistered.Reason.Length > 0
            && Enum.IsDefined(preRegistered.Verdict)
            && string.Equals(preRegistered.Challenger, CoverageArms.DiscoveryWorkflow, StringComparison.Ordinal)
            && string.Equals(preRegistered.Reference, ArmLive, StringComparison.Ordinal)
            && preRegistered.WinsRequired == PreRegisteredRule.WinsRequired;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── DRY-RUN PLUMBING CHECKS (a stub cannot prove anything else) ──────");
        Console.ResetColor();

        Line(goldDerived, $"the gold derivation produced a non-empty latent set for {scored.Count} persona(s).");
        Line(floorsDefined, "every scored persona has a DEFINED random-draw recall floor.");
        Line(precisionFloorsDefined, "every scored persona has a DEFINED R/N precision floor.");
        Line(liveMeasured, "the live arm produced a real mean — the persona loop, the adapter and the "
                         + "tool-trace extraction all ran.");
        Line(silentArms.Count == 0, silentArms.Count == 0
            ? "every deterministic arm presented at least one item."
            : $"an arm presented NOTHING: {string.Join(", ", silentArms)}. A silent control passes gate 2 for the "
            + "wrong reason.");
        Line(noneThrew, noneThrew ? "no arm threw." : $"{armsThatThrew} arm run(s) threw.");
        Line(cutBounded && cutFired, cutBounded && cutFired
            ? $"the declared-k cut ran: no cell scored more than {declaredK} items, and {cutCells.Count(s => s.OverFilledBudget)} cell(s) were actually cut."
            : !cutBounded
                ? $"a cell scored MORE than the declared k = {declaredK}, or was not marked as cut at it."
                : "no cell was ever cut — on this corpus Demo 2 presents 7–12 items, so a cut that never fires is a budget never applied.");
        Line(refusalFired, refusalFired
            ? "the equal-k rule REFUSED the stub-vs-control pairs at the declared k (2 items vs 5) as NOT COMPARABLE."
            : "the equal-k rule refused NOTHING at the declared k — a 2-item stub was paired against 5-item controls.");
        Line(rereadRan, persisted is null
            ? "no persisted live run on disk — the own-k re-read had nothing to read (not a fault)."
            : rereadRows.Count > 0
                ? $"the persisted live run ({persisted.RunAt:u}) was re-read at its own k: {rereadRows.Count} row(s)."
                : "a persisted live run exists but the re-read produced NO rows.");
        Line(everyArmHasANote, everyArmHasANote
            ? $"every one of the {CoverageArms.All.Count} registered arms carries a NOTE, so the legend printed a "
            + "caveat for each rather than a blank row."
            : $"arm(s) registered with NO note: {string.Join(", ", notelessArms)}. The legend row prints empty and the "
            + "arm's caveat exists only in source.");
        Line(ruleRendered, ruleRendered
            ? $"the pre-registered ≥ {PreRegisteredRule.WinsRequired}-of-{PreRegisteredRule.PreRegisteredPairs} rule was "
            + $"EVALUATED for the loop-vs-agent pair and rendered {preRegistered.Label}, with a reason."
            : "the pre-registered rule rendered NO verdict for the loop-vs-agent pair, or rendered one with no reason. "
            + "That is the B-2 defect: rule text with no evaluator behind it.");

        return goldDerived && floorsDefined && precisionFloorsDefined && liveMeasured && silentArms.Count == 0
            && noneThrew && cutBounded && cutFired && refusalFired && rereadRan && everyArmHasANote && ruleRendered;

        static void Line(bool ok, string text)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {text}");
            Console.ResetColor();
        }
    }

    private static void PrintPersonaHeader(
        CoveragePersona persona, GoldInterestMap gold, int poolSize, double randomLatent, double randomManifest,
        int declaredK, int relevantCarriers, double randomPrecision)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine();
        Console.WriteLine($"  ─── {persona.Id}  {persona.Name} ──────────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      {persona.Note}");
        Console.ResetColor();

        Console.WriteLine($"      latent gold  : {(gold.Latent.Count == 0 ? "(empty — persona skipped)" : string.Join(", ", gold.Latent.OrderBy(t => t, StringComparer.Ordinal)))}");
        Console.WriteLine($"      manifest gold: {(gold.Manifest.Count == 0 ? "(none)" : string.Join(", ", gold.Manifest.OrderBy(t => t, StringComparer.Ordinal)))}");
        if (gold.ExcludedPurchaseIds.Count > 0)
            Console.WriteLine($"      R3 excluded  : {string.Join(", ", gold.ExcludedPurchaseIds)}");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      FLOORS at the declared k = {declaredK}, over a {poolSize}-product eligible pool:");
        Console.WriteLine($"        recall     {Format(randomLatent)}   (random-{declaredK} draw; rises with k — each arm's own floor is");
        Console.WriteLine("                             derived at min(k, what it presented))");
        Console.WriteLine($"        precision  {Format(randomPrecision)}   ({relevantCarriers} relevant carriers / {poolSize}; the same at every k)");
        Console.WriteLine($"        manifest   {Format(randomManifest)}   (regression channel only)");
        Console.WriteLine("        Derived from this corpus. The design's 0.237 was computed against a 40-SKU pool");
        Console.WriteLine("        that does not exist here.");
        Console.ResetColor();
    }

    private static void PrintPreRegistration()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  PRE-REGISTERED, AND WHAT SURVIVED CONTACT WITH THE CORPUS:");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        // ⚠ B-2. This line used to end here, with no evaluator anywhere in the repository behind
        // it: no WinsRequired constant, no threshold comparison, no verdict — a quotable rule in
        // the shape of a met one, printed above a sign-test panel for a different comparison. The
        // constant is now named FROM the evaluator, and the verdict is rendered further down in
        // all three states, including NOT EVALUATED. Nothing on this banner is a rule the run does
        // not evaluate.
        Console.WriteLine($"    · Decision rule: {PreRegisteredRule.Statement}.");
        Console.WriteLine($"      WinsRequired = {PreRegisteredRule.WinsRequired} of {PreRegisteredRule.PreRegisteredPairs}. "
                        + "EVALUATED below, on the PRE-REGISTERED DECISION RULE panel, for the");
        Console.WriteLine($"      '{EvalPrinter.ShortArm(CoverageArms.DiscoveryWorkflow)}' vs "
                        + $"'{EvalPrinter.ShortArm(ArmLive)}' pair specifically — MET, NOT MET or");
        Console.WriteLine("      NOT EVALUATED with the reason attached. It is never simply absent.");
        Console.WriteLine($"    · DECLARED BUDGET: k = {CoverageArms.DeclaredK}, from the canonical utterance (\"…your "
                        + $"{GalaxusDemoPrompts.CoverageCohortDeclaredKInWords} best…\"). Every arm is cut to");
        Console.WriteLine("      its top k in its own order before pairing; pairs at unequal k are NOT COMPARABLE.");
        foreach (CoverageArm absent in CoverageArms.Absent)
            Console.WriteLine($"    · Arm '{absent.Label}' is DECLARED ABSENT. {absent.AbsenceReason}");
        Console.WriteLine($"    · Arms that DID run: {string.Join(", ", CoverageArms.Runnable.Select(a => a.Label))}.");

        // ⚠ This block used to end "the corpus supports at most 3 scorable personas, not 12 …
        // the pre-registered rule CANNOT be evaluated here", with the 12 typed in. The corpus
        // now authors twelve, so both halves of that sentence had to become derived: a report
        // that says a rule cannot be evaluated while the run is evaluating it is exactly the
        // kind of stale hand-typed claim Eval 03's ConstantPolicyCeiling row exists to catch.
        const int preRegisteredPersonas = 12;
        int analysed = CoveragePersonas.AnalysedCount;

        Console.WriteLine($"    · The corpus supports {analysed} scorable persona(s); the rule was pre-registered at "
                        + $"{preRegisteredPersonas}.");
        Console.WriteLine($"      At n = {analysed} the smallest two-sided p ANY split could reach is "
                        + $"{CoveragePersonas.MinimumAttainableTwoSidedP:F4} — and that");
        Console.WriteLine("      is a CEILING on the power, not the number the run will report: the sign test");
        Console.WriteLine("      discards tied pairs AND refuses unequal-k pairs, so its real n is smaller and its");
        Console.WriteLine("      real minimum p is larger. The panel below prints the attained n per comparison.");
        Console.WriteLine(analysed >= preRegisteredPersonas
            ? "      The pre-registered rule is REACHABLE at this n. Reachable is not reached: a comparison"
            + "\n      whose pairs tie or are refused still reports the n it attained, and that n can be zero."
            : "      The pre-registered rule CANNOT be evaluated at this n.");
        Console.WriteLine("    · Excluded personas and the reason for each:");
        foreach (var excluded in CoveragePersonas.Excluded)
            Console.WriteLine($"        - {excluded.Id} {excluded.Name}: {excluded.Note}");
        Console.WriteLine("    · Reps average into ONE observation per persona before pairing. Treating reps as");
        Console.WriteLine("      independent observations is pseudo-replication and inflates significance by √reps.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 02 — Latent-Interest Coverage (paired, deterministic metric)           ║
║   Live agent vs single-shot control vs popularity vs tag-join baseline        ║
║   Cut to ONE declared k · recall@k AND precision@k · equal-k pairs only       ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
