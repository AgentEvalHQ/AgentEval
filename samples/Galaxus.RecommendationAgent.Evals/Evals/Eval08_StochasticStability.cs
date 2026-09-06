// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// SNAPSHOT-POLICY: deliberately-none nothing in this suite consumes a stability snapshot, and a number in a shared store that no gate reads is a hazard a later reader can mistake for one that is (Eval08:316-319)

using System.Globalization;
using AgentEval.Assertions;
using AgentEval.MAF;
using Azure.AI.OpenAI;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Evals.Adapters;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

// ⚠ DiscoveryLoopOptions exists TWICE — Workflows' record configures a MAF workflow, Evals.Loop's
// configures the deterministic loop substrate the controls are built on, and GlobalUsings.cs puts
// both namespaces in scope for every file in this project. Aliasing rather than renaming keeps each
// definition where its own remarks explain it; RealDiscoveryLoopArm carries the same alias for the
// same reason.
using WorkflowLoopOptions = Galaxus.RecommendationAgent.Workflows.DiscoveryLoopOptions;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 08 — Repeated-Run Stability. The same customer, the same sentence, N times, on BOTH
/// architectures: how much of the answer survives a reload?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a product measurement and not a curiosity.</b> A customer who reloads the
/// recommendation panel and sees five entirely different products does not conclude that the
/// system explored; they conclude that it guessed. Run-to-run variance is therefore a first-class
/// property of a recommender, and it is invisible in every other eval in this suite: Eval 01 grades
/// one turn per case, Eval 02 averages repetitions into one observation per persona, and an average
/// is exactly the operation that destroys the quantity this eval exists to report.
/// </para>
///
/// <para><b>═══ VARIANCE IS NOT AUTOMATICALLY A DEFECT ═══</b></para>
/// <para>
/// This eval refuses to print a single "stability score", because the five things it measures do
/// not point the same way and collapsing them would hide that:
/// </para>
/// <list type="bullet">
///   <item><description><b>Top recommendation — SHOULD be stable.</b> The lead item is the one the
///   customer reads. A lead that changes on every reload is a lottery, and it is the only quantity
///   here that is GATED.</description></item>
///   <item><description><b>Set overlap (Jaccard) — should be HIGH BUT NOT 1.000.</b> A recommender
///   whose five products are byte-identical on every run has stopped exploring; one whose sets
///   barely intersect has no stable view of the customer. Reported with both degenerate ends named,
///   never gated.</description></item>
///   <item><description><b>Rank order below the lead — mildly desirable, not gated.</b> Positions
///   3 and 4 swapping is a cosmetic difference; position 1 changing is not.</description></item>
///   <item><description><b>Number of recommendations — low variance is a UI property.</b> Three
///   items one time and nine the next is a layout problem, not an inference problem. Reported.</description></item>
///   <item><description><b>Rounds taken (workflow only) — variance here is HEALTH, not noise.</b>
///   A loop that always stops at round 1 is the rubber-stamp pathology design §D.3 names; a loop
///   that always hits the cap is the opposite pathology. A distribution with mass in the middle is
///   the good outcome, so "stable" would be the wrong word for it entirely.</description></item>
/// </list>
///
/// <para><b>═══ WHAT IT MEASURES AND WHAT IT DOES NOT ═══</b></para>
/// <para>
/// <b>Both arms are LIVE and model-backed.</b> Arm A is the shipped single agent. Arm B is Demo 2's
/// MAF discovery workflow on its <b>model-backed</b> path — deliberately NOT the deterministic arm
/// that <see cref="RealDiscoveryLoopArm"/> binds into Evals 02 and 04. That arm is pinned to
/// <c>Offline: true</c> by design, and a deterministic arm's run-to-run stability is 1.000 by
/// construction: printing that number under a "workflow stability" heading would measure the
/// absence of a model and report it as a property of the workflow. When no credentials are present
/// this eval prints <b>not measured</b> and stops; it never substitutes the free arm for the paid
/// one.
/// </para>
/// <para>
/// <b>No LLM judges the recommendations, and that is a decision with a reason.</b> Judging N runs
/// would add the judge's own run-to-run variance to the agent's, and the two are not separable from
/// a single number — a variance measurement whose instrument is itself variable reports instrument
/// noise as product instability. So the recommendation metrics here are set-theoretic and
/// deterministic, and the LLM judge appears in the ONE place where it answers a question about
/// itself: the <b>judge-replication arm</b> re-grades a single FIXED agent answer N times and
/// reports the spread of the instrument. That measurement needs no calibrated judge — only a judge
/// that is meant to be deterministic — which is why it is the one judged number in this file.
/// </para>
/// <para>
/// ⏱️ Runtime: roughly 15-30 minutes at the defaults (5 runs × 2 personas × 2 live arms, plus 5
/// judge calls); 5-12 minutes with <c>--quick</c> (4 runs × 1 persona). 💰 Cost: reported per arm,
/// never gated.
/// </para>
/// </remarks>
public static class Eval08_StochasticStability
{
    // ══ Parameters ════════════════════════════════════════════════════════════════════════

    /// <summary>Repetitions per persona per arm at the defaults.</summary>
    public const int DefaultRuns = 5;

    /// <summary>Repetitions per persona per arm under <c>--quick</c>.</summary>
    public const int QuickRuns = 4;

    /// <summary>
    /// The smallest run count at which the gate carries information.
    /// </summary>
    /// <remarks>
    /// At N = 3 the only attainable modal shares are 0.333, 0.667 and 1.000, so a 0.75 threshold is
    /// arithmetically identical to demanding unanimity — a different, much stricter test wearing
    /// this one's label. <see cref="StochasticOptions.Validate"/> already refuses fewer than 3; this
    /// eval refuses fewer than 4, and says so rather than quietly reporting a gate that means
    /// something else.
    /// </remarks>
    public const int MinimumRuns = 4;

    /// <summary>Personas scored at the defaults.</summary>
    public const int DefaultPersonaCount = 2;

    /// <summary>Personas scored under <c>--quick</c>.</summary>
    public const int QuickPersonaCount = 1;

    /// <summary>
    /// The gated threshold: the modal lead product must appear in at least this fraction of runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why 0.75 and not 1.00.</b> Requiring an identical lead on every run gates against any
    /// exploration at all, and this eval's stated position is that some churn is a feature. 0.75
    /// means "at least three reloads in four show the same lead", which is the weakest majority that
    /// still rules out the lead being a coin flip between two candidates.
    /// </para>
    /// <para>
    /// <b>Why not lower.</b> Below 0.5 the modal item is not even a majority, and the threshold has
    /// to stay strictly above half for the chance floor in
    /// <see cref="ModalShareChanceFloor(int, int, int)"/> to be exactly computable at all — at or
    /// below half, two products can each hold a plurality and the events stop being mutually
    /// exclusive.
    /// </para>
    /// </remarks>
    public const double TopItemThreshold = 0.75;

    /// <summary>Arm A's label.</summary>
    public const string ArmAgent = "Single Agent (Robin) — live";

    /// <summary>Arm B's label. The word LIVE is load-bearing: this is not the deterministic arm.</summary>
    public const string ArmWorkflow = "Discovery Workflow (Demo 2) — LIVE, model-backed";

    // ══ Entry point ═══════════════════════════════════════════════════════════════════════

    /// <summary>Runs the eval.</summary>
    /// <param name="runs">
    /// Repetitions per persona per arm. Null takes <see cref="DefaultRuns"/>, or
    /// <see cref="QuickRuns"/> under <paramref name="quick"/>. Fewer than
    /// <see cref="MinimumRuns"/> is refused.
    /// </param>
    /// <param name="quick">Fewer runs and one persona instead of two.</param>
    /// <param name="dryRun">
    /// Replace both arms' models with a deliberately implausible stub. Spends nothing, exercises the
    /// stochastic runner, both arms, the metrics, the floors, the printer and the gate, and writes
    /// nothing. The stub sequence is CHOSEN so the expected metric values are known in advance, so
    /// this dry run can and does fail on a wrong answer rather than only on a crash.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 0 when the gate passed, 1 when it failed (or the instrument self-check did), 2 on a bad run
    /// count, 3 when credentials are missing and nothing was measured. ⚠ The <c>ci</c> parameter is
    /// GONE — see <see cref="CredentialGuard"/>.
    /// </returns>
    public static async Task<int> RunAsync(
        int? runs = null,
        bool quick = false,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        PrintHeader();
        PrintDesirabilityTable();

        int n = runs ?? (quick ? QuickRuns : DefaultRuns);
        if (n < MinimumRuns)
        {
            EvalPrinter.PrintRefusal(
                $"Eval 08 refused to run at {n} repetition(s).",
                $"The gate reads a modal share against a threshold of {TopItemThreshold:P0}. At N = {n} the "
              + $"attainable shares are {string.Join(", ", Enumerable.Range(1, Math.Max(n, 1)).Select(i => (i / (double)Math.Max(n, 1)).ToString("F3", CultureInfo.InvariantCulture)))}, "
              + $"so that threshold is arithmetically identical to demanding unanimity — a stricter test wearing this "
              + $"one's label. Run with at least {MinimumRuns} repetitions, or read Eval 02 for a single-turn measure.");
            return 2;
        }

        int personaCount = Math.Max(1, quick ? QuickPersonaCount : DefaultPersonaCount);
        var personas = CoveragePersonas.All.Take(personaCount).ToList();

        // ── The instrument is checked BEFORE anything is spent, in both directions. ──────
        //
        // Standing rule in this repository: extreme values are wiring faults until shown otherwise.
        // A similarity metric that returns 1.000 for everything would make every arm perfectly
        // stable, and a metric that returns 1.000 for two EMPTY sets would make a silent agent the
        // most stable of all. Both are checked here, against hand-written expectations that no part
        // of the run under test supplies.
        var selfCheck = InstrumentSelfCheck();
        PrintSelfCheck(selfCheck);
        if (!selfCheck.All(c => c.Passed))
        {
            EvalPrinter.PrintRefusal(
                "Eval 08 refused to run: its own similarity metrics are wrong.",
                "Every number this eval prints is a function of those metrics. Publishing them over a broken "
              + "instrument is how a correction gets issued from the same broken instrument that caused it.");
            return 1;
        }

        // ⚠️ HONESTY GATE — CredentialGuard, the one place the rule lives. The eval-specific half
        // of the message stays here, because the tempting substitution is specific to this eval.
        if (CredentialGuard.Blocks(
                "Eval 08", "Run-to-run stability of both live architectures", dryRun)
            is { } noCredentials)
        {
            PrintNoSubstitution();
            return noCredentials;
        }

        if (dryRun) PrintDryRunBanner();
        else { Config.PrintAzureTarget(); Console.WriteLine(); }

        IProductRetriever retriever = await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);

        PrintDerivedFloors(n);

