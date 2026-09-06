// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// SNAPSHOT-POLICY: writes            eval09_hypothesis_ab — the A/B record, with its label a required parameter

using System.Globalization;
using Azure.AI.OpenAI;
using AgentEval.MAF;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Evals.Adapters;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

// ── Two type names exist in BOTH lanes, and both duplications are deliberate ──────────────
//
//   · DiscoveryLoopOptions — Workflows' configures the shipped MAF workflow; Evals.Loop's
//     configures the deterministic loop SUBSTRATE the controls are built on. This file is one of
//     only two places both namespaces are in scope, so it aliases rather than renaming either.
//     (RealDiscoveryLoopArm.cs is the other, and carries the same alias for the same reason.)
//   · CoverageReview / QueryVocabulary — likewise duplicated. Neither is named here.
using WorkflowLoopOptions = Galaxus.RecommendationAgent.Workflows.DiscoveryLoopOptions;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 09 — the headline A/B this suite has never actually run: <b>the single agent against the
/// discovery workflow, both LIVE, on the same personas and the same request</b>, decided by a rule
/// written down before the run.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is new here, and it is two things.</b> First, both arms are model-backed. Eval 02 runs
/// Demo 2 on its DETERMINISTIC path and says so at length, precisely because pairing a live agent
/// against a code-only loop varies architecture and model presence in one comparison — the
/// co-moving-operands hazard. This eval removes that confound by running the workflow live
/// (<c>DiscoveryLoopOptions.Offline = false</c>), which is why it needs credentials and why it is
/// the only eval in this suite that does. Second, the <b>LLM-judge path is reached</b>: every other
/// eval in this project constructs <c>new MAFEvaluationHarness(verbose: false)</c> — the
/// NO-EVALUATOR overload — so <see cref="AgentEval.Core.IEvaluator"/> is unreachable by
/// construction. This one constructs <c>new MAFEvaluationHarness(evaluatorClient, verbose: false)</c>
/// and supplies <see cref="TestCase.EvaluationCriteria"/>, so the judge runs, per-criterion verdicts
/// come back, and the run reports per-criterion DELTAS between the two architectures.
/// </para>
///
/// <para><b>═══ THE ANTI-PATTERN THIS EVAL EXISTS NOT TO REPRODUCE ═══</b></para>
/// <para>
/// <c>AgentEval.TravelDemo.Evals</c>'s Eval 03 prints <c>HYPOTHESIS CONFIRMED</c> when
/// <c>workflow.LlmScore &gt; agent.LlmScore || workflow.CriteriaMetCount &gt; agent.CriteriaMetCount</c>.
/// That is an OR of two one-sided comparisons at n = 1 with no significance test, and it is wrong in
/// four separate ways at once:
/// </para>
/// <list type="number">
///   <item><description><b>n = 1.</b> One run of each arm. A single stochastic draw per arm cannot
///   separate an architecture from a coin, and there is no interval, no repetition and no
///   test.</description></item>
///   <item><description><b>An OR over two endpoints doubles the false-positive rate.</b> Two
///   independent chances to declare victory, either of which suffices, with no correction. Under a
///   true null a coin passes that disjunction about 75% of the time, not 5%.</description></item>
///   <item><description><b>Strictly-greater on a continuous-looking integer.</b> A one-point
///   difference in a judge's holistic score — a number the judge itself is not calibrated to a
///   point — counts as a win.</description></item>
///   <item><description><b>No budget control.</b> The workflow makes four to seven model calls and
///   the agent makes one turn's worth. Spending more and scoring higher is not evidence that the
///   architecture is better; it is evidence that more inference was bought.</description></item>
/// </list>
///
/// <para><b>═══ WHAT THIS EVAL DOES INSTEAD ═══</b></para>
/// <list type="bullet">
///   <item><description><b>Paired design.</b> Both arms see the SAME twelve scored personas
///   (<see cref="CoveragePersonas"/>) and the SAME utterance
///   (<see cref="GalaxusEvalPrompt.CoverageCanonical"/>), so the unit of analysis is the persona and
///   the only thing that varies is architecture. Reps average into ONE observation per cell before
///   pairing — treating reps as independent observations is pseudo-replication and inflates
///   significance by √reps.</description></item>
///   <item><description><b>Equal token budget, MEASURED.</b> One instrument
///   (<see cref="MeteredChatClient"/>) sits under BOTH arms at the raw <c>IChatClient</c> layer, so
///   it sees every model round-trip either architecture makes, tool loops included. If the two arms'
///   spend per turn differs by more than
///   <see cref="Eval09PreRegistration.MaximumTokenRatio"/>× the comparison is declared CONFOUNDED and
///   NO winner may be named — whichever arm was ahead.</description></item>
///   <item><description><b>A decision rule pre-registered IN CODE</b>
///   (<see cref="Eval09PreRegistration"/>), printed above the run, with its attainable p — and the
///   attainable p is recomputed AFTER the run from the non-tied pair count the run actually
///   attained, because the exact sign test discards ties and the theoretical best case is not the
///   number this comparison could reach.</description></item>
///   <item><description><b>Per-criterion judge deltas beside the deterministic delta.</b> The
///   deterministic latent-coverage delta is the PRIMARY endpoint and the only one in the rule. The
///   six judged criteria are six further tests, reported with their Bonferroni threshold and
///   explicitly excluded from the decision.</description></item>
///   <item><description><b>A third arm that can void the claim.</b>
///   <see cref="Broken05_RubberStampReviewer"/> — a loop whose reviewer approves on round 1, every
///   time. If the live workflow cannot beat a reviewer that never says no, the second round is
///   buying nothing and "the workflow wins" is void.</description></item>
///   <item><description><b>A fourth arm that is a FLOOR, never an entrant.</b>
///   <see cref="ContentlessFloorArm"/> presents nothing and says the right-sounding things. It
///   MEASURES what a degenerate agent scores on every judged criterion, so no criterion number is
///   printed without its floor beside it.</description></item>
/// </list>
///
/// <para><b>═══ CHANCE FLOORS ═══</b> (a number without its floor is a decoration)</para>
/// <list type="bullet">
///   <item><description><b>Latent coverage</b> — per persona, per arm, at that arm's OWN
///   presentation count k, derived from this corpus by <see cref="ChanceFloors.RandomDrawFloor"/>.
///   Printed in the coverage table as the ▲/▼ marker.</description></item>
///   <item><description><b>The sign test</b> — under H0 the challenger leads with probability 0.5
///   per non-tied pair, so "the workflow led" is a coin flip and only the p-value is a
///   result.</description></item>
///   <item><description><b>Every judged criterion</b> — NOT quoted, MEASURED, by
///   <see cref="ContentlessFloorArm"/>: a fluent, contentless answer that recommends nothing and
///   volunteers the reassurances several criteria ask for. Whatever share of criteria it is scored
///   as meeting IS the floor for those criteria on this judge, this model and this corpus.
///   ⚠ <b>CORRECTED 2026-09-06 — this sentence used to name the wrong criteria and the wrong count.</b>
///   It read <i>"the two criteria that quantify over recommendations ('every recommendation names a
///   past purchase', 'the covering note says what was NOT recommended')"</i>. The second of those is
///   an EXISTENTIAL over the covering note and is not vacuous at all; the criteria that actually
///   quantify over presented recommendations are numbers 1 and 6 (and number 4 before its
///   2026-09-06 restatement), which is <b>three, not two, and not the pair named</b>. Each criterion
///   now DECLARES its own vacuity in
///   <see cref="JudgedCriterion.VacuousOnAnAnswerWithNoRecommendations"/> rather than having it
///   inferred from the floor's met rate. Direction of the old error: it understated how much of the
///   rubric an empty answer passes, which is the flattering direction for the
///   instrument.</description></item>
/// </list>
///
/// <para>
/// ⚠ <b>Silence is never a pass.</b> A cell whose arm presented nothing is scored 0.000 on the
/// primary endpoint (0 of N gold tokens served — a real, earned zero on a persona that had a right
/// answer) and is EXCLUDED from the judged panel, because a criterion quantified over an empty set
/// of recommendations is vacuous rather than met. Both are counted and printed.
/// </para>
/// <para>
/// ⏱️ Runtime and spend: two live arms × <see cref="Reps"/> reps × twelve personas, plus one judge
/// call per arm per persona-rep across all four arms. Expect roughly 20-45 minutes and a spend the
/// cost panel reports exactly. <c>--quick</c> drops to one rep and keeps all twelve personas — n is
/// worth far more to this design than reps are.
/// </para>
/// </remarks>
public static class Eval09_HypothesisComparison
{
    /// <summary>Repetitions per persona for each LIVE arm. Reps average into one observation per cell.</summary>
    /// <remarks>
    /// Two, not three. Eval 02 repeats one live arm three times; this eval has TWO live arms and a
    /// live workflow turn is four to seven model calls, so three reps would roughly triple the bill
    /// for a variance reduction of √(3/2). Stated because it is a CHOICE and it costs power: with two
    /// draws per cell a persona that is genuinely a tie can still land as a win or a loss, and that
    /// noise is why the decision rule reads a p-value rather than a lead.
    /// </remarks>
    public const int Reps = 2;

    /// <summary>Repetitions per persona under <c>--quick</c>.</summary>
    /// <remarks>
    /// <c>--quick</c> reduces REPS and never personas. Dropping personas would shrink n, and n is
    /// what the exact sign test spends: at n = 12 a clean sweep reaches p = 0.0005, at n = 6 it
    /// reaches only 0.031, and at n = 4 it cannot reach 0.05 at all.
    /// </remarks>
    public const int QuickReps = 1;

    // ── Arm labels ───────────────────────────────────────────────────────────────────────
    //
    // ⚠ Every label is written so that EvalPrinter.ShortArm — which takes the text after the LAST
    //   em dash and then trims from any '(' — yields a DISTINCT and meaningful column header. The
    //   obvious labels ("Single Agent (Robin) — LIVE", "Discovery Workflow (Demo 2) — LIVE") both
    //   abbreviate to the single word "LIVE", which would have printed the coverage table with two
    //   identically-headed columns and no way to tell which architecture produced which number.
    //   The distinguishing word therefore goes AFTER the dash, not before it.

    /// <summary>Arm label — the shipped single agent, live. Abbreviates to "Robin".</summary>
    public const string ArmSingleAgent = "LIVE single agent — Robin (Demo 1)";

    /// <summary>Arm label — Demo 2's MAF workflow, live. Abbreviates to "discovery loop".</summary>
    /// <remarks>
    /// Deliberately NOT <see cref="DiscoveryLoopAdapter.ArmLabel"/>, which ends "deterministic arm"
    /// and belongs to Eval 02's zero-model-call row. Two rows produced by different code on
    /// different paths must never share a label — a reader who saw one number under the other's name
    /// would have no way to know.
    /// </remarks>
    public const string ArmWorkflow = "LIVE workflow — discovery loop (Demo 2)";

    /// <summary>Arm label — the rubber-stamp loop control. Abbreviates to "rubber stamp".</summary>
    public const string ArmRubberStamp = "Loop control — rubber stamp";

    /// <summary>Arm label — the measured degenerate floor. Abbreviates to "contentless answer".</summary>
    public const string ArmJudgeFloor = "FLOOR — contentless answer";

    /// <summary>Snapshot key. Deliberately distinct from Eval 02's, which Eval 03 reads.</summary>
    public const string SnapshotKey = "eval09_hypothesis_ab";

    /// <summary>The key a one-persona probe writes to. NEVER the full-cohort key.</summary>
    /// <remarks>
    /// Same rule Evals 02, 02b and 02c already apply: a stage-two probe is n = 1 and must not be
    /// readable later as the cohort record. Eval 09 had no probe form at all until 2026-09-06 —
    /// which made it the ONE eval in this suite whose stage 2 was the whole cohort, at the suite's
    /// highest price. That is a hole in the run protocol's coverage, not a property of this eval.
    /// </remarks>
    public const string ProbeSnapshotKey = SnapshotKey + "_probe";

    /// <summary>
    /// Runs the eval.
    /// </summary>
    /// <param name="quick">One repetition per live arm instead of <see cref="Reps"/>. Never drops personas.</param>
    /// <param name="dryRun">
    /// Replace BOTH live arms' chat clients with a deliberately implausible stub, and the judge with
    /// a scripted verdict source. Spends nothing, exercises the persona loop, both architectures, the
    /// judge plumbing, the token meter, the sign test, every panel and the gate, and writes no
    /// snapshot. It CAN FAIL — see <see cref="DryRunPlumbingHeld"/>.
    /// </param>
    /// <param name="onlyPersona">
    /// Restrict the run to ONE persona id — the one-item real run that is stage two of the
    /// three-stage protocol. The snapshot then goes to <see cref="ProbeSnapshotKey"/> and never to
    /// the full-cohort key. ⚠ At n = 1 nothing on the page is a cohort number: no sign test can
    /// reach a result, each judged met rate is one cell, and the forced-choice floor is still
    /// derived from the WHOLE analysis set (a probe that narrowed the rival set would flatter
    /// itself). Not honoured under <c>--ci</c> — the chain never passes it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 0 when every gate passed (or, on a dry run, when the plumbing held), 1 when a gate failed,
    /// 2 when <paramref name="onlyPersona"/> matches no scored persona,
    /// 3 when credentials are missing and nothing was measured. ⚠ The <c>ci</c> parameter is GONE —
    /// see <see cref="CredentialGuard"/>.
    /// </returns>
    public static async Task<int> RunAsync(
        bool quick = false, bool dryRun = false, string? onlyPersona = null, CancellationToken ct = default)
    {
        PrintHeader();

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

        // ⚠ THE HONESTY GUARD, and it comes before anything that could print a number.
        //
        // Two of this eval's four arms are deterministic and would happily run with no credentials
        // at all. Running them and printing a coverage table would put numbers on the page under a
        // heading that says "single agent vs workflow" while neither the agent nor the workflow had
        // been anywhere near a model. That is the exact substitution this whole suite exists to
        // prevent, so the eval refuses to measure anything rather than measure the wrong thing.
        // Routed through CredentialGuard so the rule is enforced in ONE file for all six.
        if (CredentialGuard.Blocks(
                "Eval 09", "The single-agent versus workflow comparison", dryRun,
                "BOTH entrants need a model. The rubber-stamp control and the contentless floor arm",
                "would run without one — and printing THEIR numbers under a panel headed \"single",
                "agent vs workflow\" would report two deterministic controls as if they were the two",
                "architectures. There is no partial score to show.")
            is { } noCredentials)
        {
            return noCredentials;
        }

        int reps = dryRun ? 1 : quick ? QuickReps : Reps;
        Eval09PreRegistration.Print(personas.Count, reps, dryRun);

        if (onlyPersona is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  🔬 ONE-PERSONA PROBE — {personas[0].Id} only. Stage two of the three-stage protocol.");
            Console.WriteLine("     n = 1: NO number below is a cohort result. The sign test cannot reach one (the");
            Console.WriteLine($"     smallest attainable two-sided p at 1 pair is {Eval09PreRegistration.TheoreticalMinimumTwoSidedP(1):F4}), each judged met rate is");
            Console.WriteLine($"     ONE cell, and the snapshot goes to '{ProbeSnapshotKey}' — the full-cohort");
            Console.WriteLine($"     record at '{SnapshotKey}' is untouched.");
            Console.ResetColor();
            Console.WriteLine();
        }

        if (dryRun)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  🧪 DRY RUN — both live arms run against stub models and the judge is scripted.");
            Console.WriteLine("     Nothing is spent and no snapshot is written. NO number below is a result: the");
            Console.WriteLine("     agent stub presents the same two products for every persona (and ASKS first on");
            Console.WriteLine($"     {Personas.JonasUserId}, to exercise the harness's second turn); the workflow stub");
            Console.WriteLine("     answers every model stage with a parseable envelope built from the stage's own");
            Console.WriteLine($"     context, EXCEPT on {Eval09DryRun.CancelledPersonaId}, where the InterestMapper call is");
            Console.WriteLine("     CANCELLED on both attempts — a stand-in for the 60 s ceiling. That one cell must");
            Console.WriteLine("     come out VOIDED, its cancelled attempts must reach the ledger, and the equal-budget");
            Console.WriteLine("     guard must call the run CONFOUNDED for that reason. Usage on returned stub calls is");
            Console.WriteLine("     SYNTHETIC (characters / 4) so the only hole in the usage data is the injected one.");

            // ⚠ Both injections land on ONE named persona each. Under --only they may not be in the
            //   run, and the sentences above would then describe events this run never produces.
            if (onlyPersona is not null)
            {
                bool cancelIn = personas.Any(x => string.Equals(x.Id, Eval09DryRun.CancelledPersonaId, StringComparison.Ordinal));
                bool silenceIn = personas.Any(x => string.Equals(x.Id, Personas.JonasUserId, StringComparison.Ordinal));
                Console.WriteLine($"     ⏭ ON THIS PROBE the cancellation is {(cancelIn ? "ISSUED" : "NOT issued")} and the instructed");
                Console.WriteLine($"       silence is {(silenceIn ? "ISSUED" : "NOT issued")} — each injection lands on one persona only. Every");
                Console.WriteLine("       check below whose subject is absent prints NOT APPLICABLE, never a tick.");
            }
            Console.ResetColor();
            Console.WriteLine();
        }
        else
        {
            Config.PrintAzureTarget();
            Console.WriteLine();
        }

        IProductRetriever retriever = await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);

        // ── One instrument, three ledgers. ───────────────────────────────────────────────
        //
        // The two ARM ledgers are what the equal-budget test reads. The JUDGE ledger is reported
        // separately and is deliberately NOT part of that test: the judge is applied identically to
        // every arm, so its spend cannot favour one architecture — but its input contains the arm's
        // own output, so a verbose arm costs more judge tokens, and folding that into the arm's
        // budget would charge an arm for being graded.
        var agentTokens = new Eval09TokenLedger(ArmSingleAgent);
        var workflowTokens = new Eval09TokenLedger(ArmWorkflow);
        var judgeTokens = new Eval09TokenLedger("Judge (all arms)");

        // Three separate underlying clients, one per ledger, so no accounting can leak between
        // arms. On a dry run the WORKFLOW's stub is prose-only rather than the presenting stub: the
        // loop's model stages register no tools, and handing them a stub that emits a
        // FunctionCallContent for a tool that does not exist probes MEAI's function-invocation
        // middleware instead of probing this eval. Prose that will not parse is the honest stub for
        // a stage whose contract is "emit a JSON envelope" — it exercises the call, the meter and
        // the documented degradation path, and nothing else.
        // The agent stub asks-then-presents on Jonas so the second turn is exercised here too; the
        // synthetic-usage decorator stamps characters/4 onto every RETURNED stub call, so the
        // equal-budget guard's only hole is the cancellation injected below.
        IChatClient agentClient = new MeteredChatClient(
            dryRun
                ? new Eval09SyntheticUsageClient(StubChatClient.AskThenPresentAgent(Personas.JonasUserId))
                : BuildAzureChatClient(),
            agentTokens);

        // ⚠ The workflow's dry-run stub is the ENVELOPE stub, not the prose stub. The prose stub
        //   made every model stage fail to parse and fall back — which was fine while a degraded
        //   cell was "still in the mean with a note", and is fatal now that a degraded cell is
        //   VOIDED: every workflow cell would be void and the arm would have no mean, no judged
        //   cells and no pairing. The envelope stub answers each stage with a parseable envelope
        //   built from the stage's own context (a mapper map from the purchase tags, a reviewer
        //   verdict with gaps in the catalogue's vocabulary, a ranker selection from the candidate
        //   list, presenter prose) so the LIVE code path completes, and it CANCELS the
        //   InterestMapper call — both attempts — on exactly one persona, so the VOID rule and the
        //   cancelled-call accounting are each proved on one cell while eleven others stay whole.
        //   Its prose still differs from the agent stub's, so the scripted judge, which decides by
        //   hashing the answer, produces non-tied pairs and the delta arithmetic's win and loss
        //   branches run — PROVIDED its cells exist on the judged panel. Different prose is not
        //   enough on its own: a cell that presents nothing is excluded as vacuous and enters no
        //   pair, which is why the stub's ranker cites a grounding key that resolves (see
        //   Eval09WorkflowStubClient.GroundingKey). The 2026-09-04 dry run had the prose right and
        //   the key wrong, and the plumbing check counted zero pairs.
        IChatClient workflowClient = new MeteredChatClient(
            dryRun
                ? new Eval09SyntheticUsageClient(new Eval09WorkflowStubClient(cancelForPersonaId: Eval09DryRun.CancelledPersonaId))
                : BuildAzureChatClient(),
            workflowTokens);

        IChatClient judgeClient = new MeteredChatClient(
            dryRun ? new Eval09ScriptedJudgeClient(Eval09PreRegistration.JudgedCriteria) : BuildAzureChatClient(),
            judgeTokens);

        // ⭐ THE OVERLOAD THE OTHER FOUR EVALS NEVER USE. Supplying an evaluator here is what makes
        //    TestCase.EvaluationCriteria live and TestResult.CriteriaResults non-null.
        var harness = new MAFEvaluationHarness(judgeClient, verbose: false);

        var options = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            EvaluateResponse = true,               // ⭐ the judge branch
            Verbose = false,
            ModelName = dryRun ? "(stub — dry run)" : Config.Model,
        };

        var liveAgent = RecommendationAgentFactory.Create(agentClient);

        // TWO reports over the same turns, exactly as Eval 02 keeps them. `report` scores each arm
        // at whatever it presented — the floors, the forced choice, the cost, the telemetry, the
        // snapshot. `atK` scores the same turns cut to the DECLARED budget, and it is the ONLY one
        // any pairing is allowed to read. One turn, two readings; neither is derived from the other
        // after the fact.
        int declaredK = CoverageArms.DeclaredK;
        var report = new PairedCoverageReport();
        var atK = new PairedCoverageReport();
        var judged = new Eval09JudgedReport(Eval09PreRegistration.JudgedCriteria);
        var floors = new Dictionary<string, double>(StringComparer.Ordinal);
        var notes = new List<string>();
        var roundsByArm = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        // ⚠ Only the two LIVE arms count toward clause 4. The FLOOR arm presents nothing BY
        //   CONSTRUCTION — that is what makes it a floor — and counting its emptiness as silence
        //   would void every run this eval will ever make, in the flattering-looking direction of
        //   "we could not read the comparison" rather than the honest one of reporting it.
        var silentLiveCells = new List<string>();
        var silentControlCells = new List<string>();
        int armsThatThrew = 0;
        int workflowRunsThatLooped = 0;
        int workflowRunsObserved = 0;
        int degradedStageCount = 0;
        var degradedStages = new Dictionary<string, int>(StringComparer.Ordinal);

        // ⚠ A live-workflow cell with a degraded stage is VOIDED: it leaves the mean, the judged
        //   panel and the pairing, and clause 5 names no winner while one exists. The first version
        //   kept those cells "still in the mean" with a note — a "live" arm that had quietly become
        //   part-deterministic was being averaged as if it were the architecture under test.
        var voidedCells = new List<string>();

        // Every single-agent cell whose first turn presented nothing, with what the harness's
        // second turn did. Reported cell by cell — a turn-1 silence is a harness fact.
        var secondTurns = new List<(string Cell, ClarifyingTurnOutcome Outcome)>();

        // ⚠ Derived over the WHOLE analysis set even under --only, exactly as Eval 02 does it: the
        //   cross-persona forced choice grades one answer against every customer's gold, so a probe
        //   that narrowed the rival set would flatter itself, and the 1/N chance rate below would be
        //   1/1. The persona LOOP narrows; the rival set never does.
        var goldByPersona = CoveragePersonas.All.ToDictionary(
            p => p.Id, p => InterestMapGold.Derive(p.Id), StringComparer.Ordinal);

        int scorablePersonas = goldByPersona.Count(kv => !kv.Value.LatentIsEmpty);
        double forcedChoiceFloor = InterestCoverageGrader.ForcedChoiceFloor(scorablePersonas);

        foreach (CoveragePersona persona in personas)
        {
            GoldInterestMap gold = goldByPersona[persona.Id];

            PrintPersonaHeader(persona, gold);

            if (gold.LatentIsEmpty)
            {
                notes.Add($"{persona.Id} produced an EMPTY latent-gold set and was skipped. An empty denominator "
                        + "is excluded from the mean, never scored as 0 or 1.");
                continue;
            }

            floors[persona.Id] = ChanceFloors.RandomDrawFloor(gold, ChanceFloors.DegenerateDrawSize).ExpectedLatent;

            foreach ((string label, bool live) in Arms())
            {
                int armReps = live ? reps : 1;
                var armScores = new List<CoverageScore>(armReps);
                var armCutScores = new List<CoverageScore>(armReps);

                for (int rep = 1; rep <= armReps; rep++)
                {
                    IEvaluableAgent arm = label switch
                    {
                        // A FRESH adapter per rep, so one rep's session cannot leak into the next.
                        // Wrapped in the second-turn adapter: every persona here has gold, so an
                        // answer is required, and a first turn that stops to ask is answered from
                        // the persona's profile before silence is scored. Both turns are on the
                        // agent's ledger — a turn that asks costs what it costs.
                        ArmSingleAgent => new ClarifyingTurnAdapter(new ApprovalAwareAgentAdapter(liveAgent)),
                        ArmWorkflow => new LiveDiscoveryWorkflowArm(retriever, workflowClient),
                        ArmRubberStamp => new Broken05_RubberStampReviewer(retriever),
                        ArmJudgeFloor => new ContentlessFloorArm(),
                        _ => throw new InvalidOperationException($"Unregistered arm '{label}'."),
                    };

                    string repLabel = live ? $"rep {rep}/{armReps}" : "deterministic";

                    // The GRADED TURN is counted here, on the arm's own ledger, and it is the
                    // denominator of the per-turn spend figure. Counted whether or not the turn
                    // threw: a turn that burned tokens and then failed still cost what it cost, and
                    // excluding it would report the surviving turns as cheaper than they were.
                    Eval09TokenLedger? ledger = label switch
                    {
                        ArmSingleAgent => agentTokens,
                        ArmWorkflow => workflowTokens,
                        _ => null,
                    };
                    ledger?.RecordTurn();

                    Eval09ArmCell cell = await ScoreArmAsync(
                        persona, goldByPersona, arm, harness, options, report, label, repLabel, declaredK, ct)
                        .ConfigureAwait(false);

                    string cellName = $"{persona.Id} · {EvalPrinter.ShortArm(label)} · {repLabel}";
                    bool voided = false;

                    if (arm is LiveDiscoveryWorkflowArm { LastRun: { } run })
                    {
                        workflowRunsObserved++;
                        if (run.Looped) workflowRunsThatLooped++;
                        AddRounds(roundsByArm, label, run.State.DiscoveryRound);

                        // Aggregated for the note, listed for the void. Twelve personas × two reps ×
                        // four model stages is up to ninety-six notices; the COUNT per stage is what
                        // a reader needs — and the CELL is what the rule needs.
                        foreach (string degraded in run.State.DegradedNotes)
                        {
                            degradedStageCount++;
                            string stage = degraded.Split(':', 2)[0].Trim();
                            degradedStages[stage] = degradedStages.GetValueOrDefault(stage) + 1;
                        }

                        if (run.State.DegradedNotes.Count > 0)
                        {
                            voided = true;
                            voidedCells.Add($"{cellName} ({string.Join("; ", run.State.DegradedNotes)}; "
                                          + $"{run.State.ModelCalls} model call(s) attempted)");
                        }
                    }
                    else if (arm is IDiscoveryLoopArm { LastRun: { } telemetry })
                    {
                        AddRounds(roundsByArm, label, telemetry.RoundsTaken);
                    }

                    if (arm is ClarifyingTurnAdapter { LastOutcome: { } turn }
                        && (turn.SecondTurnRan || turn.PresentedAfterFirstTurn == 0))
                    {
                        secondTurns.Add((cellName, turn));
                        Console.ForegroundColor = turn.SilentAfterSecondTurn || turn.SecondTurnThrew ? ConsoleColor.Yellow : ConsoleColor.DarkCyan;
                        Console.WriteLine($"      ↩ second turn · {turn.Describe()}");
                        Console.ResetColor();
                    }

                    if (cell.Score is not { } score)
                    {
                        armsThatThrew++;
                        notes.Add($"{persona.Id} · {label} · {repLabel} THREW and was EXCLUDED from the mean. An "
                                + "errored turn presents nothing, and 0/n is not a measurement of an architecture — "
                                + "it is the absence of one.");
                        continue;
                    }

                    if (voided)
                    {
                        // Out of the mean, out of the judged panel, never counted as silent: a cell
                        // the model did not fully produce is not an observation of the model. The
                        // judge call it cost was already spent and is on the judge ledger; its
                        // verdict is deliberately not recorded.
                        judged.NoteVacuousExclusion();
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"      {label,-34} {repLabel,-16} ⛔ VOID — a model stage fell back to code "
                                        + $"({string.Join("; ", ((LiveDiscoveryWorkflowArm)arm).LastRun!.State.DegradedNotes)}). "
                                        + $"Would have read latent {Format(score.Latent)} at k={score.PresentedCount}; NOT in the mean.");
                        Console.ResetColor();
                        continue;
                    }

                    // ⚠ SILENCE IS NOT A PASS. Every criterion in the rubric either quantifies over
                    //   the recommendations or describes the covering note around them, so a judge
                    //   asked to grade an answer with no recommendations in it will return "met" for
                    //   reasons that have nothing to do with the agent. Such a cell is excluded from
                    //   the judged panel and counted, never scored. The FLOOR arm is the single
                    //   exception, and measuring exactly which criteria come back vacuously met is
                    //   the entire reason it exists.
                    bool judgeable = cell.Presented.Count > 0 || string.Equals(label, ArmJudgeFloor, StringComparison.Ordinal);
                    bool judgedCell;
                    if (judgeable)
                    {
                        judgedCell = judged.Record(persona.Id, label, cell.Result.CriteriaResults, cell.Result.Score);
                    }
                    else
                    {
                        judged.NoteVacuousExclusion();
                        judgedCell = false;
                    }

                    PrintCellLine(label, repLabel, score, judgedCell, cell.Result.Score, judged.LastMetSummary,
                        excludedAsVacuous: !judgeable);

                    if (score.PresentedCount == 0)
                    {
                        if (live) silentLiveCells.Add(cellName);
                        else if (!string.Equals(label, ArmJudgeFloor, StringComparison.Ordinal))
                            silentControlCells.Add(cellName);
                    }

                    if (!judgedCell && score.PresentedCount > 0)
                    {
                        notes.Add($"{persona.Id} · {label} · {repLabel}: the JUDGE returned no usable per-criterion "
                                + "verdict. The cell is UNDECIDABLE on the judged panel and was excluded there. It "
                                + "still carries its deterministic coverage score, which needs no judge.");
                    }

                    armScores.Add(score);
                    if (cell.Cut is { } cutScore) armCutScores.Add(cutScore);
                }

                if (armScores.Count > 0)
                {
                    report.Record(persona.Id, label, CoverageScore.Mean(armScores));

                    // Every rep of an arm is cut to the SAME declared budget, so Mean's equal-k
                    // guard is satisfied by construction here and KUniformAcrossReps is COMPUTED
                    // from the cuts rather than asserted.
                    if (armCutScores.Count > 0)
                    {
                        atK.Record(persona.Id, label, CoverageScore.Mean(armCutScores));
                    }
                }
                else
                {
                    notes.Add($"{persona.Id} · {label}: EVERY run threw or was VOIDED, so this persona contributes NO "
                            + "observation for this arm. It is missing from the pairing rather than scored zero.");
                }
            }
        }

        // ══ PANELS ════════════════════════════════════════════════════════════════════════
        EvalPrinter.PrintPairedCoverage(report, floors,
            $"Eval 09 — LIVE agent vs LIVE workflow (paired, n = {report.Personas.Count}, {reps} rep(s) per live arm)",
            forcedChoiceFloor);

        EvalPrinter.PrintForcedChoice(report, forcedChoiceFloor, scorablePersonas);

        // ⚠ EVERY PAIRING GOES THROUGH THE EQUAL-k RULE, ON THE CUT CELLS.
        //
        // Until 2026-09-06 these five lines read `report.SignTest(...)` — the k-BLIND method whose
        // own docstring named this eval as the sole reason it was still kept. MEASURED on the
        // 2026-09-05 live run: Robin presented exactly k = 5 on all 24 reps, the workflow presented
        // 3–11 and NEVER 5, on 0 of 21 scored reps (mean k 6.875). Latent coverage is recall and
        // monotone in k, so 16 of those 21 pairs scored the workflow on a strictly larger slate.
        //
        // ⚠ AND THIS IS NOT "NOW IT IS FAIR". Switching methods cannot rescue that run's verdict
        // and must not be reported as though it might. Cutting the workflow to k = 5 can only
        // REMOVE served gold tokens (TopK is a prefix, Grade unions a set over it), while Robin's
        // 0.750 does not move because it was already at k = 5 on every rep. So the workflow's
        // own-k standing is its BEST case at equal k and it cannot reach p < 0.05 in any outcome.
        // What this changes is what the eval is allowed to SAY: pairs that are not at equal k are
        // now listed NOT COMPARABLE instead of being counted as wins, losses and ties.
        SignTestOutcome primary = atK.SignTestAtEqualK(ArmSingleAgent, ArmWorkflow, CoverageMetric.Recall);
        SignTestOutcome versusRubberStamp = atK.SignTestAtEqualK(ArmRubberStamp, ArmWorkflow, CoverageMetric.Recall);
        SignTestOutcome agentVersusRubberStamp = atK.SignTestAtEqualK(ArmRubberStamp, ArmSingleAgent, CoverageMetric.Recall);

        // The k-INVARIANT channel, which this eval never computed at all: GradeWithControls leaves
        // PrecisionAtK undefined, so the only endpoint on the page was the one that moves with k.
        // Reported, never gated — the pre-registered rule names recall and is not being rewritten
        // after the fact.
        SignTestOutcome primaryPrecision = atK.SignTestAtEqualK(ArmSingleAgent, ArmWorkflow, CoverageMetric.PrecisionAtK);

        EvalPrinter.PrintSignTest(
        [
            primary,
            versusRubberStamp,
            agentVersusRubberStamp,
            primaryPrecision,
        ]);

        // ── The contentless floor: a FLOOR CHECK, and it is no longer dressed as a sign test. ──
        //
        // The two rows that used to sit under the panel above paired each live arm against the
        // contentless arm and reported W/L/T 12/0/0, p = 0.0005. Under the equal-k rule those pairs
        // do not exist: the floor arm presents NOTHING by construction, and a silent side is never
        // at equal k with an answer. Deleting the rows would delete a real statement, so the
        // statement is made in the form it was always in — a count against a floor, with the floor
        // printed beside it — rather than as a p-value it was never entitled to.
        PrintFloorCheck(report, atK, floors, notes);

        var budget = Eval09Budget.Measure(agentTokens, workflowTokens);
        PrintBudgetPanel(budget, agentTokens, workflowTokens, judgeTokens, dryRun);

        PrintJudgePanel(judged, dryRun);

        // ⚠ READ THE PANEL ABOVE, NOT THE ONE BELOW, FOR SPEND.
        //
        // EvalPrinter.PrintCostComparison reports the HARNESS's per-arm view, and for any arm whose
        // answer reaches the harness as a replayed ScriptedTrace — the workflow, the rubber stamp
        // and the floor — AgentResponse.TokenUsage is null, so MAFEvaluationHarness falls back to
        // ESTIMATING tokens from text length and marks them TokensAreEstimated. Its workflow row is
        // therefore a guess about a turn that made four to seven real model calls. The panel is
        // printed anyway because its WALL-CLOCK column is measured and useful.
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠️  The token and cost columns in the next panel are the HARNESS's view. For every arm");
        Console.WriteLine("      that replays its answer as a scripted trace — the workflow included — the harness has");
        Console.WriteLine("      no provider usage to read and ESTIMATES from text length. The measured spend is in");
        Console.WriteLine("      the EQUAL TOKEN BUDGET panel above. Only the seconds column below is a measurement.");
        Console.ResetColor();
        EvalPrinter.PrintCostComparison(report);

        // ══ VERDICT — reported, never gated ═══════════════════════════════════════════════
        Eval09Verdict verdict = Eval09PreRegistration.Decide(
            primary, versusRubberStamp, budget, silentLiveCells.Count, voidedCells.Count);
        PrintVerdict(verdict, primary, versusRubberStamp, budget, report, judged);

        // ══ NOTES ═════════════════════════════════════════════════════════════════════════
        foreach ((string label, List<int> taken) in roundsByArm.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            int degenerate = taken.Count(r => r <= 1);
            notes.Add($"LOOP HEALTH · {label}: rounds taken "
                    + string.Join(", ", taken.GroupBy(r => r).OrderBy(g => g.Key).Select(g => $"{g.Count()}×{g.Key}"))
                    + $" · P(rounds = 1) = {(taken.Count == 0 ? "n/a" : Format(degenerate / (double)taken.Count))}. "
                    + "A reviewer that rubber-stamps round 1 shows P(rounds = 1) ≈ 1 and is INVISIBLE in a coverage "
                    + "number, which is why the distribution is printed beside it.");
        }

        if (workflowRunsObserved > 0)
        {
            notes.Add($"The live workflow traversed its loop-back edge on {workflowRunsThatLooped} of "
                    + $"{workflowRunsObserved} run(s). A workflow that never loops is a five-stage pipeline being "
                    + "billed as a loop — and its coverage number would be a fact about the pipeline, not about the "
                    + "second look this eval is supposed to be pricing.");
        }

        if (degradedStageCount > 0)
        {
            notes.Add($"DEGRADED STAGES · the live workflow fell back to a deterministic implementation "
                    + $"{degradedStageCount} time(s): "
                    + string.Join(", ", degradedStages.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                          .Select(kv => $"{kv.Key} ×{kv.Value}"))
                    + $". A stage that fell back is a stage the model did NOT do, so the {voidedCells.Count} cell(s) it "
                    + "happened on are VOIDED — out of the mean, out of the judged panel, missing from the pairing — "
                    + "and clause 5 names no winner. The loop never throws, which is a design property and also the "
                    + "way a live arm quietly stops being live; the per-arm ATTEMPTED / RETURNED / CANCELLED counts in "
                    + "the budget panel are how a reader sees it. Voided: " + string.Join("; ", voidedCells) + ".");
        }

        if (secondTurns.Count > 0)
        {
            int recovered = secondTurns.Count(t => t.Outcome.SecondTurnRan && t.Outcome.PresentedAfterSecondTurn > 0);
            int stillSilent = secondTurns.Count(t => t.Outcome.SilentAfterSecondTurn);
            notes.Add($"SECOND TURN · {secondTurns.Count} single-agent cell(s) presented NOTHING on turn 1 — the instructed "
                    + "thin-signal behaviour is to ask two clarifying questions. The harness answered from the persona's own "
                    + $"profile (question-blind, no SKU, no category, no gold) on the same session: {recovered} presented "
                    + $"after the answer, {stillSilent} still silent, and ONLY that silence reaches clause 4. Both turns are "
                    + "on the agent's token ledger. Cells: "
                    + string.Join("; ", secondTurns.Select(t => $"{t.Cell}: {t.Outcome.Describe()}")) + ".");
        }

        if (silentLiveCells.Count > 0)
        {
            notes.Add($"{silentLiveCells.Count} LIVE cell(s) presented NOTHING: {string.Join("; ", silentLiveCells.Take(8))}"
                    + (silentLiveCells.Count > 8 ? $" … and {silentLiveCells.Count - 8} more" : "")
                    + ". Each is scored 0.000 on the primary endpoint — an EARNED zero on a persona that had a right "
                    + "answer — and excluded from the judged panel, because a criterion quantified over an empty set "
                    + "of recommendations is vacuous rather than met. Silence is never a pass here, and clause 4 of "
                    + "the rule voids the verdict when it happens on a live arm.");
        }

        if (silentControlCells.Count > 0)
        {
            notes.Add($"{silentControlCells.Count} control cell(s) presented nothing: "
                    + string.Join("; ", silentControlCells.Take(6))
                    + ". A CONTROL that presents nothing passes a comparison by being broken rather than by being "
                    + "good, so this is reported even though it does not void the verdict.");
        }

        notes.Add($"The FLOOR arm presented nothing on every persona BY CONSTRUCTION — that is what makes it a "
                + "floor — so its empty cells are excluded from clause 4 rather than counted as silence. It is not "
                + "an entrant and never enters the decision rule.");

        notes.Add("The judged criteria are ADVISORY and are NOT in the decision rule. They are uncalibrated: this "
                + "repository holds no gold set and no inter-rater agreement for them, and six criteria are six "
                + $"tests, so the Bonferroni threshold is {Eval09PreRegistration.BonferroniThreshold:F5} rather than "
                + "0.05. They are printed because a reviewer will reasonably ask what a judge says, and the answer "
                + "should be available without being load-bearing.");

        notes.Add("NOT MEASURED here, and each would need its own eval: latency under concurrency, failure "
                + "containment under a hostile catalogue (Eval 04), catalogue integrity (Eval 01), and whether "
                + "either architecture is preferred by an actual customer. A single endpoint on a twelve-persona "
                + "corpus is what this run has, and it is all it has.");

        // ══ GATES ═════════════════════════════════════════════════════════════════════════
        //
        // ⚠ Deliberately NOT gated on "the workflow won". Gating on a result creates an incentive to
        //   tune the eval until it produces one, which is the same shape as letting the artifact
        //   under test supply its own pass criterion. Every gate below is about whether the
        //   INSTRUMENT was sound enough for the verdict above to mean anything.
        // A VOIDED live cell is a cell that quietly went missing from the pairing, and a pairing
        // with a hole in it is not complete — even when the hole was cut on purpose by the rule.
        bool pairingComplete = primary.EffectiveN + primary.Ties > 0
                            && report.LatentCount(ArmSingleAgent) > 0
                            && report.LatentCount(ArmWorkflow) > 0
                            && armsThatThrew == 0
                            && voidedCells.Count == 0;

        // ⚠ A DRY RUN CANNOT ESTABLISH THIS GATE'S LIVE CLAIM, so it must not print it.
        //
        // MEASURED on this eval's own first dry run: the gate showed a green tick beside the words
        // "both arms made model calls and both reported token usage" on a run where the stub had
        // reported no usage at all — while the verdict panel four lines above said UNMEASURED. A
        // ✅ next to a sentence the run did not establish is a false ✅, which is the one thing a
        // gate may never print. Under a stub the gate is downgraded to the property a stub CAN
        // establish — that the meter is wired under both arms — and its wording says so.
        bool spendMeasured = dryRun
            ? budget.BothArmsRan
            : budget.BothArmsRan && budget.BothArmsReportedTokens;

        // A rubber stamp that LEADS the live workflow means the second round bought nothing. That is
        // a real defect in the architecture under test, not a defect in this eval, and it is the one
        // outcome that voids a "workflow wins" claim outright.
        //
        // The test is on the SignTest(reference: rubber stamp, challenger: workflow) outcome, where
        // Wins counts personas the workflow covered better and Losses counts personas the rubber
        // stamp covered better. A TIE is deliberately not a failure: two arms that score identically
        // have not shown the loop to be worthless, only that this metric cannot see the difference —
        // and the coverage panel says that in its own words.
        //
        // ⚠ AND IT FAILS CLOSED WHEN THE COMPARISON WAS NEVER MADE. Until 2026-09-06 this read
        // `Losses <= Wins` alone. That was safe while the pairing was k-blind, because a k-blind
        // pairing always produces pairs; at equal k it can refuse every one, and 0 ≤ 0 is true, so
        // an unmade comparison passed a GATE that decides this eval's exit code. An absent control
        // is not a passed one — the same rule Eval 02's GATE 2 already applies.
        bool loopIsLoadBearing = Eval09PreRegistration.LoopIsLoadBearing(versusRubberStamp);

        bool judgeFloorDefined = judged.FloorIsDefined(ArmJudgeFloor);

        if (!judgeFloorDefined)
        {
            notes.Add("GATE 4 FAILED CLOSED: the contentless FLOOR arm produced no usable per-criterion verdict, so "
                    + "every judged number on this page is a figure with no floor beside it. An undefined floor is "
                    + "not a permissive one.");
        }

        PrintGate(pairingComplete, spendMeasured, loopIsLoadBearing, judgeFloorDefined, dryRun, notes);

        if (dryRun)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Snapshot NOT written — a dry run must not leave a result behind.");
            Console.WriteLine("     The GATES above ran in their DRY-RUN form.");
            if (personas.Any(x => string.Equals(x.Id, Eval09DryRun.CancelledPersonaId, StringComparison.Ordinal)))
            {
                Console.WriteLine($"     GATE 1 is EXPECTED to fail here: one workflow cell ({Eval09DryRun.CancelledPersonaId}) had its");
                Console.WriteLine("     InterestMapper call cancelled on purpose and was VOIDED, so the pairing has the");
                Console.WriteLine("     hole the rule cuts.");
            }
            else
            {
                Console.WriteLine("     GATE 1's EXPECTED dry-run failure is OUT OF SCOPE on this probe: the cancellation");
                Console.WriteLine($"     lands on {Eval09DryRun.CancelledPersonaId} and this run does not include it, so GATE 1's verdict");
                Console.WriteLine("     here says nothing about the VOID rule either way.");
            }
            Console.WriteLine("     Gate 2 is");
            Console.WriteLine("     downgraded to \"the meter is wired under both arms\". NOTHING above is a result");
            Console.WriteLine("     about either architecture. What a stub CAN establish is the plumbing, and that is");
            Console.WriteLine("     what is checked below — including that this stage can fail at all.");
            Console.ResetColor();

            return DryRunPlumbingHeld(
                report, judged, agentTokens, workflowTokens, judgeTokens,
                primary, workflowRunsObserved, armsThatThrew,
                new Eval09DryRunEvidence(voidedCells, secondTurns, budget, verdict),
                [.. personas.Select(x => x.Id)]) ? 0 : 1;
        }

        // ⚠ The label is the CALLER's, not the method's. This snapshot went to disk reading
        // "Eval 02 — Latent-Interest Coverage" for as long as it existed (MEASUREMENT_STATUS
        // §23.10, defect 4): a different eval, different arms, different question, saved under
        // another eval's name. The own-k cells are saved, and the declared-k cut beside them —
        // the pairing reads the second, so a record that kept only the first could not be
        // re-checked against the verdict it produced.
        string snapshotKey = onlyPersona is null ? SnapshotKey : ProbeSnapshotKey;
        EvalResultStore.SaveCoverage(snapshotKey, report.ToSnapshot(
            floors, declaredK, GalaxusEvalPrompt.CoverageCanonical, atK,
            onlyPersona is null
                ? "Eval 09 — Single agent vs discovery workflow"
                : $"Eval 09 PROBE (n = 1, {personas[0].Id}) — Single agent vs discovery workflow"));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Coverage snapshot saved → {EvalResultStore.StorageLocation}\\{snapshotKey}.json");
        if (onlyPersona is not null)
            Console.WriteLine($"     (probe key '{ProbeSnapshotKey}' — the full-cohort record at '{SnapshotKey}' is untouched.)");
        Console.WriteLine("     ⚠ The JUDGED panel and the TOKEN LEDGER are printed, not snapshotted. CoverageSnapshot");
        Console.WriteLine("       has no slot for either, and adding one means editing a file three other evals read.");
        Console.ResetColor();

        return pairingComplete && spendMeasured && loopIsLoadBearing && judgeFloorDefined ? 0 : 1;
    }

    /// <summary>The arms, in report order, with whether each is model-backed and therefore repeated.</summary>
    /// <remarks>
    /// Order is chosen so the reader meets the two entrants, then the control that can take the win
    /// away, then the floor. The FLOOR arm is last on purpose: it is not an entrant and a reader who
    /// met it first would read it as one.
    /// </remarks>
    private static IEnumerable<(string Label, bool Live)> Arms()
    {
        yield return (ArmSingleAgent, true);
        yield return (ArmWorkflow, true);
        yield return (ArmRubberStamp, false);
        yield return (ArmJudgeFloor, false);
    }

    /// <summary>
    /// Runs and grades one arm for one persona: the deterministic coverage score AND the judge's
    /// per-criterion verdicts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ The error check comes FIRST and nothing below it may produce a number. An errored turn
    /// produces an empty trace, an empty trace serves no token, and 0/n is a perfectly well-formed
    /// 0.000 — which would then be averaged in as if it were an observation of the architecture. It
    /// is not an observation; it is the absence of one, and the two must never average together.
    /// </para>
    /// <para>
    /// The judged cell is recorded only when the arm PRESENTED something. Every criterion in
    /// <see cref="Eval09PreRegistration.JudgedCriteria"/> either quantifies over the
    /// recommendations or describes the covering note around them, so on an empty answer a judge's
    /// "met" is vacuous. The FLOOR arm is the one exception and it is deliberate — measuring exactly
    /// which criteria come back vacuously met is the entire reason that arm exists.
    /// </para>
    /// </remarks>
    /// <param name="persona">The persona under test.</param>
    /// <param name="goldByPersona">Every scored persona's derived gold, for the forced choice.</param>
    /// <param name="agent">The arm instance for this rep.</param>
    /// <param name="harness">The judge-backed harness.</param>
    /// <param name="options">Evaluation options.</param>
    /// <param name="report">The paired coverage report.</param>
    /// <param name="armLabel">The arm's label.</param>
    /// <param name="repLabel">The repetition label, for the console line.</param>
    /// <param name="declaredK">The presentation budget the cut cell is scored at — the only cell a pairing may read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The score (null when the turn threw), the harness result and the presented calls. The
    /// CALLER decides whether the cell is recorded on the judged panel — it cannot be decided here,
    /// because whether a workflow cell is VOID is known only from the arm's telemetry after the
    /// turn, and a voided cell must not be recorded anywhere.
    /// </returns>
    private static async Task<Eval09ArmCell> ScoreArmAsync(
        CoveragePersona persona,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona,
        IEvaluableAgent agent,
        MAFEvaluationHarness harness,
        EvaluationOptions options,
        PairedCoverageReport report,
        string armLabel,
        string repLabel,
        int declaredK,
        CancellationToken ct)
    {
        var testCase = new TestCase
        {
            Name = $"{persona.Id} · {armLabel} · {repLabel}",
            Input = persona.Prompt,

            // ⭐ Supplying criteria is what flips MAFEvaluationHarness into its judge branch. The
            //    four existing evals in this project deliberately do not, and construct the
            //    no-evaluator harness so the branch is unreachable rather than merely unused.
            EvaluationCriteria = Eval09PreRegistration.JudgedCriteria,

            // The judge's holistic score decides TestResult.Passed. Nothing in this eval reads
            // TestResult.Passed, so the threshold is set where it cannot be mistaken for a bar this
            // run enforces: zero.
            PassingScore = 0,
        };

        TestResult result;
        using (EvalRuntime.BeginTurn())
        {
            result = await harness.RunEvaluationAsync(agent, testCase, options, ct).ConfigureAwait(false);
        }

        report.RecordCost(armLabel, result.Performance);

        if (result.HasError)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      {armLabel,-34} {repLabel,-16} ❌ the turn threw: {result.Error?.Message}");
            Console.WriteLine("                                                          EXCLUDED — not scored 0.000.");
            Console.ResetColor();
            return new Eval09ArmCell(null, null, result, []);
        }

        IReadOnlyList<PresentedCall> presented = PresentedCall.FromToolUsage(result.ToolUsage);

        // ⚠ TWO readings of one turn, and only one of them may ever be paired.
        //
        // `Own` is the arm at whatever k it chose — the floors, the forced choice, the telemetry.
        // `Cut` is the same turn cut to the DECLARED budget, which is the only cell an equal-k
        // pairing may touch. Until 2026-09-06 this method produced only the first and the eval
        // paired it k-blind: MEASURED on the 2026-09-05 live run, Robin presented exactly 5 items
        // on all 24 reps and the workflow presented 3–11 and never 5, on 0 of 21 scored reps.
        // Latent coverage is recall and monotone in k, so 16 of those 21 pairs compared a longer
        // slate against a shorter one and the difference was reported as architecture.
        CoverageScore own = InterestCoverageGrader.GradeWithControls(persona.Id, goldByPersona, presented);
        CoverageScore cut = InterestCoverageGrader.GradeAtDeclaredK(persona.Id, goldByPersona, presented, declaredK);

        return new Eval09ArmCell(own, cut, result, presented);
    }

    /// <summary>
    /// The contentless-floor comparison, printed as the FLOOR CHECK it is rather than as a paired
    /// sign test it is not entitled to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not a sign test.</b> The floor arm presents nothing, on purpose — that is what
    /// makes it a floor. Under the equal-k rule a silent side is never comparable with an answer, so
    /// pairing against it produces twelve refusals and no verdict. The claim it used to carry
    /// ("both live architectures beat a contentless answer on 12 of 12 personas, p = 0.0005") is
    /// true and worth keeping; it is a count of personas that presented at least one gold-carrying
    /// product, and it is printed as that.
    /// </para>
    /// <para>
    /// <b>And a stronger check is printed beside it,</b> because "beat an empty answer" is a bar any
    /// arm clears by presenting one right item: whether each arm cleared its OWN random-draw floor,
    /// persona by persona, at the k it actually presented. That floor is derived from the corpus,
    /// it rises with k, and it is the number a coverage cell has to be read against.
    /// </para>
    /// </remarks>
    /// <param name="report">The own-k report — floors are derived at each arm's own presentation count.</param>
    /// <param name="atK">The declared-budget report, for the cut cells' own floors.</param>
    /// <param name="floors">The per-persona degenerate-draw floor, for the record.</param>
    /// <param name="notes">Notes to append the finding to.</param>
    private static void PrintFloorCheck(
        PairedCoverageReport report, PairedCoverageReport atK,
        IReadOnlyDictionary<string, double> floors, List<string> notes)
    {
        string[] entrants = [ArmSingleAgent, ArmWorkflow, ArmRubberStamp];

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── FLOOR CHECK — not a paired sign test, and it never was one ────────");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("      The contentless arm presents NOTHING by construction, so no pair with it is at");
        Console.WriteLine("      equal k and the equal-k rule refuses every one. What survives is a COUNT with a");
        Console.WriteLine("      floor beside it, which is what the 12/0/0 always was.");
        Console.ResetColor();

        // A floor that is not at the floor is a wiring fault, and it is checked before it is used.
        var floorCells = report.Personas.Select(p => report.ScoreOf(p, ArmJudgeFloor)).Where(s => s is not null).ToList();
        bool floorIsZero = floorCells.Count > 0 && floorCells.All(s => s!.Value.Latent == 0.0);
        if (!floorIsZero)
        {
            notes.Add($"🔴 THE CONTENTLESS FLOOR ARM DID NOT SCORE 0.000 on every persona "
                    + $"({floorCells.Count(s => s!.Value.Latent != 0.0)} of {floorCells.Count} nonzero). It is supposed "
                    + "to present nothing. Every 'beats the floor' count below is uninterpretable until that is "
                    + "explained — the floor is supplying the bar it is measured against.");
        }

        foreach (string arm in entrants)
        {
            var cells = report.Personas
                .Select(p => (Persona: p, Score: report.ScoreOf(p, arm)))
                .Where(x => x.Score is { IsScorable: true })
                .ToList();

            if (cells.Count == 0) continue;

            int beatsContentless = cells.Count(x => x.Score!.Value.Latent > 0.0);
            var belowOwn = cells.Where(x => x.Score!.Value.AboveOwnFloor is not true).Select(x => x.Persona).ToList();
            double meanFloor = cells.Average(x => x.Score!.Value.LatentFloor);
            double meanK = cells.Average(x => (double)x.Score!.Value.PresentedCount);

            bool clean = beatsContentless == cells.Count && belowOwn.Count == 0;
            Console.ForegroundColor = clean ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine($"      {EvalPrinter.ShortArm(arm),-26} beats the contentless answer on {beatsContentless}/{cells.Count} "
                            + $"· clears its OWN random-draw floor on {cells.Count - belowOwn.Count}/{cells.Count} "
                            + $"(mean floor {Format(meanFloor)} at mean k {meanK:F1})"
                            + (belowOwn.Count > 0 ? $" · BELOW on {string.Join(", ", belowOwn)}" : ""));
            Console.ResetColor();
        }

        var cutFloor = atK.Personas.Select(p => atK.ScoreOf(p, ArmJudgeFloor)).Where(s => s is not null).ToList();
        notes.Add("FLOOR CHECK, not a sign test: the contentless arm is SILENT by construction, so under the equal-k "
                + "rule every pair with it is NOT COMPARABLE and no p-value is available for it. The claim that "
                + "survives is a count — how many personas each arm beat an empty answer on, and how many it cleared "
                + "its own random-draw floor on — printed above with the floor beside it. The earlier form of this "
                + "result, 'W/L/T 12/0/0, p = 0.0005', paired a k = 5 answer against a k = 0 non-answer and is "
                + "withdrawn as a p-value, not as a finding."
                + (cutFloor.Count > 0 ? $" At the declared budget the floor arm's precision@k is {Format(cutFloor.Average(s => s!.Value.PrecisionAtK))}, which is 0 by construction and not a measurement of anything." : ""));
    }

    /// <summary>One arm's one rep: both scores, the harness result they were read from, and the presented calls.</summary>
    /// <param name="Score">The coverage score at the arm's OWN k, or null when the turn threw. Floors and telemetry only.</param>
    /// <param name="Cut">The same turn cut to the DECLARED budget — the only cell that may be paired.</param>
    /// <param name="Result">The harness result, judge rows included.</param>
    /// <param name="Presented">The presentation calls in the trace.</param>
    private readonly record struct Eval09ArmCell(
        CoverageScore? Score, CoverageScore? Cut, TestResult Result, IReadOnlyList<PresentedCall> Presented);

    /// <summary>The per-cell console line, printed by the caller once the cell's fate is decided.</summary>
    /// <remarks>
    /// A cell with no judged number has one of two fates and the line names which: the judge was
    /// asked and returned no usable verdict (undecidable), or the judge was never consulted because
    /// the arm presented nothing (vacuous). Printing both as "undecidable" read as a judge fault
    /// when it was a silent arm — MEASURED on the 2026-09-04 dry run, where all twelve workflow
    /// cells were vacuous and the panel said the judge could not decide them.
    /// </remarks>
    private static void PrintCellLine(
        string armLabel, string repLabel, CoverageScore score, bool judgedOk, int judgeScore, string metSummary,
        bool excludedAsVacuous = false)
    {
        string judgeColumn = judgedOk
            ? $"{judgeScore,3}/100 {metSummary}"
            : excludedAsVacuous ? "— vacuous (presented nothing)" : "— undecidable";

        Console.ForegroundColor = score.IsScorable && score.Latent > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
        Console.WriteLine($"      {armLabel,-34} {repLabel,-16} latent {Format(score.Latent)} "
                        + $"({score.LatentServed}/{score.LatentTotal}) vs floor {Format(score.LatentFloor)} at k="
                        + $"{score.PresentedCount}  forced-choice {Format(score.ForcedChoice)}  "
                        + $"judge {judgeColumn}"
                        + (score.PhantomCount > 0 ? $"  ⚠ phantom {score.PhantomCount}" : ""));
        Console.ResetColor();
    }

    /// <summary>
    /// Whether the dry run proved the PLUMBING — the only thing a stub can prove, and the reason
    /// stage one of this repository's three-stage run protocol exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seven properties. Every one of them is a wiring fact that would silently corrupt a live run,
    /// and every one of them CAN fail — a dry run that returns 0 unconditionally is not a stage.
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Both live arms produced a real mean.</b> NaN means the persona loop,
    ///   an adapter or the tool-trace extraction is broken, not that the stub was modest.</description></item>
    ///   <item><description><b>The token meter saw model calls from BOTH arms.</b> This is the check
    ///   that the metered client actually reached each architecture. Zero on the workflow side would
    ///   mean the loop silently built its own client from <c>Config</c> and the whole equal-budget
    ///   instrument was measuring one arm.</description></item>
    ///   <item><description><b>The workflow arm ran the MAF graph.</b> At least one run observed,
    ///   with a resolved terminal stop reason.</description></item>
    ///   <item><description><b>The judge path is REACHABLE.</b> At least one cell came back with a
    ///   verdict for every criterion. This is the gap this eval exists to close, and a dry run that
    ///   did not check it could ship the closure unwired.</description></item>
    ///   <item><description><b>The FLOOR arm produced a defined per-criterion rate.</b></description></item>
    ///   <item><description><b>At least one judged pair came out NON-TIED.</b> The delta arithmetic
    ///   has a win, a loss and a tie branch; a run where everything ties exercises one of them and
    ///   prints a full panel of zeroes that is indistinguishable from a broken comparison. MEASURED:
    ///   with both arms on the same stub their answers were byte-identical and every one of the
    ///   seventy-two judged pairs tied. MEASURED AGAIN (2026-09-05): with the workflow stub citing a
    ///   grounding key that never resolved, every workflow cell presented k = 0, was excluded from
    ///   the judged panel as vacuous, and there were ZERO pairs — a second way to the same zeroes,
    ///   which the check now names separately.</description></item>
    ///   <item><description><b>The primary sign test computed a defined attainable p.</b></description></item>
    ///   <item><description><b>No arm threw.</b></description></item>
    /// </list>
    /// </remarks>
    /// <param name="report">The paired coverage report.</param>
    /// <param name="judged">The judged report.</param>
    /// <param name="agentTokens">The single agent's ledger.</param>
    /// <param name="workflowTokens">The workflow's ledger.</param>
    /// <param name="judgeTokens">The judge's ledger.</param>
    /// <param name="primary">The primary sign test.</param>
    /// <param name="workflowRunsObserved">How many live workflow runs reported telemetry.</param>
    /// <param name="armsThatThrew">How many arm runs threw.</param>
    /// <param name="evidence">What the injected cancellation and the second turn produced.</param>
    /// <param name="personasInRun">
    /// The persona ids this run actually looped over. ⚠ <b>APPLICABILITY COMES FROM THIS, NOT FROM
    /// THE RESULT.</b> Five of the checks below assert properties of an injection that lands on ONE
    /// named persona — the cancelled InterestMapper on <see cref="Eval09DryRun.CancelledPersonaId"/>
    /// and the instructed silence on <c>Personas.JonasUserId</c>. Under <c>--only</c> those personas
    /// may not be in the run at all, and a check whose subject never ran must say so rather than
    /// print a verdict. MEASURED 2026-09-06, on the first <c>-- 9 --dry-run --only USR-MI-02</c>:
    /// all five printed <b>❌</b> and the plumbing check returned false — five red ticks for
    /// injections that were never issued. <b>A red tick for an absent subject is the same defect as
    /// a green one</b>; both read applicability out of the outcome.
    /// </param>
    private static bool DryRunPlumbingHeld(
        PairedCoverageReport report,
        Eval09JudgedReport judged,
        Eval09TokenLedger agentTokens,
        Eval09TokenLedger workflowTokens,
        Eval09TokenLedger judgeTokens,
        SignTestOutcome primary,
        int workflowRunsObserved,
        int armsThatThrew,
        Eval09DryRunEvidence evidence,
        IReadOnlyCollection<string> personasInRun)
    {
        ArgumentNullException.ThrowIfNull(personasInRun);

        // The two injections' subjects, asked of the INPUT.
        bool cancellationWasIssued = personasInRun.Contains(Eval09DryRun.CancelledPersonaId, StringComparer.Ordinal);
        bool silencePersonaRan = personasInRun.Contains(Personas.JonasUserId, StringComparer.Ordinal);

        bool agentMeasured = !double.IsNaN(report.MeanLatent(ArmSingleAgent));
        bool workflowMeasured = !double.IsNaN(report.MeanLatent(ArmWorkflow));
        bool bothMetered = agentTokens.Calls > 0 && workflowTokens.Calls > 0;
        bool graphRan = workflowRunsObserved > 0;
        bool judgeReachable = judgeTokens.Calls > 0 && judged.DecidedCells > 0;
        bool floorDefined = judged.FloorIsDefined(ArmJudgeFloor);
        bool pComputable = !double.IsNaN(primary.MinimumAttainableP);
        bool noneThrew = armsThatThrew == 0;

        // ── The three properties the injected cancellation exists to prove. ──────────────
        //
        // (a) VOID fired on the cancelled persona and on NO other: its workflow cell is missing
        //     from the report (not zero, not averaged), every other persona's workflow cell is
        //     present. One void proves the rule fires; eleven survivors prove it does not fire on
        //     a cell whose stages all returned.
        string cancelled = Eval09DryRun.CancelledPersonaId;
        bool voidFiredOnCancelled = evidence.VoidedCells.Any(c => c.StartsWith(cancelled, StringComparison.Ordinal))
                                 && report.ScoreOf(cancelled, ArmWorkflow) is null;
        bool voidFiredOnlyThere = evidence.VoidedCells.All(c => c.StartsWith(cancelled, StringComparison.Ordinal))
                               && report.Personas.Where(p => !string.Equals(p, cancelled, StringComparison.Ordinal))
                                                 .All(p => report.ScoreOf(p, ArmWorkflow) is not null);

        // (b) The CANCELLED attempts reached the ledger — on the workflow arm, and on it alone —
        //     so the meter records an attempt that never returned instead of forgetting it.
        bool cancelledRecorded = workflowTokens.Cancelled >= 1 && agentTokens.Cancelled == 0 && agentTokens.Failed == 0;

        // (c) The equal-budget guard is CONFOUNDED for that reason, and it DISCRIMINATES: the arm
        //     with complete usage is not flagged, the arm with the cancelled call is. A guard that
        //     flagged both would be a guard that flags everything; a guard that flagged neither
        //     would be the 2026-09-04 guard.
        bool guardDiscriminates = agentTokens.UsageComplete && !workflowTokens.UsageComplete
                               && evidence.Budget.Confounded
                               && evidence.Budget.Reasons.Any(r => r.Contains("cancelled", StringComparison.Ordinal));

        // (d) The verdict read clause 5 first: a voided live cell names no winner.
        bool verdictVoided = evidence.Verdict.Outcome == Eval09Outcome.ArmNotLive;

        // (e) The second turn is wired on the single-agent arm: on Jonas the stub asked, the
        //     harness answered, and the merged trace carried turn 2's presentations.
        var secondTurnWired = evidence.SecondTurns
            .Where(t => t.Outcome is { SecondTurnRan: true, SecondTurnThrew: false, PresentedAfterFirstTurn: 0, PresentedAfterSecondTurn: > 0 })
            .ToList();

        // The per-criterion delta arithmetic has three branches — win, loss, tie — and a run in
        // which every pair ties exercises exactly one of them while printing a full panel of
        // zeroes that looks like a working comparison. This asserts at least one NON-TIED pair
        // somewhere in the judged panel, which is the only evidence that the other two branches
        // run at all.
        //
        // The tie count is kept for the MESSAGE, not the predicate: zero non-tied pairs has two
        // causes that need different fixes — every pair tied (the arms' answers are identical), or
        // no pair exists (one arm was excluded from the judged panel on every persona). MEASURED
        // 2026-09-05: the second, printed as the first, sent a whole session after the wrong cause.
        int nonTiedJudgedPairs = 0;
        int tiedJudgedPairs = 0;
        for (int i = 0; i < judged.CriterionCount; i++)
        {
            (int wins, int losses, int ties) = judged.PairedCounts(ArmSingleAgent, ArmWorkflow, i);
            nonTiedJudgedPairs += wins + losses;
            tiedJudgedPairs += ties;
        }
        bool judgedDeltasExercised = nonTiedJudgedPairs > 0;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── DRY-RUN PLUMBING CHECKS (a stub cannot prove anything else) ──────");
        Console.ResetColor();

        Line(agentMeasured, "the LIVE-agent arm produced a real mean — the persona loop, the adapter and the "
                          + "tool-trace extraction all ran.");
        Line(workflowMeasured, "the LIVE-workflow arm produced a real mean — the MAF graph, the presenter and the "
                             + "replay onto PresentRecommendation all ran.");
        Line(bothMetered, bothMetered
            ? $"the token meter saw model calls from BOTH arms (agent {agentTokens.Calls}, workflow "
              + $"{workflowTokens.Calls}). The equal-budget instrument is under both architectures."
            : $"the token meter saw agent={agentTokens.Calls}, workflow={workflowTokens.Calls} call(s). A zero means "
              + "that arm built its own chat client and the equal-budget instrument is measuring ONE arm.");
        Line(graphRan, graphRan
            ? $"the workflow arm ran the MAF graph {workflowRunsObserved} time(s) and resolved a terminal stop reason."
            : "the workflow arm reported NO run telemetry at all.");
        Line(judgeReachable, judgeReachable
            ? $"the LLM-judge path is REACHABLE: {judgeTokens.Calls} judge call(s), {judged.DecidedCells} cell(s) "
              + "returned a verdict for every criterion. This is the gap Eval 09 exists to close."
            : "the LLM-judge path produced NO decidable cell. The gap this eval exists to close is not closed.");
        Line(floorDefined, floorDefined
            ? "the contentless FLOOR arm produced a defined per-criterion met rate, so every judged number has a "
              + "floor beside it."
            : "the FLOOR arm produced NO defined rate — every judged number would print without its floor.");
        Line(judgedDeltasExercised, judgedDeltasExercised
            ? $"the per-criterion delta arithmetic saw {nonTiedJudgedPairs} NON-TIED pair(s) and {tiedJudgedPairs} tied, so "
              + "its win and loss branches actually ran rather than a panel of zeroes standing in for them."
            : tiedJudgedPairs > 0
                ? $"EVERY judged pair tied ({tiedJudgedPairs} of them), so only the tie branch of the delta arithmetic ran. "
                  + "A panel of zeroes is indistinguishable from a broken comparison — make the two arms' answers differ."
                : "NO judged pair exists: on no persona were BOTH live arms decidable, so the delta arithmetic ran on "
                  + "nothing. A missing pair is not a tie — an arm that presented nothing on every cell was excluded "
                  + "from the judged panel as vacuous, and the two arms' answers never met. Make that arm present.");
        Line(pComputable, "the primary sign test computed a defined attainable p from the non-tied pair count.");
        Line(noneThrew, noneThrew ? "no arm threw." : $"{armsThatThrew} arm run(s) threw.");

        if (!cancellationWasIssued)
        {
            NotApplicable($"the VOID rule, the cancelled-attempt ledger and clause 5's precedence are NOT CHECKED on "
                        + $"this run: the injection lands on {cancelled}, which is not in it. No cancellation was "
                        + "issued, so there is nothing here to fire or to fail — this is not a passed check.");
        }
        else
        {
            Line(voidFiredOnCancelled && voidFiredOnlyThere, voidFiredOnCancelled && voidFiredOnlyThere
                ? $"a DEGRADED stage VOIDS its cell, and only its cell: {cancelled}'s workflow cell (InterestMapper cancelled on both "
                  + $"attempts, fell back to the code-derived map) is MISSING from the report — not zero, not averaged — and the "
                  + $"other {report.Personas.Count - 1} workflow cell(s), whose stages all returned, are present. Voided: "
                  + string.Join("; ", evidence.VoidedCells)
                : $"the VOID rule did not fire where it should ({cancelled} present: {report.ScoreOf(cancelled, ArmWorkflow) is not null}; "
                  + $"voided cells: {evidence.VoidedCells.Count}) or fired where it should not. A degraded 'live' cell would be "
                  + "back in the mean.");
            Line(cancelledRecorded, cancelledRecorded
                ? $"a CANCELLED attempt reaches the ledger: workflow {workflowTokens.Accounting}; agent {agentTokens.Accounting}. "
                  + "The meter no longer forgets a call that never returned."
                : $"the cancelled attempts did NOT reach the right ledger: workflow {workflowTokens.Accounting}; agent "
                  + $"{agentTokens.Accounting}.");
            Line(guardDiscriminates, guardDiscriminates
                ? "the equal-budget guard treats MISSING usage as CONFOUNDED and DISCRIMINATES: the agent ledger is usage-complete "
                  + "and is not flagged; the workflow ledger has a cancelled call and is — reasons: "
                  + string.Join(" | ", evidence.Budget.Reasons)
                : $"the guard did not discriminate: agent complete={agentTokens.UsageComplete}, workflow complete="
                  + $"{workflowTokens.UsageComplete}, confounded={evidence.Budget.Confounded}, reasons: "
                  + string.Join(" | ", evidence.Budget.Reasons));
            Line(verdictVoided, verdictVoided
                ? $"the verdict read clause 5 first and named NO winner: {evidence.Verdict.Outcome}."
                : $"the verdict was {evidence.Verdict.Outcome}; a run with a voided live cell must not get past clause 5.");
        }

        if (!silencePersonaRan)
        {
            NotApplicable($"the harness's SECOND TURN is NOT CHECKED on this run: the agent stub asks first only on "
                        + $"{Personas.JonasUserId}, which is not in it. Nothing asked, so nothing could answer — this "
                        + "is not a passed check.");
        }
        else
        Line(secondTurnWired.Count > 0, secondTurnWired.Count > 0
            ? "the harness's SECOND TURN is wired on the single-agent arm: "
              + string.Join(", ", secondTurnWired.Select(t => $"{t.Cell} (k {t.Outcome.PresentedAfterFirstTurn}→{t.Outcome.PresentedAfterSecondTurn})"))
              + " — turn 1 asked and presented nothing, the reply reached the same session, the merged trace carried turn 2."
            : "the harness's SECOND TURN did NOT fire on the single-agent arm, or carried no turn-2 presentation into the "
              + "graded trace. Jonas's instructed silence would still void clause 4.");

        // ⚠ The injection-dependent conjuncts are folded in ONLY when their injection was issued.
        //   This is not "assume they passed" — the whole clause is dropped, and the printed
        //   NOT APPLICABLE line above says which claim this run is therefore NOT making.
        return agentMeasured && workflowMeasured && bothMetered && graphRan
            && judgeReachable && floorDefined && judgedDeltasExercised && pComputable && noneThrew
            && (!cancellationWasIssued
                || (voidFiredOnCancelled && voidFiredOnlyThere && cancelledRecorded && guardDiscriminates && verdictVoided))
            && (!silencePersonaRan || secondTurnWired.Count > 0);

        static void Line(bool ok, string text)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            foreach (string wrapped in Wrap($"  {(ok ? "✅" : "❌")} {text}", 96)) Console.WriteLine(wrapped);
            Console.ResetColor();
        }

        // ⚠ Deliberately NOT a ✅. A check whose subject never ran has established nothing, and a
        //   green tick beside it is the false-✅ this file already carries a paragraph about.
        static void NotApplicable(string text)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (string wrapped in Wrap($"  ⏭ NOT APPLICABLE — {text}", 96)) Console.WriteLine(wrapped);
            Console.ResetColor();
        }
    }

    private static void AddRounds(Dictionary<string, List<int>> roundsByArm, string label, int rounds)
    {
        if (!roundsByArm.TryGetValue(label, out List<int>? taken)) roundsByArm[label] = taken = [];
        taken.Add(rounds);
    }

    private static IChatClient BuildAzureChatClient()
    {
        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        return azureClient.GetChatClient(Config.Model).AsIChatClient();
    }

    // ══ PANELS ════════════════════════════════════════════════════════════════════════════

    private static void PrintPersonaHeader(CoveragePersona persona, GoldInterestMap gold)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine();
        Console.WriteLine($"  ─── {persona.Id}  {persona.Name} ──────────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      {persona.Note}");
        Console.WriteLine($"      latent gold: {(gold.Latent.Count == 0 ? "(empty — persona skipped)" : string.Join(", ", gold.Latent.OrderBy(t => t, StringComparer.Ordinal)))}");
        Console.ResetColor();
    }

    private static void PrintBudgetPanel(
        Eval09Budget budget,
        Eval09TokenLedger agentTokens,
        Eval09TokenLedger workflowTokens,
        Eval09TokenLedger judgeTokens,
        bool dryRun)
    {
        Console.WriteLine();
        Top();
        Title("EQUAL TOKEN BUDGET — the precondition, measured by ONE instrument");
        Divider();
        Grey(
        [
            "  Both arms' spend is read by the SAME MeteredChatClient, sitting at the raw IChatClient layer UNDER "
          + "each architecture — so it counts every model round-trip either one makes, including the agent's tool "
          + "loop and every stage of the workflow. Neither arm reports its own spend, which is the point: the "
          + "artifact under test never supplies an input to its own pass/fail.",
        ]);
        Divider();

        // Per arm: ATTEMPTED / RETURNED / CANCELLED / FAILED / NO-USAGE, then the tokens the
        // returned calls reported. A reader can see from this row alone when a "live" arm was not
        // live — the 2026-09-04 Demo 2 run would have printed attempted 7 · returned 1 · cancelled 6.
        Console.ForegroundColor = ConsoleColor.White;
        Row($"  {"ledger",-20} {"attempt",7} {"ret",4} {"canc",4} {"fail",4} {"no-use",6} {"half",4} {"prompt",8} {"compl",7} {"tok/turn",8}");
        Divider();
        foreach (Eval09TokenLedger ledger in new[] { agentTokens, workflowTokens, judgeTokens })
        {
            Row($"  {Fit(ledger.Name, 20)} {ledger.Calls,7} {ledger.Returned,4} {ledger.Cancelled,4} {ledger.Failed,4} "
              + $"{ledger.ReturnedWithoutUsage,6} {ledger.PartialUsage,4} {ledger.PromptTokens,8} {ledger.CompletionTokens,7} "
              + $"{(ledger.Turns == 0 || !ledger.UsageComplete ? "n/a" : ledger.TokensPerTurn.ToString("F0", CultureInfo.InvariantCulture)),8}");
        }
        Console.ResetColor();
        Grey(
        [
            "  tok/turn prints n/a for a ledger with ANY cancelled, failed or usage-less call on it: a total with a hole "
          + "in it is a lower bound, and dividing a lower bound by turns reports an arm as cheaper the less of it ran.",
            "  ⚠ 'half' counts calls that returned a usage block with ONE side missing. Until 2026-09-06 there was no "
          + "such column and no such state: the missing half was folded in as a ZERO, the ledger still read complete, "
          + "and clause 2's ratio was computed from it. An absent number is not a zero — at either level.",
        ]);

        // ⚠ THE MONEY, AND WHY IT IS PRINTED HERE AND NOT ONLY IN TOKENS.
        //
        // This is the most expensive command in the suite and until 2026-09-06 it printed no
        // currency figure at all: the published "USD 29.49" for the 2026-09-05 run has no printer
        // behind it in this tree, so a reader could not check it and the next run could not
        // reproduce it. The TOKENS are the provider's own; the RATE is ModelPricing's declared row,
        // named on the line so nobody has to guess which table it came from. Where a ledger is
        // incomplete the figure is labelled a LOWER BOUND rather than suppressed or, worse,
        // rendered as though it were whole.
        Divider();
        PrintMoney(agentTokens, workflowTokens, judgeTokens, dryRun);

        Divider();
        if (!budget.BothArmsRan)
        {
            Red(["  ❌ ONE OR BOTH ARMS MADE NO MODEL CALL AT ALL. Nothing here is a comparison of two "
               + "architectures. A live arm reporting zero calls is a wiring fault, not frugality."]);
        }
        else if (!budget.BothArmsReportedTokens)
        {
            var lines = new List<string>
            {
                "  ❌ CONFOUNDED — USAGE INCOMPLETE. At least one arm has a call whose spend the provider never "
              + "reported — cancelled at the ceiling, failed, or returned without usage — so the equal-budget "
              + "precondition is UNMEASURED. Unmeasured is not equal, it is not 'fewer tokens', and no winner may be "
              + "named on this run.",
            };
            foreach (string reason in budget.Reasons) lines.Add("      · " + reason);
            if (dryRun)
            {
                lines.Add("      On this dry run the ONLY hole is the injected one: returned stub calls carry synthetic "
                        + "usage (characters / 4), so a reason above that is not the cancelled InterestMapper call "
                        + "would be a wiring fault.");
            }
            Red(lines);
        }
        else
        {
            string headline = budget.Confounded
                ? $"  ❌ CONFOUNDED — spend ratio {budget.Ratio:F2}× exceeds the pre-registered "
                  + $"{Eval09PreRegistration.MaximumTokenRatio:F2}×."
                : $"  ✅ COMPARABLE — spend ratio {budget.Ratio:F2}×, within the pre-registered "
                  + $"{Eval09PreRegistration.MaximumTokenRatio:F2}×.";

            if (budget.Confounded) Red([headline]); else Green([headline]);

            Grey(
            [
                $"      {EvalPrinter.ShortArm(ArmSingleAgent)} spent {budget.AgentTokensPerTurn:F0} token(s) per "
              + $"graded turn; {EvalPrinter.ShortArm(ArmWorkflow)} spent {budget.WorkflowTokensPerTurn:F0}.",
                "      Spending more and scoring higher is not evidence that an architecture is better. It is "
              + "evidence that more inference was bought. Where the ratio is outside the band the comparison is "
              + "reported as confounded and NO winner is named — whichever arm led.",
            ]);
        }

        Divider();
        Grey(
        [
            "  Judge spend is listed above and is deliberately NOT in the ratio: the judge is applied identically "
          + "to every arm, so it cannot favour an architecture — but its input contains the arm's own output, so a "
          + "verbose arm costs more judge tokens, and charging that to the arm would bill an architecture for "
          + "being graded.",
        ]);
        Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints what the run cost in money, from the ledgers' MEASURED tokens at a NAMED rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tokens are measured; the rate is declared.</b> Every token in the figure came out of a
    /// provider usage block — that is what the ledgers count — and the price per thousand comes from
    /// <see cref="ModelPricing"/>'s row for the resolved deployment. The row and its numbers are
    /// printed beside the total, because a currency figure whose rate a reader cannot see is a
    /// figure they cannot check.
    /// </para>
    /// <para>
    /// <b>UNKNOWN is never rendered as zero.</b> If no rate matches the deployment the panel says so
    /// and reports tokens only; if a ledger is incomplete — a cancelled call, a failed call, an
    /// absent usage block or HALF a usage block — its money is labelled a LOWER BOUND.
    /// </para>
    /// </remarks>
    /// <param name="agentTokens">The single agent's ledger.</param>
    /// <param name="workflowTokens">The workflow's ledger.</param>
    /// <param name="judgeTokens">The judge's ledger.</param>
    /// <param name="dryRun">True on a dry run, where the tokens are synthetic and there is no bill.</param>
    private static void PrintMoney(
        Eval09TokenLedger agentTokens, Eval09TokenLedger workflowTokens, Eval09TokenLedger judgeTokens, bool dryRun)
    {
        string model = dryRun ? "(stub — dry run)" : Config.Model;
        var rate = ModelPricing.GetPricing(model);

        if (rate is null)
        {
            Yellow(
            [
                dryRun
                    ? "  💵 COST: NONE — a dry run spends nothing, and the tokens in the panel above are SYNTHETIC "
                    + "(characters / 4). No rate is looked up, because there is no bill to price."
                    : $"  💵 COST: UNKNOWN — no ModelPricing row matches the deployment '{model}', so this run reports "
                    + "TOKENS ONLY. An unpriced run is not a free one, and printing 0.0000 here would say it was.",
            ]);
            return;
        }

        (decimal inPer1K, decimal outPer1K, _, _) = rate.Value;
        decimal total = 0m;
        var lines = new List<string>
        {
            $"  💵 COST — tokens are the provider's own; the rate is ModelPricing['{model}'] = "
          + $"{inPer1K:F5}/1K prompt, {outPer1K:F5}/1K completion (USD).",
        };

        foreach (Eval09TokenLedger ledger in new[] { agentTokens, workflowTokens, judgeTokens })
        {
            decimal money = (ledger.PromptTokens / 1000m * inPer1K) + (ledger.CompletionTokens / 1000m * outPer1K);
            total += money;
            lines.Add($"      {Fit(ledger.Name, 24),-24} USD {money,9:F4}"
                    + (ledger.UsageComplete ? "" : $"   ⚠ LOWER BOUND — {ledger.UsageGap}"));
        }

        bool allComplete = agentTokens.UsageComplete && workflowTokens.UsageComplete && judgeTokens.UsageComplete;
        lines.Add($"      {"TOTAL",-24} USD {total,9:F4}"
                + (allComplete ? "" : "   ⚠ LOWER BOUND — at least one ledger has a hole in it"));

        if (allComplete) Grey(lines); else Yellow(lines);
    }

    private static void PrintJudgePanel(Eval09JudgedReport judged, bool dryRun)
    {
        Console.WriteLine();
        Top();
        Title("PER-CRITERION JUDGE DELTAS — advisory, never gated, floor beside every number");
        Divider();
        Yellow(
        [
            $"  These {Eval09PreRegistration.JudgedCriteria.Count} criteria are UNCALIBRATED. There is no gold set "
          + "and no inter-rater agreement for them anywhere in this repository, so a met rate here is a hypothesis "
          + "about the architecture, not a measurement of it.",
            $"  And {Eval09PreRegistration.JudgedCriteria.Count} criteria are {Eval09PreRegistration.JudgedCriteria.Count} "
          + $"TESTS: the family-wise error rate at alpha = {Eval09PreRegistration.PrimaryAlpha:F2} is about "
          + $"{1 - Math.Pow(1 - Eval09PreRegistration.PrimaryAlpha, Eval09PreRegistration.JudgedCriteria.Count):P0}, "
          + $"so the Bonferroni threshold is {Eval09PreRegistration.BonferroniThreshold:F5} and a row is coloured only "
          + "when it clears THAT. None of them enters the decision rule.",
        ]);
        if (dryRun)
        {
            Yellow(["  🧪 DRY RUN — the verdicts below came from a scripted judge that decides by hashing the answer "
                  + "text. They prove the parse and the delta arithmetic, and nothing else."]);
        }
        Divider();

        // ⚠ THE DENOMINATORS, PRINTED ONCE AND UP FRONT. Every rate in the table below is a mean
        //   over the personas that arm was decidable on, and that is NOT the same set for the two
        //   arms: a VOIDED workflow cell, a silent live cell and an undecidable judge verdict each
        //   remove a persona from one column and not the other. A rate without its n is a
        //   decoration, and 0.000 over nine personas and 0.000 over twelve used to print identically.
        Grey(
        [
            $"  Personas with at least one decidable cell — {EvalPrinter.ShortArm(ArmSingleAgent)}: "
          + $"{judged.DecidedPersonaCount(ArmSingleAgent)} · {EvalPrinter.ShortArm(ArmWorkflow)}: "
          + $"{judged.DecidedPersonaCount(ArmWorkflow)} · {EvalPrinter.ShortArm(ArmRubberStamp)}: "
          + $"{judged.DecidedPersonaCount(ArmRubberStamp)} · {EvalPrinter.ShortArm(ArmJudgeFloor)}: "
          + $"{judged.DecidedPersonaCount(ArmJudgeFloor)}. The two live columns are NOT necessarily over the "
          + "same personas, and the W/L/T counts pair only where BOTH were decidable.",
        ]);
        Divider();

        Console.ForegroundColor = ConsoleColor.White;
        Row($"  {"#",2} {"agent",6} {"workflow",9} {"Δ",7} {"W/L/T",8} {"p",7} {"FLOOR",6}");
        Console.ResetColor();
        Divider();

        for (int i = 0; i < Eval09PreRegistration.JudgedCriteria.Count; i++)
        {
            double agent = judged.MetRate(ArmSingleAgent, i);
            double workflow = judged.MetRate(ArmWorkflow, i);
            double floor = judged.MetRate(ArmJudgeFloor, i);
            (int wins, int losses, int ties) = judged.PairedCounts(ArmSingleAgent, ArmWorkflow, i);
            double p = PairedCoverageReport.ExactTwoSidedSignP(wins, wins + losses);
            double delta = double.IsNaN(agent) || double.IsNaN(workflow) ? double.NaN : workflow - agent;

            bool survivesBonferroni = p < Eval09PreRegistration.BonferroniThreshold;

            Console.ForegroundColor = survivesBonferroni ? ConsoleColor.Green : ConsoleColor.Gray;
            Row($"  {i + 1,2} {Format(agent),6} {Format(workflow),9} {Format(delta),7} "
              + $"{wins}/{losses}/{ties,-4} {p,7:F4} {Format(floor),6}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (string line in Wrap("       " + Eval09PreRegistration.JudgedCriteria[i], InnerWidth))
                Row(line);

            // ⚠ The criterion's DECLARED vacuity crossed with the floor arm's MEASURED met rate.
            //   These are two different facts and the panel used to print one label for both — see
            //   Eval09PreRegistration.CaveatFor, which carries the measured reason.
            var caveat = Eval09PreRegistration.CaveatFor(
                GalaxusEvalCriteria.AdvisoryCriteria[i].VacuousOnAnAnswerWithNoRecommendations, floor);
            string caveatText = Eval09PreRegistration.CaveatText(caveat, floor);

            if (caveatText.Length > 0)
            {
                Console.ForegroundColor = caveat is JudgedRowCaveat.VacuousAndUninterpretable
                    ? ConsoleColor.Yellow
                    : ConsoleColor.DarkYellow;
                foreach (string line in Wrap("       " + caveatText, InnerWidth))
                    Row(line);
            }
            Console.ResetColor();
        }

        Divider();
        Grey(
        [
            $"  Decidable judged cells: {judged.DecidedCells}. Undecidable — the judge returned no verdict for "
          + $"every criterion, or the arm presented nothing at all: {judged.UndecidedCells}. An undecidable cell "
          + "is EXCLUDED and never scored, because a criterion quantified over an empty set of recommendations is "
          + "vacuous, and vacuously-met is the flattering direction.",
            "",
            $"  Cells matched by POSITION rather than by criterion text: {judged.PositionMatchedCells}. That "
          + "fallback runs only when the judge returned exactly one row per criterion but did not echo their "
          + "wording, and it is counted here because \"we assumed the order\" is otherwise invisible in a number.",
            "",
            "  ⚠ MAFEvaluationHarness copies the judge's score and rows but NOT its EvaluationFailed flag, so an "
          + "instrument failure cannot be read off TestResult — a parse failure arrives as a score of 50 that is "
          + "indistinguishable from a mediocre grade. This panel therefore detects it STRUCTURALLY: a cell counts "
          + "only when the judge returned a verdict for EVERY criterion. Deliberately conservative in the safe "
          + "direction.",
        ]);
        Bottom();
        Console.WriteLine();
    }

    private static void PrintVerdict(
        Eval09Verdict verdict,
        SignTestOutcome primary,
        SignTestOutcome versusRubberStamp,
        Eval09Budget budget,
        PairedCoverageReport report,
        Eval09JudgedReport judged)
    {
        double agentMean = report.MeanLatent(ArmSingleAgent);
        double workflowMean = report.MeanLatent(ArmWorkflow);

        Console.WriteLine();
        Top();
        Title("THE PRE-REGISTERED VERDICT — reported, never gated");
        Divider();

        Console.ForegroundColor = verdict.Outcome switch
        {
            Eval09Outcome.WorkflowWins => ConsoleColor.Green,
            Eval09Outcome.SingleAgentWins => ConsoleColor.Green,
            Eval09Outcome.Confounded => ConsoleColor.Red,
            Eval09Outcome.ArmNotLive => ConsoleColor.Red,
            Eval09Outcome.NotComparableAtEqualK => ConsoleColor.Red,
            _ => ConsoleColor.Yellow,
        };
        WrapRow("  " + verdict.Headline);
        Console.ResetColor();

        Divider();
        Console.ForegroundColor = ConsoleColor.White;
        Row("  primary endpoint  paired latent coverage, workflow − single agent");
        WrapRow($"  arms              {Format(workflowMean)} (workflow, n={report.LatentCount(ArmWorkflow)})  vs  "
              + $"{Format(agentMean)} (agent, n={report.LatentCount(ArmSingleAgent)})");
        // ⚠ THESE THREE ROWS ARE NOT PRINTED FOR A COMPARISON THAT WAS NEVER MADE.
        //
        // An exact sign test over zero pairs returns W/L/T 0/0/0 and p = 1.0000 BY ARITHMETIC.
        // Printing that in the summary block reads as "the arms tied and the p-value agrees" — the
        // flattering misreading — and it read exactly that way on the 2026-09-05 run shape, where
        // ArmNotLive fires before the NOT-COMPARABLE branch and the panel still said "the paired
        // result ran 0/0/0 … at p = 1.0000". Clause 1 below already refuses that reading; the rows
        // a reader meets FIRST must not contradict it.
        if (primary.Undecidable)
        {
            WrapRow($"  paired result     NOT COMPARABLE — 0 pairs at equal k, {primary.Excluded.Count} refused");
            WrapRow("  exact two-sided p n/a — an empty sign test returns 1.0000 by arithmetic, not by measurement, "
                  + "and it is not evidence that the arms agree");
            WrapRow("  attainable p      n/a — no pair was compared, so no split of this run could have reached any p");
        }
        else
        {
            Row($"  paired result     W/L/T {primary.Wins}/{primary.Losses}/{primary.Ties}   mean Δ {Format(primary.MeanDelta)}");
            Row($"  exact two-sided p {primary.PValue:F4}   alpha {Eval09PreRegistration.PrimaryAlpha:F2}");
            WrapRow($"  attainable p      {primary.MinimumAttainableP:F4} at the n = {primary.EffectiveN} this run "
                  + $"ATTAINED after {primary.Ties} tie(s)");
        }

        WrapRow($"  (ceiling was      {Eval09PreRegistration.TheoreticalMinimumTwoSidedP(report.Personas.Count):F5} "
              + $"at n = {report.Personas.Count} with no ties — a bound, never the number a run reports)");
        WrapRow($"  budget ratio      {(budget.BothArmsReportedTokens ? budget.Ratio.ToString("F2", CultureInfo.InvariantCulture) + "×" : "UNMEASURED")}"
              + $"   limit {Eval09PreRegistration.MaximumTokenRatio:F2}×");
        WrapRow(versusRubberStamp.Undecidable
            ? $"  rubber stamp      NOT COMPARABLE at equal k — {versusRubberStamp.Excluded.Count} refused, so the "
            + "loop-is-load-bearing control was not established either way"
            : $"  rubber stamp      workflow W/L/T {versusRubberStamp.Wins}/{versusRubberStamp.Losses}/"
            + $"{versusRubberStamp.Ties} against a reviewer that never says no");
        Console.ResetColor();

        Divider();
        Console.ForegroundColor = verdict.Outcome == Eval09Outcome.WorkflowWins ? ConsoleColor.Green : ConsoleColor.Yellow;
        foreach (string reason in verdict.Reasons) WrapRow("  · " + reason);
        Console.ResetColor();

        if (verdict.Outcome != Eval09Outcome.WorkflowWins)
        {
            Divider();
            Section("READ THIS AS A RESULT, NOT AS A DISAPPOINTMENT");
            Divider();
            Console.ForegroundColor = ConsoleColor.White;
            foreach (string paragraph in NegativeResultText(verdict, primary, budget, workflowMean, agentMean, judged))
            {
                if (paragraph.Length == 0) Row("");
                else WrapRow("  " + paragraph);
            }
            Console.ResetColor();
        }

        Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// The message for every outcome that is not "the workflow wins". The most credible output this
    /// suite can produce, so it is written out in full rather than compressed into a status word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four obligations, one per paragraph: say plainly what happened; bound the claim so an absence
    /// of evidence is not read as evidence of the opposite; admit that the instrument may be the
    /// limit; and name what would legitimately change the answer, while ruling out the one move that
    /// would not.
    /// </para>
    /// <para>
    /// ⚠ <b>It branches on the OUTCOME, and the first version did not — which made it lie.</b>
    /// MEASURED on this eval's own first dry run: the workflow led 10-0 at p = 0.0020 and the verdict
    /// was CONFOUNDED on the budget clause, and this block still printed "a difference this design
    /// cannot separate from chance (p = 0.0020)". A single fixed narrative attached to "not a win"
    /// will eventually describe a run it does not fit, and a false sentence about a real number is
    /// worse than no sentence at all. There is now one branch per <see cref="Eval09Outcome"/>.
    /// </para>
    /// </remarks>
    /// <param name="verdict">The verdict, whose outcome selects the narrative.</param>
    /// <param name="primary">The primary sign test.</param>
    /// <param name="budget">The measured budget comparison.</param>
    /// <param name="workflowMean">The workflow's mean latent coverage.</param>
    /// <param name="agentMean">The single agent's mean latent coverage.</param>
    /// <param name="judged">The judged report, for the criterion count.</param>
    /// <returns>Paragraphs, unwrapped. An empty string is a blank line.</returns>
    // internal, not private: Eval 03's Eval09RemedyIsLedgerDerived control asserts that the
    // ArmNotLive remedy stops prescribing a timeout fix on a run whose calls all returned. A
    // remedy that only exists inside a print method cannot be checked by anything.
    internal static IReadOnlyList<string> NegativeResultText(
        Eval09Verdict verdict,
        SignTestOutcome primary,
        Eval09Budget budget,
        double workflowMean,
        double agentMean,
        Eval09JudgedReport judged)
    {
        int pairs = primary.Wins + primary.Losses + primary.Ties;
        bool significant = !primary.Undecidable && primary.PValue < Eval09PreRegistration.PrimaryAlpha;
        string direction = primary.ChallengerLeads ? "toward the workflow"
                         : primary.Losses > primary.Wins ? "toward the single agent"
                         : "in neither direction";

        // ⚠ ONE PLACE RENDERS "what the endpoint did", and it refuses to render a comparison that
        //   was never made as a result. Three of the branches below used to interpolate
        //   primary.Wins/Losses/Ties and primary.PValue unconditionally. At equal k those are
        //   0/0/0 and 1.0000 BY ARITHMETIC when every pair was refused, and the ArmNotLive branch
        //   in particular fires BEFORE the NOT-COMPARABLE verdict — so the 2026-09-05 run shape
        //   printed "the paired result ran 0/0/0 (W/L/T) in neither direction … at p = 1.0000",
        //   which is the flattering misreading this eval had just removed from clause 1.
        string endpoint = primary.Undecidable
            ? $"the endpoint was NOT COMPARABLE: all {primary.Excluded.Count} persona(s) were refused at equal k, so "
            + "there is no W/L/T and no p-value — the 1.0000 an empty sign test returns is the absence of a "
            + $"comparison. {Format(agentMean)} against {Format(workflowMean)} are the two arms' UNPAIRED means, "
            + "and a difference between unpaired means over different persona sets is not a result"
            : $"the paired result ran {primary.Wins}/{primary.Losses}/{primary.Ties} (W/L/T) {direction}, "
            + $"{Format(agentMean)} against {Format(workflowMean)}, at p = {primary.PValue:F4}";

        var text = new List<string>();

        // ── 1. WHAT HAPPENED ────────────────────────────────────────────────────────
        switch (verdict.Outcome)
        {
            case Eval09Outcome.ArmNotLive:
                text.Add("The comparison was VOIDED before its result could be read, because the arm labelled LIVE "
                       + "was not live on every cell: a model stage timed out or failed and the loop fell back to its "
                       + "deterministic node, as it is built to. Those cells were removed, not averaged.");
                text.Add("");
                text.Add($"WHAT IT SAYS. On the cells that survived, {endpoint} — a number about a SUBSET of personas, "
                       + "on a run whose arm was partly code. It is printed so the reader can see what was lost, and "
                       + "it is not the comparison.");
                break;

            case Eval09Outcome.Confounded:
                text.Add("The comparison was VOIDED before its result could be read, and it was voided by a clause "
                       + "this eval fixed in advance precisely so that it could not be waived afterwards.");
                text.Add("");
                text.Add((budget.BothArmsReportedTokens
                    ? $"WHAT IT SAYS. The two arms did not spend comparably: {budget.AgentTokensPerTurn:F0} tokens per "
                    + $"graded turn for the single agent against {budget.WorkflowTokensPerTurn:F0} for the workflow, a "
                    + $"ratio of {budget.Ratio:F2}× against a pre-registered limit of "
                    + $"{Eval09PreRegistration.MaximumTokenRatio:F2}×. On the endpoint itself, {endpoint}"
                    : $"WHAT IT SAYS. The arms' spend is UNMEASURED ({string.Join("; ", budget.Reasons)}). On the "
                    + $"endpoint itself, {endpoint}")
                    + (primary.Undecidable ? "." : significant ? "." : ", which does not reach alpha anyway."));

                if (significant && primary.ChallengerLeads)
                {
                    text.Add("");
                    text.Add("⚠️ THE WORKFLOW LED, AND SIGNIFICANTLY, AND THAT NUMBER IS STILL NOT REPORTABLE AS A WIN. "
                           + "A lead bought with more inference is a fact about a budget, not about an architecture, "
                           + "and the whole reason clause 2 was written down before the run was that this is exactly "
                           + "the moment it would be tempting to set aside.");
                }
                break;

            case Eval09Outcome.LoopNotLoadBearing:
                text.Add("A reviewer that approves on round 1 every time — a control with the loop's topology and no "
                       + "judgement in it at all — covered more of the gold than the real loop did. The architecture "
                       + "claim is void, and it is void for a reason that is worth more than the comparison it "
                       + "cancelled.");
                text.Add("");
                text.Add("WHAT IT SAYS. The second round is not paying for itself on this endpoint. That is a defect "
                       + "in the reviewer or in what the loop does with a second look — NOT a defect in this eval, and "
                       + "not evidence that loops in general do not help. It is also the failure that fails in the "
                       + "FLATTERING direction: a rubber-stamping reviewer produces a cheap, fast, clean-looking run, "
                       + "and the wrong conclusion drawn from it is 'the architecture does not help' when the truth is "
                       + "'the checker is broken'.");
                break;

            case Eval09Outcome.SilenceInTheComparison:
                text.Add("At least one live arm presented NOTHING on a persona that had a right answer, so part of "
                       + "this comparison is between an answer and an absence. No winner is named on a run in that "
                       + "state.");
                text.Add("");
                text.Add("WHAT IT SAYS. A silent cell is scored as the earned zero it is, so it does depress that "
                       + "arm's mean — but an architecture that sometimes says nothing is not being measured on the "
                       + "same thing as one that always answers, and averaging the two would report a reliability "
                       + "failure as a quality difference.");
                break;

            case Eval09Outcome.NotComparableAtEqualK:
                text.Add("A comparison this verdict depends on was NOT MADE. Every persona in it was refused at equal "
                       + "k: on each one the two arms presented different numbers of items, or a side was silent, and "
                       + "latent coverage is a recall — monotone in the number of items presented. Pairing them "
                       + "anyway would have measured list length and reported it as architecture.");
                text.Add("");
                text.Add(primary.Undecidable
                    ? $"WHAT IT SAYS. Nothing about which architecture is better. The {primary.Excluded.Count} "
                    + "refused pair(s) are listed in the sign-test panel with both k's, so the reader can see exactly "
                    + "where the comparison ran out. ⚠ The p-value beside an empty sign test is 1.0000 by arithmetic, "
                    + "not by measurement, and it is not evidence that the arms agree."
                    : "WHAT IT SAYS. The PRIMARY endpoint did produce comparable pairs, but the rubber-stamp control "
                    + "did not — and that control is the one that would void an architecture claim, so a result read "
                    + "without it would be read without its own veto. The refused pairs are listed in the sign-test "
                    + "panel with both k's.");
                break;

            case Eval09Outcome.SingleAgentWins:
                text.Add("The comparison came out in the direction the design did not predict: one agent with eleven "
                       + "tools beat the five-executor workflow on the endpoint this eval pre-registered.");
                text.Add("");
                text.Add($"WHAT IT SAYS. On {pairs} paired personas the single agent covered {Format(agentMean)} of "
                       + $"the latent gold against the workflow's {Format(workflowMean)}, {primary.Losses}/"
                       + $"{primary.Wins} on non-tied pairs, at p = {primary.PValue:F4}. The rule that declared it was "
                       + "written before the run and was written to be able to point this way.");
                break;

            default:
                text.Add("The five-executor workflow did not beat one agent with eleven tools on the endpoint this "
                       + "eval pre-registered. That is a finding. It is the finding this eval was built to be able to "
                       + "produce, and producing it is the reason the decision rule was written down before the run "
                       + "rather than after it.");
                text.Add("");
                text.Add($"WHAT IT SAYS. On {pairs} paired personas, an interest mapper, a coverage reviewer, a "
                       + "conditional loop-back edge, a ranker and a presenter — "
                       + (budget.BothArmsReportedTokens
                            ? $"at {budget.Ratio:F2}× the token spend of the single agent"
                            : "at a token spend this run could not measure")
                       + $" — moved latent-interest coverage from {Format(agentMean)} to {Format(workflowMean)}, a "
                       + $"difference this design cannot separate from chance (p = {primary.PValue:F4}, with "
                       + $"{primary.Ties} of the pairs tied outright).");
                break;
        }

        // ── 2. WHAT IT DOES NOT SAY ─────────────────────────────────────────────────
        text.Add("");
        text.Add("WHAT IT DOES NOT SAY. It does not say the workflow is worse. It does not say loops do not help. It "
               + "does not say the architecture has no value — Eval 04 measures a containment property the single "
               + "agent has no structure for at all, and this eval says nothing about that, about latency under load, "
               + "about auditability, or about what a customer would prefer. One endpoint that did not move is one "
               + "endpoint that did not move.");

        // ── 3. AND IT MAY BE THE INSTRUMENT ─────────────────────────────────────────
        text.Add("");
        text.Add("AND IT MAY BE THE INSTRUMENT. Eval 02 measured this metric's entire discriminating band as the gap "
               + "between a tag-join oracle and a one-pass control. A difference smaller than that band is not "
               + "evidence about anything, whichever direction it points, and this eval inherits that ceiling "
               + "wholesale.");

        // ── 4. WHAT WOULD CHANGE THE ANSWER ─────────────────────────────────────────
        text.Add("");
        text.Add(verdict.Outcome switch
        {
            // ⚠ THE REMEDY IS DERIVED FROM THIS RUN'S OWN LEDGER, not printed from a prior run's
            // diagnosis. It used to prescribe raising DiscoveryLoopOptions.ModelCallTimeout on
            // every ArmNotLive verdict, citing 2026-09-04's "6 of 7 calls abandoned at the 60 s
            // ceiling". On 2026-09-05 the ledger read 120 attempted / 120 returned / 0 cancelled
            // and five stages fell back anyway — on unparseable output. The panel sent the reader
            // to a timeout that had not fired. A stage falls back for two different reasons and
            // they have two different fixes, so the ledger decides which sentence is printed.
            Eval09Outcome.ArmNotLive when budget.EveryWorkflowCallReturned =>
                $"WHAT WOULD CHANGE THE ANSWER. NOT the timeout. This run's ledger reads {budget.WorkflowAttempted} "
              + $"attempted / {budget.WorkflowReturned} returned / {budget.WorkflowCancelled} cancelled / "
              + $"{budget.WorkflowFailed} failed on the workflow arm, so every model call this eval asked for came "
              + "back. A stage that fell back with its call in hand fell back on CONTENT — an interest envelope or a "
              + "reviewer verdict the stage could not parse — and raising DiscoveryLoopOptions.ModelCallTimeout would "
              + "fix none of it. What would: make the stages' envelopes parseable (a schema-constrained response, or "
              + "a repair pass that is itself counted as a degraded stage), then re-run. Lowering the bar for what "
              + "counts as live would not be a fix either way.",

            // The third case the ledger can be in, and it is neither of the two above: NOTHING was
            // attempted. `EveryWorkflowCallReturned` is false there (0 > 0 fails), so without this
            // the branch below would print "0 attempted / 0 returned / 0 cancelled … so calls
            // really did go missing" — a claim the ledger it quotes contradicts in the same
            // sentence. A remedy derived from a ledger has to survive an EMPTY ledger.
            Eval09Outcome.ArmNotLive when budget.WorkflowAttempted == 0 =>
                "WHAT WOULD CHANGE THE ANSWER. Not the timeout, and not the parsing: this run's workflow ledger "
              + "records NO attempted model call at all, so no stage got as far as failing. Find out why the meter "
              + "saw nothing — an unbound client, a stage skipped before its call, or a ledger that is not wired "
              + "under this arm — before reading anything else on this page as a fact about the workflow.",

            Eval09Outcome.ArmNotLive =>
                $"WHAT WOULD CHANGE THE ANSWER. A run on which every model stage RETURNS. This run's workflow ledger "
              + $"reads {budget.WorkflowAttempted} attempted / {budget.WorkflowReturned} returned / "
              + $"{budget.WorkflowCancelled} cancelled / {budget.WorkflowFailed} failed, so calls really did go "
              + "missing: raise the per-call ceiling (DiscoveryLoopOptions.ModelCallTimeout) or fix the deployment's "
              + "latency, and re-run. ⚠ Check the count before acting on this: a cell whose call RETURNED and still "
              + "fell back failed on unparseable content, and no timeout change touches that. Lowering the bar for "
              + "what counts as live would not be a fix.",

            Eval09Outcome.NotComparableAtEqualK =>
                "WHAT WOULD CHANGE THE ANSWER. Making the two arms present the same number of items. The utterance "
              + $"declares a budget of k = {CoverageArms.DeclaredK} and both arms are cut to it, so a refusal here "
              + "means an arm UNDER-FILLED the budget — it presented fewer than k and there is nothing to cut. That "
              + "is an instruction-following property of the arm, and it is worth measuring in its own right rather "
              + "than papered over. ⚠ What would NOT be a fix: pairing k-blind again. Coverage is recall and monotone "
              + "in k, so an unequal-k pairing measures list length as much as architecture, and it measures it in "
              + "whichever direction the more verbose arm happens to run.",

            Eval09Outcome.Confounded when !budget.BothArmsReportedTokens =>
                "WHAT WOULD CHANGE THE ANSWER. Getting complete usage out of the provider on every attempted call — "
              + "a wiring or a ceiling problem, not a measurement one. Until every attempt returns and reports, this "
              + "run cannot tell an architectural difference from a purchased one, and it should not be re-read as "
              + "though it could.",

            Eval09Outcome.Confounded =>
                "WHAT WOULD CHANGE THE ANSWER. Equalising the budget rather than the verdict: cap the workflow's "
              + "stages, or give the single agent the same number of model calls to spend, and re-run. Raising "
              + $"MaximumTokenRatio past {Eval09PreRegistration.MaximumTokenRatio:F2}× so that this run clears it "
              + "would not be a fix — it would be moving the bar after seeing the ball.",

            Eval09Outcome.LoopNotLoadBearing =>
                "WHAT WOULD CHANGE THE ANSWER. Fixing the reviewer, then re-running. Note which way that cuts: this "
              + "control is the reason a broken reviewer shows up as a defect rather than as a quiet argument against "
              + "the architecture.",

            Eval09Outcome.SilenceInTheComparison =>
                "WHAT WOULD CHANGE THE ANSWER. Finding out why an arm went silent — a refusal, a tool budget, a "
              + "timeout, an empty retrieval — and either fixing it or scoring reliability as its own endpoint "
              + "instead of letting it leak into a quality number.",

            // An undecidable primary can reach this switch under ANOTHER verdict — ArmNotLive,
            // silence and the loop control all fire before the NOT-COMPARABLE branch — and the
            // power sentence below would then blame an n the run never had a chance to collect.
            _ when primary.Undecidable =>
                "WHAT WOULD CHANGE THE ANSWER. Not more personas: the endpoint had no comparable pair at all, so "
              + "power is not the binding constraint here. Making the two arms present the SAME number of items is, "
              + "and after that whichever fault this verdict names above.",

            _ =>
                $"WHAT WOULD CHANGE THE ANSWER. n = {primary.EffectiveN} non-tied pairs is what this run had, and the "
              + $"smallest p it could have reached was {primary.MinimumAttainableP:F4}. A larger persona corpus, more "
              + "repetitions per cell, or — most honestly — an endpoint the loop is actually built to move would each "
              + $"be a legitimate next step. So would calibrating the {judged.CriterionCount} judged criteria against "
              + "a gold set, which would let them carry weight instead of sitting in an advisory panel.",
        });

        text.Add("");
        text.Add("Re-running until it wins would not.");

        return text;
    }

    private static void PrintGate(
        bool pairingComplete, bool spendMeasured, bool loopIsLoadBearing, bool judgeFloorDefined,
        bool dryRun, IReadOnlyList<string> notes)
    {
        Console.WriteLine();

        GateLine(pairingComplete, "GATE 1 — PAIRING COMPLETE", new[]
        {
            "Both live arms produced a scorable observation, no arm run threw, NO live-workflow cell",
            "was VOIDED for a degraded stage, and the paired set is non-empty. A comparison",
            "assembled from cells that went missing — quietly, or by the rule — is not paired.",
        });

        GateLine(spendMeasured,
            dryRun ? "GATE 2 — SPEND INSTRUMENT WIRED  (token check N/A under a stub)" : "GATE 2 — SPEND MEASURED",
            dryRun
                ? new[]
                {
                    "Both arms made model calls, so the meter is under both architectures. It does NOT say",
                    "token usage was reported — a stub reports none, and the verdict panel above correctly",
                    "shows the budget as UNMEASURED. The live form of this gate additionally requires both",
                    "arms to report tokens, and a dry run cannot establish that.",
                }
                : new[]
                {
                    "Both arms made model calls and both reported token usage. FAILS CLOSED when either is",
                    "zero or unreported: an unmeasured budget is not an equal one, and a live arm reporting",
                    "zero tokens is a wiring fault until proven otherwise, not an efficient architecture.",
                });

        GateLine(loopIsLoadBearing, "GATE 3 — THE LOOP IS LOAD-BEARING", new[]
        {
            "A reviewer that rubber-stamps round 1 was COMPARED with the live workflow at equal k and",
            "did NOT lead it. If it had, the second round would be costing tokens for nothing, the",
            "reviewer rather than the architecture would be the thing under test, and any 'workflow",
            "wins' claim would be void. FAILS CLOSED when no persona could be paired at equal k: a",
            "comparison that was never made did not establish that the stamp does not lead.",
        });

        GateLine(judgeFloorDefined, "GATE 4 — EVERY JUDGED NUMBER HAS ITS FLOOR", new[]
        {
            "The contentless FLOOR arm produced a defined per-criterion met rate, so no criterion",
            "number was printed without the score a degenerate answer gets beside it. An undefined",
            "floor is not a permissive one.",
        });

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("  NOT GATED, on purpose: whether the workflow won. Gating on a result creates an incentive");
        Console.WriteLine("  to tune the eval until it produces one — the same shape as letting the artifact under");
        Console.WriteLine("  test supply its own pass criterion. The verdict panel above reports it instead.");
        Console.ResetColor();

        foreach (string note in notes)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (string line in Wrap("  · " + note, 96)) Console.WriteLine(line);
            Console.ResetColor();
        }

        static void GateLine(bool ok, string title, IReadOnlyList<string> body)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {title}");
            foreach (string line in body) Console.WriteLine($"       {line}");
            Console.ResetColor();
        }
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 09 — Hypothesis Comparison: SINGLE AGENT vs WORKFLOW, both LIVE        ║
║   Paired · equal-token-budget · pre-registered rule · rubber-stamp control    ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    // ══ Box-drawing, local to this file ═══════════════════════════════════════════════════
    //
    // EvalPrinter's frame helpers are private to EvalPrinter. They are re-implemented here at the
    // same 82-column width rather than promoted to public, because promoting them means editing a
    // file that four other evals print through — and this suite is being extended by several hands
    // at once. A duplicated box border is a cheaper defect than a merge that loses a panel.

    private const int BoxWidth = 82;
    private const int InnerWidth = 78;

    private static void Top() => Console.WriteLine("╔" + new string('═', BoxWidth - 2) + "╗");

    private static void Bottom() => Console.WriteLine("╚" + new string('═', BoxWidth - 2) + "╝");

    private static void Divider() => Console.WriteLine("╠" + new string('═', BoxWidth - 2) + "╣");

    private static void Title(string title)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Row("  " + title);
        Console.ResetColor();
    }

    private static void Section(string heading)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Row("  " + heading);
        Console.ResetColor();
    }

    private static void Row(string content)
    {
        string text = content.Length > InnerWidth + 2 ? content[..(InnerWidth + 2)] : content;
        Console.WriteLine("║" + text.PadRight(BoxWidth - 2) + "║");
    }

    /// <summary>
    /// Emits one logical line, WRAPPED to the frame rather than clipped by it.
    /// </summary>
    /// <remarks>
    /// ⚠ MEASURED on this eval's own first dry run: the verdict headline came out as
    /// <c>"NO WIN — CONFOUNDED. The arms' spend was not measured, and an unmeasured budge"</c> and
    /// the attainable-p line lost its closing clause. <see cref="Row"/> clips, and a clipped
    /// sentence in the most-read line of the panel does not read as truncated — it reads as a
    /// shorter, different sentence. Every line whose length is not controlled by a format string
    /// goes through here.
    /// </remarks>
    /// <param name="content">The line, of any length.</param>
    private static void WrapRow(string content)
    {
        foreach (string line in Wrap(content, InnerWidth + 2)) Row(line);
    }

    private static void Grey(IReadOnlyList<string> lines) => Coloured(ConsoleColor.DarkGray, lines);

    private static void Yellow(IReadOnlyList<string> lines) => Coloured(ConsoleColor.Yellow, lines);

    private static void Red(IReadOnlyList<string> lines) => Coloured(ConsoleColor.Red, lines);

    private static void Green(IReadOnlyList<string> lines) => Coloured(ConsoleColor.Green, lines);

    /// <remarks>
    /// WRAPS rather than clips, for the reason given on <see cref="WrapRow"/>: every block passed
    /// here is prose, and prose is the thing a clip silently rewrites. Callers pass whole sentences
    /// and let this decide where the lines break — hand-broken lines plus a "  " indent overflowed
    /// the frame and lost their last few words, which is how the budget panel came to advertise a
    /// "MeteredChatClient, sitting at the raw ICh".
    /// </remarks>
    private static void Coloured(ConsoleColor colour, IReadOnlyList<string> lines)
    {
        Console.ForegroundColor = colour;
        foreach (string line in lines)
        {
            if (line.Length == 0) Row("");
            else WrapRow(line);
        }
        Console.ResetColor();
    }

    private static string Fit(string text, int width) =>
        text.Length <= width ? text.PadRight(width) : text[..Math.Max(0, width - 1)] + "…";

    /// <summary>Wraps on whitespace, preserving the leading indent of the first line.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxWidth">Maximum line width.</param>
    internal static IEnumerable<string> Wrap(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) { yield return string.Empty; yield break; }
        if (text.Length <= maxWidth) { yield return text; yield break; }

        string indent = new(' ', text.Length - text.TrimStart().Length);
        var current = new System.Text.StringBuilder(indent);

        foreach (string word in text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length + word.Length + 1 > maxWidth && current.Length > indent.Length)
            {
                yield return current.ToString();
                current.Clear().Append(indent);
            }
            if (current.Length > indent.Length) current.Append(' ');
            current.Append(word);
        }

        if (current.Length > indent.Length) yield return current.ToString();
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F3", CultureInfo.InvariantCulture);
}