        // One transport for both live arms: the same deployment, the same endpoint, so a difference
        // between the arms is architecture and not configuration.
        IChatClient? liveClient = dryRun
            ? null
            : new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential)
                .GetChatClient(Config.Model).AsIChatClient();

        // ⚠ NO evaluator on this harness. The recommendation metrics are set-theoretic; the ONE
        // judged number in this eval is produced by a ChatClientEvaluator called directly, further
        // down, on a fixed input. Passing an evaluator here would flip TestResult.Passed into a
        // judge's holistic number for a run whose criteria list is empty anyway.
        var harness = new MAFEvaluationHarness(verbose: false);

        var options = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            EvaluateResponse = false,
            Verbose = false,
            ModelName = dryRun ? "(stub — dry run)" : Config.Model,
        };

        Console.WriteLine($"  N = {n} repetition(s) per persona · {personas.Count} persona(s) · 2 architectures");
        Console.WriteLine($"  Personas: {string.Join(", ", personas.Select(p => $"{p.Id} {p.Name}"))}");
        Console.WriteLine();

        // ── Arm A — the single agent ────────────────────────────────────────────────────
        ArmStability agentArm = await RunArmAsync(
            ArmAgent,
            personas,
            n,
            harness,
            options,
            runIndex => BuildAgentAgent(runIndex, liveClient, dryRun),
            armIndex: null,
            ct).ConfigureAwait(false);

        // ── Arm B — the discovery workflow, model-backed ────────────────────────────────
        var workflowArms = new List<Eval08LiveWorkflowArm>();
        ArmStability workflowArm = await RunArmAsync(
            ArmWorkflow,
            personas,
            n,
            harness,
            options,
            _ =>
            {
                var arm = new Eval08LiveWorkflowArm(
                    retriever,
                    dryRun ? Eval08Stubs.DegradingWorkflowClient() : liveClient!,
                    dryRun);
                workflowArms.Add(arm);
                return arm;
            },
            armIndex: workflowArms,
            ct).ConfigureAwait(false);

        // ── Report ──────────────────────────────────────────────────────────────────────
        PrintArm(agentArm, n);
        PrintArm(workflowArm, n);
        PrintRoundsDistribution(workflowArm);
        PrintSpend(agentArm);
        PrintSpend(workflowArm);

        // Item 8.17. The line above reports what the HARNESS saw at this arm's boundary, which on a
        // replayed answer is an estimate of the wrong text; this one reports what the LOOP was
        // billed, read from the provider's usage blocks inside it.
        PrintWorkflowChatSpend(workflowArm);

        // ── The one judged number: the JUDGE's own variance, on a fixed input ────────────
        JudgeReplication? judge = null;
        if (!dryRun && liveClient is not null)
        {
            judge = await RunJudgeReplicationAsync(agentArm, workflowArm, n, liveClient, ct).ConfigureAwait(false);
            PrintJudgeReplication(judge, n);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Judge replication: NOT MEASURED in a dry run — it needs a real judge to have a variance.");
            Console.ResetColor();
            Console.WriteLine();
        }

        // ── Gate ────────────────────────────────────────────────────────────────────────
        if (dryRun)
        {
            var plumbing = DryRunPlumbing(agentArm, workflowArm, n);
            PrintDryRunVerdict(plumbing);

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Nothing written. A stub's stability number sitting in a store under a real key would "
                            + "read later as a measurement, and nothing about the JSON would say 'stub'.");
            Console.ResetColor();

            return plumbing.All(p => p.Passed) ? 0 : 1;
        }

        bool passed = PrintGate(agentArm, workflowArm, n);
        PrintWhatThisDoesNotProve();

        // No snapshot is written even on a live run, and that is deliberate rather than an omission:
        // nothing in this suite consumes a stability snapshot, and EvalResultStore's typed records
        // are read by Eval 03's comparison. Adding a record here would put a number in a shared
        // store that no gate reads and that a later reader could mistake for one that is.
        return passed ? 0 : 1;
    }

    // ══ Arms ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs one arm over every persona and projects each repetition onto a
    /// <see cref="RunObservation"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="StochasticRunner"/> drives the repetitions, and three of its properties are
    /// worked around here rather than around.</b>
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Evaluation options go in the CONSTRUCTOR</b>, not into
    ///   <c>RunStochasticTestAsync</c>. The runner ignores per-call options entirely, so a
    ///   <c>ModelName</c> passed the obvious way silently produces runs with no cost data at
    ///   all.</description></item>
    ///   <item><description><b>The runner calls <c>RunEvaluationAsync</c>, never the streaming
    ///   path</b>, so tool records carry no timing and every duration-based tool assertion would
    ///   SILENTLY SKIP. None is made here; the durations reported are the harness's own wall-clock
    ///   figures, which the non-streaming path does record.</description></item>
    ///   <item><description><b>The per-turn tool scopes have to be opened per RUN.</b> The runner
    ///   invokes the agent itself, so wrapping the whole call in one
    ///   <c>EvalRuntime.BeginTurn()</c> would give N runs ONE 24-call budget and one presentation
    ///   capture — the later runs would starve and their silence would be scored as instability.
    ///   <see cref="Eval08TurnScopedAgent"/> opens the scope inside <c>InvokeAsync</c> instead, so
    ///   it is per run by construction.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="armLabel">The arm's printed label.</param>
    /// <param name="personas">Personas to score.</param>
    /// <param name="runs">Repetitions per persona.</param>
    /// <param name="harness">The judge-free harness.</param>
    /// <param name="options">Evaluation options — passed to the runner's constructor.</param>
    /// <param name="agentForRun">Builds a FRESH agent for one run. Called once per run, in order.</param>
    /// <param name="armIndex">
    /// When the arm carries workflow telemetry, the list its factory appends to. Zipped positionally
    /// with the results; a length mismatch DROPS the telemetry rather than mis-attributing it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<ArmStability> RunArmAsync(
        string armLabel,
        IReadOnlyList<CoveragePersona> personas,
        int runs,
        MAFEvaluationHarness harness,
        EvaluationOptions options,
        Func<int, IEvaluableAgent> agentForRun,
        List<Eval08LiveWorkflowArm>? armIndex,
        CancellationToken ct)
    {
        var runner = new StochasticRunner(harness, statisticsCalculator: null, evaluationOptions: options);
        var perPersona = new List<PersonaStability>(personas.Count);
        var emptinessFailures = new List<string>();

        foreach (CoveragePersona persona in personas)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"  ─── {armLabel} · {persona.Id} {persona.Name} · {runs} run(s) ───────────");
            Console.ResetColor();

            int created = 0;
            int indexBefore = armIndex?.Count ?? 0;

            var testCase = new TestCase
            {
                Name = $"{armLabel} · {persona.Id}",
                Input = persona.Prompt,

                // ⚠ Deliberately NO EvaluationCriteria — see the type remarks. With none, the
                // harness sets Passed = "the text was not empty" and Score to 0 or 100. Both are
                // read below ONLY under that name; neither is called a quality score anywhere.
                PassingScore = 0,
            };

            var stochasticOptions = new StochasticOptions(
                Runs: runs,
                SuccessRateThreshold: 1.0,          // "every run said SOMETHING" — an emptiness check, not quality
                MaxParallelism: 1,                  // sequential: run i's agent is created before run i+1's
                OnProgress: p => PrintProgress(persona, p));

            var factory = new Eval08AgentFactory(() => agentForRun(created++));

            StochasticResult stochastic;
            try
            {
                stochastic = await runner
                    .RunStochasticTestAsync(factory, testCase, stochasticOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    ❌ the whole {runs}-run block threw: {ex.Message}");
                Console.WriteLine("       This persona contributes NO observation. It is missing from the report "
                                + "rather than scored 0 — an absent measurement is not a bad one.");
                Console.ResetColor();
                continue;
            }

            // The one StochasticAssertion worth making on this arm, and it is about EMPTINESS.
            // Score-shaped assertions (mean, standard deviation, percentiles) are meaningless here:
            // with no criteria every score is exactly 0 or 100, so their "distribution" describes
            // whether text arrived, not how good it was.
            try
            {
                stochastic.Should().HavePassRateAtLeast(1.0,
                    because: "every repetition must produce a non-empty response; a silent run is a failure to "
                           + "answer, and silence must never be counted as agreement with the other runs");
            }
            catch (StochasticAssertionException ex)
            {
                emptinessFailures.Add($"{persona.Id}: {FirstLine(ex.Message)}");
            }

            // Positional zip with the arm index — and it refuses rather than guesses.
            IReadOnlyList<Eval08LiveWorkflowArm>? telemetry = null;
            if (armIndex is not null)
            {
                var slice = armIndex.Skip(indexBefore).ToList();
                telemetry = slice.Count == stochastic.IndividualResults.Count ? slice : null;
                if (telemetry is null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    ⚠️  {slice.Count} arm instance(s) for {stochastic.IndividualResults.Count} "
                                    + "result(s): the workflow telemetry is DROPPED for this persona rather than "
                                    + "attached to the wrong run.");
                    Console.ResetColor();
                }
            }

            var observations = new List<RunObservation>(stochastic.IndividualResults.Count);
            for (int i = 0; i < stochastic.IndividualResults.Count; i++)
            {
                observations.Add(Observe(stochastic.IndividualResults[i], i + 1, telemetry?[i]));
            }

            perPersona.Add(Summarise(persona, observations, runs));
        }

        return new ArmStability(armLabel, perPersona, emptinessFailures);
    }

    /// <summary>Builds one repetition of the single-agent arm.</summary>
    /// <remarks>
    /// In a DRY RUN each repetition gets its own stub with a CHOSEN sku list, so the metrics have a
    /// known right answer — see <see cref="Eval08Stubs.PresentationPlan"/>. In a live run every
    /// repetition gets a fresh <c>ApprovalAwareAgentAdapter</c>, and therefore a fresh MAF session:
    /// the adapter creates one session lazily and reuses it for its whole life, so a shared adapter
    /// would let run 1's conversation prime run 2 and the arm would look more stable than it is.
    /// </remarks>
    /// <param name="runIndex">0-based repetition index.</param>
    /// <param name="liveClient">The shared chat client, or null in a dry run.</param>
    /// <param name="dryRun">True for the stub path.</param>
    private static IEvaluableAgent BuildAgentAgent(int runIndex, IChatClient? liveClient, bool dryRun)
    {
        var chatClient = dryRun
            ? StubChatClient.PresentingAgent(Eval08Stubs.PresentationPlan(runIndex))
            : liveClient ?? throw new InvalidOperationException(
                "A live run reached the agent factory with no chat client. Refusing to run rather than "
              + "silently falling back to a stub and reporting its numbers as the agent's.");

        return new Eval08TurnScopedAgent(new ApprovalAwareAgentAdapter(
            RecommendationAgentFactory.Create(chatClient)));
    }

    /// <summary>Projects one repetition onto the observation the metrics read.</summary>
    /// <param name="result">The harness result.</param>
    /// <param name="run">1-based run number.</param>
    /// <param name="workflow">The workflow arm instance for this run, when there is one.</param>
    private static RunObservation Observe(TestResult result, int run, Eval08LiveWorkflowArm? workflow)
    {
        var presented = PresentedCall.FromToolUsage(result.ToolUsage);

        // Distinct, order preserved. A duplicate presentation of one sku is a different defect
        // (Eval 01 owns it) and counting it twice here would inflate the set size.
        var skus = new List<string>();
        foreach (var call in presented.OrderBy(c => c.Order))
        {
            if (call.Sku.Length > 0 && !skus.Contains(call.Sku, StringComparer.Ordinal)) skus.Add(call.Sku);
        }

        DiscoveryState? state = workflow?.LastResult?.State;

        return new RunObservation(
            Run: run,
            Skus: skus,
            Errored: result.HasError,
            ErrorMessage: result.Error?.Message,
            DurationMs: result.Performance?.TotalDuration.TotalMilliseconds ?? double.NaN,
            PromptTokens: result.Performance?.PromptTokens,
            CompletionTokens: result.Performance?.CompletionTokens,
            TokensEstimated: result.Performance?.TokensAreEstimated ?? true,
            EstimatedCost: result.Performance?.EstimatedCost,
            RoundsTaken: state?.DiscoveryRound,
            StopReason: state?.ResolveStopReason().ToString(),
            ModelCalls: state?.ModelCalls,
            WorkflowSpend: state?.Spend.Snapshot(),
            DegradedNotes: state?.DegradedNotes.Count ?? 0,
            StubTextSeen: result.ActualOutput?.Contains(StubChatClient.StubText, StringComparison.Ordinal) == true,
            Text: result.ActualOutput);
    }

    // ══ The metrics ═══════════════════════════════════════════════════════════════════════

    /// <summary>Summarises one persona's repetitions.</summary>
    /// <param name="persona">The persona.</param>
    /// <param name="observations">Its repetitions, in run order.</param>
    /// <param name="requestedRuns">How many were asked for — a shortfall is itself a finding.</param>
    private static PersonaStability Summarise(
        CoveragePersona persona, IReadOnlyList<RunObservation> observations, int requestedRuns)
    {
        var scored = observations.Where(o => !o.Errored).ToList();
        int errored = observations.Count - scored.Count;
        int silent = scored.Count(o => o.Skus.Count == 0);

        // ── Top-item stability ───────────────────────────────────────────────────────────
        //
        // The denominator is every NON-ERRORED run, silent ones included. A run that presented
        // nothing cannot agree with the modal lead, so it counts against the share rather than
        // being quietly dropped from the denominator — dropping it is the flattering direction and
        // it is exactly how "the agent answered twice and stayed silent three times" becomes
        // "1.000 stable".
        var leads = scored.Where(o => o.Skus.Count > 0).Select(o => o.Skus[0]).ToList();
        string? modal = null;
        double topShare = double.NaN;
        if (scored.Count > 0 && leads.Count > 0)
        {
            var group = leads.GroupBy(s => s, StringComparer.Ordinal)
                             .OrderByDescending(g => g.Count())
                             .ThenBy(g => g.Key, StringComparer.Ordinal)
                             .First();
            modal = group.Key;
            topShare = group.Count() / (double)scored.Count;
        }

        // ── Pairwise set overlap and rank agreement ──────────────────────────────────────
        var jaccards = new List<double>();
        int undefinedJaccard = 0;
        var ranks = new List<double>();
        int undefinedRank = 0;

        for (int i = 0; i < scored.Count; i++)
        {
            for (int j = i + 1; j < scored.Count; j++)
            {
                double jaccard = Jaccard(scored[i].Skus, scored[j].Skus);
                if (double.IsNaN(jaccard)) undefinedJaccard++; else jaccards.Add(jaccard);

                double rank = RankAgreement(scored[i].Skus, scored[j].Skus);
                if (double.IsNaN(rank)) undefinedRank++; else ranks.Add(rank);
            }
        }

        // ── Set size, support and core ───────────────────────────────────────────────────
        var sizes = scored.Select(o => (double)o.Skus.Count).ToList();
        var support = new HashSet<string>(scored.SelectMany(o => o.Skus), StringComparer.Ordinal);
        var answering = scored.Where(o => o.Skus.Count > 0).ToList();
        HashSet<string> core = answering.Count == 0
            ? []
            : new HashSet<string>(answering[0].Skus, StringComparer.Ordinal);
        foreach (var o in answering.Skip(1)) core.IntersectWith(o.Skus);

        int minMatches = MinimumMatches(scored.Count);

        return new PersonaStability(
            PersonaId: persona.Id,
            PersonaName: persona.Name,
            Runs: observations,
            RequestedRuns: requestedRuns,
            ErroredRuns: errored,
            SilentRuns: silent,
            ModalTopSku: modal,
            TopItemStability: topShare,
            MinimumMatchesForGate: minMatches,
            MeanJaccard: Mean(jaccards),
            UndefinedJaccardPairs: undefinedJaccard,
            MeanRankAgreement: Mean(ranks),
            UndefinedRankPairs: undefinedRank,
            MeanSetSize: Mean(sizes),
            SetSizeSd: StandardDeviation(sizes),
            MinSetSize: sizes.Count == 0 ? 0 : (int)sizes.Min(),
            MaxSetSize: sizes.Count == 0 ? 0 : (int)sizes.Max(),
            SupportSize: support.Count,
            CoreSize: core.Count,
            TopItemFloorAtSupport: ModalShareChanceFloor(support.Count, scored.Count, minMatches),
            TopItemFloorAtCatalogue: ModalShareChanceFloor(Catalogue.Default.All.Count, scored.Count, minMatches));
    }

    /// <summary>
    /// Jaccard similarity of two recommendation SETS. <b>Two empty sets are UNDEFINED, never
    /// 1.000.</b>
    /// </summary>
    /// <remarks>
    /// The set-theoretic convention J(∅, ∅) = 1 is the single most dangerous line this eval could
    /// contain. Two runs that both presented nothing agree about nothing; scoring them as perfectly
    /// similar makes a totally silent agent the most stable one in the report, and the failure
    /// points in the flattering direction, which is where they survive review.
    /// </remarks>
    /// <param name="a">First run's skus.</param>
    /// <param name="b">Second run's skus.</param>
    public static double Jaccard(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Count == 0 && b.Count == 0) return double.NaN;

        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        var setB = new HashSet<string>(b, StringComparer.Ordinal);
        int intersection = setA.Count(setB.Contains);
        int union = setA.Count + setB.Count - intersection;
        return union == 0 ? double.NaN : intersection / (double)union;
    }

    /// <summary>
    /// Rank agreement between two runs, over the items they have in common: the fraction of
    /// item-pairs ordered the same way in both. Undefined below two common items.
    /// </summary>
    /// <remarks>
    /// This is Kendall's concordance restricted to the intersection, so it answers "when both runs
    /// showed these two products, did they agree on which came first?" and nothing else. It
    /// deliberately says nothing about items only one run showed — that difference is already the
    /// Jaccard number, and folding it in here would make one disagreement count twice.
    /// The chance floor is 0.500: a coin flip on every comparable pair.
    /// </remarks>
    /// <param name="a">First run's skus, in presentation order.</param>
    /// <param name="b">Second run's skus, in presentation order.</param>
    public static double RankAgreement(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var rankA = Ranks(a);
        var rankB = Ranks(b);
        var common = rankA.Keys.Where(rankB.ContainsKey).OrderBy(k => rankA[k]).ToList();
        if (common.Count < 2) return double.NaN;

        int concordant = 0, discordant = 0;
        for (int i = 0; i < common.Count; i++)
        {
            for (int j = i + 1; j < common.Count; j++)
            {
                int sa = Math.Sign(rankA[common[i]] - rankA[common[j]]);
                int sb = Math.Sign(rankB[common[i]] - rankB[common[j]]);
                if (sa == sb) concordant++; else discordant++;
            }
        }

        int pairs = concordant + discordant;
        return pairs == 0 ? double.NaN : concordant / (double)pairs;

        static Dictionary<string, int> Ranks(IReadOnlyList<string> items)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++) map.TryAdd(items[i], i);
            return map;
        }
    }

    /// <summary>
    /// The chance floor for the gated metric: how often an agent that picks its lead product
    /// UNIFORMLY AT RANDOM from a pool of <paramref name="poolSize"/> lands on the same product in
    /// at least <paramref name="minMatches"/> of <paramref name="runs"/> runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exact, not approximate, and exact only because <paramref name="minMatches"/> is strictly more
    /// than half of <paramref name="runs"/>: at most one product can hold a strict majority, so the
    /// per-product events are mutually exclusive and the union is a plain sum —
    /// <c>P · Σ C(n, j) p^j (1-p)^(n-j)</c> for j from m to n, with p = 1/P. Below half they overlap
    /// and this arithmetic would overstate the floor; the method returns NaN there rather than
    /// printing a number it cannot justify.
    /// </para>
    /// <para>
    /// <b>Two pools are reported, and the tight one is the one that matters.</b> The catalogue-wide
    /// pool is generous — a bigger pool makes coincidence rarer and the floor smaller, which is the
    /// FLATTERING direction. The realised-support pool (the distinct products this arm actually
    /// presented across the run block) asks the harder question: given the products this agent was
    /// choosing among, is its lead a decision or a draw?
    /// </para>
    /// </remarks>
    /// <param name="poolSize">How many products the lead could have been drawn from.</param>
    /// <param name="runs">Repetitions.</param>
    /// <param name="minMatches">How many must share a lead.</param>
    public static double ModalShareChanceFloor(int poolSize, int runs, int minMatches)
    {
        if (runs <= 0 || minMatches <= 0) return double.NaN;
        if (minMatches * 2 <= runs) return double.NaN;    // the exclusivity argument fails — no number.
        if (poolSize <= 1) return 1.0;                    // a one-product pool agrees with itself, always.
        if (minMatches > runs) return 0.0;

        double p = 1.0 / poolSize;
        double total = 0.0;
        for (int j = minMatches; j <= runs; j++)
            total += Binomial(runs, j) * Math.Pow(p, j) * Math.Pow(1.0 - p, runs - j);

        return Math.Min(1.0, poolSize * total);

        static double Binomial(int n, int k)
        {
            double result = 1.0;
            for (int i = 1; i <= k; i++) result = result * (n - k + i) / i;
            return result;
        }
    }

    /// <summary>
    /// An APPROXIMATE chance floor for the mean pairwise Jaccard of two independent uniform k-draws
    /// from a pool of <paramref name="poolSize"/>.
    /// </summary>
    /// <remarks>
    /// It is the ratio of the expectations — E|A ∩ B| / E|A ∪ B| = (k²/P) / (2k - k²/P) — and NOT
    /// the expectation of the ratio, which is what the metric actually averages. Jensen puts the two
    /// apart, so this is labelled an approximation everywhere it is printed and nothing is gated on
    /// it. It is here to answer "is 0.6 overlap impressive?" — against a floor near 0.03 it is.
    /// </remarks>
    /// <param name="poolSize">Pool.</param>
    /// <param name="drawSize">How many items each run presents.</param>
    public static double JaccardChanceFloorApproximate(int poolSize, double drawSize)
    {
        if (poolSize <= 0 || drawSize <= 0) return double.NaN;
        double intersection = drawSize * drawSize / poolSize;
        double union = 2 * drawSize - intersection;
        return union <= 0 ? double.NaN : intersection / union;
    }

    /// <summary>The smallest run count that satisfies <see cref="TopItemThreshold"/> at N runs.</summary>
    /// <param name="runs">Repetitions actually scored.</param>
    public static int MinimumMatches(int runs) =>
        runs <= 0 ? 0 : (int)Math.Ceiling(TopItemThreshold * runs - 1e-9);

    // ══ The instrument self-check ══════════════════════════════════════════════════════════

    /// <summary>
    /// Checks the similarity metrics against hand-written expectations before any money is spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing produced by the run under test appears in this method.</b> The inputs are literals
    /// and so are the expected answers. That is the point: a metric that returns 1.000 for every
    /// input would make every arm look perfectly stable and every number in the report would be
    /// unearned, and the artifact cannot be allowed to supply the evidence that it works.
    /// </para>
    /// <para>
    /// Both directions are covered — the metric must return 1.000 when it should, and must NOT
    /// return 1.000 when it should not, and must return UNDEFINED rather than 1.000 for two silent
    /// runs.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CheckLine> InstrumentSelfCheck()
    {
        string[] abc = ["A", "B", "C"];
        string[] abd = ["A", "B", "D"];
        string[] cba = ["C", "B", "A"];
        string[] xyz = ["X", "Y", "Z"];
        string[] none = [];

        var checks = new List<CheckLine>
        {
            Check("Jaccard of identical sets is 1.000", Jaccard(abc, abc), 1.0),
            Check("Jaccard of {A,B,C} and {A,B,D} is 0.500", Jaccard(abc, abd), 0.5),
            Check("Jaccard of disjoint sets is 0.000 — the metric CAN report total instability",
                  Jaccard(abc, xyz), 0.0),
            Check("Jaccard of a present set and a SILENT run is 0.000, not undefined",
                  Jaccard(abc, none), 0.0),
            Check("Jaccard of two SILENT runs is UNDEFINED (NaN), never 1.000 — silence is not agreement",
                  Jaccard(none, none), double.NaN),
            Check("Rank agreement of a list with itself is 1.000", RankAgreement(abc, abc), 1.0),
            Check("Rank agreement of a list with its REVERSE is 0.000", RankAgreement(abc, cba), 0.0),
            Check("Rank agreement with fewer than 2 common items is UNDEFINED (NaN)",
                  RankAgreement(abc, ["A"]), double.NaN),
            Check("Modal-share floor is 1.000 when the pool holds one product (chance always agrees)",
                  ModalShareChanceFloor(1, 5, 4), 1.0),
            Check("Modal-share floor at pool 5, N 4, m 3 is 0.1360 — 5·(4·(1/5)³·(4/5) + (1/5)⁴)",
                  ModalShareChanceFloor(5, 4, 3), 5 * (4 * Math.Pow(0.2, 3) * 0.8 + Math.Pow(0.2, 4))),
            Check("Modal-share floor REFUSES a threshold at or below half the runs (NaN, not a number)",
                  ModalShareChanceFloor(50, 4, 2), double.NaN),
            Check("Gate needs 4 of 5 runs to share a lead", MinimumMatches(5), 4),
            Check("Gate needs 3 of 4 runs to share a lead", MinimumMatches(4), 3),
        };

        return checks;

        static CheckLine Check(string label, double actual, double expected)
        {
            bool ok = double.IsNaN(expected)
                ? double.IsNaN(actual)
                : !double.IsNaN(actual) && Math.Abs(actual - expected) < 1e-9;
            return new CheckLine(ok, label, $"expected {Fmt(expected)}, got {Fmt(actual)}");
        }
    }

    // ══ The gate ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Prints and evaluates the gate. Exactly one quantity is gated, on both live arms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three conditions, all of which must hold for every scored persona of both arms.</b>
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Defined.</b> A persona whose runs all errored, or which never
    ///   presented anything at all, has an UNDEFINED stability. It fails. An undecidable measurement
    ///   is not a passed one, and the empty-denominator case is the one that fails in the flattering
    ///   direction.</description></item>
    ///   <item><description><b>Above the threshold.</b> The modal lead product appears in at least
    ///   <see cref="TopItemThreshold"/> of the non-errored runs.</description></item>
    ///   <item><description><b>Above its OWN realised-support chance floor.</b> Clearing 0.75 while
    ///   sitting at the floor means the agent had so few candidates that coincidence explains it.
    ///   This is the condition that stops a degenerate one-product arm from passing a stability gate
    ///   by presenting the same product forever.</description></item>
    /// </list>
    /// <para>
    /// <b>And a liveness condition on the workflow arm.</b> Its model stages fall back to the
    /// deterministic composition when a model call fails to parse, and a fully-degraded workflow is
    /// deterministic — stability 1.000, earned by nothing. If no run of the arm made a single model
    /// call, the arm is not the arm it says it is and the gate is UNDECIDABLE. It fails closed.
    /// </para>
    /// </remarks>
    /// <param name="agent">Arm A.</param>
    /// <param name="workflow">Arm B.</param>
    /// <param name="runs">Repetitions requested.</param>
    private static bool PrintGate(ArmStability agent, ArmStability workflow, int runs)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  ═══ GATE — the LEAD product only ═══════════════════════════════════════");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  The modal lead product must appear in ≥ {TopItemThreshold:P0} of the non-errored runs "
                        + $"({MinimumMatches(runs)} of {runs}) for every persona of both live arms, and must beat that");
        Console.WriteLine("  persona's own realised-support chance floor. Nothing else here is gated: set overlap,");
        Console.WriteLine("  rank order, answer size, rounds, latency and cost are REPORTED.");
        Console.ResetColor();
        Console.WriteLine();

        bool passed = true;

        foreach (ArmStability arm in new[] { agent, workflow })
        {
            if (arm.Personas.Count == 0)
            {
                Line(false, $"{arm.Label}: NO persona produced an observation. Nothing was measured, so nothing passed.");
                passed = false;
                continue;
            }

            foreach (PersonaStability p in arm.Personas)
            {
                bool defined = !double.IsNaN(p.TopItemStability);
                bool aboveThreshold = defined && p.TopItemStability >= TopItemThreshold - 1e-9;
                bool aboveFloor = defined && !double.IsNaN(p.TopItemFloorAtSupport)
                               && p.TopItemStability > p.TopItemFloorAtSupport + 1e-9;
                bool ok = defined && aboveThreshold && aboveFloor;
                passed &= ok;

                string detail = !defined
                    ? "UNDEFINED — every run errored or nothing was ever presented. Failing closed."
                    : $"{p.TopItemStability:P1} on '{p.ModalTopSku}' "
                      + $"(threshold {TopItemThreshold:P0}, support floor {Fmt(p.TopItemFloorAtSupport)} over "
                      + $"{p.SupportSize} distinct product(s))"
                      + (aboveThreshold ? "" : " — BELOW THRESHOLD")
                      + (aboveFloor ? "" : " — AT OR BELOW ITS OWN CHANCE FLOOR, so the number carries no information");

                Line(ok, $"{arm.ShortLabel} · {p.PersonaId} {p.PersonaName}: {detail}");
            }
        }

        // Liveness on the workflow arm.
        var workflowRuns = workflow.Personas.SelectMany(p => p.Runs).ToList();
        int withModelCalls = workflowRuns.Count(r => r.ModelCalls is > 0);
        bool live = workflowRuns.Count > 0 && withModelCalls > 0;
        passed &= live;
        Line(live, workflowRuns.Count == 0
            ? "LIVENESS · workflow: no run completed, so the arm's model participation is unknown. Failing closed."
            : $"LIVENESS · workflow: {withModelCalls} of {workflowRuns.Count} run(s) made at least one model call. "
              + (live
                 ? "The arm under test is the model-backed one."
                 : "EVERY run fell through to the deterministic composition — this is a stability measurement of the "
                 + "FALLBACK, not of the workflow, and 1.000 would be earned by nothing. Failing closed."));

        // Stub leakage — cheap, and it catches the one confusion that would poison the whole report.
        int stubbed = agent.Personas.Concat(workflow.Personas).SelectMany(p => p.Runs).Count(r => r.StubTextSeen);
        bool clean = stubbed == 0;
        passed &= clean;
        Line(clean, clean
            ? "PROVENANCE: no run carried the stub's marker text. These numbers came from a model."
            : $"PROVENANCE: {stubbed} run(s) carried the DRY-RUN stub marker in a live report. Every number above is void.");

        foreach (ArmStability arm in new[] { agent, workflow })
        {
            foreach (string failure in arm.EmptinessFailures)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠️  {arm.ShortLabel} · non-empty-response check: {failure}");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
        Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(passed ? "  ✅ EVAL 08 PASSED" : "  ❌ EVAL 08 FAILED — exit code 1");
        Console.ResetColor();
        Console.WriteLine();

        return passed;
    }

    // ══ Dry-run plumbing ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// What the dry run can actually prove, checked against values chosen in advance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dry run that returns 0 unconditionally is a defect, so every line here is falsifiable and
    /// three of them have exact expected numbers that the stub sequence was designed to produce:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>The sku written by the stub is the sku the metric reads.</b> Run i
    ///   presents <see cref="Eval08Stubs.PresentationPlan"/>'s list; if extraction or the argument
    ///   names were wrong the support size would not match.</description></item>
    ///   <item><description><b>The measured overlap equals the arithmetic.</b> The plan is
    ///   {A,B,C} on every run but one, which presents {A,B,D} — so the mean pairwise Jaccard is a
    ///   number this file computes by hand and compares. A metric stuck at 1.000 fails here, and so
    ///   does one stuck anywhere else.</description></item>
    ///   <item><description><b>Top-item stability is 1.000 and the lead is the stub's first
    ///   product</b> — the plan keeps the lead fixed on purpose, so the two quantities are
    ///   separable: overlap moves while the lead does not.</description></item>
    ///   <item><description><b>The workflow ran end to end and spent nothing.</b> Every run reaches
    ///   the model seam (the degrading stub is called), every model call fails to parse and falls
    ///   back, rounds land inside [1, cap], and the presentation channel is non-empty.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="agent">Arm A.</param>
    /// <param name="workflow">Arm B.</param>
    /// <param name="runs">Repetitions.</param>
    private static IReadOnlyList<CheckLine> DryRunPlumbing(ArmStability agent, ArmStability workflow, int runs)
    {
        var lines = new List<CheckLine>();

        var agentPersonas = agent.Personas;
        var agentRuns = agentPersonas.SelectMany(p => p.Runs).ToList();

        lines.Add(new CheckLine(
            agentRuns.Count > 0 && agentRuns.All(r => !r.Errored),
            "no repetition of the single-agent arm threw inside the harness",
            $"{agentRuns.Count(r => r.Errored)} of {agentRuns.Count} errored"));

        lines.Add(new CheckLine(
            agentRuns.Count > 0 && agentRuns.All(r => r.Skus.Count > 0),
            "tool ARGUMENTS survive the round trip — every stubbed presentation was read back as a sku",
            $"{agentRuns.Count(r => r.Skus.Count == 0)} silent run(s)"));

        double expectedJaccard = Eval08Stubs.ExpectedMeanJaccard(runs);
        foreach (PersonaStability p in agentPersonas)
        {
            lines.Add(new CheckLine(
                !double.IsNaN(p.MeanJaccard) && Math.Abs(p.MeanJaccard - expectedJaccard) < 1e-9,
                $"{p.PersonaId}: measured mean Jaccard equals the arithmetic for the stub plan",
                $"expected {Fmt(expectedJaccard)}, got {Fmt(p.MeanJaccard)}"));

            lines.Add(new CheckLine(
                !double.IsNaN(p.TopItemStability) && Math.Abs(p.TopItemStability - 1.0) < 1e-9
                    && string.Equals(p.ModalTopSku, Eval08Stubs.FixedLeadSku, StringComparison.Ordinal),
                $"{p.PersonaId}: the lead is stable at 1.000 on '{Eval08Stubs.FixedLeadSku}' while the SET moves — "
              + "the two metrics are separable, not one number printed twice",
                $"got {Fmt(p.TopItemStability)} on '{p.ModalTopSku}'"));

            lines.Add(new CheckLine(
                p.SupportSize == Eval08Stubs.ExpectedSupport(runs),
                $"{p.PersonaId}: realised support is the stub's product set",
                $"expected {Eval08Stubs.ExpectedSupport(runs)} distinct sku(s), got {p.SupportSize}"));
        }

        var workflowRuns = workflow.Personas.SelectMany(p => p.Runs).ToList();

        lines.Add(new CheckLine(
            workflowRuns.Count > 0 && workflowRuns.All(r => !r.Errored),
            "the discovery workflow completed on every repetition",
            $"{workflowRuns.Count(r => r.Errored)} of {workflowRuns.Count} errored"));

        lines.Add(new CheckLine(
            workflowRuns.Count > 0 && workflowRuns.All(r => r.Skus.Count > 0),
            "the workflow's screened answer reached the grader's channel on every repetition",
            $"{workflowRuns.Count(r => r.Skus.Count == 0)} run(s) presented nothing"));

        lines.Add(new CheckLine(
            workflowRuns.Count > 0
                && workflowRuns.All(r => r.RoundsTaken is int rounds
                                      && rounds >= 1 && rounds <= DiscoveryState.DefaultMaxDiscoveryRounds),
            $"rounds taken land inside [1, {DiscoveryState.DefaultMaxDiscoveryRounds}] on every repetition — the "
          + "loop-back edge and the cap are both wired",
            string.Join(", ", workflowRuns.Select(r => r.RoundsTaken?.ToString(CultureInfo.InvariantCulture) ?? "?"))));

        lines.Add(new CheckLine(
            workflowRuns.Count > 0 && workflowRuns.All(r => r.ModelCalls is > 0),
            "every workflow repetition REACHED the model seam (the stub was called) — so a live run would too",
            $"model calls: {string.Join(", ", workflowRuns.Select(r => r.ModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "?"))}"));

        lines.Add(new CheckLine(
            workflowRuns.Count > 0 && workflowRuns.All(r => r.DegradedNotes > 0),
            "every workflow repetition DEGRADED, as an implausible stub must make it — this is the fallback seam "
          + "firing, and it is why a live run's 1.000 would have to be checked against liveness",
            $"degraded notes: {string.Join(", ", workflowRuns.Select(r => r.DegradedNotes.ToString(CultureInfo.InvariantCulture)))}"));

        lines.Add(new CheckLine(
            agentRuns.Concat(workflowRuns).All(r => r.EstimatedCost is null or 0m),
            "nothing was priced — a dry run must not produce a spend figure",
            $"{agentRuns.Concat(workflowRuns).Count(r => r.EstimatedCost is > 0m)} run(s) carried a cost"));

        return lines;
    }

    // ══ The judged number: the JUDGE's own variance ════════════════════════════════════════

    /// <summary>
    /// Re-grades ONE fixed agent answer N times and reports the spread of the judge's score.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the input is fixed.</b> Judging N different agent answers would give a spread that
    /// mixes the agent's variance with the judge's, and no arithmetic separates them afterwards.
    /// Holding the answer constant leaves exactly one source of variation: the instrument.
    /// </para>
    /// <para>
    /// <b>What a spread of zero would and would not prove.</b> It would show the judge is repeatable
    /// on this input. It would NOT show the judge is right — a judge that returned the constant 50
    /// on everything also has zero spread, which is why <c>EvaluationResult.EvaluationFailed</c> is
    /// counted in its own column: <c>ChatClientEvaluator</c> returns exactly 50 with that flag set
    /// when it cannot parse a verdict, and <c>MAFEvaluationHarness</c> copies the score without the
    /// flag. An unchecked 50 is not a grade.
    /// </para>
    /// <para>
    /// <b>Chance floor for these criteria: not established, and deliberately not invented.</b>
    /// <see cref="GalaxusEvalCriteria.Advisory"/> has no gold set, no inter-rater agreement and no
    /// calibration run anywhere in this repository, so what a degenerate agent would score on them
    /// is UNKNOWN. That is precisely why the score itself is never gated and never compared across
    /// arms here — the only quantity read is the SPREAD, and a spread needs no calibrated scale to
    /// be meaningful.
    /// </para>
    /// </remarks>
    /// <param name="agent">Arm A, source of the fixed answer.</param>
    /// <param name="workflow">Arm B, the fallback source when Arm A never answered.</param>
    /// <param name="runs">How many times to re-grade.</param>
    /// <param name="client">The judge's chat client.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<JudgeReplication?> RunJudgeReplicationAsync(
        ArmStability agent, ArmStability workflow, int runs, IChatClient client, CancellationToken ct)
    {
        // The fixed answer is a REAL agent answer, taken from the first repetition that produced
        // one. A synthetic paragraph would measure the judge on text no agent wrote.
        (string Input, string Output, string Source)? subject = null;
        foreach (ArmStability arm in new[] { agent, workflow })
        {
            foreach (PersonaStability p in arm.Personas)
            {
                foreach (RunObservation r in p.Runs)
                {
                    if (r.Errored || r.Skus.Count == 0 || string.IsNullOrWhiteSpace(r.Text)) continue;
                    var persona = CoveragePersonas.All.First(cp => string.Equals(cp.Id, p.PersonaId, StringComparison.Ordinal));
                    subject = (persona.Prompt, r.Text!, $"{arm.ShortLabel} · {p.PersonaId} · run {r.Run}");
                    break;
                }
                if (subject is not null) break;
            }
            if (subject is not null) break;
        }

        if (subject is null) return null;

        var evaluator = new ChatClientEvaluator(client);
        var scores = new List<int>(runs);
        int instrumentFailures = 0;

        for (int i = 1; i <= runs; i++)
        {
            try
            {
                EvaluationResult result = await evaluator
                    .EvaluateAsync(subject.Value.Input, subject.Value.Output, GalaxusEvalCriteria.Advisory, ct)
                    .ConfigureAwait(false);

                // ⚠ A failed evaluation returns the 50 fallback. It is counted as an INSTRUMENT
                // FAILURE and excluded from the spread, never averaged in as if it were a grade.
                if (result.EvaluationFailed) { instrumentFailures++; continue; }
                scores.Add(result.OverallScore);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                instrumentFailures++;
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"    judge replication {i}/{runs} threw: {ex.Message}");
                Console.ResetColor();
            }
        }

        return new JudgeReplication(subject.Value.Source, scores, instrumentFailures, runs);
    }

    // ══ Printing ══════════════════════════════════════════════════════════════════════════

    private static void PrintArm(ArmStability arm, int runs)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  ═══ {arm.Label} ═══════════════════════════════════");
        Console.ResetColor();

        if (arm.Personas.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("    No persona produced an observation.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    persona          top-1    lead       Jaccard    rank   size  (sd)  range   "
                        + "support  core  err/silent");
        Console.ResetColor();

        foreach (PersonaStability p in arm.Personas)
        {
            Console.ForegroundColor = double.IsNaN(p.TopItemStability) ? ConsoleColor.Red
                : p.TopItemStability >= TopItemThreshold ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine(
                $"    {Fit(p.PersonaId, 15)}  {Fmt(p.TopItemStability),6}  {Fit(p.ModalTopSku ?? "—", 10)} "
              + $"{Fmt(p.MeanJaccard),8}  {Fmt(p.MeanRankAgreement),6}  "
              + $"{p.MeanSetSize,5:F1} ({p.SetSizeSd,4:F2}) {$"{p.MinSetSize}-{p.MaxSetSize}",-6} "
              + $"{p.SupportSize,7}  {p.CoreSize,4}  {p.ErroredRuns}/{p.SilentRuns}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"      floors: lead-by-chance {Fmt(p.TopItemFloorAtSupport)} at realised support "
                            + $"({p.SupportSize} product(s)), {Fmt(p.TopItemFloorAtCatalogue)} at the "
                            + $"{Catalogue.Default.All.Count}-product catalogue · "
                            + $"Jaccard-by-chance ≈ {Fmt(JaccardChanceFloorApproximate(Catalogue.Default.All.Count, p.MeanSetSize))} "
                            + "(approximation) · rank-by-chance 0.500");
            if (p.UndefinedJaccardPairs > 0 || p.UndefinedRankPairs > 0)
            {
                Console.WriteLine($"      undefined pairs EXCLUDED from the means: {p.UndefinedJaccardPairs} Jaccard "
                                + $"(both runs silent), {p.UndefinedRankPairs} rank (fewer than 2 common products). "
                                + "Excluded, never counted as 1.000.");
            }
            if (p.Runs.Count != p.RequestedRuns)
            {
                Console.WriteLine($"      ⚠️ {p.Runs.Count} of {p.RequestedRuns} requested repetitions completed.");
            }
            Console.ResetColor();

            foreach (RunObservation r in p.Runs)
            {
                Console.ForegroundColor = r.Errored ? ConsoleColor.Red
                    : r.Skus.Count == 0 ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
                Console.WriteLine($"        run {r.Run}: "
                    + (r.Errored ? $"ERRORED — {Clip(r.ErrorMessage ?? "", 90)}"
                       : r.Skus.Count == 0 ? "presented NOTHING (counted against the lead share, not dropped)"
                       : string.Join(" → ", r.Skus))
                    + (r.RoundsTaken is int rounds ? $"   [rounds {rounds}, {r.StopReason}, {r.ModelCalls} model call(s)]" : ""));
                Console.ResetColor();
            }
        }
    }

    private static void PrintRoundsDistribution(ArmStability workflow)
    {
        var rounds = workflow.Personas.SelectMany(p => p.Runs)
                                      .Where(r => r.RoundsTaken is int)
                                      .Select(r => r.RoundsTaken!.Value)
                                      .ToList();
        if (rounds.Count == 0) return;

        int cap = DiscoveryState.DefaultMaxDiscoveryRounds;
        double atOne = rounds.Count(r => r <= 1) / (double)rounds.Count;
        double atCap = rounds.Count(r => r >= cap) / (double)rounds.Count;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  ═══ Rounds taken — the ONE distribution where variance is HEALTH ═══════════");
        Console.ResetColor();

        Console.WriteLine("    " + string.Join("  ", rounds.GroupBy(r => r).OrderBy(g => g.Key)
                                                     .Select(g => $"{g.Count()}×{g.Key} round(s)")));
        Console.WriteLine($"    P(rounds = 1) = {atOne:F3}   P(rounds = cap {cap}) = {atCap:F3}   n = {rounds.Count}");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    Both ends are pathologies and they are OPPOSITE ones. P(rounds = 1) ≈ 1 is design §D.3's");
        Console.WriteLine("    rubber stamp: a reviewer that approves whatever the first pass found, and it is invisible");
        Console.WriteLine("    in any coverage number. P(rounds = cap) ≈ 1 is a reviewer that never approves, so the loop");
        Console.WriteLine("    buys nothing but latency and every answer is degraded. Mass in the middle is the good");
        Console.WriteLine("    outcome — so a LOW variance here would be the finding, not a reassurance.");
        Console.ResetColor();
    }

    private static void PrintSpend(ArmStability arm)
    {
        var runs = arm.Personas.SelectMany(p => p.Runs).Where(r => !r.Errored).ToList();
        if (runs.Count == 0) return;

        var durations = runs.Select(r => r.DurationMs).Where(d => !double.IsNaN(d)).ToList();
        var tokens = runs.Where(r => r.PromptTokens is not null || r.CompletionTokens is not null)
                         .Select(r => (double)((r.PromptTokens ?? 0) + (r.CompletionTokens ?? 0))).ToList();
        decimal cost = runs.Sum(r => r.EstimatedCost ?? 0m);
        int estimated = runs.Count(r => r.TokensEstimated);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  ─── {arm.ShortLabel} · latency, tokens, cost (REPORTED, never gated) ─────");
        Console.ResetColor();
        Console.WriteLine($"    latency ms : mean {Mean(durations),8:F0}  sd {StandardDeviation(durations),7:F0}  "
                        + $"min {(durations.Count == 0 ? 0 : durations.Min()),7:F0}  max {(durations.Count == 0 ? 0 : durations.Max()),7:F0}");
        Console.WriteLine($"    tokens     : mean {Mean(tokens),8:F0}  sd {StandardDeviation(tokens),7:F0}  "
                        + $"min {(tokens.Count == 0 ? 0 : tokens.Min()),7:F0}  max {(tokens.Count == 0 ? 0 : tokens.Max()),7:F0}");
        // ⚠ Invariant, and the currency symbol is written out. ModelPricing's table is in USD;
        // a plain "C4" format renders it in the MACHINE's culture, so a run on a Swiss box printed
        // a USD figure labelled CHF.
        Console.WriteLine($"    cost       : USD {cost.ToString("F4", CultureInfo.InvariantCulture)} "
                        + $"total over {runs.Count} run(s)");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (estimated > 0)
        {
            Console.WriteLine($"    ⚠️ {estimated} of {runs.Count} run(s) report ESTIMATED tokens: the provider returned no");
            Console.WriteLine("       usage, so the harness derived them from text length (≈ chars/4). The cost above is");
            Console.WriteLine("       derived from those, and for an arm whose answer is REPLAYED from workflow state — the");
            Console.WriteLine("       discovery arm — the text the estimate reads is not what any model was billed for.");
            Console.WriteLine("       Read it as an order of magnitude, and read the workflow's model-call COUNT instead.");
        }
        else
        {
            Console.WriteLine("    Token counts are provider-reported, not estimated.");
        }
        Console.WriteLine("    Latency spread is reported because a recommender that answers in 4 s and then in 40 s is a");
        Console.WriteLine("    different product each time. It is not gated: this is a demo deployment on a shared quota.");
        Console.ResetColor();
    }

    /// <summary>
    /// The workflow arm's OWN spend, summed from the provider's usage blocks inside the loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this panel is separate from <see cref="PrintSpend"/>.</b> That one reports what the
    /// HARNESS saw at the agent boundary. On this arm the harness sees a replay: the answer text is
    /// re-composed from finished workflow state, so before item 8.17 the harness estimated tokens
    /// from that string's length and printed a currency figure derived from it — the eval's own
    /// prose called the resulting <c>USD 0.0062</c> an artefact and pointed at the model-call COUNT
    /// instead. The count is not a bill. This panel is the bill, and it comes from inside the loop,
    /// where the four model-backed stages actually call the deployment.
    /// </para>
    /// <para>
    /// <b>What it may and may not say.</b> Tokens are reported only from usage blocks; a call that
    /// returned none is counted as an ABSENCE and the total is labelled a LOWER BOUND. Currency is
    /// printed only when <c>ModelPricing</c> has a row for the deployment, and the rate and its
    /// source are printed beside the money — that table's <c>gpt-5-mini</c> row is marked
    /// "(placeholder)" in library source, so the tokens are the result and the currency is
    /// arithmetic over a declared rate. When no row matches, the money is UNKNOWN and stays UNKNOWN.
    /// </para>
    /// </remarks>
    /// <param name="arm">The workflow arm.</param>
    private static void PrintWorkflowChatSpend(ArmStability arm)
    {
        var spends = arm.Personas
            .SelectMany(p => p.Runs)
            .Select(r => r.WorkflowSpend)
            .OfType<ChatSpendSnapshot>()
            .ToList();

        if (spends.Count == 0) return;

        int calls = spends.Sum(s => s.Calls);
        int withUsage = spends.Sum(s => s.CallsWithUsage);
        int withoutUsage = spends.Sum(s => s.CallsWithoutUsage);
        long prompt = spends.Sum(s => s.PromptTokens);
        long completion = spends.Sum(s => s.CompletionTokens);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  ─── Discovery workflow · what the LOOP spent (provider usage blocks) ─────");
        Console.ResetColor();

        if (calls == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    0 model call(s) over {spends.Count} run(s) — the loop ran fully deterministic and");
            Console.WriteLine("    was billed nothing. That is a measured zero, not a missing figure.");
            Console.ResetColor();
            return;
        }

        // ⚠ InvariantCulture on every figure below. `N0` and `0.0` render in the MACHINE's culture
        //   otherwise — the first live run of the sibling meter printed `7’202` on this Swiss box —
        //   and a token count nobody can grep out of a log is a count the next reader re-types.
        Console.WriteLine($"    model      : {Config.Model}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    calls      : {calls} over {spends.Count} run(s) "
          + $"({(double)calls / spends.Count:0.0} per run) · {withUsage} reported usage, {withoutUsage} did not"));

        if (withUsage == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    tokens     : usage NOT REPORTED by the provider for any of the {calls} call(s).");
            Console.WriteLine("    cost       : UNKNOWN — and UNKNOWN is not zero. Nothing is estimated in its place.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    prompt     : {prompt,10:N0} tok"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    completion : {completion,10:N0} tok"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    total      : {prompt + completion,10:N0} tok"));

        if (withoutUsage > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    ⚠ LOWER BOUND — {withoutUsage} of {calls} call(s) returned no usage block. Their tokens are");
            Console.WriteLine("      UNKNOWN, not zero, and are absent from the totals above.");
            Console.ResetColor();
        }

        var rate = ModelPricing.GetPricing(Config.Model);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (rate is null)
        {
            Console.WriteLine($"    cost       : NOT COMPUTED — ModelPricing has no row matching '{Config.Model}', and this");
            Console.WriteLine("                 panel will not invent a rate. The token counts above stand on their own.");
        }
        else
        {
            decimal cost = (prompt / 1000m * rate.Value.InputPer1K) + (completion / 1000m * rate.Value.OutputPer1K);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    rate       : USD {rate.Value.InputPricePerMillion:0.####} / 1M in · "
              + $"USD {rate.Value.OutputPricePerMillion:0.####} / 1M out   [source: AgentEval ModelPricing table]"));
            Console.WriteLine($"    cost       : USD {cost.ToString("F4", CultureInfo.InvariantCulture)}"
                            + (withoutUsage > 0 ? "   ← over the REPORTED tokens only, so a lower bound too" : ""));
            Console.WriteLine("    ⚠ that table's row for this deployment is marked '(placeholder)' in library source.");
            Console.WriteLine("      Read the TOKENS as the measurement and the currency as arithmetic over a declared rate.");
        }
        Console.ResetColor();
    }

    private static void PrintJudgeReplication(JudgeReplication? judge, int runs)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  ═══ Judge replication — the INSTRUMENT's own variance ═════════════════════");
        Console.ResetColor();

        if (judge is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("    NOT MEASURED — no repetition produced an answer to re-grade. Nothing is substituted.");
            Console.ResetColor();
            return;
        }

        var scores = judge.Scores.Select(s => (double)s).ToList();
        Console.WriteLine($"    Subject: ONE fixed answer from {judge.Source}, re-graded {judge.Attempts} time(s)");
        Console.WriteLine($"    Criteria: GalaxusEvalCriteria.Advisory ({GalaxusEvalCriteria.Advisory.Count} axes) — "
                        + "ADVISORY, uncalibrated, and never gated here.");

        if (scores.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    Every one of {judge.Attempts} judgements failed to parse. This channel is reporting an");
            Console.WriteLine("    INSTRUMENT FAILURE, not a score. ChatClientEvaluator returns 50 on a parse failure and");
            Console.WriteLine("    MAFEvaluationHarness copies that 50 without the flag — do not quote any 50 you see.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"    scores     : {string.Join(", ", judge.Scores)}");
        Console.WriteLine($"    mean {Mean(scores):F1}   sd {StandardDeviation(scores):F2}   "
                        + $"range {scores.Min():F0}-{scores.Max():F0} (spread {scores.Max() - scores.Min():F0})   "
                        + $"instrument failures {judge.InstrumentFailures}");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("    The INPUT did not change, so every point of this spread is the judge's own. Read it before");
        Console.WriteLine("    reading any judged number elsewhere in this suite: a difference between two arms that is");
        Console.WriteLine("    smaller than this spread is not a difference between the arms.");
        Console.WriteLine("    A spread of zero would show repeatability, NOT correctness — a judge stuck on a constant");
        Console.WriteLine("    also has zero spread, which is why parse failures are counted in their own column above.");
        Console.ResetColor();
    }

    private static void PrintSelfCheck(IReadOnlyList<CheckLine> checks)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  ─── instrument self-check (hand-written expectations, no run data) ─────");
        Console.ResetColor();
        foreach (CheckLine c in checks)
        {
            Console.ForegroundColor = c.Passed ? ConsoleColor.DarkGreen : ConsoleColor.Red;
            Console.WriteLine($"    {(c.Passed ? "✅" : "❌")} {c.Label}"
                            + (c.Passed ? "" : $"   ({c.Detail})"));
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static void PrintDryRunVerdict(IReadOnlyList<CheckLine> checks)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── DRY-RUN PLUMBING CHECKS (a stub cannot prove anything else) ──────");
        Console.ResetColor();

        foreach (CheckLine c in checks)
        {
            Console.ForegroundColor = c.Passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(c.Passed ? "✅" : "❌")} {c.Label}   ({c.Detail})");
            Console.ResetColor();
        }

        bool all = checks.All(c => c.Passed);
        Console.WriteLine();
        Console.ForegroundColor = all ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(all
            ? "  ✅ DRY RUN — the plumbing held. This says NOTHING about either architecture's stability:"
            : "  ❌ DRY RUN — the plumbing did NOT hold. Do not run the paid stage until this is green.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     no model was called, both arms answered from a stub, and the stability numbers above are");
        Console.WriteLine("     properties of the stub sequence this file chose. The exit code reflects the plumbing only.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintDerivedFloors(int runs)
    {
        int catalogue = Catalogue.Default.All.Count;
        int m = MinimumMatches(runs);

        EvalPrinter.PrintFloors($"Eval 08 — chance floors at N = {runs} (gate needs {m} of {runs})", new[]
        {
            $"LEAD, catalogue pool     : {Fmt(ModalShareChanceFloor(catalogue, runs, m))}   an agent drawing its lead "
          + $"uniformly from all {catalogue} products repeats it {m}+ times in {runs} this often. Exact — the "
          + $"{TopItemThreshold:P0} threshold is a strict majority, so at most one product can hold it.",

            $"LEAD, 20-product pool    : {Fmt(ModalShareChanceFloor(20, runs, m))}   the same agent restricted to a "
          + "plausible retrieval shortlist. Printed because the catalogue-wide figure is the GENEROUS one: a bigger "
          + "pool makes coincidence rarer and the floor smaller, which flatters the gate.",

            $"LEAD, 5-product pool     : {Fmt(ModalShareChanceFloor(5, runs, m))}   an agent choosing among five "
          + "finalists. The per-persona floor printed beside each row is computed at that arm's OWN realised support, "
          + "which is tighter still, and the gate reads THAT one.",

            $"SET overlap (Jaccard)    : ≈ {Fmt(JaccardChanceFloorApproximate(catalogue, ChanceFloors.DegenerateDrawSize))}   "
          + $"two independent uniform {ChanceFloors.DegenerateDrawSize}-draws from the catalogue. APPROXIMATE — it is the "
          + "ratio of expectations, not the expectation of the ratio, and nothing is gated on it.",

            "RANK agreement           : 0.5000   exactly, for any pair of independent orderings: a coin flip on every "
          + "comparable pair.",

            "A CONSTANT agent         : 1.0000 on the lead, 1.0000 on overlap, 0.0000 spread on set size. It passes the "
          + "threshold and then FAILS the support-floor condition, because its realised support is one product and the "
          + "floor there is 1.0000. Stability alone cannot tell a good recommender from a stuck one — Evals 01, 02 and "
          + "03 are what do that, and this eval does not claim to.",

            "The judged axes          : NO floor is stated, because none has been established. Advisory criteria with no "
          + "gold set and no calibration run: what a degenerate agent scores on them is unknown. That is why only the "
          + "judge's SPREAD is read here, and a spread needs no calibrated scale.",
        });
    }

    private static void PrintDesirabilityTable()
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  ─── what SHOULD be stable, and what should not ──────────────────────────");
        Console.ResetColor();
        Console.WriteLine("    GATED    lead product         stable is GOOD      a lead that changes every reload is a lottery");
        Console.WriteLine("    report   set overlap          high, NOT 1.000     1.000 means it has stopped exploring");
        Console.WriteLine("    report   rank below the lead  mildly good         positions 3 and 4 swapping is cosmetic");
        Console.WriteLine("    report   answer size          low variance        3 items then 9 is a layout problem");
        Console.WriteLine("    report   rounds taken         variance is HEALTH  always-1 is a rubber stamp; always-cap");
        Console.WriteLine("                                                      is a reviewer that never approves");
        Console.WriteLine("    report   latency / cost       informational       a shared demo quota is not a product SLO");
        Console.WriteLine();
    }

    private static void PrintNoSubstitution()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     NOTHING was substituted. Demo 2's DETERMINISTIC discovery arm runs without a key and is");
        Console.WriteLine("     bound into this project — and it is deliberately NOT run here. Its run-to-run stability");
        Console.WriteLine("     is 1.000 by construction, and printing that under a workflow-stability heading would be");
        Console.WriteLine("     a measurement of the absence of a model reported as a property of the workflow.");
        Console.WriteLine("     Eval 08 measured NOTHING on this invocation. It did not measure zero.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintWhatThisDoesNotProve()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  What this does and does not mean:");
        Console.WriteLine($"    · n = {CoveragePersonas.All.Count} personas exist; this eval scores a SMALL SUBSET of them, on one");
        Console.WriteLine("      utterance each. It is a variance measurement, not a coverage measurement.");
        Console.WriteLine("    · A stable lead is not a CORRECT lead. A stuck agent scores 1.000 here and fails Eval 02.");
        Console.WriteLine("      The support-floor condition catches the degenerate case; it does not make this a quality gate.");
        Console.WriteLine("    · Nothing here decomposes the variance into its sources — sampling temperature, retrieval");
        Console.WriteLine("      ties, session state and the deployment's own drift all land in one number.");
        Console.WriteLine("    · The two arms are not paired for a statistical comparison. Different architectures on the");
        Console.WriteLine("      same personas at this n supports a description, not a significance claim.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintProgress(CoveragePersona persona, StochasticProgress p)
    {
        string eta = p.EstimatedRemaining is { } remaining ? $", ~{remaining.TotalSeconds:F0}s left" : "";
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    run {p.CurrentRun}/{p.TotalRuns} · {persona.Id} · "
                        + $"{p.LastResult?.ToolCallCount ?? 0} tool call(s) "
                        + $"({p.Elapsed.TotalSeconds:F0}s elapsed{eta})");
        Console.ResetColor();
    }

    private static void PrintDryRunBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  🧪 DRY RUN — stub models on BOTH arms, nothing spent, nothing written.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     The agent stub presents a CHOSEN sku list per repetition, so the expected overlap and lead");
        Console.WriteLine("     stability are known in advance and this run can fail on a WRONG ANSWER, not only on a crash.");
        Console.WriteLine("     The workflow stub answers every model stage with implausible prose, so every stage degrades");
        Console.WriteLine("     to the deterministic composition — which is the seam a live run's liveness check exists for.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 08 — Repeated-Run Stability   (single agent AND workflow, both LIVE)   ║
║   Same customer, same sentence, N times: how much survives a reload?          ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void Line(bool ok, string text)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(ok ? "✅" : "❌")} {text}");
        Console.ResetColor();
    }

    // ══ Small numeric helpers ═════════════════════════════════════════════════════════════

    private static double Mean(IReadOnlyList<double> values) =>
        values.Count == 0 ? double.NaN : values.Average();

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return values.Count == 0 ? double.NaN : 0.0;
        double mean = values.Average();
        double sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (values.Count - 1));     // sample sd: n repetitions are a sample
    }

    private static string Fmt(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F4", CultureInfo.InvariantCulture);

    private static string Fmt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Fit(string text, int width) =>
        text.Length <= width ? text.PadRight(width) : text[..Math.Max(0, width - 1)] + "…";

    private static string Clip(string text, int max) =>
        text.Length <= max ? text.Replace("\n", " ", StringComparison.Ordinal)
                           : text.Replace("\n", " ", StringComparison.Ordinal)[..max] + "…";

    private static string FirstLine(string text)
    {
        int index = text.IndexOf('\n', StringComparison.Ordinal);
        return (index < 0 ? text : text[..index]).TrimEnd('\r');
    }

    // ══ Nested result shapes ══════════════════════════════════════════════════════════════
    //
    // Nested rather than file-level so this eval can be added beside other new evals without any
    // chance of a type-name collision in the shared Galaxus.RecommendationAgent.Evals namespace.

    /// <summary>One repetition, projected onto everything the metrics read.</summary>
    /// <param name="Run">1-based run number.</param>
    /// <param name="Skus">Distinct presented skus, in presentation order.</param>
    /// <param name="Errored">True when the harness caught an exception for this run.</param>
    /// <param name="ErrorMessage">That exception's message.</param>
    /// <param name="DurationMs">Wall-clock milliseconds, from the harness.</param>
    /// <param name="PromptTokens">Prompt tokens, when reported.</param>
    /// <param name="CompletionTokens">Completion tokens, when reported.</param>
    /// <param name="TokensEstimated">True when the harness derived the counts from text length.</param>
    /// <param name="EstimatedCost">Derived cost, when the deployment is priced.</param>
    /// <param name="RoundsTaken">Discovery rounds, on the workflow arm only.</param>
    /// <param name="StopReason">Terminal stop reason, on the workflow arm only.</param>
    /// <param name="ModelCalls">Model calls the workflow made — the arm's liveness signal.</param>
    /// <param name="WorkflowSpend">
    /// What those calls cost, from the provider's usage blocks inside the loop. Null on the agent
    /// arm, which has no workflow. A count of calls is not a bill; this is the bill.
    /// </param>
    /// <param name="DegradedNotes">How many stages fell back to their deterministic composition.</param>
    /// <param name="StubTextSeen">True when the dry-run stub's marker text appears in the answer.</param>
    /// <param name="Text">The answer text, kept for the judge-replication subject.</param>
    public sealed record RunObservation(
        int Run,
        IReadOnlyList<string> Skus,
        bool Errored,
        string? ErrorMessage,
        double DurationMs,
        int? PromptTokens,
        int? CompletionTokens,
        bool TokensEstimated,
        decimal? EstimatedCost,
        int? RoundsTaken,
        string? StopReason,
        int? ModelCalls,
        ChatSpendSnapshot? WorkflowSpend,
        int DegradedNotes,
        bool StubTextSeen,
        string? Text = null);

    /// <summary>One persona's repetitions, summarised.</summary>
    /// <param name="PersonaId">Customer id.</param>
    /// <param name="PersonaName">Display name.</param>
    /// <param name="Runs">Every repetition, in order.</param>
    /// <param name="RequestedRuns">How many were asked for.</param>
    /// <param name="ErroredRuns">How many threw — excluded from every metric and reported.</param>
    /// <param name="SilentRuns">How many answered with no recommendation at all.</param>
    /// <param name="ModalTopSku">The most common lead product, or null when nothing was ever presented.</param>
    /// <param name="TopItemStability">Modal lead share over NON-ERRORED runs. NaN when undefined.</param>
    /// <param name="MinimumMatchesForGate">Runs that must share a lead at this n.</param>
    /// <param name="MeanJaccard">Mean pairwise set overlap over DEFINED pairs.</param>
    /// <param name="UndefinedJaccardPairs">Pairs where both runs were silent — excluded, never 1.000.</param>
    /// <param name="MeanRankAgreement">Mean pairwise rank concordance over DEFINED pairs.</param>
    /// <param name="UndefinedRankPairs">Pairs with fewer than two products in common.</param>
    /// <param name="MeanSetSize">Mean number of recommendations.</param>
    /// <param name="SetSizeSd">Sample standard deviation of that count.</param>
    /// <param name="MinSetSize">Smallest answer.</param>
    /// <param name="MaxSetSize">Largest answer.</param>
    /// <param name="SupportSize">Distinct products presented anywhere in the block.</param>
    /// <param name="CoreSize">Products present in EVERY answering run.</param>
    /// <param name="TopItemFloorAtSupport">Chance floor at the realised support — the one the gate reads.</param>
    /// <param name="TopItemFloorAtCatalogue">Chance floor at the whole catalogue — the generous one.</param>
    public sealed record PersonaStability(
        string PersonaId,
        string PersonaName,
        IReadOnlyList<RunObservation> Runs,
        int RequestedRuns,
        int ErroredRuns,
        int SilentRuns,
        string? ModalTopSku,
        double TopItemStability,
        int MinimumMatchesForGate,
        double MeanJaccard,
        int UndefinedJaccardPairs,
        double MeanRankAgreement,
        int UndefinedRankPairs,
        double MeanSetSize,
        double SetSizeSd,
        int MinSetSize,
        int MaxSetSize,
        int SupportSize,
        int CoreSize,
        double TopItemFloorAtSupport,
        double TopItemFloorAtCatalogue);

    /// <summary>One architecture's whole block.</summary>
    /// <param name="Label">Printed label.</param>
    /// <param name="Personas">Per-persona summaries.</param>
    /// <param name="EmptinessFailures">
    /// Personas where <c>StochasticAssertions.HavePassRateAtLeast(1.0)</c> tripped — i.e. some
    /// repetition produced no text at all. Reported under that name and never as a quality figure.
    /// </param>
    public sealed record ArmStability(
        string Label,
        IReadOnlyList<PersonaStability> Personas,
        IReadOnlyList<string> EmptinessFailures)
    {
        /// <summary>A short label for tables.</summary>
        public string ShortLabel => Label.Split('(')[0].Trim();
    }

    /// <summary>The judge re-graded on one fixed answer.</summary>
    /// <param name="Source">Which run's answer was re-graded.</param>
    /// <param name="Scores">Every PARSED score. Fallback 50s are not here.</param>
    /// <param name="InstrumentFailures">Judgements that failed to parse or threw.</param>
    /// <param name="Attempts">How many judgements were attempted.</param>
    public sealed record JudgeReplication(
        string Source,
        IReadOnlyList<int> Scores,
        int InstrumentFailures,
        int Attempts);

    /// <summary>One pass/fail line in a self-check or plumbing block.</summary>
    /// <param name="Passed">Whether it held.</param>
    /// <param name="Label">What was checked.</param>
    /// <param name="Detail">The measured values.</param>
    public sealed record CheckLine(bool Passed, string Label, string Detail);
}