// ══════════════════════════════════════════════════════════════════════════════════════════
//  THE PRE-REGISTERED RULE
// ══════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What the run concluded about the hypothesis.</summary>
public enum Eval09Outcome
{
    /// <summary>The workflow met every clause of the rule.</summary>
    WorkflowWins,

    /// <summary>The single agent met every clause, with the direction reversed.</summary>
    SingleAgentWins,

    /// <summary>Neither arm separated from the other at the pre-registered alpha.</summary>
    NoDifferenceDetected,

    /// <summary>The arms' spend was unequal or unmeasured, so no winner may be named.</summary>
    Confounded,

    /// <summary>The rubber-stamp control led the workflow, voiding any architecture claim.</summary>
    LoopNotLoadBearing,

    /// <summary>An arm was silent where a right answer existed, so the comparison cannot be read.</summary>
    SilenceInTheComparison,

    /// <summary>
    /// A "live" cell was not live: a model stage timed out or failed and the workflow fell back to
    /// its deterministic node. Such a cell is VOIDED, and a run with a voided live cell names no
    /// winner.
    /// </summary>
    ArmNotLive,

    /// <summary>
    /// A comparison the verdict depends on had NO pair at equal k — the primary endpoint, or the
    /// rubber-stamp control that would void an architecture claim. Every persona in it was refused
    /// because the two arms presented different numbers of items, or one side was silent.
    /// </summary>
    /// <remarks>
    /// This is not "no difference detected" and must never be printed as one. A comparison that
    /// could not be made has no direction, and reading its p-value — necessarily 1.0000 over zero
    /// pairs — as agreement between the arms is the flattering misreading. MEASURED on the
    /// 2026-09-05 run: 0 of 21 workflow reps presented the agent's k of 5. It covers the
    /// loop-is-load-bearing control for the same reason: <c>Losses &lt;= Wins</c> is trivially true
    /// at 0/0, so an unmade control comparison would otherwise read as a cleared one.
    /// </remarks>
    NotComparableAtEqualK,
}

/// <summary>The verdict, with every reason it reached that verdict.</summary>
/// <param name="Outcome">The conclusion.</param>
/// <param name="Headline">The one-line statement printed in the panel.</param>
/// <param name="Reasons">Every clause of the rule and how it came out.</param>
public sealed record Eval09Verdict(Eval09Outcome Outcome, string Headline, IReadOnlyList<string> Reasons);

/// <summary>The measured comparison of the two live arms' token spend.</summary>
/// <param name="BothArmsRan">Both arms made at least one model call.</param>
/// <param name="BothArmsReportedTokens">
/// Both arms' usage is COMPLETE: every attempted call returned and reported usage, and at least one
/// graded turn was counted. False when either arm has a cancelled, failed or usage-less call on
/// its ledger — a total with a hole in it is a lower bound, not a spend.
/// </param>
/// <param name="AgentTokensPerTurn">Mean total tokens per graded turn for the single agent, or NaN when incomplete.</param>
/// <param name="WorkflowTokensPerTurn">Mean total tokens per graded turn for the workflow, or NaN when incomplete.</param>
/// <param name="Ratio">Larger over smaller, or NaN when unmeasured.</param>
/// <param name="Reasons">Every reason the comparison is confounded, in the order it was found. Empty when it is not.</param>
/// <param name="WorkflowAttempted">Model calls the workflow arm's ledger saw asked for.</param>
/// <param name="WorkflowReturned">How many of them came back.</param>
/// <param name="WorkflowCancelled">How many were cancelled at the per-call ceiling.</param>
/// <param name="WorkflowFailed">How many threw.</param>
public sealed record Eval09Budget(
    bool BothArmsRan,
    bool BothArmsReportedTokens,
    double AgentTokensPerTurn,
    double WorkflowTokensPerTurn,
    double Ratio,
    IReadOnlyList<string> Reasons,
    int WorkflowAttempted = 0,
    int WorkflowReturned = 0,
    int WorkflowCancelled = 0,
    int WorkflowFailed = 0)
{
    /// <summary>
    /// True when EVERY workflow model call the ledger saw came back — nothing cancelled at a
    /// ceiling, nothing failed.
    /// </summary>
    /// <remarks>
    /// ⚠ This exists so the remedy panel can stop prescribing a timeout fix for a run that had no
    /// timeouts. MEASURED on 2026-09-05: 120 attempted / 120 returned / 0 cancelled, while five
    /// stages still fell back — on output the stage could not PARSE. Raising the per-call ceiling
    /// would have fixed none of them, and the panel said to raise it anyway, because the sentence
    /// was printed unconditionally from a PRIOR run's diagnosis.
    /// </remarks>
    public bool EveryWorkflowCallReturned =>
        WorkflowAttempted > 0 && WorkflowCancelled == 0 && WorkflowFailed == 0 && WorkflowReturned == WorkflowAttempted;

    /// <summary>True when the arms' spend differs by more than the pre-registered factor, or could not be measured.</summary>
    /// <remarks>
    /// An UNMEASURED ratio is confounded, not clean, and so is an INCOMPLETE one. Failing open on
    /// either would let a run with a hole in its usage data declare a winner — and the hole opens
    /// in the flattering direction: MEASURED on the 2026-09-04 Demo 2 live run, 6 of 7 calls were
    /// cancelled at the 60 s ceiling, so the first version of this guard saw the workflow spend
    /// LESS the more of it had stopped being live.
    /// </remarks>
    public bool Confounded => Reasons.Count > 0;

    /// <summary>Measures the two ledgers.</summary>
    /// <remarks>
    /// Deliberately takes no dry-run flag. The measurement is the measurement: under a stub that
    /// reports no usage, <see cref="BothArmsReportedTokens"/> comes out false and
    /// <see cref="Confounded"/> comes out true, which is the honest reading of that run and not a
    /// special case. Only the GATE distinguishes the two modes, and it says so in its own wording.
    /// </remarks>
    /// <param name="agent">The single agent's ledger.</param>
    /// <param name="workflow">The workflow's ledger.</param>
    public static Eval09Budget Measure(Eval09TokenLedger agent, Eval09TokenLedger workflow)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(workflow);

        var reasons = new List<string>();

        bool bothRan = agent.Calls > 0 && workflow.Calls > 0;
        if (!bothRan)
            reasons.Add("one or both arms made NO model call at all — a wiring fault, not frugality");

        // ⚠ COMPLETE, not merely non-zero. A cancelled call is an attempt whose spend nobody saw.
        if (agent.UsageGap is { } agentGap) reasons.Add($"{agent.Name}: {agentGap}");
        if (workflow.UsageGap is { } workflowGap) reasons.Add($"{workflow.Name}: {workflowGap}");

        bool bothComplete = agent.UsageComplete && workflow.UsageComplete
                         && agent.TotalTokens > 0 && workflow.TotalTokens > 0
                         && agent.Turns > 0 && workflow.Turns > 0;

        if (bothRan && agent.UsageComplete && workflow.UsageComplete && !bothComplete)
            reasons.Add("a ledger reported zero tokens or zero graded turns, so no per-turn figure exists");

        double a = bothComplete ? agent.TokensPerTurn : double.NaN;
        double w = bothComplete ? workflow.TokensPerTurn : double.NaN;

        double ratio = bothComplete && Math.Min(a, w) > 0
            ? Math.Max(a, w) / Math.Min(a, w)
            : double.NaN;

        if (bothComplete && (double.IsNaN(ratio) || ratio > Eval09PreRegistration.MaximumTokenRatio))
        {
            reasons.Add(double.IsNaN(ratio)
                ? "the spend ratio is undefined"
                : $"spend ratio {ratio:F2}× exceeds the pre-registered {Eval09PreRegistration.MaximumTokenRatio:F2}×");
        }

        return new Eval09Budget(bothRan, bothComplete, a, w, ratio, reasons,
            WorkflowAttempted: workflow.Calls,
            WorkflowReturned: workflow.Returned,
            WorkflowCancelled: workflow.Cancelled,
            WorkflowFailed: workflow.Failed);
    }
}