// ══════════════════════════════════════════════════════════════════════════════════════════
//  Supporting types. Prefixed Eval08 so this file can land beside other new evals without a
//  name collision in the shared namespace.
// ══════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Opens the per-turn tool scopes around ONE invocation, so a stochastic block gets N budgets
/// rather than one.
/// </summary>
/// <remarks>
/// <see cref="StochasticRunner"/> invokes the agent itself, so the caller cannot open a scope
/// per run from outside — a <c>using</c> around the whole block would give every repetition a
/// share of ONE 24-call budget and ONE presentation capture. The later runs would then present
/// nothing, and this eval would score that silence as instability. Wrapping the invocation is
/// what makes "fresh scope per turn" true by construction instead of by discipline.
/// </remarks>
/// <param name="inner">The agent being wrapped.</param>
internal sealed class Eval08TurnScopedAgent(IEvaluableAgent inner) : IEvaluableAgent
{
    private readonly IEvaluableAgent _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        using (EvalRuntime.BeginTurn())
        {
            return await _inner.InvokeAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// An <see cref="IAgentFactory"/> over a caller-supplied builder, so each repetition gets a fresh
/// agent — and, on the workflow arm, a fresh arm instance whose telemetry belongs to that run.
/// </summary>
/// <param name="build">Builds one agent. Called once per repetition, in run order.</param>
internal sealed class Eval08AgentFactory(Func<IEvaluableAgent> build) : IAgentFactory
{
    private readonly Func<IEvaluableAgent> _build = build ?? throw new ArgumentNullException(nameof(build));

    /// <inheritdoc/>
    public string ModelId => Config.ModelOverride ?? Config.PreferredDeployment;

    /// <inheritdoc/>
    public string ModelName => "Galaxus stability arm";

    /// <inheritdoc/>
    public ModelConfiguration? Configuration => null;

    /// <inheritdoc/>
    public IEvaluableAgent CreateAgent() => _build();
}

/// <summary>
/// Demo 2's MAF discovery workflow as a stability arm, on its <b>model-backed</b> path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <see cref="RealDiscoveryLoopArm"/>.</b> That arm is pinned to
/// <c>Offline: true</c> so Evals 03 and 04 keep their stated "needs no credentials" property, and
/// its remarks say so. A deterministic arm has no run-to-run variance by construction, so measuring
/// its stability would produce 1.000 and report the absence of a model as a property of the
/// workflow. This arm exists to run the same graph WITH the model in it.
/// </para>
/// <para>
/// <b>What is substituted: nothing, on the live path.</b> No node overrides — the shipped mapper,
/// search, reviewer, ranker and presenter all run. The progress sink is a recorder rather than the
/// console one, which is what keeps ten full recommendation trays out of the eval's own report;
/// the sink receives the same events either way. On the dry-run path the presenter is swapped for
/// the deterministic one with <c>print: false</c> for that same reason, exactly as
/// <see cref="RealDiscoveryLoopArm"/> does.
/// </para>
/// <para>
/// <b>The answer reaches the metrics through the same channel every other arm uses</b> —
/// <c>PresentRecommendation</c> tool calls in a real trace, replayed from
/// <c>DiscoveryState.Presented</c>, i.e. what survived the guardrail pipeline rather than what the
/// ranker chose. Replaying the ranker's selection would report items the customer never saw and
/// would overstate the overlap by exactly the number of things the guardrails removed.
/// </para>
/// </remarks>
internal sealed class Eval08LiveWorkflowArm : IEvaluableAgent
{
    private readonly IProductRetriever _retriever;
    private readonly IChatClient _chatClient;
    private readonly bool _dryRun;

    /// <summary>Builds one repetition's arm.</summary>
    /// <param name="retriever">The bound retriever every arm searches with.</param>
    /// <param name="chatClient">The live client, or the degrading stub in a dry run.</param>
    /// <param name="dryRun">True to silence the presenter and mark the arm as stubbed.</param>
    public Eval08LiveWorkflowArm(IProductRetriever retriever, IChatClient chatClient, bool dryRun = false)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _dryRun = dryRun;
    }

    /// <inheritdoc/>
    public string Name => Eval08_StochasticStability.ArmWorkflow;

    /// <summary>The last run's full result — graph, routes, state — or null before the first turn.</summary>
    public DiscoveryRunResult? LastResult { get; private set; }

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // The customer is read from the PROMPT, as the live agent and every control read it. An arm
        // configured out of band would be running a different experiment from the one it is beside.
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

        var catalogue = Catalogue.Default;
        var recorder = new RecordingDiscoveryProgressSink();

        // ⚠ The UTTERANCE, not the framed prompt: DiscoveryState.SessionRequest is a typed slot for
        // what the customer said, and the eval's "[session] You are speaking with customer …" header
        // is harness scaffolding. Passing the frame was MEASURED turning that header into a
        // stated-need interest that retrieved nothing — the arm looked broken and the harness was.
        var options = new WorkflowLoopOptions(
            Offline: false,
            PersonalizationDisabled: false,
            SessionRequest: GalaxusEvalPrompt.UtteranceFrom(prompt),
            MaxRounds: DiscoveryState.DefaultMaxDiscoveryRounds,
            ChatClient: _chatClient,
            Retriever: _retriever,
            Progress: recorder,
            Nodes: _dryRun
                ? new DiscoveryNodeOverrides(Presenter: new DeterministicPresenter(catalogue, recorder, print: false))
                : null);

        // ⚠ The customer-facing panel is DISCARDED, not disabled. On the live path the presenter is
        // the SHIPPED ModelPresenter — the stage under test — so it is not swapped out; it runs,
        // screens through the real guardrail pipeline, composes the answer and writes it to the
        // console exactly as the demo does. N repetitions × two personas × a full recommendation
        // tray would bury this eval's own report, so its BYTES go to a null writer for the duration
        // of the turn. Nothing about the run changes: the state this method then reads — presented
        // items, rounds, stop reason, degraded notes, model calls — is identical either way. (The
        // dry-run path swaps in the deterministic presenter with print:false for the same reason;
        // there the presenter is not the thing being measured.)
        DiscoveryRunResult result;
        TextWriter previousOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            result = await GalaxusDiscoveryLoop
                .RunAsync(userId, options, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        LastResult = result;

        var trace = new ScriptedTrace();
        foreach (PresentedItem item in result.State.Presented)
            trace.Present(item.ProductId, item.WhyThis, item.Evidence, item.OutOfStock);

        // ModelId is stamped only when a model actually ran. A deployment name on a fully-degraded
        // turn is the one line a reader would quote as evidence that the model produced this answer.
        //
        // ⚠ And the same discipline on the token count, which is the whole of item 8.17 on this arm.
        //   The harness estimates from ActualOutput when TokenUsage is null, and this arm's
        //   ActualOutput is REPLAYED from workflow state — so the estimate was of a string nothing
        //   was billed for, which is how a `USD 0.0062` artefact got printed under a cost heading.
        //   The workflow now carries the provider's own usage, so hand that over instead — but ONLY
        //   when it is complete. Setting TokenUsage sets TokensAreEstimated = false, so a partial
        //   total (some call returned no usage block) would be published as a measured whole. When
        //   it is partial, leave it null and let PrintWorkflowChatSpend name the absence.
        ChatSpendSnapshot spend = result.State.Spend.Snapshot();
        var usage = spend.Complete
            ? new TokenUsage
            {
                PromptTokens = (int)spend.PromptTokens,
                CompletionTokens = (int)spend.CompletionTokens,
            }
            : null;

        return trace.Say(result.State.FinalAnswer)
                    .ToResponse(modelId: result.State.ModelCalls > 0 ? Config.Model : null, usage: usage);
    }
}

/// <summary>
/// The dry run's stubs, and the arithmetic their sequences were CHOSEN to produce.
/// </summary>
/// <remarks>
/// <para>
/// A dry run that can only fail by crashing proves that the code runs, not that it is right. The
/// agent plan below moves the SET while holding the LEAD fixed, so the two metrics the report
/// separates are separately checkable: mean pairwise Jaccard has one exact expected value, top-item
/// stability has another, and a metric stuck at any constant fails at least one of them.
/// </para>
/// </remarks>
internal static class Eval08Stubs
{
    /// <summary>The lead product every repetition presents first. Real, in-stock, catalogue-resident.</summary>
    public const string FixedLeadSku = "GLX-8003";