/// <summary>
/// Eval 09's decision rule, written down IN CODE and printed above the run — before a single model
/// call is made.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the rule lives in a type rather than in a comment.</b> A rule in prose is a rule that can
/// be reinterpreted after the numbers arrive. <see cref="Decide"/> is the only thing that names a
/// winner, it takes the run's own outcomes as arguments, and <see cref="Print"/> shows the reader
/// exactly what it will do before it does it. The failure this guards against is not dishonesty; it
/// is the ordinary drift of deciding what counts as a win once you can see which way the wind blew.
/// </para>
/// <para>
/// <b>The attainable p is stated twice, on purpose.</b> Before the run it is a CEILING —
/// <see cref="TheoreticalMinimumTwoSidedP"/> at the full persona count, achievable only by a clean
/// sweep with no ties. After the run the verdict panel prints
/// <see cref="SignTestOutcome.MinimumAttainableP"/>, computed from the non-tied pair count the run
/// actually attained, because the exact sign test discards ties and every tie costs power. At n = 12
/// the ceiling is 0.00049; a comparison whose pairs mostly tied can easily have a real floor of 1.000,
/// meaning no result was reachable at all. The ceiling is never the number a run reports.
/// </para>
/// </remarks>
/// <summary>
/// What a judged criterion's row must be read against: the criterion's DECLARED vacuity crossed with
/// the contentless floor arm's MEASURED met rate.
/// </summary>
/// <remarks>
/// A high floor and a vacuous criterion are two different facts, and this suite printed one label for
/// both until 2026-09-06. See <see cref="Eval09PreRegistration.CaveatFor"/> for the measured reason.
/// </remarks>
public enum JudgedRowCaveat
{
    /// <summary>Nothing to caveat: the criterion is not vacuous and the floor arm never met it.</summary>
    None = 0,