    /// <summary>The second product every repetition presents.</summary>
    public const string SecondSku = "GLX-2001";

    /// <summary>The third product every repetition presents EXCEPT the odd one out.</summary>
    public const string ThirdSku = "GLX-2006";

    /// <summary>The third product the odd repetition presents instead.</summary>
    public const string SwappedSku = "GLX-7001";

    /// <summary>
    /// The sku list repetition <paramref name="runIndex"/> presents: the same three every time,
    /// except run index 2, which swaps the third.
    /// </summary>
    /// <param name="runIndex">0-based repetition index.</param>
    public static string[] PresentationPlan(int runIndex) =>
        runIndex == 2
            ? [FixedLeadSku, SecondSku, SwappedSku]
            : [FixedLeadSku, SecondSku, ThirdSku];

    /// <summary>
    /// The mean pairwise Jaccard the plan produces at <paramref name="runs"/> repetitions, computed
    /// here from the plan's shape rather than measured from the run.
    /// </summary>
    /// <remarks>
    /// One repetition differs from the other (n-1) in one of three products, so its pairs score
    /// 2/4 = 0.5 and every other pair scores 1.0. With C(n,2) pairs in total and (n-1) of them
    /// involving the odd repetition, the mean is (C(n,2) - (n-1) + 0.5·(n-1)) / C(n,2).
    /// </remarks>
    /// <param name="runs">Repetitions.</param>
    public static double ExpectedMeanJaccard(int runs)
    {
        if (runs < 2) return double.NaN;
        if (runs < 3) return 1.0;                       // run index 2 does not exist below 3 runs
        double pairs = runs * (runs - 1) / 2.0;
        double odd = runs - 1;
        return (pairs - odd + 0.5 * odd) / pairs;
    }

    /// <summary>The distinct products the plan presents across <paramref name="runs"/> repetitions.</summary>
    /// <param name="runs">Repetitions.</param>
    public static int ExpectedSupport(int runs) => runs >= 3 ? 4 : 3;

    /// <summary>
    /// A chat client that answers every workflow model stage with the stub's unmistakable prose.
    /// </summary>
    /// <remarks>
    /// Every model stage of the discovery loop asks for structured JSON, so this prose fails to
    /// parse and each stage falls back to its deterministic composition and records a degraded note.
    /// That is the point: the dry run exercises the LIVE path — the model seam, the parse, the
    /// timeout guard and the fallback — rather than the offline path, and the resulting degraded-note
    /// count is what proves the liveness check on a paid run is checking something real.
    /// </remarks>
    public static StubChatClient DegradingWorkflowClient() => new(_ => []);
}