    /// <summary>Declared vacuous AND the floor met it every time. The row carries no information.</summary>
    VacuousAndUninterpretable = 1,

    /// <summary>Declared vacuous but the floor did not exploit it. A fact about the JUDGE.</summary>
    DeclaredVacuousButFloorDisagrees = 2,

    /// <summary>Declared vacuous and the floor is NOT MEASURED. Absent is not zero.</summary>
    DeclaredVacuousFloorUnmeasured = 3,

    /// <summary>Not vacuous, and the floor arm EARNS it every time. The row is hard, not empty.</summary>
    FloorEarnsItEveryTime = 4,

    /// <summary>Not vacuous, and the floor arm earns it some of the time.</summary>
    FloorEarnsItSometimes = 5,
}

public static class Eval09PreRegistration
{
    /// <summary>The significance level for the single primary test.</summary>
    public const double PrimaryAlpha = 0.05;

    /// <summary>
    /// The largest spend ratio between the two live arms that still permits naming a winner.
    /// </summary>
    /// <remarks>
    /// <b>CHOSEN, and chosen before the run.</b> 1.50 is not derived from anything — there is no
    /// principled conversion between tokens and coverage — so it is declared rather than justified,
    /// and it is declared in advance so it cannot be moved to fit a result. The reasoning is only
    /// this: a workflow makes four to seven model calls where an agent makes one turn's worth, so an
    /// unbounded ratio is the expected case, and a comparison at 3× spend is a comparison of budgets.
    /// A run that trips this reports CONFOUNDED and names no winner in EITHER direction.
    /// </remarks>
    public const double MaximumTokenRatio = 1.50;

    /// <summary>
    /// The judged criteria. <see cref="GalaxusEvalCriteria.Advisory"/> verbatim — not a second copy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That file's remarks say the criteria are not wired into <c>TestCase.EvaluationCriteria</c>
    /// anywhere in this project, and until this eval that was true. It stays true in the sense that
    /// mattered: nothing here GATES on them. What changes is that they are now actually asked, so
    /// the answer is on the page instead of being a hypothetical.
    /// </para>
    /// <para>
    /// They are reused rather than re-authored deliberately. A second, Eval-09-specific rubric would
    /// be a rubric written after seeing which architecture this eval was about to compare, which is
    /// how a criterion set quietly acquires a preferred answer.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> JudgedCriteria => GalaxusEvalCriteria.Advisory;

    /// <summary>The Bonferroni-corrected threshold for the judged criteria, which are six tests.</summary>
    public static double BonferroniThreshold => PrimaryAlpha / Math.Max(1, JudgedCriteria.Count);

    /// <summary>
    /// What a judged row's caveat is: the criterion's DECLARED vacuity crossed with the floor arm's
    /// MEASURED met rate.
    /// </summary>
    /// <param name="declaredVacuous">
    /// Whether the criterion quantifies over presented recommendations —
    /// <see cref="JudgedCriterion.VacuousOnAnAnswerWithNoRecommendations"/>. An INPUT-side fact.
    /// </param>
    /// <param name="floorMetRate">The contentless floor arm's measured met rate. A RESULT-side fact.</param>
    /// <returns>The caveat this row carries, or <see cref="JudgedRowCaveat.None"/>.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This exists because the panel used to read vacuity out of the RESULT.</b> The rule was
    /// <c>floorMetRate ≥ 0.999 ⇒ "VACUOUS — an answer that recommends nothing satisfies it"</c>, and
    /// on the 2026-09-05 paid run it fired on three rows and was <b>wrong on two of them</b>:
    /// criteria 3 and 5 were met by the floor arm because <see cref="ContentlessFloorArm.Answer"/>
    /// says those things in so many words, deliberately. The printed sentence asserted a mechanism
    /// that was false, and its effect was to discount criterion 5 — where the workflow scored 0.000
    /// against a floor that had EARNED 1.000, at p = 0.0005 — as carrying no information.
    /// </para>
    /// <para>
    /// The two facts are now crossed rather than conflated, and the DISAGREEMENT cases are the ones
    /// worth reading: a criterion declared vacuous whose floor came back low says the judge did not
    /// read it vacuously, which is a calibration observation this suite had no way to state.
    /// </para>
    /// </remarks>
    public static JudgedRowCaveat CaveatFor(bool declaredVacuous, double floorMetRate)
    {
        if (double.IsNaN(floorMetRate))
            return declaredVacuous ? JudgedRowCaveat.DeclaredVacuousFloorUnmeasured : JudgedRowCaveat.None;

        bool floorMeetsItAlways = floorMetRate >= 0.999;

        if (declaredVacuous)
            return floorMeetsItAlways ? JudgedRowCaveat.VacuousAndUninterpretable : JudgedRowCaveat.DeclaredVacuousButFloorDisagrees;

        if (floorMeetsItAlways) return JudgedRowCaveat.FloorEarnsItEveryTime;
        return floorMetRate > 0 ? JudgedRowCaveat.FloorEarnsItSometimes : JudgedRowCaveat.None;
    }

    /// <summary>The sentence a caveat prints, or empty when there is none.</summary>
    /// <param name="caveat">The caveat.</param>
    /// <param name="floorMetRate">The floor arm's met rate, for the rates that quote one.</param>
    /// <returns>The caveat sentence. Empty for <see cref="JudgedRowCaveat.None"/>.</returns>
    public static string CaveatText(JudgedRowCaveat caveat, double floorMetRate) => caveat switch
    {
        JudgedRowCaveat.VacuousAndUninterpretable =>
            "⚠️ UNINTERPRETABLE — this criterion quantifies over the recommendations an answer presents, so an "
          + "answer that presents none meets it by the arithmetic of the empty set, and the contentless floor "
          + "arm did meet it on every persona. A floor that cannot lose does not make a live arm's score harsh; "
          + "it makes it unreadable. Nothing on this row separates the two architectures.",

        JudgedRowCaveat.DeclaredVacuousButFloorDisagrees =>
            $"⚠️ DECLARED VACUOUS, but the floor arm met it only {floorMetRate:P0} of the time. The criterion "
          + "quantifies over presented recommendations, so an empty answer satisfies it logically — and the judge "
          + "did NOT read it that way. That disagreement is a fact about the JUDGE, not about either arm, and it "
          + "is the reason the declaration is not inferred from this number.",

        JudgedRowCaveat.DeclaredVacuousFloorUnmeasured =>
            "⚠️ DECLARED VACUOUS and the floor is NOT MEASURED on this run, so there is nothing to read this row "
          + "against. Absent is not zero.",

        JudgedRowCaveat.FloorEarnsItEveryTime =>
            "⚠️ the contentless floor arm meets this criterion on every persona — and it EARNS it: the criterion "
          + "does not quantify over recommendations, so the floor arm's answer satisfies it by saying so. The row "
          + "is HARD, not vacuous, and a live arm below the floor here is a finding rather than an artefact.",

        JudgedRowCaveat.FloorEarnsItSometimes =>
            $"⚠️ the contentless floor arm meets this criterion {floorMetRate:P0} of the time. Read every number "
          + "on this row against that, not against zero.",

        _ => string.Empty,
    };

    /// <summary>
    /// The smallest two-sided p an exact sign test could reach at <paramref name="pairs"/> non-tied
    /// pairs: 2 × (1/2)^n, clamped to 1.
    /// </summary>
    /// <param name="pairs">Non-tied pairs.</param>
    public static double TheoreticalMinimumTwoSidedP(int pairs) =>
        pairs <= 0 ? 1.0 : Math.Min(1.0, 2.0 * Math.Pow(0.5, pairs));

    /// <summary>Prints the rule, before any model call is made.</summary>
    /// <param name="personas">How many personas the analysis set holds.</param>
    /// <param name="reps">Repetitions per live arm.</param>
    /// <param name="dryRun">True on a dry run.</param>
    public static void Print(int personas, int reps, bool dryRun)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  PRE-REGISTERED DECISION RULE — fixed before the run, evaluated by Eval09PreRegistration.Decide:");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    H0  the workflow and the single agent cover the same share of latent gold.");
        Console.WriteLine("    H1  the workflow covers more.");
        Console.WriteLine();
        Console.WriteLine($"    Analysis set   {personas} scored personas, one shared utterance, {reps} rep(s) per live arm.");
        Console.WriteLine("                   Reps average into ONE observation per cell before pairing.");
        Console.WriteLine("    Endpoint       paired per-persona latent coverage, workflow − single agent.");
        Console.WriteLine($"    Test           exact two-sided sign test on non-tied pairs, alpha = {PrimaryAlpha:F2}.");
        Console.WriteLine();
        Console.WriteLine("    DECLARE 'the workflow wins' iff ALL FIVE hold:");
        Console.WriteLine($"      (1) p < {PrimaryAlpha:F2} AND the workflow leads (wins > losses);");
        Console.WriteLine($"      (2) the two arms' token spend per turn differs by at most {MaximumTokenRatio:F2}×,");
        Console.WriteLine("          measured by one instrument under both arms, with EVERY attempted call returned");
        Console.WriteLine("          and reporting usage — a cancelled or failed call makes the spend UNMEASURED,");
        Console.WriteLine("          and unmeasured is CONFOUNDED, never 'fewer tokens';");
        Console.WriteLine("      (3) the workflow leads the rubber-stamp control on the same endpoint;");
        Console.WriteLine("      (4) neither live arm presented NOTHING on a persona that had gold — and for the");
        Console.WriteLine("          single agent, 'nothing' means nothing after the customer answered its");
        Console.WriteLine("          clarifying questions from their own profile (ClarifyingTurnAdapter);");
        Console.WriteLine("      (5) every live-workflow cell was fully model-backed. A stage that timed out or");
        Console.WriteLine("          failed and fell back to its deterministic node VOIDS that cell: it leaves the");
        Console.WriteLine("          mean and the judged panel, and a run with a voided live cell names no winner.");
        Console.WriteLine("    The mirror rule declares 'the single agent wins' on the same clauses reversed.");
        Console.WriteLine("    Anything else prints as NO WIN, with the clause that stopped it.");
        Console.WriteLine();
        Console.WriteLine($"    Attainable p   CEILING {TheoreticalMinimumTwoSidedP(personas):F5} at n = {personas}, i.e. a clean sweep");
        Console.WriteLine("                   with no ties. The exact test DISCARDS ties, so the p this comparison");
        Console.WriteLine("                   can actually reach is computed from its own non-tied count AFTER the");
        Console.WriteLine("                   run and printed in the verdict panel. Read that one, never this one.");
        Console.WriteLine();
        Console.WriteLine($"    Secondary      {JudgedCriteria.Count} judged criteria = {JudgedCriteria.Count} more tests. Bonferroni threshold");
        Console.WriteLine($"                   {BonferroniThreshold:F5}. Reported, uncalibrated, and NOT in the rule above.");
        if (dryRun) Console.WriteLine("    ⚠ DRY RUN     no clause below can be satisfied by a stub. The rule still runs.");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// True when the rubber-stamp control was actually COMPARED with the live workflow at equal k
    /// and did not lead it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>An undecidable comparison is a FAIL here, not a pass.</b> The test used to be
    /// <c>Losses &lt;= Wins</c> alone, which was safe only while the pairing was k-blind: a k-blind
    /// pairing always produces pairs. At equal k it can refuse every persona, and <c>0 &lt;= 0</c>
    /// is true — so an unmade comparison passed the clause whose whole job is to void an
    /// architecture claim, and it passed GATE 3, which decides this eval's exit code. An absent
    /// control is not a cleared one; the same rule Eval 02's GATE 2 already states.
    /// </para>
    /// <para>
    /// A TIE is deliberately still a pass: two arms that score identically have not shown the loop
    /// to be worthless, only that this metric cannot see the difference. A REFUSAL is different —
    /// it is the absence of the observation, not an observation of no difference.
    /// </para>
    /// </remarks>
    /// <param name="versusRubberStamp">The workflow against the rubber-stamp reviewer, at equal k.</param>
    public static bool LoopIsLoadBearing(SignTestOutcome versusRubberStamp)
    {
        ArgumentNullException.ThrowIfNull(versusRubberStamp);
        return !versusRubberStamp.Undecidable && versusRubberStamp.Losses <= versusRubberStamp.Wins;
    }

    /// <summary>
    /// Applies the rule. The ONLY thing in this eval that names a winner.
    /// </summary>
    /// <remarks>
    /// Clause order is deliberate: the two conditions that VOID a comparison — silence and an unequal
    /// budget — are checked before the significance test, so a run can never report "the workflow
    /// wins, but the comparison was confounded". A confounded comparison has no winner to report.
    /// </remarks>
    /// <param name="primary">The primary sign test, single agent (A) versus workflow (B).</param>
    /// <param name="versusRubberStamp">The workflow against the rubber-stamp control.</param>
    /// <param name="budget">The measured spend comparison.</param>
    /// <param name="silentCells">How many cells presented nothing.</param>
    /// <param name="voidedCells">How many live-workflow cells were VOIDED because a model stage fell back.</param>
    public static Eval09Verdict Decide(
        SignTestOutcome primary, SignTestOutcome versusRubberStamp, Eval09Budget budget, int silentCells,
        int voidedCells = 0)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(versusRubberStamp);
        ArgumentNullException.ThrowIfNull(budget);

        var reasons = new List<string>
        {
            primary.Undecidable
                ? $"CLAUSE 1 (significance): NOT COMPARABLE. Not one of the {primary.Excluded.Count} persona(s) could "
                + "be paired at equal k — the two arms presented different numbers of items, or one side was silent. "
                + "There is no p-value here, and the 1.0000 an empty sign test returns is the absence of a "
                + "comparison, NOT agreement between the arms. Refused: "
                + string.Join("; ", primary.Excluded.Take(6))
                + (primary.Excluded.Count > 6 ? $"; … and {primary.Excluded.Count - 6} more" : "") + "."
                : $"CLAUSE 1 (significance): p = {primary.PValue:F4} against alpha = {PrimaryAlpha:F2}, with the workflow "
                + $"{(primary.ChallengerLeads ? "leading" : primary.Wins == primary.Losses ? "level" : "behind")} "
                + $"{primary.Wins}/{primary.Losses} on non-tied pairs, over {primary.ComparedN} pair(s) at equal k"
                + (primary.Excluded.Count > 0
                    ? $" — {primary.Excluded.Count} persona(s) were REFUSED as not comparable and entered neither the "
                    + $"count nor the p-value ({string.Join("; ", primary.Excluded.Take(4))}"
                    + (primary.Excluded.Count > 4 ? ", …" : "") + ")"
                    : "")
                + $". The smallest p this n could reach was {primary.MinimumAttainableP:F4}"
                + (primary.UnderpoweredByConstruction
                    ? " — above alpha, so NO split of these pairs could have produced a significant result. This clause "
                    + "was unreachable on this run, and that is a fact about the run's power, not about the architectures."
                    : "."),

            budget.BothArmsReportedTokens
                ? $"CLAUSE 2 (equal budget): spend ratio {budget.Ratio:F2}× against a limit of {MaximumTokenRatio:F2}× "
                + $"({budget.AgentTokensPerTurn:F0} tokens/turn for the agent, {budget.WorkflowTokensPerTurn:F0} for the "
                + $"workflow), every attempted call returned and reported. {(budget.Confounded ? "OUTSIDE the band — the comparison is confounded." : "Within the band.")}"
                : "CLAUSE 2 (equal budget): the arms' spend is UNMEASURED — "
                + string.Join("; ", budget.Reasons.DefaultIfEmpty("usage incomplete"))
                + ". An unmeasured budget is treated as confounded, not as equal — failing open here would let a run "
                + "with a hole in its usage data declare a winner, and the hole opens in the flattering direction.",

            // ⚠ "The rubber stamp did not lead" is a CLAIM, and a claim needs a comparison behind
            // it. At equal k this pairing can refuse every persona, and 0/0/0 with `Losses > Wins`
            // false used to render as the reassuring half of this sentence — an unmade comparison
            // read as a control that was cleared. Same fault as clause 1's, one clause further
            // down, and this one gates the eval's exit code through GATE 3.
            versusRubberStamp.Undecidable
                ? $"CLAUSE 3 (the loop is load-bearing): NOT COMPARABLE. None of the "
                + $"{versusRubberStamp.Excluded.Count} persona(s) could be paired at equal k against the reviewer "
                + "that approves on round 1 every time, so this control was not established either way. It is NOT "
                + "'the rubber stamp did not lead' — nothing was compared. Refused: "
                + string.Join("; ", versusRubberStamp.Excluded.Take(4))
                + (versusRubberStamp.Excluded.Count > 4 ? ", …" : "") + "."
                : $"CLAUSE 3 (the loop is load-bearing): against a reviewer that approves on round 1 every time, the "
                + $"workflow went {versusRubberStamp.Wins}/{versusRubberStamp.Losses}/{versusRubberStamp.Ties} "
                + $"(W/L/T) over {versusRubberStamp.ComparedN} pair(s) at equal k"
                + (versusRubberStamp.Excluded.Count > 0 ? $", {versusRubberStamp.Excluded.Count} refused" : "")
                + $". {(versusRubberStamp.Losses > versusRubberStamp.Wins ? "The rubber stamp LED — the second round bought nothing." : "The rubber stamp did not lead.")}",

            silentCells == 0
                ? "CLAUSE 4 (no silence): every cell presented at least one recommendation."
                : $"CLAUSE 4 (no silence): {silentCells} cell(s) presented NOTHING on a persona that had gold. Those "
                + "are scored as earned zeros, and their presence means part of this comparison is between an answer "
                + "and an absence.",

            // ⚠ "timed out" is NOT in this sentence, and its absence is a correction. The clause
            // used to say "time out or fail", and the remedy panel below used to prescribe raising
            // DiscoveryLoopOptions.ModelCallTimeout, citing a 2026-09-04 run in which 6 of 7 model
            // calls were abandoned at the 60 s ceiling. On the 2026-09-05 run that cause did not
            // occur: the ledger records 120 attempted / 120 returned / 0 cancelled. Both fallback
            // sites fire on CONTENT — an interest envelope that did not parse, a reviewer verdict
            // that did not parse twice — and a timeout remedy would have fixed none of them.
            // The clause now names what the code actually does and lets the run say which it was.
            voidedCells == 0
                ? "CLAUSE 5 (the live arm was live): no live-workflow cell fell back to a deterministic stage."
                : $"CLAUSE 5 (the live arm was live): {voidedCells} live-workflow cell(s) had a model stage fall back to "
                + "its deterministic node. A stage falls back when its call does not RETURN (cancelled at the per-call "
                + "ceiling, or failed) or when it returns output the stage cannot PARSE — and those are different "
                + "faults with different fixes, so read the ATTEMPTED / RETURNED / CANCELLED counts in the budget "
                + "panel before choosing one. Those cells are VOIDED — out of the mean, out of the judged panel, "
                + "missing from the pairing — and a comparison with a voided live cell names no winner.",
        };

        if (voidedCells > 0)
        {
            return new Eval09Verdict(Eval09Outcome.ArmNotLive,
                $"NO WIN — the 'live' workflow arm was NOT LIVE on {voidedCells} cell(s): a model stage fell back to "
              + "code. Those cells are voided and the comparison cannot be read as architecture against architecture.",
                reasons);
        }

        if (silentCells > 0)
        {
            return new Eval09Verdict(Eval09Outcome.SilenceInTheComparison,
                "NO WIN — an arm was SILENT on a persona that had a right answer.", reasons);
        }

        if (budget.Confounded)
        {
            return new Eval09Verdict(Eval09Outcome.Confounded,
                budget.BothArmsReportedTokens
                    ? $"NO WIN — CONFOUNDED. The arms' spend differed by {budget.Ratio:F2}×, so this is a comparison "
                    + "of budgets, not of architectures."
                    : "NO WIN — CONFOUNDED. The arms' spend was not measured (" + string.Join("; ", budget.Reasons)
                    + "), and an unmeasured budget is not an equal one.",
                reasons);
        }

        if (!LoopIsLoadBearing(versusRubberStamp))
        {
            return versusRubberStamp.Undecidable
                ? new Eval09Verdict(Eval09Outcome.NotComparableAtEqualK,
                    $"NO WINNER — the LOOP-IS-LOAD-BEARING control was NOT COMPARABLE. All "
                  + $"{versusRubberStamp.Excluded.Count} persona(s) were refused at equal k against the rubber-stamp "
                  + "reviewer, so the clause that would void an architecture claim was never evaluated. An absent "
                  + "control is not a cleared one.",
                    reasons)
                : new Eval09Verdict(Eval09Outcome.LoopNotLoadBearing,
                    "NO WIN — a reviewer that RUBBER-STAMPS round 1 led the real loop. Any architecture claim is void.",
                    reasons);
        }

        // ⚠ BEFORE any p-value is read. An empty sign test returns p = 1.0000, and 1.0000 read as
        // "the arms agree" is the flattering misreading of a comparison that was never made.
        if (primary.Undecidable)
        {
            return new Eval09Verdict(Eval09Outcome.NotComparableAtEqualK,
                $"NO WINNER — NOT COMPARABLE. All {primary.Excluded.Count} persona(s) were refused at equal k: the "
              + "arms presented different numbers of items, or a side was silent. A comparison that could not be "
              + "made has no direction.",
                reasons);
        }

        if (primary.PValue < PrimaryAlpha && primary.ChallengerLeads)
        {
            return new Eval09Verdict(Eval09Outcome.WorkflowWins,
                $"WORKFLOW WINS — every clause of the pre-registered rule held (p = {primary.PValue:F4}).", reasons);
        }

        if (primary.PValue < PrimaryAlpha && primary.Losses > primary.Wins)
        {
            return new Eval09Verdict(Eval09Outcome.SingleAgentWins,
                $"SINGLE AGENT WINS — the mirror rule held, in the direction the design did not predict "
              + $"(p = {primary.PValue:F4}).", reasons);
        }

        return new Eval09Verdict(Eval09Outcome.NoDifferenceDetected,
            "NO WIN — no difference this design can distinguish from chance.", reasons);
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════
//  INSTRUMENTS
// ══════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One arm's measured model spend. Written by <see cref="MeteredChatClient"/> and by nothing else.
/// </summary>
/// <remarks>
/// A ledger per arm rather than one keyed dictionary, because the clients are constructed once and
/// handed to two different architectures — a shared, keyed ledger would need the caller to say which
/// arm each call belonged to, and the caller is exactly the layer that does not know.
/// </remarks>
public sealed class Eval09TokenLedger
{
    private readonly Lock _gate = new();
    private int _attempted;
    private int _returned;
    private int _cancelled;
    private int _failed;
    private int _returnedWithoutUsage;
    private int _partialUsage;
    private long _prompt;
    private long _completion;
    private int _turns;

    /// <summary>Creates a ledger.</summary>
    /// <param name="name">The label this ledger is reported under.</param>
    public Eval09TokenLedger(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The label this ledger is reported under.</summary>
    public string Name { get; }

    /// <summary>How many model round-trips this arm ATTEMPTED — returned, cancelled or failed alike. Non-zero proves the client reached it.</summary>
    public int Calls { get { lock (_gate) return _attempted; } }

    /// <summary>Attempts that returned a response.</summary>
    public int Returned { get { lock (_gate) return _returned; } }

    /// <summary>
    /// Attempts that were CANCELLED — the 60 s ceiling in <c>DiscoveryModelCall</c>, or a caller's
    /// token. A cancelled call spent tokens the provider never reported and this ledger cannot
    /// see; see <see cref="UsageComplete"/>.
    /// </summary>
    public int Cancelled { get { lock (_gate) return _cancelled; } }

    /// <summary>Attempts that threw anything other than a cancellation.</summary>
    public int Failed { get { lock (_gate) return _failed; } }

    /// <summary>Attempts that returned but carried NO usage from the provider.</summary>
    public int ReturnedWithoutUsage { get { lock (_gate) return _returnedWithoutUsage; } }

    /// <summary>
    /// Calls that returned a usage block with ONE of its two halves missing. Counted separately from
    /// <see cref="ReturnedWithoutUsage"/> on purpose: an absent block and a half-block are different
    /// facts, and folding the missing half in as a zero is how a lower bound gets printed as a spend.
    /// </summary>
    public int PartialUsage { get { lock (_gate) return _partialUsage; } }

    /// <summary>Prompt tokens the provider reported, summed over the calls that reported any.</summary>
    public long PromptTokens { get { lock (_gate) return _prompt; } }

    /// <summary>Completion tokens the provider reported, summed over the calls that reported any.</summary>
    public long CompletionTokens { get { lock (_gate) return _completion; } }

    /// <summary>Prompt plus completion.</summary>
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// True when EVERY attempted call returned AND reported usage — the only state in which this
    /// ledger's total is the arm's spend rather than a lower bound on it.
    /// </summary>
    /// <remarks>
    /// ⚠ MEASURED on the 2026-09-04 Demo 2 live run: 6 of 7 model calls were abandoned at the 60 s
    /// ceiling. The first version of this ledger recorded usage only on a RETURNED response, so
    /// the workflow arm's recorded spend FELL the more it timed out, and the equal-budget guard —
    /// which compares recorded spend — got EASIER to pass precisely when the arm had stopped being
    /// live. A ledger with a cancelled call on it does not know what that arm spent, and a guard
    /// that reads it must say UNMEASURED, never "fewer tokens".
    /// </remarks>
    public bool UsageComplete
    {
        get
        {
            lock (_gate)
                return _attempted > 0 && _cancelled == 0 && _failed == 0 && _returnedWithoutUsage == 0
                    && _partialUsage == 0;
        }
    }

    /// <summary>What is missing from the total, in one clause, or null when nothing is.</summary>
    public string? UsageGap
    {
        get
        {
            lock (_gate)
            {
                if (_attempted == 0) return "made no model call at all";
                var parts = new List<string>(3);
                if (_cancelled > 0) parts.Add($"{_cancelled} cancelled");
                if (_failed > 0) parts.Add($"{_failed} failed");
                if (_returnedWithoutUsage > 0) parts.Add($"{_returnedWithoutUsage} returned without usage");
                if (_partialUsage > 0)
                    parts.Add($"{_partialUsage} returned HALF a usage block (one of prompt/completion missing) — the "
                            + "total is a LOWER BOUND, not a spend");
                return parts.Count == 0
                    ? null
                    : $"{string.Join(", ", parts)} of {_attempted} attempted call(s) — the spend on those is unknown";
            }
        }
    }

    /// <summary>The per-arm accounting line: attempted / returned / cancelled / failed / no-usage.</summary>
    public string Accounting
    {
        get
        {
            lock (_gate)
                return $"attempted {_attempted} · returned {_returned} · cancelled {_cancelled} · failed {_failed} · "
                     + $"returned without usage {_returnedWithoutUsage} · half a usage block {_partialUsage}";
        }
    }

    /// <summary>How many GRADED TURNS this arm ran — the denominator of the per-turn figure.</summary>
    /// <remarks>
    /// Counted by the eval, not by the client, and that distinction is the whole point of the
    /// per-turn number. One graded turn of the workflow is four to seven model calls; one graded
    /// turn of the agent is one call plus its tool loop. Dividing by CALLS would report the price of
    /// a call, which the workflow would win trivially by making more, smaller ones. Dividing by
    /// TURNS reports the price of an answer, which is what the customer pays for.
    /// </remarks>
    public int Turns { get { lock (_gate) return _turns; } }

    /// <summary>Mean total tokens per graded turn, or 0 when no turn has been counted.</summary>
    public double TokensPerTurn
    {
        get { lock (_gate) return _turns == 0 ? 0.0 : (_prompt + _completion) / (double)_turns; }
    }

    /// <summary>Records one model round-trip that RETURNED, and whatever usage the provider reported.</summary>
    /// <param name="usage">The provider's usage, or null when it reported none — counted as such, never as zero tokens.</param>
    public void RecordReturned(UsageDetails? usage)
    {
        lock (_gate)
        {
            _attempted++;
            _returned++;
            if (usage is null || (usage.InputTokenCount is null && usage.OutputTokenCount is null))
            {
                _returnedWithoutUsage++;
                return;
            }

            // ⚠ "AN ABSENCE IS NOT A ZERO" APPLIES TO EACH HALF, NOT ONLY TO THE BLOCK.
            //
            // This method used to fall straight through to `?? 0` on both sides, so a response
            // carrying an input count and no output count was recorded as a COMPLETE reading with a
            // completion of zero. `ReturnedWithoutUsage` stayed 0, `UsageComplete` stayed true, and
            // the equal-budget clause — the precondition that decides whether this eval may name a
            // winner at all — computed its ratio from a half-measured total and printed it as a
            // measurement. Direction: FLATTERING. It renders a lower bound as a spend.
            //
            // The identical defect was found and fixed in the agent's own meter on 2026-09-06
            // (`ChatSpend.Record`, MEASUREMENT_STATUS §60.2) and was NOT fixed here — in the eval
            // whose entire clause 2 rests on it. A half-block is now its own third state: neither a
            // complete reading nor an absent one.
            if (usage.InputTokenCount is null || usage.OutputTokenCount is null) _partialUsage++;

            _prompt += usage.InputTokenCount ?? 0;
            _completion += usage.OutputTokenCount ?? 0;
        }
    }

    /// <summary>Records one model round-trip that was CANCELLED before it returned. Its spend is unknown.</summary>
    public void RecordCancelled()
    {
        lock (_gate)
        {
            _attempted++;
            _cancelled++;
        }
    }

    /// <summary>Records one model round-trip that threw. Its spend is unknown.</summary>
    public void RecordFailed()
    {
        lock (_gate)
        {
            _attempted++;
            _failed++;
        }
    }

    /// <summary>Records that one GRADED TURN has completed on this arm.</summary>
    public void RecordTurn()
    {
        lock (_gate) _turns++;
    }
}

/// <summary>
/// An <see cref="IChatClient"/> decorator that counts every model round-trip and every token the
/// provider reports, and changes nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the meter has to be here and not anywhere else.</b> The two arms report spend through
/// completely different channels: the single agent's usage reaches
/// <c>TestResult.Performance.PromptTokens</c> through <c>MAFAgentAdapter</c>, while the workflow's
/// <c>DiscoveryModelCall</c> reads <c>response.Text</c> and discards <c>response.Usage</c> entirely,
/// so the workflow arm's <c>Performance</c> would report ZERO tokens for a turn that made seven
/// model calls. Comparing those two numbers would say the workflow is free. One instrument, at the
/// raw client layer, under both architectures, is the only arrangement in which the equal-budget
/// precondition is measurable at all.
/// </para>
/// <para>
/// ⚠ <b>An attempt that does not return is still an attempt.</b> The first version recorded
/// <c>response.Usage</c> after the inner call returned and nothing otherwise, so a call the 60 s
/// ceiling cancelled left no mark on the ledger at all: the arm's recorded spend went DOWN as it
/// timed out, and the equal-budget guard got easier to pass the less live the arm was. Every
/// attempt is now recorded as returned, cancelled or failed, and a ledger with a cancelled or
/// failed call on it is usage-INCOMPLETE — see <see cref="Eval09TokenLedger.UsageComplete"/>.
/// </para>
/// <para>
/// ⚠ It does NOT dispose the client it wraps. The same underlying client may be shared, and a
/// decorator that disposed its inner would take the other arm down with it.
/// </para>
/// </remarks>
public sealed class MeteredChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly Eval09TokenLedger _ledger;

    /// <summary>Wraps a client.</summary>
    /// <param name="inner">The client to meter.</param>
    /// <param name="ledger">Where the counts go.</param>
    public MeteredChatClient(IChatClient inner, Eval09TokenLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(ledger);
        _inner = inner;
        _ledger = ledger;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response;
        try
        {
            response = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ledger.RecordCancelled();
            throw;
        }
        catch
        {
            _ledger.RecordFailed();
            throw;
        }

        _ledger.RecordReturned(response.Usage);
        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long? input = null;
        long? output = null;

        // The enumerator is driven by hand because a `yield` may not sit inside a try/catch, and
        // the cancelled-versus-failed distinction is the whole point of the accounting.
        IAsyncEnumerator<ChatResponseUpdate> updates =
            _inner.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await updates.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _ledger.RecordCancelled();
                    throw;
                }
                catch
                {
                    _ledger.RecordFailed();
                    throw;
                }

                if (!moved) break;
                ChatResponseUpdate update = updates.Current;

                // Usage arrives as a UsageContent, normally on the final update. Accumulated into
                // locals rather than mutated onto a UsageDetails: a provider that reports
                // incrementally must not have its earlier counts silently replaced by its last one,
                // and UsageDetails is built once, at the end, from the totals.
                foreach (UsageContent content in update.Contents.OfType<UsageContent>())
                {
                    if (content.Details.InputTokenCount is { } i) input = (input ?? 0) + i;
                    if (content.Details.OutputTokenCount is { } o) output = (output ?? 0) + o;
                }

                yield return update;
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }

        _ledger.RecordReturned(input is null && output is null
            ? null
            : new UsageDetails { InputTokenCount = input, OutputTokenCount = output });
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Deliberately empty — see the type remarks.
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════
//  ARMS
// ══════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Demo 2's real MAF discovery loop, run on its <b>LIVE</b> path — the arm Eval 02 deliberately does
/// not have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a second type naming a workflow type, when
/// <see cref="RealDiscoveryLoopArm"/>'s remarks say there is exactly one.</b> That statement is
/// about the arm reachable through <see cref="DiscoveryLoopAdapter"/>, which is pinned to
/// <c>Offline: true</c> so that Evals 03 and 04 keep their stated "needs no credentials" property
/// and <c>-- 2 --dry-run</c> keeps spending nothing. Eval 09's entire subject is the LIVE workflow,
/// so it cannot reuse that arm and it deliberately does not Bind itself anywhere: nothing outside
/// this eval can reach this type, and no other eval's numbers can change because it exists.
/// </para>
/// <para>
/// <b>Everything else is the shipped loop.</b> The graph, the five executors, the conditional
/// loop-back edge, the message-borne round counter, the deterministic pre-gate, the two structural
/// approval vetoes, <c>CoverageVerdictProjection</c>, the shipped <c>QueryVocabulary</c>, the shipped
/// query planner, the post-checks and the <c>GuardrailPipeline</c> all run unmodified, through the
/// same <c>GalaxusDiscoveryLoop.RunAsync</c> the demo calls — with the four model-backed stages
/// (mapper, reviewer, ranker, presenter) actually calling the model.
/// </para>
/// <para>
/// <b>The answer reaches the grader through the same channel every other arm uses.</b>
/// <c>DiscoveryState.Presented</c> — what survived the guardrail pipeline, not what the Ranker chose
/// — is replayed as <c>PresentRecommendation</c> calls with the four frozen argument names, so
/// <c>PresentedCall.FromToolUsage</c> reads this arm exactly as it reads the single agent. Replaying
/// the Ranker's selection instead would report items the customer was never shown and would flatter
/// this arm by precisely the number of things the guardrails removed.
/// </para>
/// <para>
/// ⚠ <b>A degraded stage is still counted, and it is still reported.</b> Every model stage falls
/// back to its deterministic implementation when the call fails or its envelope will not parse; the
/// loop is built that way on purpose and it never throws. That means a "live" turn can silently be
/// part deterministic. <see cref="LastRun"/> carries <c>State.DegradedNotes</c> and the eval prints
/// every one of them, because a run whose stages fell back is not the architecture the comparison
/// claims to be pricing.
/// </para>
/// </remarks>
public sealed class LiveDiscoveryWorkflowArm : IEvaluableAgent
{
    private readonly IProductRetriever _retriever;
    private readonly IChatClient _chatClient;

    /// <summary>Builds the arm.</summary>
    /// <param name="retriever">The same bound retriever every other arm searches with.</param>
    /// <param name="chatClient">The METERED chat client. Never <c>null</c>: an unmetered live arm is unmeasurable.</param>
    public LiveDiscoveryWorkflowArm(IProductRetriever retriever, IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(chatClient);
        _retriever = retriever;
        _chatClient = chatClient;
    }

    /// <inheritdoc/>
    public string Name => Eval09_HypothesisComparison.ArmWorkflow;

    /// <summary>The most recent run's full result, or null before the first invocation.</summary>
    public DiscoveryRunResult? LastRun { get; private set; }

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // The customer is read from the PROMPT, exactly as the live agent and every control read it.
        // An arm configured out of band would be running a different experiment from the one it is
        // being paired against.
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

        var recorder = new RecordingDiscoveryProgressSink();

        // ⚠ The UTTERANCE, not the framed prompt. DiscoveryState.SessionRequest is a typed slot for
        // what the customer SAID; the eval's "[session] You are speaking with customer …" header is
        // harness scaffolding. Passing the frame was MEASURED turning that header into a stated-need
        // interest, searching the catalogue for it, retrieving nothing, and tripping the pre-gate on
        // an interest the harness had invented — the arm looked broken and the harness was.
        var options = new WorkflowLoopOptions(
            Offline: false,                                   // ⭐ the whole point of this arm
            PersonalizationDisabled: false,
            SessionRequest: GalaxusEvalPrompt.UtteranceFrom(prompt),
            MaxRounds: DiscoveryState.DefaultMaxDiscoveryRounds,
            ChatClient: _chatClient,                          // ⭐ metered, so the budget is measurable
            Retriever: _retriever,
            Progress: recorder);

        DiscoveryRunResult result = await GalaxusDiscoveryLoop
            .RunAsync(userId, options, cancellationToken)
            .ConfigureAwait(false);

        LastRun = result;

        var trace = new ScriptedTrace();
        foreach (PresentedItem item in result.State.Presented)
            trace.Present(item.ProductId, item.WhyThis, item.Evidence, item.OutOfStock);

        // ModelId IS stamped here, unlike the deterministic arm: a model genuinely ran on this turn,
        // and the number of stages that fell back is reported separately rather than by pretending
        // no model was involved at all.
        return trace.Say(result.State.FinalAnswer).ToResponse(modelId: Config.IsConfigured ? Config.Model : "stub");
    }
}

/// <summary>
/// The measured CHANCE FLOOR for every judged criterion: a fluent, contentless answer that
/// recommends nothing and says the reassuring things.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a floor, never an entrant.</b> It reads no gold, calls no model, and is not paired
/// against anything in the decision rule. Its only job is to answer the question every judged number
/// on the page begs: <i>what does an agent that understands nothing score on this criterion?</i>
/// </para>
/// <para>
/// <b>Why the text is written the way it is.</b> It deliberately volunteers the advisory framing
/// ("I recommend only, you decide"), the hedge ("where I am unsure I will say so") and the
/// price-silence that three of the six criteria ask for, while containing no product, no purchase
/// id and no evidence at all. That is the adversary a criterion list has to survive: not a bad
/// agent, but a well-mannered empty one. A criterion this arm meets on every persona is a criterion
/// that separates nothing, and the judged panel says so on that row.
/// </para>
/// <para>
/// <b>On the primary endpoint it scores a real 0.000</b> — zero of N gold tokens served, at k = 0,
/// against a floor of 0.000, so it is marked below its own floor. That is the arithmetic working, not
/// a special case: an arm that presents nothing has served nothing.
/// </para>
/// </remarks>
public sealed class ContentlessFloorArm : IEvaluableAgent
{
    /// <summary>The answer. Constant for every persona — that is what makes it a floor.</summary>
    public const string Answer =
        "Thank you for getting in touch. I want to be genuinely useful here rather than impressive, so I will be "
      + "careful not to overstate anything. I only ever recommend — you are the one who decides, and nothing I say "
      + "commits you to anything. Where I am not sure about something I will say so plainly in that place rather "
      + "than presenting it with the same confidence as the rest. I have not quoted any price, discount, stock "
      + "level or delivery date, because those belong on the product page where they are kept current. I am also "
      + "conscious of the things I have deliberately not drawn on. Do let me know if you would like me to go further.";

    /// <inheritdoc/>
    public string Name => Eval09_HypothesisComparison.ArmJudgeFloor;

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = prompt;

        // No Present() call at all — the trace is prose and nothing else. ModelId stays null: no
        // model ran, and stamping a deployment name here is the one line a reader would quote as
        // evidence that an agent produced this row.
        return Task.FromResult(new ScriptedTrace().Say(Answer).ToResponse(modelId: null));
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════
//  THE JUDGED REPORT
// ══════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Per-criterion judge verdicts, per persona, per arm — and the paired deltas between two arms.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cell is DECIDABLE only when the judge returned a verdict for every criterion.</b>
/// <c>MAFEvaluationHarness</c> copies the judge's score and rows onto <c>TestResult</c> but not its
/// <c>EvaluationResult.EvaluationFailed</c> flag, so an instrument failure is invisible on the
/// harness's own output — a parse failure arrives as a score of 50 that is indistinguishable from a
/// mediocre grade. This class therefore detects it STRUCTURALLY instead: a working judge always
/// returns one row per criterion, so a cell missing rows is treated as undecidable and excluded.
/// Deliberately conservative in the safe direction — a genuine 50 with a full set of rows is kept.
/// </para>
/// <para>
/// <b>Rows are matched by TEXT first and by position only as a fallback.</b> Matching by position
/// alone assumes the judge echoed the criteria in order, which nothing enforces; matching by text
/// alone breaks the moment a judge paraphrases. When the fallback is used it is recorded and printed,
/// because "we assumed the order" is exactly the kind of assumption that is invisible in a number.
/// </para>
/// </remarks>
public sealed class Eval09JudgedReport
{
    private readonly IReadOnlyList<string> _criteria;
    private readonly Dictionary<(string Persona, string Arm), List<double[]>> _cells = [];

    /// <summary>Creates the report over a fixed criterion list.</summary>
    /// <param name="criteria">The criteria, in the order they are sent to the judge.</param>
    public Eval09JudgedReport(IReadOnlyList<string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count == 0) throw new ArgumentException("A judged report needs at least one criterion.", nameof(criteria));
        _criteria = criteria;
    }

    /// <summary>How many criteria are judged.</summary>
    public int CriterionCount => _criteria.Count;

    /// <summary>How many arm-persona-rep cells returned a verdict for every criterion.</summary>
    public int DecidedCells { get; private set; }

    /// <summary>How many were excluded as undecidable.</summary>
    public int UndecidedCells { get; private set; }

    /// <summary>How many cells had to be matched by POSITION because the judge did not echo the criteria.</summary>
    public int PositionMatchedCells { get; private set; }

    /// <summary>
    /// Records that a cell was excluded because the arm presented nothing, so every criterion would
    /// have been graded over an empty set.
    /// </summary>
    /// <remarks>
    /// It increments the same counter a judge failure does, because from the panel's point of view
    /// they are the same thing — a cell with no usable verdict — and both must be visible. What must
    /// NOT happen is for a vacuous cell to vanish silently: the denominator of every judged rate
    /// would then quietly shrink around the answers that happened to contain something.
    /// </remarks>
    public void NoteVacuousExclusion() => UndecidedCells++;

    /// <summary>A short "4/6" summary of the most recently recorded cell, for the live console line.</summary>
    public string LastMetSummary { get; private set; } = "";

    /// <summary>
    /// Records one cell's verdicts. Returns false when the cell was undecidable and nothing was
    /// recorded.
    /// </summary>
    /// <param name="personaId">The persona.</param>
    /// <param name="arm">The arm label.</param>
    /// <param name="results">The judge's per-criterion rows, or null.</param>
    /// <param name="score">The judge's holistic score, kept only for the console line.</param>
    public bool Record(string personaId, string arm, IReadOnlyList<CriterionResult>? results, int score)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        _ = score;

        if (results is null || results.Count == 0)
        {
            UndecidedCells++;
            LastMetSummary = "";
            return false;
        }

        var met = new double[_criteria.Count];
        var matched = new bool[_criteria.Count];

        for (int i = 0; i < _criteria.Count; i++)
        {
            CriterionResult? row = results.FirstOrDefault(r =>
                string.Equals(r.Criterion?.Trim(), _criteria[i].Trim(), StringComparison.OrdinalIgnoreCase));

            if (row is null) continue;
            met[i] = row.Met ? 1.0 : 0.0;
            matched[i] = true;
        }

        if (matched.Any(m => !m))
        {
            // Fallback: position, and ONLY when the judge returned exactly as many rows as there are
            // criteria. Anything else is a judge that did not answer the question asked.
            if (results.Count != _criteria.Count)
            {
                UndecidedCells++;
                LastMetSummary = "";
                return false;
            }

            PositionMatchedCells++;
            for (int i = 0; i < _criteria.Count; i++) met[i] = results[i].Met ? 1.0 : 0.0;
        }

        if (!_cells.TryGetValue((personaId, arm), out List<double[]>? reps))
            _cells[(personaId, arm)] = reps = [];
        reps.Add(met);

        DecidedCells++;
        LastMetSummary = $"{met.Count(m => m > 0.5)}/{_criteria.Count}";
        return true;
    }

    /// <summary>
    /// One arm's rep-averaged met value for one criterion on one persona, or NaN when the cell was
    /// never decidable.
    /// </summary>
    /// <param name="personaId">The persona.</param>
    /// <param name="arm">The arm.</param>
    /// <param name="criterionIndex">Zero-based criterion index.</param>
    public double CellValue(string personaId, string arm, int criterionIndex) =>
        _cells.TryGetValue((personaId, arm), out List<double[]>? reps) && reps.Count > 0
            ? reps.Average(r => r[criterionIndex])
            : double.NaN;

    /// <summary>One arm's mean met rate for one criterion, over the personas it was decidable on.</summary>
    /// <param name="arm">The arm.</param>
    /// <param name="criterionIndex">Zero-based criterion index.</param>
    public double MetRate(string arm, int criterionIndex)
    {
        var values = _cells
            .Where(kv => string.Equals(kv.Key.Arm, arm, StringComparison.Ordinal))
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => kv.Value.Average(r => r[criterionIndex]))
            .ToList();

        return values.Count == 0 ? double.NaN : values.Average();
    }

    /// <summary>
    /// How many PERSONAS contributed at least one decidable cell for this arm — the denominator
    /// behind every met rate printed for it.
    /// </summary>
    /// <remarks>
    /// ⚠ It is not the same number for every arm and it is not the cohort size. A live-workflow cell
    /// that was VOIDED, an arm that presented nothing, and a judge that returned no usable verdict
    /// each remove a persona from ONE arm's denominator and not the other's. A met rate of 0.000
    /// over nine personas and one over twelve are different statements, and the panel used to print
    /// them identically.
    /// </remarks>
    /// <param name="arm">The arm.</param>
    public int DecidedPersonaCount(string arm) =>
        _cells.Count(kv => string.Equals(kv.Key.Arm, arm, StringComparison.Ordinal) && kv.Value.Count > 0);

    /// <summary>
    /// True when the floor arm produced a defined met rate for EVERY criterion — the precondition for
    /// printing any judged number at all.
    /// </summary>
    /// <param name="floorArm">The floor arm's label.</param>
    public bool FloorIsDefined(string floorArm)
    {
        for (int i = 0; i < _criteria.Count; i++)
            if (double.IsNaN(MetRate(floorArm, i))) return false;
        return true;
    }

    /// <summary>
    /// Paired win / loss / tie counts for one criterion: how many personas the challenger met it on
    /// and the reference did not, and vice versa.
    /// </summary>
    /// <remarks>
    /// Only personas where BOTH arms were decidable enter the count. A pair with one side missing is
    /// dropped rather than imputed — imputing a missing verdict is inventing a judge's opinion.
    /// </remarks>
    /// <param name="reference">The reference arm.</param>
    /// <param name="challenger">The challenger arm.</param>
    /// <param name="criterionIndex">Zero-based criterion index.</param>
    public (int Wins, int Losses, int Ties) PairedCounts(string reference, string challenger, int criterionIndex)
    {
        int wins = 0, losses = 0, ties = 0;

        foreach (string persona in _cells.Keys
                     .Where(k => string.Equals(k.Arm, reference, StringComparison.Ordinal))
                     .Select(k => k.Persona)
                     .Distinct(StringComparer.Ordinal))
        {
            double a = CellValue(persona, reference, criterionIndex);
            double b = CellValue(persona, challenger, criterionIndex);
            if (double.IsNaN(a) || double.IsNaN(b)) continue;

            if (b > a) wins++;
            else if (b < a) losses++;
            else ties++;
        }

        return (wins, losses, ties);
    }
}

/// <summary>What the dry run injects, in one place, so the banner, the stub and the plumbing check cannot disagree.</summary>
public static class Eval09DryRun
{
    /// <summary>
    /// The persona whose InterestMapper call the workflow stub CANCELS on both attempts. Chosen to
    /// be a persona with nothing else special about it in this eval — not Jonas, whose cell is the
    /// second-turn probe on the agent arm — so the two injected events are separable in the output.
    /// </summary>
    public const string CancelledPersonaId = Personas.MirjamUserId;
}

/// <summary>What the dry run has to show the plumbing check, beyond the reports.</summary>
/// <param name="VoidedCells">Every live-workflow cell the VOID rule removed, with why.</param>
/// <param name="SecondTurns">Every single-agent cell whose first turn presented nothing, with what the second turn did.</param>
/// <param name="Budget">The measured budget comparison.</param>
/// <param name="Verdict">The verdict the rule reached.</param>
public sealed record Eval09DryRunEvidence(
    IReadOnlyList<string> VoidedCells,
    IReadOnlyList<(string Cell, ClarifyingTurnOutcome Outcome)> SecondTurns,
    Eval09Budget Budget,
    Eval09Verdict Verdict);

/// <summary>
/// Stamps SYNTHETIC usage — characters ÷ 4 on each side — onto every response the wrapped stub
/// RETURNS, and touches nothing that throws.
/// </summary>
/// <remarks>
/// Dry-run only. It exists so the equal-budget guard can be tested for the ONE property a stub
/// with no usage cannot show: that a cancelled call, and only a cancelled call, makes the spend
/// unmeasured. With every returned call carrying usage, the guard's reason list on a dry run
/// names exactly the injected hole — or a wiring fault. The numbers are meaningless as spend and
/// the budget panel says so.
/// </remarks>
/// <param name="inner">The stub to decorate.</param>
public sealed class Eval09SyntheticUsageClient(IChatClient inner) : IChatClient
{
    private readonly IChatClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        ChatResponse response = await _inner.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);

        response.Usage ??= new UsageDetails
        {
            InputTokenCount = Math.Max(1, list.Sum(m => m.Text.Length) / 4),
            OutputTokenCount = Math.Max(1, (response.Text ?? string.Empty).Length / 4),
        };
        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose() { }
}

/// <summary>
/// The workflow's dry-run stub: answers each of the four model stages with a PARSEABLE envelope
/// built from that stage's own context, so the live code path completes end to end — and CANCELS
/// the InterestMapper call, both attempts, for one named persona.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an envelope stub replaced the prose stub.</b> Once a degraded stage VOIDS its cell, a stub
/// whose every stage degrades voids every cell: the workflow arm has no mean, no judged cell and no
/// pairing, and the dry run can prove nothing about the parts of the instrument that read them. So
/// the stub reads the stage off the user message's own headings — the mapper's <c>PURCHASES</c> /
/// <c>IN-SESSION REQUEST</c>, the reviewer's <c>COVERAGE LEDGER</c>, the ranker's
/// <c>CANDIDATES — the ONLY products you may select</c>, the presenter's
/// <c>SELECTED, GROUPED BY INTEREST</c> — and answers in the shape each parses.
/// </para>
/// <para>
/// <b>Deliberately implausible, still.</b> Interest labels say DRY RUN; query terms are the
/// customer's own purchase tags and leaf categories copied off the message; the reviewer never
/// approves, it writes one gap per interest in a catalogue leaf name (round 2 then repeats the
/// query, the projection refuses the repeat, and the loop exits GapsUnresolvable — so the stub
/// loops exactly once, and P(rounds = 1) = 0 for it); the ranker takes candidates round-robin
/// across interests and cites, for each, a grounding key that RESOLVES by its shape alone (the
/// key half of a <c>key=value</c> token, else a whole <c>prefix:suffix</c> tag — see
/// <c>GroundingKey</c>), so the evidence check lets the item through and the cell PRESENTS; the
/// presenter writes one sentence that names the stub. No stage reads gold, no stage invents a
/// product id, no stage cites a key the context did not list.
/// </para>
/// <para>
/// <b>Why the ranker's key has to resolve.</b> A workflow cell that presents nothing is excluded
/// from the judged panel as vacuous, so it has no judged cell and enters no pair. The scripted
/// judge can only make two arms differ on cells that exist: with the first listed token cited
/// (a spec VALUE, usually numeric) every selection was dropped <c>attribute_not_found</c>, every
/// workflow cell was k = 0, and the delta arithmetic saw zero pairs — not sixty tied ones.
/// </para>
/// <para>
/// <b>The cancellation.</b> On the mapper message carrying <c>CUSTOMER {cancelForPersonaId}</c> the
/// stub throws <see cref="OperationCanceledException"/> — the same exception the 60 s ceiling
/// produces, caught by the same <c>catch</c> in <c>DiscoveryModelCall</c>, degrading the same way.
/// Both attempts, so the mapper falls back and the cell is voided; the meter above records two
/// cancelled attempts; the guard reads them.
/// </para>
/// </remarks>
public sealed partial class Eval09WorkflowStubClient : IChatClient
{
    /// <summary>The presenter prose. Distinct from the agent stub's, so the hashed judge can tell the arms apart.</summary>
    public const string StubProse =
        "DRY RUN — WORKFLOW ENVELOPE STUB. Every structured stage above answered with a parseable envelope built "
      + "from its own context, so the live path ran end to end; no model reasoned about anything. If this sentence "
      + "appears in a report you meant to be real, the run did not reach Azure.";

    private readonly string _cancelForPersonaId;

    /// <summary>Creates the stub.</summary>
    /// <param name="cancelForPersonaId">The persona whose InterestMapper call is cancelled on every attempt.</param>
    public Eval09WorkflowStubClient(string cancelForPersonaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelForPersonaId);
        _cancelForPersonaId = cancelForPersonaId;
    }

    /// <summary>How many calls were CANCELLED by injection. Non-zero proves the injection reached the stub.</summary>
    public int InjectedCancellations { get; private set; }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = options;

        string context = (messages ?? []).Where(m => m.Role == ChatRole.User).Select(m => m.Text).LastOrDefault() ?? string.Empty;

        string text;
        if (context.Contains("IN-SESSION REQUEST", StringComparison.Ordinal) && context.Contains("PURCHASES", StringComparison.Ordinal))
        {
            if (context.Contains($"CUSTOMER {_cancelForPersonaId} ", StringComparison.Ordinal))
            {
                InjectedCancellations++;
                throw new OperationCanceledException(
                    $"DRY RUN — injected cancellation of the InterestMapper call for {_cancelForPersonaId}, standing in "
                  + "for the 60 s model-call ceiling.");
            }

            text = MapperEnvelope(context);
        }
        else if (context.Contains("COVERAGE LEDGER", StringComparison.Ordinal))
        {
            text = ReviewerEnvelope(context);
        }
        else if (context.Contains("CANDIDATES — the ONLY products you may select", StringComparison.Ordinal))
        {
            text = RankerEnvelope(context);
        }
        else
        {
            text = StubProse;
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = "stub",
            FinishReason = ChatFinishReason.Stop,
        });
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose() { }

    // ── Stage 1: the mapper. Two interests from the customer's own purchase lines. ────────

    private static string MapperEnvelope(string context)
    {
        var leaves = new List<string>();
        var tags = new List<string>();

        foreach (System.Text.RegularExpressions.Match m in PurchaseLine().Matches(context))
        {
            string leaf = m.Groups["leaf"].Value.Trim();
            if (leaf.Length > 0 && !leaves.Contains(leaf, StringComparer.OrdinalIgnoreCase)) leaves.Add(leaf);
        }

        foreach (System.Text.RegularExpressions.Match m in UseTagsLine().Matches(context))
        {
            foreach (string tag in m.Groups["tags"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = tag.IndexOf(':');
                if (colon <= 0 || colon >= tag.Length - 1) continue;
                string prefix = tag[..colon];
                if (prefix is not ("context" or "trip" or "use" or "skill" or "season" or "terrain" or "style")) continue;
                string suffix = tag[(colon + 1)..].Replace('-', ' ');
                if (!tags.Contains(suffix, StringComparer.OrdinalIgnoreCase)) tags.Add(suffix);
            }
        }

        var interests = new List<string>();
        if (leaves.Count > 0)
            interests.Add(Interest("DRY RUN INTEREST — the leaf categories on the account", "DIRECT", leaves.Take(3)));
        if (tags.Count > 0)
            interests.Add(Interest("DRY RUN INTEREST — the use tags on the account", "LATENT", tags.Take(3)));
        if (interests.Count == 0)
        {
            // No purchases in the message (the opt-out shape): one interest from the request line.
            var request = RequestLine().Match(context);
            string need = request.Success ? request.Groups["text"].Value : "headphones";
            interests.Add(Interest("DRY RUN INTEREST — the in-session request", "DIRECT", [need]));
        }

        return $$"""{"interests":[{{string.Join(",", interests)}}],"anti_interests":[],"constraints":[],"summary":"DRY RUN — a stub map copied off the purchase lines; nothing was inferred."}""";

        static string Interest(string label, string kind, IEnumerable<string> terms) =>
            $$$"""{"label":{{{Json(label)}}},"kind":"{{{kind}}}","confidence":0.8,"evidence":[],"rationale":"DRY RUN — copied from the message, not reasoned.","query_terms":[{{{string.Join(",", terms.Select(Json))}}}],"category_hints":[],"attribute_hints":{}}""";
    }

    // ── Stage 3: the reviewer. Never approves; one gap per interest, in the catalogue's words. ──

    private static string ReviewerEnvelope(string context)
    {
        var interestIds = InterestMapLine().Matches(context).Select(m => m.Groups["id"].Value).Distinct(StringComparer.Ordinal).ToList();

        // Leaf names off the candidates the reviewer was actually shown, per interest, and one
        // fallback leaf for an interest that has none — the catalogue's own vocabulary either way.
        var leafByInterest = new Dictionary<string, string>(StringComparer.Ordinal);
        string? anyLeaf = null;
        foreach (System.Text.RegularExpressions.Match m in ReviewerCandidateLine().Matches(context))
        {
            string leaf = m.Groups["leaf"].Value.Trim();
            string forId = m.Groups["for"].Value;
            if (leaf.Length == 0) continue;
            anyLeaf ??= leaf;
            leafByInterest.TryAdd(forId, leaf);
        }

        var gaps = new List<string>();
        foreach (string id in interestIds)
        {
            string leaf = leafByInterest.GetValueOrDefault(id) ?? anyLeaf ?? "Photography";
            gaps.Add($$"""{"interest_id":{{Json(id)}},"why_uncovered":"DRY RUN — the stub reviewer never approves; it asks for one more look in the catalogue's own leaf name.","next_query":{{Json(leaf)}},"next_category":null,"next_attributes":null}""");
        }

        return $$"""{"covered_interest_ids":[],"gaps":[{{string.Join(",", gaps)}}],"new_interest":null,"stop_reason":"GAPS_REMAIN","assessment":"DRY RUN — scripted verdict, one gap per interest, no judgement."}""";
    }

    // ── Stage 4: the ranker. Candidates round-robin across interests, a key that resolves. ──

    private static string RankerEnvelope(string context)
    {
        var byInterest = new Dictionary<string, Queue<(string Id, string Key)>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (System.Text.RegularExpressions.Match m in RankerCandidateLine().Matches(context))
        {
            string id = m.Groups["id"].Value;
            string forId = m.Groups["for"].Value;
            string key = GroundingKey(m.Groups["keys"].Value);
            if (key.Length == 0) continue;

            if (!byInterest.TryGetValue(forId, out var queue))
            {
                byInterest[forId] = queue = new Queue<(string, string)>();
                order.Add(forId);
            }
            queue.Enqueue((id, key));
        }

        var selections = new List<string>();
        bool any = true;
        while (any && selections.Count < 5)
        {
            any = false;
            foreach (string forId in order)
            {
                if (selections.Count >= 5) break;
                if (!byInterest[forId].TryDequeue(out var pick)) continue;
                any = true;
                selections.Add($$"""{"product_id":{{Json(pick.Id)}},"interest_id":{{Json(forId)}},"why_this":"DRY RUN — a stub selection taken from the candidate list in order; no model reasoned about fit.","grounding_attribute_key":{{Json(pick.Key)}},"grounding_review_id":null}""");
            }
        }

        return $$"""{"selections":[{{string.Join(",", selections)}}]}""";
    }

    /// <summary>
    /// A grounding key that RESOLVES on the product, chosen from the tokens the ranker context
    /// listed for it — by token SHAPE, because the list carries no other signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context's <c>attribute keys:</c> line is <c>Product.Attributes</c> — every tag, every tag
    /// suffix, every spec key, every spec VALUE and every <c>key=value</c> pair — sorted ordinal and
    /// cut at fourteen, so its first entry is usually a numeric spec value such as <c>230-g</c>.
    /// <c>Product.TryGetAttributeValue</c> resolves a spec key, a whole tag or a tag prefix and
    /// nothing else. MEASURED (2026-09-05): citing the first token put every one of the sixty
    /// dry-run selections through <c>attribute_not_found</c>, every workflow cell presented k = 0,
    /// every workflow judged cell was excluded as vacuous, and the plumbing check had no pair to
    /// count — which it printed as "every pair tied".
    /// </para>
    /// <para>
    /// Two shapes resolve by construction: the key half of a <c>key=value</c> token is a spec key,
    /// and a <c>prefix:suffix</c> token is a whole tag. Anything else is cited as-is, so a product
    /// whose first fourteen tokens carry neither still fails the evidence check the way it should —
    /// the stub is not allowed to invent a key the context did not show it.
    /// </para>
    /// </remarks>
    /// <param name="listedTokens">The comma-separated token list from the candidate's <c>attribute keys:</c> line.</param>
    private static string GroundingKey(string listedTokens)
    {
        string[] tokens = listedTokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            int equals = token.IndexOf('=');
            if (equals > 0) return token[..equals];
        }

        foreach (string token in tokens)
        {
            if (token.IndexOf(':') > 0) return token;
        }

        return tokens.FirstOrDefault() ?? string.Empty;
    }

    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    // "  PUR-XX-01  2025-04-11  Product Name  ·  Root > Group > Leaf"
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s{2}PUR-[A-Z]{2}-\d{2}\s+\d{4}-\d{2}-\d{2}\s+.+?\s+·\s+(?<path>[^\r\n]+?)>\s*(?<leaf>[^>\r\n]+)$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PurchaseLine();

    // "      use tags: context:multi-day, trip:hut-to-hut, …"
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s+use tags:\s*(?<tags>[^\r\n]*)$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UseTagsLine();

    // '  "the customer's sentence"' under IN-SESSION REQUEST
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s{2}""(?<text>[^""\r\n]+)""\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex RequestLine();

    // "  I-1  LATENT  0.80  label" — the INTEREST MAP rows of the reviewer and ranker contexts
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s{2}(?<id>I-\d+)\s+(?:DIRECT|LATENT)\s+\d\.\d\d\s", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex InterestMapLine();

    // "  GLX-1234  Title  ·  Root > Group > Leaf  (for I-1, score 0.1234)" — reviewer context
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s{2}(?<id>GLX-\d{4})\s+.+?\s+·\s+(?<path>[^\r\n(]+?)>\s*(?<leaf>[^>\r\n(]+?)\s+\(for\s+(?<for>I-\d+),", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ReviewerCandidateLine();

    // "  GLX-1234  Title  ·  path  (for I-1, score …, n rating(s))\n      attribute keys: a, b, c" — ranker context
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s{2}(?<id>GLX-\d{4})\s+.+?\(for\s+(?<for>I-\d+),[^\r\n]*\r?\n\s+attribute keys:\s*(?<keys>[^\r\n]*)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex RankerCandidateLine();
}

/// <summary>
/// A dry-run stub that returns one fixed, deliberately implausible line of prose and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the workflow's model stages contract to emit a JSON envelope, and a stub that
/// emitted tool calls for tools those stages do not register would probe MEAI's function-invocation
/// middleware rather than this eval. Prose that will not parse exercises the call, the meter and the
/// documented fall-back to the deterministic implementation — which is the behaviour the dry run
/// USED to confirm was wired. Eval 09's dry run no longer uses it: once a degraded stage VOIDS its
/// cell, a stub that degrades every stage voids every cell, and
/// <see cref="Eval09WorkflowStubClient"/> replaced it. Kept as the simplest all-degrading probe.
/// </para>
/// <para>
/// <b>Its text differs from <see cref="StubChatClient.StubText"/> on purpose.</b> With both arms
/// emitting the same bytes the two arms' answers were identical, so every paired comparison —
/// deterministic and judged — tied by construction and the win/loss branches were never reached.
/// Two stubs that a downstream instrument cannot tell apart make that instrument untestable.
/// </para>
/// </remarks>
/// <param name="text">The prose to return for every call.</param>
public sealed class Eval09ProseStubClient(string text) : IChatClient
{
    /// <summary>The workflow arm's dry-run prose. Unmistakable, and distinct from the agent stub's.</summary>
    public const string WorkflowStubText =
        "DRY RUN — WORKFLOW STUB. This prose stood in for the discovery loop's model stages; no model ran. Every "
      + "structured stage upstream of it failed to parse this text as its JSON envelope and fell back to its "
      + "deterministic implementation, which is one of the things this run exists to confirm is wired. If this "
      + "sentence appears in a report you meant to be real, the run did not reach Azure.";

    private readonly string _text = text ?? throw new ArgumentNullException(nameof(text));

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = messages;
        _ = options;

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text))
        {
            ModelId = "stub",
            FinishReason = ChatFinishReason.Stop,
        });
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose() { }
}

/// <summary>
/// A scripted judge for the dry run: emits a well-formed verdict envelope with no model behind it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists so the dry run can prove the judge path is REACHABLE</b> — the one property this
/// eval was written to establish, and the one a stub that returned prose could not exercise at all:
/// <c>ChatClientEvaluator</c> would fail to parse, return its 50-point fallback with no rows, and
/// every cell would come out undecidable. The dry run would then pass while proving the opposite of
/// what it claims.
/// </para>
/// <para>
/// <b>Deliberately arbitrary, and it says so.</b> Each criterion's verdict is decided by a hash of
/// the answer text, so the two arms genuinely differ and the delta arithmetic is exercised — and so
/// that no dry-run judged number can be mistaken for an opinion about anything.
/// </para>
/// <para>
/// <b>What it cannot do.</b> It can only separate two arms on a cell that reaches the judged panel.
/// The eval excludes a cell that presented nothing as vacuous BEFORE the verdict is recorded, so an
/// arm whose stub never gets an item through the guardrails has no judged cell, whatever this
/// class returns for it. Two verdicts that are never paired are not a tie, and a plumbing line that
/// says "every pair tied" over an arm with no cells is reading the wrong cause.
/// </para>
/// </remarks>
public sealed class Eval09ScriptedJudgeClient : IChatClient
{
    private const string Marker = "DRY RUN — SCRIPTED VERDICT, decided by hashing the answer text. Not an opinion.";

    private readonly IReadOnlyList<string> _criteria;

    /// <summary>Creates the scripted judge.</summary>
    /// <param name="criteria">The criteria it will emit a row for.</param>
    public Eval09ScriptedJudgeClient(IReadOnlyList<string> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        _criteria = criteria;
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = options;

        // The judge prompt carries the answer under evaluation, so hashing it makes two different
        // answers produce two different verdicts. FNV-1a rather than string.GetHashCode: the latter
        // is randomised per process in .NET, and a dry run whose verdicts change between two
        // invocations of the same binary would be a stub that cannot be reasoned about.
        string prompt = string.Join('\n', (messages ?? []).Select(m => m.Text));
        int seed = (int)(Fnv1a(prompt) & 0x7fffffff);

        var rows = new List<string>(_criteria.Count);
        int metCount = 0;
        for (int i = 0; i < _criteria.Count; i++)
        {
            bool met = ((seed >> i) & 1) == 1;
            if (met) metCount++;
            rows.Add($$"""{"criterion":{{System.Text.Json.JsonSerializer.Serialize(_criteria[i])}},"met":{{(met ? "true" : "false")}},"explanation":{{System.Text.Json.JsonSerializer.Serialize(Marker)}}}""");
        }

        int score = _criteria.Count == 0 ? 0 : (int)Math.Round(100.0 * metCount / _criteria.Count);
        string json = $$"""
            {"criteriaResults":[{{string.Join(",", rows)}}],"overallScore":{{score}},"summary":{{System.Text.Json.JsonSerializer.Serialize(Marker)}},"improvements":[]}
            """;

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = "scripted-judge",
            FinishReason = ChatFinishReason.Stop,
        });
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <summary>FNV-1a over UTF-16 code units. Stable across processes, unlike string.GetHashCode.</summary>
    /// <param name="text">The text to hash.</param>
    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}
