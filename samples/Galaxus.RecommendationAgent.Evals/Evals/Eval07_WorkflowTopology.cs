// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// SNAPSHOT-POLICY: writes            eval07_topology — the topology record, byte-compared across waves

using System.Runtime.CompilerServices;
using AgentEval.Assertions;
using AgentEval.MAF;
using Galaxus.RecommendationAgent.Evals.Adapters;
using Galaxus.RecommendationAgent.Workflows;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 07 — <b>workflow topology</b>. The only eval in this suite whose subject is the GRAPH:
/// which executors ran, in what order, over which edges, and — the assertion this eval exists for
/// — <b>whether the conditional loop-back edge <c>CoverageReviewer → Discovery</c> actually
/// fired</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam this closes.</b> <see cref="RealDiscoveryLoopArm.LastResult"/> has carried the built
/// <c>Workflow</c> and the five executor ids since the arm was written, with a remark saying it is
/// there so that <c>MAFWorkflowAdapter.FromMAFWorkflow(…)</c> can assert
/// <c>HaveTraversedEdge("CoverageReviewer", "Discovery")</c> — and it ended with "nothing reads it
/// yet". This file reads it. Evals 01–04 grade what the loop PRODUCED; nothing until now graded
/// what the loop DID, and the whole Workflow-over-single-agent decision in the design rests on the
/// second thing.
/// </para>
///
/// <para><b>═══ WHAT ACTUALLY RUNS, AND WHAT IS REPLAYED — read this before quoting a number ═══</b></para>
///
/// <list type="number">
///   <item><description>
///   <b>The workflow runs for real.</b> Each case invokes the bound
///   <see cref="RealDiscoveryLoopArm"/>, which runs <c>GalaxusDiscoveryLoop.RunAsync</c> through
///   MAF's own <c>InProcessExecution</c>: five executors, five conditional edges, the shipped
///   routing predicates. The route trace, the round counter and the super-step count in this
///   report are all produced by that run.
///   </description></item>
///   <item><description>
///   <b>The GRAPH is MAF's own.</b> <c>MAFWorkflowAdapter.FromMAFWorkflow(run.Workflow, …)</c>
///   reflects the built workflow through <c>Workflow.ReflectEdges()</c>, and that is where the five
///   declared nodes and the five declared edges — <c>CoverageReviewer → Discovery</c> among them —
///   come from. Nothing about the topology is authored here.
///   </description></item>
///   <item><description>
///   <b>The TRAVERSAL is replayed, and that is not a shortcut — the bridge cannot drive this
///   workflow.</b> <c>MAFWorkflowAdapter.ExecuteWorkflowAsync(prompt)</c> goes through
///   <c>MAFWorkflowEventBridge</c>, which sends the workflow a <c>string</c>. Every executor in this
///   graph has a <c>[MessageHandler]</c> that takes a <c>DiscoveryState</c>, so the string is
///   undeliverable. Both directions were measured on this graph before this file was written:
///   handed the already-executed <c>run.Workflow</c> MAF refuses outright — <i>"Cannot use a
///   Workflow that is already owned by another runner or parent workflow"</i> — and handed a
///   FRESH, never-run workflow the bridge returns <b>zero steps, zero edges and no error at all</b>.
///   The second one is the dangerous one: a silent empty result. So the events AgentEval assembles
///   its <c>WorkflowExecutionResult</c> from are replayed here from
///   <see cref="DiscoveryRunResult.RoutesTaken"/> — the ids the workflow's own edge predicates
///   published as they fired — and every step output is a fact read back off the final
///   <c>DiscoveryState</c>. Nothing is invented and nothing is timed by the replay.
///   </description></item>
/// </list>
///
/// <para>
/// ⚠ <b>The replay's clock is not the run's clock.</b> <c>WorkflowExecutionResult.TotalDuration</c>
/// measures the replay, which takes microseconds. This eval therefore never sets
/// <c>WorkflowTestCase.MaxDuration</c> and never calls <c>HaveCompletedWithin</c> — both would read
/// the replay's clock and pass unconditionally, which is not an assertion. Latency is reported from
/// the real turn, timed by <c>MAFEvaluationHarness</c> around <c>arm.InvokeAsync</c>, and from
/// <see cref="DiscoveryRunResult.Elapsed"/>, timed inside the loop.
/// </para>
///
/// <para><b>═══ THE THREE WITNESSES — why the edge claim is not the edge's own word ═══</b></para>
///
/// <para>
/// A route event is published by the edge PREDICATE, and a predicate returning true is not by
/// itself proof that a message was delivered. So the loop-back claim is never read off the route
/// trace alone. Three separately-produced integers have to agree:
/// </para>
/// <list type="bullet">
///   <item><description><b>the route trace</b> — how many times <c>review-to-more-discovery</c>
///   fired, published by the edge predicate in <c>DiscoveryWorkflowFactory</c>;</description></item>
///   <item><description><b>the round counter</b> — <c>DiscoveryState.DiscoveryRound</c>, incremented
///   by the PRODUCER at the end of each completed round, inside <c>CatalogueDiscoverySearch</c>,
///   nowhere near an edge;</description></item>
///   <item><description><b>MAF's super-step count</b> — <c>SuperStepCompletedEvent</c>s counted off
///   MAF's own scheduler stream in <c>GalaxusDiscoveryLoop</c>.</description></item>
/// </list>
/// <para>
/// The graph forces two identities between them: <c>Discovery</c> has exactly two incoming edges and
/// the mapper runs once, so <c>loop-backs = rounds − 1</c>; and one super-step per executor
/// activation gives <c>super-steps = 2·rounds + 3</c>. Both are checked on every case. A loop-back
/// predicate that fired without delivering, or a delivery without a predicate, breaks the first; a
/// changed graph breaks the second. That check needs no pinned expectation at all.
/// </para>
///
/// <para><b>═══ BOTH DIRECTIONS — an assertion that can only pass is not an assertion ═══</b></para>
///
/// <para>
/// The corpus is a 2×2, and all four cells are occupied by real customers on the shipped
/// deterministic path: {loops, does not loop} × {approved exit, degraded PARTIAL exit}. The SAME
/// <c>HaveTraversedEdge("CoverageReviewer", "Discovery")</c> is run on every case; on a case pinned
/// to loop it must validate, and on a case pinned not to loop it must FAIL. No constant answer can
/// pass this eval — see <see cref="PrintFloors"/> for the derived floors.
/// </para>
/// <para>
/// Occupying all four cells is what stops "the loop-back fired" being read as a proxy for "the run
/// degraded". Renzo loops twice and still exits APPROVED; Luca never loops and still exits PARTIAL.
/// </para>
///
/// <para><b>═══ ⚠ WHAT MAKES THE EDGE FIRE, MEASURED 2026-09-06 (Wave 3) ═══</b></para>
///
/// <para>
/// The rows below used to tell a reader that a looping customer "leaves round 1 with gaps the
/// reviewer can still act on". <b>On the shipped deterministic corpus that is false for every one
/// of them.</b> Measured, per case, by the advisory row <c>what opened the gap the loop-back edge
/// read</c>: <b>0 of 4</b> non-abstention cases ever had a gap written against a MAPPER interest,
/// and each round's own assessment says <c>0 gap(s) with a concrete next query</c>. The gap the
/// edge predicate reads is written by ACCEPTING a mid-run interest proposed from review text —
/// nobody has searched it yet, so <c>CoverageVerdictProjection.Project</c>'s second structural veto
/// refuses to approve over it, and the run goes round again.
/// </para>
/// <para>
/// Proven in both directions by ablation, not inferred: forcing
/// <c>ReviewSnippetInterestProposer.Propose</c> to return null makes <b>every</b> case stop at
/// round 1 and takes GATE B from 4 of 5 pins matching to <b>2 of 5</b> (Renzo, Marco AND Mirjam
/// all fail). So on this arm the loop-back's discriminator is not coverage completeness — it is
/// whether one review-snippet proposal survived <c>QueryVocabulary</c>. That is reported and never
/// gated; the pins still carry the direction, and gating the mechanism would pin the eval to
/// whichever mechanism happens to be load-bearing this month.
/// </para>
/// <para>
/// ⚠ <b>GATE B's live failure (<c>USR-RB-10</c>) is downstream of exactly this.</b> Renzo's one
/// proposal is refused because every one of its four terms is out of vocabulary
/// (<c>vierundzwanzig · hundertfünf · deckt · strasse</c>, off a German review of a lens his
/// contentless session utterance retrieved). <b>The prescribed remedy was built and measured and
/// it is REFUSED</b> — see <c>MEASUREMENT_STATUS</c> §28: making the proposer rank snippets by
/// terms the vocabulary would admit puts Renzo back on his pin exactly (loops twice, exits
/// APPROVED) and then flips <b>Nadia</b>, the ⭐ negative-direction case, so GATE B still fails,
/// the corpus's non-looping direction collapses from two cases to one, and the loop-back edge
/// becomes effectively unconditional. A fix that leaves the gate red and weakens the control it
/// was meant to serve is not a fix.
/// </para>
///
/// <para><b>═══ WHAT THIS EVAL DOES NOT PROVE ═══</b></para>
///
/// <list type="bullet">
///   <item><description>
///   <b>Nothing about the agent.</b> The bound arm runs the loop's DETERMINISTIC path — zero model
///   calls, no credentials, nothing spent. Every number here is a fact about the loop's MECHANICS.
///   It is not evidence that a model-backed reviewer would loop, or would stop.
///   </description></item>
///   <item><description>
///   <b>Nothing about recommendation quality.</b> A run can traverse every edge in the right order
///   and recommend badly. Eval 02 is the coverage measurement; this one would be green either way.
///   </description></item>
///   <item><description>
///   <b>Not that the exit edge distinguishes an approved run from a degraded one.</b> It cannot, by
///   design: approval and exhaustion leave the reviewer through the SAME
///   <c>review-to-ranker</c> edge. The discriminator is state, not topology, and this eval checks
///   the state agreement rather than pretending the route carries it.
///   </description></item>
///   <item><description>
///   <b>Not the round-cap termination.</b> No customer in this corpus reaches
///   <c>round-limit-reached</c> — Marco exhausts his queries at round 3 instead. It is printed as an
///   instrument finding, not gated. The demo lane's <c>DiscoveryTerminationProbe</c> forces that
///   condition with a scripted reviewer; this eval deliberately does not, because its subject is
///   what the shipped loop does on real customers.
///   </description></item>
///   <item><description>
///   <b>Not tool behaviour inside executors.</b> The deterministic path calls no tools through MAF,
///   so <c>HaveNoToolErrors()</c> here could never fail and is not used. (The API note stands
///   generally: workflow <c>ToolCallRecord.Exception</c> is always null.)
///   </description></item>
/// </list>
///
/// <para><b>═══ A FINDING FROM THIS EVAL'S FIRST RUN, KEPT HERE BECAUSE IT SHAPED THE FILE ═══</b></para>
///
/// <para>
/// The chain originally carried a flat <c>HaveNonEmptyOutput(because: "the Presenter always
/// composes an answer, PARTIAL or not")</c>. It failed, on Luca — and the assertion was wrong, not
/// the loop: with nothing to present, <c>DeterministicPresenter</c> leaves
/// <c>DiscoveryState.FinalAnswer</c> at <b>zero characters</b>. The Presenter executor still runs
/// and the Ranker → Presenter edge is still traversed; the tray is simply empty.
/// </para>
/// <para>
/// The tempting repair — assert non-empty output only where the run happened to present something —
/// is the one that must not be made. That takes the assertion's APPLICABILITY from the RESULT, so a
/// run that silently presented nothing would exempt itself from the check by failing it. The
/// applicability is taken from the INPUT instead: <see cref="TopologyCase.PresentsAnswerText"/> is
/// authored per customer, and the eval checks the BICONDITIONAL — text if and only if items — in
/// both directions, on a corpus that contains one of each.
/// </para>
///
/// <para>
/// ⏱️ Runtime: well under a second for all five cases. No model calls, no credentials, no network,
/// nothing spent — which is why this eval prints no Azure target: there is no paid call to warn
/// about, and printing a deployment name a run never contacts is how a reader concludes a model was
/// involved.
/// </para>
/// </remarks>
public static class Eval07_WorkflowTopology
{
    /// <summary>Storage key for this eval's snapshot. Distinct from every other eval's.</summary>
    public const string SnapshotKey = "eval07_topology";

    /// <summary>The workflow pattern label carried on the adapter.</summary>
    public const string WorkflowPattern = "ConditionalLoop";

    /// <summary>
    /// Runs the topology eval.
    /// </summary>
    /// <param name="dryRun">
    /// True runs ONE case and asserts only the PLUMBING — the binding, the handle, the reflected
    /// graph, the id mapping, the replay, the three witnesses, and that the edge assertion is
    /// capable of returning false. It writes no snapshot and prints no topology verdicts. It spends
    /// nothing, but then neither does the full run: what a dry run buys here is a fast, loud check
    /// that the instrument is wired, and it can and does fail.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 when every gate passed, 1 when one failed.</returns>
    public static async Task<int> RunAsync(bool dryRun = false, CancellationToken ct = default)
    {
        PrintHeader(dryRun);

        // No credential guard and no Config.PrintAzureTarget() here, deliberately: this eval makes
        // no model call on any path. The honest banner is the "nothing spent" line in the header,
        // not an Azure deployment name that would never be contacted. Compare Eval 04, which is in
        // the same position.
        if (!DiscoveryLoopAdapter.IsBound)
        {
            EvalPrinter.PrintRefusal(
                "Eval 07 refused to run: there is no workflow to inspect.",
                DiscoveryLoopAdapter.AbsenceReason
              + " This eval's entire subject is the graph, so there is nothing to substitute and nothing "
              + "partial to report. It refuses rather than printing a topology for something else.");
            return 1;
        }

        var retriever = await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);
        var context = new CoverageArmContext(
            retriever,
            LiveAgentFactory: () => throw new InvalidOperationException(
                "Eval 07 runs no live agent. Its subject is the workflow graph, and the bound arm runs the "
              + "loop's deterministic path."),
            DryRun: dryRun);

        var harness = new MAFEvaluationHarness(verbose: false);
        var options = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            // No evaluator is supplied, so EvaluateResponse would have nothing to run. Said
            // explicitly so nobody reads a missing judge as a judge that scored 100.
            EvaluateResponse = false,
            Verbose = false,
            ModelName = "(no model — deterministic discovery loop)",
        };

        var observations = new List<Observation>();

        foreach (TopologyCase topologyCase in dryRun ? [Cases[0]] : Cases)
        {
            Observation observation = await ObserveAsync(topologyCase, context, harness, options, ct)
                .ConfigureAwait(false);

            observations.Add(observation);
            PrintCase(observation);

            if (observation.Refusal is not null) return 1;
        }

        return dryRun
            ? PlumbingGate(observations[0])
            : Report(observations);
    }

    // ══ The corpus ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The five cases: a 2×2 over {loops, does not loop} × {approved, degraded}, plus a second
    /// looping-and-degraded customer that reaches the loop by a different route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="TopologyCase.ExpectsLoopBack"/> is a PIN, and it is declared as one.</b> It was
    /// authored by measuring the shipped deterministic loop on this corpus, which means the artifact
    /// under test supplied the value. That is exactly the shape this repository's gate
    /// self-examination rule warns about, so two things are true about how it is used:
    /// </para>
    /// <list type="number">
    ///   <item><description>the pin is NOT what makes the loop-back gate meaningful — what makes it
    ///   meaningful is that the corpus contains BOTH values, so no constant answer can pass;
    ///   and</description></item>
    ///   <item><description>the three-witness agreement gate uses no pin at all: it is an equality
    ///   between the route trace, the round counter and MAF's super-step count, each produced by a
    ///   different part of the system.</description></item>
    /// </list>
    /// <para>
    /// A pin that goes stale therefore fails LOUDLY and says so. If a customer's history or the
    /// catalogue changes such that Nadia starts looping, this eval fails — and that is the correct
    /// outcome, because the alternative is an eval that quietly stops testing the direction it was
    /// built for.
    /// </para>
    /// <para>
    /// ⚠ <b>CORRECTED 2026-09-06 (Wave 4): Marco's and Mirjam's descriptions were each other's.</b>
    /// Marco's said <i>"Two loop-backs, three rounds … gaps-unresolvable, not the round cap"</i> and
    /// Mirjam's said <i>"LOOPS ONCE and exits DEGRADED on no-progress"</i>. Measured on every run
    /// this eval has ever printed: Marco is <b>1 loop-back, 2 rounds, `no-progress`, 11 items</b> and
    /// Mirjam is <b>2 loop-backs, 3 rounds, `gaps-unresolvable`, 8 items</b>. Both cells exist and
    /// both are the ones the design wanted; they were attached to the wrong customer. <b>No pin
    /// moved and no verdict moved</b> — <c>ExpectsLoopBack</c> and <c>PresentsAnswerText</c> are
    /// identical for both, and both cases passed GATE B before and after. What was wrong is the
    /// sentence a reader diagnosing a GATE B failure meets first, in the eval that is currently
    /// red. Held by Eval 03's gating row <c>TopologyCaseProseMatchesTheRun</c>, which compares the
    /// stop reason a case's own prose NAMES against the one the run produces.
    /// </para>
    /// <para>
    /// ⚠ <b>CORRECTED AGAIN 2026-09-06 (Wave 4 verification run): the Wave-4 correction above was
    /// right in ONE space and wrong in the other, and nobody had run the row in the other one.</b>
    /// The deterministic loop is <b>not space-invariant</b>. Measured, all five cases, both spaces:
    /// </para>
    /// <list type="table">
    ///   <item><description><c>USR-RB-10</c> Renzo — 0/1/<c>coverage-sufficient</c> in BOTH (the pin
    ///   says he must loop; he does not; that is GATE B's live failure)</description></item>
    ///   <item><description><c>USR-MI-02</c> Marco — concept <b>1 loop-back / 2 rounds /
    ///   no-progress</b>, real <b>2 / 3 / gaps-unresolvable</b></description></item>
    ///   <item><description><c>USR-MB-13</c> Mirjam — concept <b>2 / 3 / gaps-unresolvable
    ///   (DEGRADED)</b>, real <b>1 / 2 / coverage-sufficient (APPROVED)</b></description></item>
    ///   <item><description><c>USR-NB-01</c> Nadia — 0/1/<c>coverage-sufficient</c> in BOTH</description></item>
    ///   <item><description><c>USR-LF-04</c> Luca — 0/1/<c>gaps-unresolvable</c> in BOTH</description></item>
    /// </list>
    /// <para>
    /// So Marco and Mirjam <b>swap round counts</b> between the spaces, Mirjam's exit disposition
    /// flips DEGRADED → APPROVED, and <c>no-progress</c> is not reachable at all on the real path.
    /// A single sentence describing "the run" is therefore wrong in whichever space it was not
    /// written for — and the Wave-4 sentence was written for the concept space, which is why
    /// <c>-- 3 --real-vectors</c> exited <b>1</b> the first time anyone ran it after that fix.
    /// </para>
    /// <para>
    /// ⚠ <b>And a THIRD case was wrong in both spaces, which the Wave-4 row could not see.</b>
    /// Renzo's text asserted, in the present tense, that <i>"the reviewer sends him back for more
    /// discovery twice and then approves"</i>. He has never done that on the shipped tree in either
    /// space — that is precisely the failure GATE B reports two lines below the sentence. The row
    /// missed it because Renzo's text named no frozen stop reason and the row only examined cases
    /// that did — the scope limit declared in <c>MEASUREMENT_STATUS</c> §41.4, realised. Every case
    /// now carries an <c>OBSERVED PER SPACE</c> clause and the row requires all five.
    /// </para>
    /// <para>
    /// <b>The clause is a PIN on the description, not on the verdict.</b> It records what the
    /// deterministic loop does; it is not <c>ExpectsLoopBack</c> and it cannot make a gate pass.
    /// Renzo's clause deliberately records a run that contradicts his pin, and the prose says so.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TopologyCase> Cases { get; } =
    [
        new(Personas.RenzoUserId, "Renzo Bianchi", ExpectsLoopBack: true, PresentsAnswerText: true,
            "The cell that stops 'the loop-back fired' being read as a synonym for 'the run degraded': the "
          + "PIN says the reviewer must send him back for more discovery and then approve anyway. ⚠ THE PIN "
          + "IS UNMET AND THAT IS GATE B's LIVE FAILURE — he exits at round 1 in BOTH spaces, so the "
          + "OBSERVED clause below records a run that contradicts ExpectsLoopBack on purpose. Why it is "
          + "refused rather than re-pinned, and what the remedy costs, is MEASUREMENT_STATUS §28, §36 "
          + "and §42. "
          + "OBSERVED PER SPACE: ConceptVectors 0 loop-backs / 1 round / coverage-sufficient · "
          + "RealVectors 0 loop-backs / 1 round / coverage-sufficient."),

        new(Personas.MarcoUserId, "Marco Iten", ExpectsLoopBack: true, PresentsAnswerText: true,
            "LOOPS and exits DEGRADED in both spaces. He is the only customer in this corpus that ever "
          + "reaches the stop reason dedup makes possible — a round that re-finds what it already had adds "
          + "zero NEW ids, so the loop stops instead of spending the rest of its budget — and he reaches it "
          + "in the CONCEPT space only. The PARTIAL answer still leaves through the same exit edge, which is "
          + "the property the graph is built for. "
          + "OBSERVED PER SPACE: ConceptVectors 1 loop-back / 2 rounds / no-progress · "
          + "RealVectors 2 loop-backs / 3 rounds / gaps-unresolvable."),

        new(Personas.MirjamUserId, "Mirjam Bosshard", ExpectsLoopBack: true, PresentsAnswerText: true,
            "LOOPS in both spaces, and the EXIT DISPOSITION is the one thing in this corpus that the "
          + "embedding space flips: the reviewer spends the whole budget and stops on its own judgement "
          + "rather than on the counter in the concept space, and is satisfied a round earlier on the real "
          + "one. Never the round cap either way. She is why the per-space clause exists at all — a single "
          + "sentence describing 'the run' was wrong in whichever space it was not written for. "
          + "OBSERVED PER SPACE: ConceptVectors 2 loop-backs / 3 rounds / gaps-unresolvable · "
          + "RealVectors 1 loop-back / 2 rounds / coverage-sufficient."),

        new(Personas.NadiaUserId, "Nadia Brunner", ExpectsLoopBack: false, PresentsAnswerText: true,
            "⭐ THE NEGATIVE DIRECTION. Coverage is satisfied in round 1, so the loop-back edge must NOT "
          + "have been traversed. The same assertion that must validate on the three cases above must FAIL "
          + "here — an edge that fires unconditionally is a bug that a positive-only test cannot see. "
          + "OBSERVED PER SPACE: ConceptVectors 0 loop-backs / 1 round / coverage-sufficient · "
          + "RealVectors 0 loop-backs / 1 round / coverage-sufficient."),

        new(Personas.LucaUserId, "Luca Ferrari", ExpectsLoopBack: false, PresentsAnswerText: false,
            "The fourth cell: does NOT loop and still exits DEGRADED. One purchase, so the map is thin and "
          + "the reviewer has nothing runnable left after round 1. He is the suite's ABSTENTION persona and "
          + "presents nothing — measured here, that means a zero-character FinalAnswer — so he is scored on "
          + "ROUTING, and on the text/items biconditional, never on the content of an answer he correctly "
          + "did not give. "
          + "OBSERVED PER SPACE: ConceptVectors 0 loop-backs / 1 round / gaps-unresolvable · "
          + "RealVectors 0 loop-backs / 1 round / gaps-unresolvable."),
    ];

    /// <summary>One customer, and what the graph is expected to do for them.</summary>
    /// <param name="PersonaId">A customer id from <see cref="Personas.AllPersonaIds"/>.</param>
    /// <param name="DisplayName">The customer's name, for the report.</param>
    /// <param name="ExpectsLoopBack">
    /// True when <c>CoverageReviewer → Discovery</c> must have been traversed at least once. A PIN —
    /// see the remarks on <see cref="Cases"/> for what it is and is not allowed to carry.
    /// </param>
    /// <param name="PresentsAnswerText">
    /// True when this customer has reachable candidates and the Presenter is therefore expected to
    /// compose a non-empty answer. <b>Authored per customer, from the INPUT.</b> Deriving it from the
    /// run's own output instead would let a run that silently presented nothing exempt itself from
    /// the check by failing it — see the finding in the type remarks.
    /// </param>
    /// <param name="Why">
    /// Why this case is in the corpus, in the report's own words. <b>It MUST end with an
    /// <c>OBSERVED PER SPACE:</c> clause naming, for every non-<c>Auto</c> member of
    /// <c>EmbeddingSpaceChoice</c>, the loop-back count, the round count and the frozen stop reason
    /// that member produces</b> — format <c>&lt;Member&gt; &lt;n&gt; loop-back(s) / &lt;m&gt;
    /// round(s) / &lt;reason&gt;</c>, separated by <c>·</c>. Eval 03's gating row
    /// <c>TopologyCaseProseMatchesTheRun</c> parses it, checks all three numbers against the run in
    /// the space this process RESOLVED (never the one it requested — <c>--real-vectors</c> falls
    /// back to concept without credentials), and refuses a case that names a frozen stop reason
    /// anywhere OUTSIDE such a clause, because that is exactly the space-blind sentence the clause
    /// exists to retire.
    /// </param>
    public sealed record TopologyCase(
        string PersonaId,
        string DisplayName,
        bool ExpectsLoopBack,
        bool PresentsAnswerText,
        string Why);

    // ══ One case ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs one case end to end: the real loop turn, the reflected graph, the replay, and the
    /// AgentEval assertion chain.
    /// </summary>
    private static async Task<Observation> ObserveAsync(
        TopologyCase topologyCase,
        CoverageArmContext context,
        MAFEvaluationHarness harness,
        EvaluationOptions options,
        CancellationToken ct)
    {
        // ── the arm ──────────────────────────────────────────────────────────────────────────
        //
        // Through DiscoveryLoopAdapter.Create, never `new RealDiscoveryLoopArm(...)`: the ONE
        // BINDING in Program.cs is what guarantees every eval in this suite is looking at the same
        // arm. The type test is not a downcast of convenience — LastResult (the Workflow and the
        // executor ids) is not on IDiscoveryLoopArm, and it is the only handle this eval has.
        // Anything else bound here is refused rather than inspected as if it were the loop.
        if (DiscoveryLoopAdapter.Create(context) is not RealDiscoveryLoopArm arm)
        {
            return Observation.Refused(topologyCase,
                "The bound discovery loop is not a RealDiscoveryLoopArm, so it exposes no Workflow handle. "
              + "Eval 07 has nothing to reflect and refuses rather than reporting a topology for whatever "
              + "else was bound.");
        }

        string prompt = GalaxusEvalPrompt.For(topologyCase.PersonaId, Personas.CanonicalPromptFor(topologyCase.PersonaId));

        var testCase = new TestCase
        {
            Name = $"topology · {topologyCase.PersonaId}",
            Input = prompt,
            PassingScore = 0,
        };

        TestResult turn;
        using (EvalRuntime.BeginTurn())
        {
            turn = await harness.RunEvaluationAsync(arm, testCase, options, ct).ConfigureAwait(false);
        }

        if (turn.HasError)
        {
            return Observation.Refused(topologyCase,
                $"The loop turn threw ({turn.Error?.GetType().Name}: {turn.Error?.Message}). A turn that threw "
              + "produced no route trace, and an empty trace grades as a graph that never took a wrong edge — "
              + "the flattering direction. Refused.");
        }

        if (arm.LastResult is not { } run)
        {
            return Observation.Refused(topologyCase,
                "The arm completed but LastResult is null, so there is no Workflow and no route trace to read. "
              + "That is the handle this eval exists to consume; without it there is nothing to measure.");
        }

        // ── PLAN ITEM 1.3 / V-3: a run that LOST AN EXECUTOR did not measure the topology ────
        //
        //   DiscoveryRunResult.ExecutorFailures has existed since correction ⑦ and, until
        //   2026-09-06, was read by the demo surface and Eval 09 alone. A partially-failed run
        //   still produced a Workflow, a route trace and a stop reason — so every gate below
        //   graded it, on a trace missing whatever the failed node would have contributed. That is
        //   the flattering direction twice over: a node that never ran took no wrong edge, and a
        //   loop that died early cannot exceed a round cap.
        if (run.Failed)
        {
            return Observation.Refused(topologyCase,
                $"{run.ExecutorFailures.Count} executor(s) FAILED in this run: "
              + string.Join(" · ", run.ExecutorFailures)
              + ". The workflow still produced a trace, and grading it would score a graph that is missing "
              + "whatever the failed node would have contributed — a node that never ran took no wrong edge, "
              + "and a loop that died early cannot exceed a round cap. Refused, not scored.");
        }

        // ── the persona wiring check, in the direction that hurts ────────────────────────────
        //
        // RealDiscoveryLoopArm reads the customer out of the PROMPT and falls back to Nadia when it
        // finds no id. A frame that stopped carrying the id would therefore run all five cases as
        // Nadia and print five identical, self-consistent rows — every gate green, one customer
        // measured. So the state's own CustomerId is compared against the case.
        if (!string.Equals(run.State.CustomerId, topologyCase.PersonaId, StringComparison.Ordinal))
        {
            return Observation.Refused(topologyCase,
                $"The turn ran customer '{run.State.CustomerId}' but the case is '{topologyCase.PersonaId}'. The "
              + "arm reads the customer from the prompt and falls back to a default when it finds no id, so this "
              + "is a harness fault that would otherwise print five rows about one persona.");
        }

        // ── the GRAPH, straight out of MAF ───────────────────────────────────────────────────
        //
        // FromMAFWorkflow does no execution: it calls Workflow.ReflectEdges() and maps MAF's real
        // "{CleanName}_{guid}" ids onto the five frozen clean names. Only the GraphDefinition is
        // taken from it — its executor func cannot drive this workflow (see the type remarks).
        var reflected = MAFWorkflowAdapter.FromMAFWorkflow(
            run.Workflow, DiscoveryWorkflowFactory.WorkflowName, run.ExecutorIds, WorkflowPattern);

        if (reflected.GraphDefinition is not { } graph)
        {
            return Observation.Refused(topologyCase,
                "MAF reflected no graph off the built workflow, so there are no declared edges to compare the "
              + "traversal against. Every structural assertion below would then be checking the trace against "
              + "itself.");
        }

        // ── the replay, and the harness that consumes it ─────────────────────────────────────
        var adapter = new MAFWorkflowAdapter(
            DiscoveryWorkflowFactory.WorkflowName,
            (_, replayCt) => ReplayAsync(run, replayCt),
            run.ExecutorIds,
            WorkflowPattern,
            graph);

        var workflowHarness = new WorkflowEvaluationHarness(verbose: false);

        var workflowCase = new WorkflowTestCase
        {
            Name = $"topology · {topologyCase.PersonaId}",
            Input = prompt,
            Description = topologyCase.Why,
            ExpectedExecutors = DiscoveryExecutorIds.All,

            // Non-strict on purpose. StrictExecutorOrder is a SequenceEqual against the step list,
            // and a looping run revisits Discovery and CoverageReviewer — so strict order would fail
            // every case that loops, which is to say it would fail for the reason this eval exists.
            // Order is asserted below against a walk reconstructed from the route ids instead.
            StrictExecutorOrder = false,

            // MaxDuration is deliberately NOT set: the harness would compare the REPLAY's clock,
            // which is microseconds, and pass unconditionally. Real latency is reported instead.
            Tags = ["topology", "loop-back", topologyCase.ExpectsLoopBack ? "expects-loop" : "expects-no-loop"],
        };

        WorkflowTestResult workflowResult = await workflowHarness
            .RunWorkflowTestAsync(adapter, workflowCase, new WorkflowTestOptions
            {
                Timeout = TimeSpan.FromMinutes(1),
                CaptureTelemetry = true,
                Verbose = false,
            }, ct)
            .ConfigureAwait(false);

        if (workflowResult.ExecutionResult is not { } exec)
        {
            return Observation.Refused(topologyCase,
                $"The workflow harness produced no execution result ({workflowResult.Error?.GetType().Name ?? "no error reported"}). "
              + "There is no step list and no traversed-edge list, so nothing below could fail — which is why it is "
              + "refused rather than scored.");
        }

        // ── the three witnesses ──────────────────────────────────────────────────────────────
        int loopBacksInTrace = run.RoutesTaken.Count(
            id => string.Equals(id, DiscoveryRouteIds.ReviewToMoreDiscovery, StringComparison.Ordinal));
        int rounds = run.State.DiscoveryRound;
        int superSteps = run.SuperSteps;

        bool roundsAgree = loopBacksInTrace == rounds - 1;
        bool superStepsAgree = superSteps == (2 * rounds) + 3;

        // ── the assertion this eval exists for ───────────────────────────────────────────────
        //
        // ⚠ WorkflowAssertionBuilder is DEFERRED: it accumulates failures and throws nothing until
        // Validate() is called. Reading IsValid first is what lets the SAME assertion be run on a
        // case that must pass it and on a case that must fail it. Validate() is then called only
        // where a pass is required — a builder that is never validated enforces nothing, and that
        // silence is the trap this comment exists to keep out of the file.
        var loopBackProbe = exec.Should().HaveTraversedEdge(
            DiscoveryExecutorIds.CoverageReviewer,
            DiscoveryExecutorIds.Discovery,
            because: "the loop-back edge is the whole reason this is a Workflow and not a single agent");

        bool loopBackTraversed = loopBackProbe.IsValid;

        // ── the negative capability check ────────────────────────────────────────────────────
        //
        // An edge the graph does not contain must come back false on EVERY case, looping or not.
        // Without it, "IsValid was false" on the negative cases could equally mean the assertion is
        // broken and answers false to everything.
        bool impossibleEdgeRejected = !exec.Should()
            .HaveTraversedEdge(DiscoveryExecutorIds.Presenter, DiscoveryExecutorIds.InterestMapper)
            .IsValid;

        // ── the rest of the structural chain ─────────────────────────────────────────────────
        //
        // ⚠ THREE failures are captured separately, not one. They are different KINDS of fault and
        // they feed different gates: a wrong loop direction must not be reported as a broken graph,
        // and an empty answer must not be either. Folding them into one string was measured doing
        // exactly that — pinning Nadia to loop turned GATE A red as well, and a reader would have
        // gone looking for a topology regression that did not exist.
        string? structuralFailure = null;
        string? loopBackFailure = null;
        string? answerFailure = null;

        try
        {
            var builder = exec.Should()
                .HaveGraphStructure(because: "the graph is MAF's own ReflectEdges output, not an authored fixture")
                .HaveEntryPoint(DiscoveryExecutorIds.InterestMapper, because: "stage 1 is the workflow's start executor")
                .HaveNodes([.. DiscoveryExecutorIds.All])
                .HaveAtLeastSteps(5, because: "a completed run activates all five executors at least once")
                .HaveExecutedInOrderBecause(
                    "the step list must equal the walk reconstructed from the route ids, so the two "
                  + "observation channels cannot disagree about what ran",
                    [.. WalkFrom(run.RoutesTaken)])
                .HaveExecutionPathBecause(
                    "the traversed-edge chain must be a single connected path from the entry node",
                    [.. WalkFrom(run.RoutesTaken)])
                .HaveNoErrors(because: "an executor that failed would have been published as a Degraded event")
                .HaveConditionalRouting(because: "every edge in this graph carries a condition")
                .HaveUsedEdgeType(EdgeType.Conditional)
                .HaveTraversedEdge(DiscoveryExecutorIds.InterestMapper, DiscoveryExecutorIds.Discovery,
                    because: "round 1's queries come from the interest map")
                .HaveTraversedEdge(DiscoveryExecutorIds.Discovery, DiscoveryExecutorIds.CoverageReviewer,
                    because: "every round is reviewed")
                .HaveTraversedEdge(DiscoveryExecutorIds.CoverageReviewer, DiscoveryExecutorIds.Ranker,
                    because: "approval AND exhaustion leave through this one edge — the run always reaches the Ranker")
                .HaveTraversedEdge(DiscoveryExecutorIds.Ranker, DiscoveryExecutorIds.Presenter,
                    because: "the answer is always rendered, PARTIAL or not")
                .HaveRoutingDecision(DiscoveryExecutorIds.CoverageReviewer, DiscoveryRouteIds.ReviewToRanker,
                    because: "the reviewer's last decision is always the exit");

            foreach (string executorId in DiscoveryExecutorIds.All)
                builder = builder.HaveInvokedExecutor(executorId, because: "all five stages run on every completed turn");

            // ⭐ WITHOUT THIS LINE THE ENTIRE BLOCK ABOVE IS A NO-OP THAT REPORTS SUCCESS.
            builder.Validate();
        }
        catch (WorkflowAssertionException ex)
        {
            structuralFailure = ex.Message;
        }

        // ── the loop-back, where a pass is required — its OWN failure ───────────────────────
        if (topologyCase.ExpectsLoopBack)
        {
            try { loopBackProbe.Validate(); }
            catch (WorkflowAssertionException ex) { loopBackFailure = ex.Message; }
        }

        // ── the answer text, where a pass is required — its OWN failure ─────────────────────
        //
        // Scoped by the case's AUTHORED expectation, never by what this run produced. The abstention
        // persona is expected to end with an empty tray; the biconditional in GATE C checks the
        // other direction for him.
        if (topologyCase.PresentsAnswerText)
        {
            try
            {
                exec.Should()
                    .HaveNonEmptyOutput(because: "this customer has reachable candidates, so the Presenter composes a tray")
                    .Validate();
            }
            catch (WorkflowAssertionException ex) { answerFailure = ex.Message; }
        }

        string stopReason = arm.LastRun?.StopReason ?? "(no telemetry)";

        // ── the traversal is checked against MAF's DECLARATION, not against itself ───────────
        //
        // The traversed list is replayed from the route trace; the declared list is reflected off
        // the built workflow. An edge that was walked but never declared means the two channels
        // disagree about what this graph even is, and every assertion above would then be checking
        // the trace against a topology it invented.
        var declaredEdgeIds = graph.Edges
            .Select(e => $"{e.SourceExecutorId}->{e.TargetExecutorId}")
            .ToList();

        var traversedEdgePairs = (exec.Graph?.TraversedEdges ?? [])
            .Select(e => $"{e.SourceExecutorId}->{e.TargetExecutorId}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool everyTraversedEdgeIsDeclared = traversedEdgePairs.Count > 0
            && traversedEdgePairs.All(pair => declaredEdgeIds.Contains(pair, StringComparer.Ordinal));

        return new Observation
        {
            Case = topologyCase,
            Routes = run.RoutesTaken,
            Steps = [.. exec.Steps.Select(s => s.ExecutorId)],
            TraversedEdgeIds = [.. exec.Graph?.TraversedEdges?.Select(e => e.EdgeId) ?? []],
            TraversedEdgePairs = traversedEdgePairs,
            EveryTraversedEdgeIsDeclared = everyTraversedEdgeIsDeclared,
            DeclaredEdgeIds = declaredEdgeIds,
            GraphNodeIds = [.. graph.Nodes.Select(n => n.NodeId)],
            LoopBackTraversed = loopBackTraversed,
            LoopBacksInTrace = loopBacksInTrace,
            Rounds = rounds,
            MaxRounds = run.State.MaxRounds,
            SuperSteps = superSteps,
            RoundsAgree = roundsAgree,
            SuperStepsAgree = superStepsAgree,
            ImpossibleEdgeRejected = impossibleEdgeRejected,
            StructuralFailure = structuralFailure,
            LoopBackFailure = loopBackFailure,
            AnswerFailure = answerFailure,
            HarnessAssertions = workflowResult.AssertionResults ?? [],
            HarnessPassed = workflowResult.Passed,
            StopReason = stopReason,
            Approved = run.State.CoverageApproved,
            PartialAnswer = run.State.IsPartialAnswer,
            PresentedCount = run.State.Presented.Count,
            ProposalsMade = run.State.Proposals.Count,
            ProposalsAccepted = run.State.Proposals.Count(p => p.Accepted),
            ProposalRefusals = [.. run.State.Proposals.Where(p => !p.Accepted && p.Refusal is not null)
                                                     .Select(p => p.Refusal!)],
            MapperGapsAtAnyRound = run.State.Interests
                .Where(i => !i.IsReviewerInferred)
                .Count(i => !string.IsNullOrWhiteSpace(run.State.CoverageFor(i.Id).LastGapReason)),
            ModelCalls = run.State.ModelCalls,
            LoopElapsed = run.Elapsed,
            TurnElapsed = turn.Performance?.TotalDuration ?? TimeSpan.Zero,
            ReplayElapsed = exec.TotalDuration,
            EstimatedCost = turn.Performance?.EstimatedCost,
            TotalTokens = turn.Performance?.TotalTokens,
            TokensAreEstimated = turn.Performance?.TokensAreEstimated ?? true,
            PresentToolCalls = turn.ToolCallCount,
            FinalAnswerLength = run.State.FinalAnswer.Length,
        };
    }

    // ══ The replay ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Replays one completed run as the <see cref="WorkflowEvent"/> stream
    /// <see cref="MAFWorkflowAdapter"/> consumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything yielded here is read back off the run.</b> The edges are
    /// <see cref="DiscoveryRunResult.RoutesTaken"/> — the ids the workflow's own edge predicates
    /// published, with immediate repeats collapsed because MAF may evaluate a predicate more than
    /// once per super-step. The step outputs are facts from the final <c>DiscoveryState</c>: the
    /// interest labels, the per-round query log, the ranked and dropped counts, and the composed
    /// answer. No text is invented and no timing is fabricated — the adapter's clock measures this
    /// replay, and the report never presents that clock as the run's latency.
    /// </para>
    /// <para>
    /// <b>The walk is verified as it is emitted.</b> Each route id names a source and a target; if a
    /// route's source is not where the walk currently stands, the trace is not a connected path and
    /// this throws rather than emitting a graph that never happened. The exception surfaces as a
    /// refusal, never as a green run.
    /// </para>
    /// </remarks>
    /// <param name="run">The completed run.</param>
    /// <param name="ct">Cancellation.</param>
    private static async IAsyncEnumerable<WorkflowEvent> ReplayAsync(
        DiscoveryRunResult run,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var state = run.State;
        string current = DiscoveryExecutorIds.InterestMapper;
        int round = 0;

        yield return new ExecutorOutputEvent(current, DescribeMapper(state));

        foreach (string routeId in run.RoutesTaken)
        {
            ct.ThrowIfCancellationRequested();

            var (source, target) = Endpoints(routeId);

            if (!string.Equals(source, current, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The route trace is not a connected walk: route '{routeId}' leaves '{source}' but the walk "
                  + $"stands at '{current}'. A trace that cannot be walked is a wiring fault in the route "
                  + "instrumentation, and replaying it anyway would assert a path the run never took.");
            }

            // The reviewer is the only node with two outgoing edges, so it is the only node that
            // makes a routing DECISION. Recorded with the demo's own route ids as the candidates.
            if (string.Equals(source, DiscoveryExecutorIds.CoverageReviewer, StringComparison.Ordinal))
            {
                yield return new RoutingDecisionEvent(
                    source,
                    [DiscoveryRouteIds.ReviewToMoreDiscovery, DiscoveryRouteIds.ReviewToRanker],
                    routeId,
                    SelectionReason: routeId == DiscoveryRouteIds.ReviewToMoreDiscovery
                        ? "gaps remain and budget, progress and a runnable query are all left"
                        : state.CoverageApproved ? "coverage sufficient" : $"degraded — {state.StopReason}");
            }

            yield return new EdgeTraversedEvent(
                source,
                target,
                // MAF reflects every edge in this graph as Conditional — each carries a predicate.
                // It reports NO edge as EdgeType.Loop, so WorkflowGraphSnapshot.HasLoops is false
                // even though the graph plainly contains a cycle. The traversal is classified the
                // same way MAF classifies the declaration rather than being upgraded here, so the
                // report never claims a type MAF did not give.
                EdgeType.Conditional,
                EdgeId: routeId,
                ConditionResult: true,
                RoutingReason: routeId == DiscoveryRouteIds.ReviewToMoreDiscovery ? "gaps remain" : null);

            current = target;
            if (string.Equals(target, DiscoveryExecutorIds.Discovery, StringComparison.Ordinal)) round++;

            yield return new ExecutorOutputEvent(current, Describe(state, current, round));
        }

        yield return new WorkflowCompleteEvent();
    }

    private static string DescribeMapper(DiscoveryState state) =>
        $"{state.Interests.Count} interest(s) mapped before a single catalogue record was seen: "
      + string.Join(", ", state.Interests.Select(i => $"\"{i.Label}\""));

    private static string Describe(DiscoveryState state, string executorId, int round) => executorId switch
    {
        DiscoveryExecutorIds.Discovery => DescribeRound(state, round),

        DiscoveryExecutorIds.CoverageReviewer =>
            $"round {round} reviewed — {state.Candidates.Count} candidate(s) on the ledger, "
          + $"{state.DroppedQueryTerms.Count} proposed term(s) refused by the vocabulary constraint so far",

        DiscoveryExecutorIds.Ranker =>
            $"{state.Ranked.Count} ranked; {state.DroppedSkus.Count} SKU(s) REMOVED by the deterministic post-checks",

        // The Presenter's output is the run's actual composed answer, which is what makes
        // FinalOutput a real string rather than a marker.
        DiscoveryExecutorIds.Presenter => state.FinalAnswer,

        _ => executorId,
    };

    private static string DescribeRound(DiscoveryState state, int round)
    {
        var queries = state.QueryLog.Where(q => q.Round == round).ToList();
        int discovered = queries.Sum(q => q.NewProductIds.Count);

        return queries.Count == 0
            ? $"round {round} — the query log carries no row for this round"
            : $"round {round} — {queries.Count} quer(y/ies), {discovered} NEW product id(s): "
            + string.Join(" · ", queries.Select(q => $"\"{q.Query}\" → {q.Hits} hit(s), {q.NewProductIds.Count} new"));
    }

    /// <summary>The two endpoints of one route id. The single join between route ids and node ids.</summary>
    /// <remarks>
    /// A switch rather than a dictionary so an added route id is a COMPILE-time hole here, and an
    /// unknown one throws rather than being silently walked past — an unrecognised edge in a
    /// topology eval is the one thing that must never be skipped quietly.
    /// </remarks>
    /// <param name="routeId">One of <see cref="DiscoveryRouteIds"/>.</param>
    private static (string Source, string Target) Endpoints(string routeId) => routeId switch
    {
        DiscoveryRouteIds.MapToDiscovery => (DiscoveryExecutorIds.InterestMapper, DiscoveryExecutorIds.Discovery),
        DiscoveryRouteIds.DiscoveryToReview => (DiscoveryExecutorIds.Discovery, DiscoveryExecutorIds.CoverageReviewer),
        DiscoveryRouteIds.ReviewToMoreDiscovery => (DiscoveryExecutorIds.CoverageReviewer, DiscoveryExecutorIds.Discovery),
        DiscoveryRouteIds.ReviewToRanker => (DiscoveryExecutorIds.CoverageReviewer, DiscoveryExecutorIds.Ranker),
        DiscoveryRouteIds.RankerToPresenter => (DiscoveryExecutorIds.Ranker, DiscoveryExecutorIds.Presenter),
        _ => throw new InvalidOperationException(
            $"Unknown route id '{routeId}'. The route vocabulary is frozen in DiscoveryRouteIds; a new edge has "
          + "to be joined to its node ids here before this eval can walk it."),
    };

    /// <summary>The node sequence a route trace walks, entry node first.</summary>
    /// <param name="routes">Route ids in order.</param>
    private static IReadOnlyList<string> WalkFrom(IReadOnlyList<string> routes)
    {
        var walk = new List<string> { DiscoveryExecutorIds.InterestMapper };
        foreach (string routeId in routes) walk.Add(Endpoints(routeId).Target);
        return walk;
    }

    // ══ The gates ════════════════════════════════════════════════════════════════════════════

    private static int Report(IReadOnlyList<Observation> observations)
    {
        var rows = new List<ControlRowSnapshot>();

        // ── GATE A — structure ───────────────────────────────────────────────────────────────
        bool structureHeld = observations.All(
            o => o.StructuralFailure is null && o.HarnessPassed && o.EveryTraversedEdgeIsDeclared);

        foreach (Observation o in observations)
        {
            bool ok = o.StructuralFailure is null && o.HarnessPassed && o.EveryTraversedEdgeIsDeclared;

            rows.Add(new ControlRowSnapshot(
                $"{o.Case.PersonaId} · structure",
                "all five executors invoked, the step list equal to the walk reconstructed from the route ids, "
              + "the entry node InterestMapper, and every traversed edge one MAF actually declared",
                ok
                    ? $"ok — {o.Steps.Count} step(s): {string.Join(" → ", o.Steps)}; "
                    + $"{o.TraversedEdgePairs.Count} distinct edge(s) walked, all declared"
                    : o.StructuralFailure is not null
                        ? "FAILED — " + Flatten(o.StructuralFailure)
                        : !o.EveryTraversedEdgeIsDeclared
                            ? $"FAILED — walked [{string.Join(", ", o.TraversedEdgePairs)}] against declared "
                            + $"[{string.Join(", ", o.DeclaredEdgeIds)}]"
                            : "FAILED — the workflow harness's own built-in assertions: "
                            + string.Join(" · ", o.HarnessAssertions
                                .Where(a => !a.Passed)
                                .Select(a => $"{a.AssertionName}: {a.FailureMessage}")),
                Tripped: ok));
        }

        // ── GATE B — the loop-back, both directions ──────────────────────────────────────────
        bool directionsHeld = observations.All(
            o => o.LoopBackTraversed == o.Case.ExpectsLoopBack && o.LoopBackFailure is null);
        bool witnessesHeld = observations.All(o => o.RoundsAgree && o.SuperStepsAgree);
        bool negativeCapable = observations.All(o => o.ImpossibleEdgeRejected);
        bool corpusHasBoth = observations.Any(o => o.Case.ExpectsLoopBack)
                          && observations.Any(o => !o.Case.ExpectsLoopBack);

        foreach (Observation o in observations)
        {
            rows.Add(new ControlRowSnapshot(
                $"{o.Case.PersonaId} · loop-back {(o.Case.ExpectsLoopBack ? "FIRES" : "does NOT fire")}",
                o.Case.ExpectsLoopBack
                    ? "HaveTraversedEdge(CoverageReviewer → Discovery) must VALIDATE — this customer leaves "
                    + "round 1 with an OPEN GAP the reviewer can still act on"
                    : "the SAME assertion must FAIL — this customer leaves round 1 with nothing the reviewer "
                    + "can send back for, so a conditional edge must not fire",
                $"traversed = {o.LoopBackTraversed}; {o.LoopBacksInTrace} loop-back(s) in the route trace, "
              + $"{o.Rounds} of {o.MaxRounds} round(s), {o.SuperSteps} super-step(s)",
                Tripped: o.LoopBackTraversed == o.Case.ExpectsLoopBack));

            rows.Add(new ControlRowSnapshot(
                $"{o.Case.PersonaId} · three witnesses agree",
                "loop-backs = rounds − 1 (the edge predicate against the producer's round counter) AND "
              + "super-steps = 2·rounds + 3 (both against MAF's own scheduler count). No pin is involved.",
                $"{o.LoopBacksInTrace} = {o.Rounds} − 1 → {o.RoundsAgree}; "
              + $"{o.SuperSteps} = 2·{o.Rounds} + 3 → {o.SuperStepsAgree}",
                Tripped: o.RoundsAgree && o.SuperStepsAgree));
        }

        rows.Add(new ControlRowSnapshot(
            "the edge assertion can say NO",
            "HaveTraversedEdge(Presenter → InterestMapper) — an edge the graph does not contain — must come "
          + "back false on every case. Without it, a false on the negative cases could equally mean the "
          + "assertion answers false to everything.",
            negativeCapable
                ? $"rejected on all {observations.Count} case(s)"
                : "AT LEAST ONE CASE ACCEPTED AN EDGE THAT DOES NOT EXIST",
            Tripped: negativeCapable));

        // ── ADVISORY — what actually opened the gap the loop-back edge reads ─────────────────
        //
        // ⚠ MEASURED 2026-09-06 (Wave 3), because the eval's own prose named a mechanism the run
        // refutes. The loop-back edge fires on `OpenGaps.Count > 0`, and on the shipped
        // deterministic corpus NOT ONE of those gaps came from a mapper interest the reviewer
        // could not serve: every mapper interest is COVERED at the end of round 1 on all four
        // non-abstention cases, and each round's assessment says "0 gap(s) with a concrete next
        // query". The gap that makes the edge fire is written by ACCEPTING a mid-run interest
        // proposed from review text — a newly-added interest nobody has searched, which
        // `CoverageVerdictProjection.Project`'s second structural veto refuses to approve over.
        //
        // So the discriminator between a looping and a non-looping customer, TODAY, is whether
        // that proposal survived QueryVocabulary — not whether coverage was incomplete. That is
        // reported and NEVER gated: gating it would pin the eval to the mechanism that happens to
        // be load-bearing this month, and the pins already carry the direction. It is printed so
        // that the next reader of a GATE B failure does not have to re-derive it, as this wave did.
        foreach (Observation o in observations)
        {
            rows.Add(new ControlRowSnapshot(
                $"{o.Case.PersonaId} · what opened the gap the loop-back edge read",
                "the edge's predicate is OpenGaps.Count > 0. Two different things write a gap: a mapper "
              + "interest the reviewer could not serve, and a mid-run interest proposed from review text "
              + "and ACCEPTED (which nobody has searched yet, so approval is vetoed in code). This row "
              + "says which one, per case — it does not judge either",
                $"loop-back traversed = {o.LoopBackTraversed} · mapper interest(s) ever given a gap reason = "
              + $"{o.MapperGapsAtAnyRound} · mid-run proposals {o.ProposalsAccepted} accepted of {o.ProposalsMade}"
              + (o.ProposalRefusals.Count == 0
                    ? string.Empty
                    : " · refused: " + string.Join(" | ", o.ProposalRefusals.Select(Flatten))),
                Tripped: true,
                Gating: false));
        }

        rows.Add(new ControlRowSnapshot(
            "the corpus contains both directions",
            "at least one case that must loop and at least one that must not — a corpus with only one "
          + "direction lets a constant answer pass, and a constant answer is not a measurement",
            $"{observations.Count(o => o.Case.ExpectsLoopBack)} looping, "
          + $"{observations.Count(o => !o.Case.ExpectsLoopBack)} non-looping",
            Tripped: corpusHasBoth));

        // ── GATE C — termination ─────────────────────────────────────────────────────────────
        bool reasonsKnown = observations.All(o => DiscoveryStopReasons.IsKnown(o.StopReason));
        bool statesAgree = observations.All(o => o.Approved != o.PartialAnswer
                                             && (o.Approved == (o.StopReason == DiscoveryStopReasons.CoverageSufficient)));
        bool degradedVisible = observations.Any(o => o.Approved) && observations.Any(o => !o.Approved);

        foreach (Observation o in observations)
        {
            rows.Add(new ControlRowSnapshot(
                $"{o.Case.PersonaId} · termination",
                "the run ended in one of the four FROZEN stop reasons, and the reason agrees with the "
              + "approved/PARTIAL flags — 'coverage-sufficient' if and only if approved",
                $"{o.StopReason} · approved = {o.Approved} · partial = {o.PartialAnswer} · "
              + $"{o.PresentedCount} item(s) presented",
                Tripped: DiscoveryStopReasons.IsKnown(o.StopReason)
                      && o.Approved != o.PartialAnswer
                      && (o.Approved == (o.StopReason == DiscoveryStopReasons.CoverageSufficient))));
        }

        // The answer CHANNEL, both directions. Not a quality claim — a consistency one: text if and
        // only if items, against an expectation authored from the customer, not read off the run.
        bool answerChannelHeld = observations.All(o =>
            o.AnswerFailure is null
         && (o.FinalAnswerLength > 0) == o.Case.PresentsAnswerText
         && (o.PresentedCount > 0) == o.Case.PresentsAnswerText);

        bool answerCorpusHasBoth = observations.Any(o => o.Case.PresentsAnswerText)
                                && observations.Any(o => !o.Case.PresentsAnswerText);

        foreach (Observation o in observations)
        {
            rows.Add(new ControlRowSnapshot(
                $"{o.Case.PersonaId} · answer channel {(o.Case.PresentsAnswerText ? "carries a tray" : "is correctly EMPTY")}",
                o.Case.PresentsAnswerText
                    ? "this customer has reachable candidates, so the Presenter must compose a non-empty "
                    + "answer AND present at least one item"
                    : "this customer is the ABSTENTION persona: the Presenter is expected to compose NOTHING "
                    + "and present nothing. Measured, that is a zero-character FinalAnswer — and the pairing "
                    + "is what stops an empty tray on any OTHER customer being read as a clean run.",
                $"{o.FinalAnswerLength} char(s), {o.PresentedCount} item(s) presented",
                Tripped: (o.FinalAnswerLength > 0) == o.Case.PresentsAnswerText
                      && (o.PresentedCount > 0) == o.Case.PresentsAnswerText));
        }

        rows.Add(new ControlRowSnapshot(
            "the answer-channel expectation is authored, and the corpus has both values",
            "at least one customer expected to answer and one expected to abstain. The flag is authored per "
          + "customer from the INPUT — deriving it from the run's own output would let a run that silently "
          + "presented nothing exempt itself from the check by failing it.",
            $"{observations.Count(o => o.Case.PresentsAnswerText)} expected to answer, "
          + $"{observations.Count(o => !o.Case.PresentsAnswerText)} expected to abstain",
            Tripped: answerCorpusHasBoth));

        rows.Add(new ControlRowSnapshot(
            "the degraded path is distinguishable",
            "at least one APPROVED exit and at least one DEGRADED exit are observed. Both leave the reviewer "
          + "through the SAME review-to-ranker edge by design, so the discriminator has to be state — this row "
          + "is what proves the state discriminator was exercised in both directions rather than assumed.",
            $"{observations.Count(o => o.Approved)} approved, {observations.Count(o => !o.Approved)} degraded; "
          + $"stop reasons seen: {string.Join(", ", observations.Select(o => o.StopReason).Distinct())}",
            Tripped: degradedVisible));

        // ── an instrument finding, never gated ───────────────────────────────────────────────
        var unseen = DiscoveryStopReasons.All
            .Where(r => !observations.Any(o => string.Equals(o.StopReason, r, StringComparison.Ordinal)))
            .ToList();

        rows.Add(new ControlRowSnapshot(
            "every frozen stop reason is reachable on this corpus",
            "all four of DiscoveryStopReasons.All are observed on a real customer",
            unseen.Count == 0
                ? "all four observed"
                : $"NOT observed here: {string.Join(", ", unseen)} — forced instead by the demo lane's "
                + "DiscoveryTerminationProbe with a scripted reviewer, which is a different claim",
            Tripped: unseen.Count == 0,
            Gating: false));

        PrintSummary(rows);
        PrintFloors(observations);

        bool gateA = structureHeld;
        bool gateB = directionsHeld && witnessesHeld && negativeCapable && corpusHasBoth;
        bool gateC = reasonsKnown && statesAgree && degradedVisible && answerChannelHeld && answerCorpusHasBoth;

        PrintGate(gateA, gateB, gateC, directionsHeld, witnessesHeld, negativeCapable, corpusHasBoth,
                  reasonsKnown, statesAgree, degradedVisible, answerChannelHeld, answerCorpusHasBoth);

        PrintCost(observations);

        EvalResultStore.SaveControls(SnapshotKey, new ControlSnapshot
        {
            Label = "Eval 07 — Workflow topology: the loop-back edge, both directions",
            Controls = rows,
            AllControlsTripped = gateA && gateB && gateC,
        });

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Snapshot saved → {EvalResultStore.StorageLocation}");
        Console.ResetColor();

        return gateA && gateB && gateC ? 0 : 1;
    }

    /// <summary>
    /// The dry-run gate: plumbing only, and every one of these can fail.
    /// </summary>
    private static int PlumbingGate(Observation o)
    {
        var checks = new List<(string Name, bool Ok, string Observed)>
        {
            ("the loop handed back a Workflow and five executor ids",
             o.GraphNodeIds.Count == DiscoveryExecutorIds.All.Count,
             $"{o.GraphNodeIds.Count} node(s): {string.Join(", ", o.GraphNodeIds)}"),

            ("MAF's guid-suffixed ids were mapped onto the five FROZEN clean names",
             DiscoveryExecutorIds.All.All(id => o.GraphNodeIds.Contains(id, StringComparer.Ordinal))
                && !o.GraphNodeIds.Any(id => id.Contains('_', StringComparison.Ordinal)),
             string.Join(", ", o.GraphNodeIds)),

            ("the reflected graph DECLARES the loop-back edge",
             o.DeclaredEdgeIds.Contains(
                 $"{DiscoveryExecutorIds.CoverageReviewer}->{DiscoveryExecutorIds.Discovery}", StringComparer.Ordinal),
             string.Join(", ", o.DeclaredEdgeIds)),

            ("the replay produced a step list and a traversed-edge list",
             o.Steps.Count >= 5 && o.TraversedEdgeIds.Count >= 4,
             $"{o.Steps.Count} step(s), {o.TraversedEdgeIds.Count} traversed edge(s)"),

            ("every traversed edge is one MAF declared",
             o.EveryTraversedEdgeIsDeclared,
             $"walked [{string.Join(", ", o.TraversedEdgePairs)}] against declared "
           + $"[{string.Join(", ", o.DeclaredEdgeIds)}]"),

            ("the three witnesses agree",
             o.RoundsAgree && o.SuperStepsAgree,
             $"{o.LoopBacksInTrace} loop-back(s), {o.Rounds} round(s), {o.SuperSteps} super-step(s)"),

            ("the edge assertion is capable of returning FALSE",
             o.ImpossibleEdgeRejected,
             "HaveTraversedEdge(Presenter → InterestMapper) rejected"),

            ("the answer channel matches the customer's AUTHORED expectation",
             (o.FinalAnswerLength > 0) == o.Case.PresentsAnswerText
                && (o.PresentedCount > 0) == o.Case.PresentsAnswerText,
             $"expected text: {o.Case.PresentsAnswerText}; got {o.FinalAnswerLength} char(s), "
           + $"{o.PresentedCount} item(s)"),

            ("the structural chain validated",
             o.StructuralFailure is null,
             o.StructuralFailure is null ? "no failures" : Flatten(o.StructuralFailure)),
        };

        Console.WriteLine();
        foreach (var (name, ok, observed) in checks)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(ok ? "✅" : "❌")}  {name}");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"        {observed}");
            Console.ResetColor();
        }

        bool passed = checks.All(c => c.Ok);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠️  THIS WAS A DRY RUN. It exercised the real arm, the real graph reflection and the real");
        Console.WriteLine("      assertion chain on ONE case, and it spent nothing — but then neither does the full run,");
        Console.WriteLine("      because this eval never calls a model. A green dry run means the INSTRUMENT is wired.");
        Console.WriteLine("      It reports no topology verdict, writes no snapshot, and says nothing about the loop.");
        Console.ResetColor();
        Console.WriteLine();

        return passed ? 0 : 1;
    }

    // ══ Printing ═════════════════════════════════════════════════════════════════════════════

    private static void PrintHeader(bool dryRun)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 07 — Workflow topology: DID THE LOOP ACTUALLY LOOP?                    ║
║   MAF graph via ReflectEdges · the loop-back edge asserted in BOTH directions ║
║   no model calls · no credentials · nothing spent                             ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        // The shared declaration Evals 03 and 04 also print, so all three model-free evals say it
        // in one voice and a reader can tell at a glance which kind of run they are looking at.
        CredentialGuard.DeclareModelFree(
            "Eval 07", "the loop's MECHANICS — which executors ran, over which edges");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     The bound arm runs Demo 2's loop on its DETERMINISTIC path, so nothing below is a fact");
        Console.WriteLine("     about recommendation quality either, or about what a model-backed reviewer would decide.");
        Console.ResetColor();

        if (dryRun)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n  ▶ DRY RUN — one case, plumbing assertions only, no snapshot written.");
            Console.ResetColor();
        }
    }

    private static void PrintCase(Observation o)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine();
        Console.WriteLine($"  ─── {o.Case.PersonaId}  {o.Case.DisplayName} ───────────────────────────────");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (string line in Wrap(o.Case.Why, 92)) Console.WriteLine($"      {line}");
        Console.ResetColor();

        if (o.Refusal is { } refusal)
        {
            EvalPrinter.PrintRefusal($"Eval 07 refused case {o.Case.PersonaId}.", refusal);
            return;
        }

        // ── the route list, which is the thing this eval is here to print ────────────────────
        Console.WriteLine();
        Console.WriteLine("      route trace — the ids the workflow's OWN edge predicates published, in order,");
        Console.WriteLine("      with immediate repeats collapsed (MAF may evaluate a predicate more than once):");

        string current = DiscoveryExecutorIds.InterestMapper;
        int round = 1;
        foreach (string routeId in o.Routes)
        {
            var (source, target) = Endpoints(routeId);
            bool isLoopBack = routeId == DiscoveryRouteIds.ReviewToMoreDiscovery;
            if (isLoopBack) round++;

            Console.ForegroundColor = isLoopBack ? ConsoleColor.Yellow : ConsoleColor.Gray;
            Console.WriteLine(isLoopBack
                ? $"        {source,-17} ↩──[{routeId}]──▶ {target,-17}  ⭐ THE LOOP-BACK → round {round}"
                : $"        {source,-17} ──[{routeId}]──▶ {target}");
            Console.ResetColor();
            current = target;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"        terminal node: {current}");
        Console.ResetColor();

        // ── the verdict ──────────────────────────────────────────────────────────────────────
        bool directionOk = o.LoopBackTraversed == o.Case.ExpectsLoopBack;

        Console.WriteLine();
        Console.ForegroundColor = directionOk ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(
            $"      {(directionOk ? "✅" : "❌")}  HaveTraversedEdge(CoverageReviewer → Discovery) = {o.LoopBackTraversed}"
          + $"   (expected {o.Case.ExpectsLoopBack})");
        Console.ResetColor();

        Console.ForegroundColor = (o.RoundsAgree && o.SuperStepsAgree) ? ConsoleColor.DarkGreen : ConsoleColor.Red;
        Console.WriteLine(
            $"      {((o.RoundsAgree && o.SuperStepsAgree) ? "✅" : "❌")}  three witnesses: "
          + $"{o.LoopBacksInTrace} loop-back(s) in the trace · {o.Rounds} of {o.MaxRounds} round(s) on the "
          + $"producer's counter · {o.SuperSteps} MAF super-step(s)");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"           identities: loop-backs = rounds−1 → {o.RoundsAgree}   "
                        + $"super-steps = 2·rounds+3 → {o.SuperStepsAgree}");
        Console.ResetColor();

        Console.ForegroundColor = o.Approved ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"      {(o.Approved ? "✅" : "⚠️ ")}  termination: {o.StopReason}"
                        + $"   ({(o.PartialAnswer ? "DEGRADED — PARTIAL answer" : "approved — complete answer")}), "
                        + $"{o.PresentedCount} item(s) presented");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      steps      : {string.Join(" → ", o.Steps)}");
        Console.WriteLine($"      declared   : {string.Join(", ", o.DeclaredEdgeIds)}");
        Console.WriteLine($"      timing     : loop {o.LoopElapsed.TotalMilliseconds:F0} ms · "
                        + $"turn {o.TurnElapsed.TotalMilliseconds:F0} ms · replay {o.ReplayElapsed.TotalMilliseconds:F1} ms "
                        + "(the replay clock is NOT the run's latency)");
        // ⚠ The token figure is the harness's LENGTH-BASED ESTIMATE, not a provider count: no model
        // ran, so no usage was reported and PerformanceMetrics fell back to characters ÷ 4. It is
        // printed with that label rather than as a token count, because a bare number here would
        // read as evidence that something was tokenised.
        Console.WriteLine($"      spend      : {o.ModelCalls} model call(s) · "
                        + $"{(o.TotalTokens is { } t
                            ? o.TokensAreEstimated
                                ? $"{t} token(s) — ESTIMATED FROM TEXT LENGTH, nothing was tokenised"
                                : $"{t} token(s) reported by the provider"
                            : "no token usage reported")} · "
                        + $"{(o.EstimatedCost is { } c ? c.ToString("C4") : "$0.0000 — no priced model was called")}");
        Console.WriteLine($"      answer     : {o.FinalAnswerLength} char(s), {o.PresentToolCalls} PresentRecommendation "
                        + "call(s) replayed onto the answer channel (NOT scored here — see Evals 01 and 02)");
        Console.ResetColor();

        // The three failure kinds are printed under their own headings, because "the graph is
        // wrong", "the loop went the wrong way" and "the answer was empty" send a reader to three
        // different files.
        PrintFailure("STRUCTURE", o.StructuralFailure);
        PrintFailure("LOOP-BACK", o.LoopBackFailure);
        PrintFailure("ANSWER CHANNEL", o.AnswerFailure);
    }

    private static void PrintFailure(string heading, string? failure)
    {
        if (failure is null) return;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine();
        Console.WriteLine($"      ❌ {heading}");
        foreach (string line in failure.Split('\n')) Console.WriteLine($"      {line.TrimEnd()}");
        Console.ResetColor();
    }

    private static void PrintSummary(IReadOnlyList<ControlRowSnapshot> rows)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ┌──────────────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  Eval 07 — every structural check, gating rows first                             │");
        Console.WriteLine("  └──────────────────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();

        foreach (ControlRowSnapshot row in rows.OrderByDescending(r => r.Gating))
        {
            Console.ForegroundColor = row.Gating
                ? row.Tripped ? ConsoleColor.Green : ConsoleColor.Red
                : row.Tripped ? ConsoleColor.DarkGreen : ConsoleColor.Yellow;
            Console.WriteLine($"    {(row.Tripped ? "✅" : row.Gating ? "❌" : "⚠️ ")}  {row.Name}"
                            + (row.Gating ? "" : "   (instrument finding — never gates)"));
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (string line in Wrap("expected: " + row.Expectation, 88)) Console.WriteLine($"          {line}");
            Console.ResetColor();

            Console.ForegroundColor = row.Tripped ? ConsoleColor.DarkGreen
                                    : row.Gating ? ConsoleColor.Red : ConsoleColor.Yellow;
            foreach (string line in Wrap("observed: " + row.Observed, 88)) Console.WriteLine($"          {line}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// The chance floors, derived from THIS corpus at run time rather than quoted.
    /// </summary>
    /// <remarks>
    /// Every claim in this eval is structural, so the "degenerate agent" whose score sets the floor
    /// is a degenerate INSTRUMENT: one that answers without looking at the graph.
    /// </remarks>
    private static void PrintFloors(IReadOnlyList<Observation> observations)
    {
        int n = observations.Count;
        int looping = observations.Count(o => o.Case.ExpectsLoopBack);
        int notLooping = n - looping;
        double coinAll = Math.Pow(0.5, n);
        double reasonAll = Math.Pow(0.25, n);

        EvalPrinter.PrintFloors($"Eval 07 — chance floors over {n} case(s)",
        [
            $"loop-back direction, per case — binary. A constant \"yes\" scores {looping}/{n}; a constant \"no\" "
          + $"scores {notLooping}/{n}; a fair coin scores {n / 2.0:F1}/{n} in expectation and gets ALL {n} right "
          + $"with p = {coinAll:F4}. The gate requires all {n}, which no constant answer can reach — that is "
          + "what having both directions in the corpus buys.",

            $"all five executors invoked, per case — floor 0.000. The assertion reads the STEP list, and steps "
          + "exist only where a traversal event was observed; an arm that never ran the graph produces zero "
          + "steps and scores zero. There is no way to score above the floor without executing.",

            "execution path — floor ≈ 0.000. The path is compared against a walk reconstructed independently "
          + "from the route ids; a random ordering of the 5-to-9 node visits matches with p ≤ 1/5! = 0.0083 on "
          + "the shortest case and less on every longer one.",

            $"termination, per case — a random pick from the four FROZEN stop reasons is right with p = 0.250, "
          + $"and must ALSO agree with the approved/PARTIAL flags. All {n} by chance: p = {reasonAll:F5}.",

            "three-witness agreement — NOT a chance-scoreable claim. It is an equality between three integers "
          + "produced by three different parts of the system (an edge predicate, the search node's round "
          + "counter, MAF's scheduler). There is nothing to guess; either they agree or the wiring is broken.",

            "⚠ every floor above is a floor for the INSTRUMENT, not for an agent. This eval scores no answer, "
          + "so no floor here bounds recommendation quality — Eval 02 owns that, with its own derived floors.",
        ]);
    }

    private static void PrintGate(
        bool gateA, bool gateB, bool gateC,
        bool directionsHeld, bool witnessesHeld, bool negativeCapable, bool corpusHasBoth,
        bool reasonsKnown, bool statesAgree, bool degradedVisible,
        bool answerChannelHeld, bool answerCorpusHasBoth)
    {
        Console.WriteLine();

        Console.ForegroundColor = gateA ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(gateA
            ? "  ✅ GATE A — STRUCTURE. Every case invoked all five executors, in the order the route trace says,"
            : "  ❌ GATE A — STRUCTURE. A case's step list, order or edge set did not match the graph.");
        Console.WriteLine(gateA
            ? "       over edges MAF itself declares, entering at InterestMapper and leaving at Presenter."
            : "       Read the failure text printed under that case: it names the assertion and both sides.");
        Console.ResetColor();

        Console.ForegroundColor = gateB ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(gateB
            ? "  ✅ GATE B — THE LOOP-BACK, BOTH DIRECTIONS. It fired on every case that had gaps left and on"
            : "  ❌ GATE B — THE LOOP-BACK. The edge did not behave as a CONDITIONAL edge must.");
        if (gateB)
        {
            Console.WriteLine("       none that did not, and the route trace, the producer's round counter and MAF's");
            Console.WriteLine("       super-step count agree about how many times.");
        }
        else
        {
            Console.WriteLine($"       direction matches pin : {directionsHeld}");
            Console.WriteLine($"       three witnesses agree : {witnessesHeld}");
            Console.WriteLine($"       assertion can say no  : {negativeCapable}");
            Console.WriteLine($"       corpus has both       : {corpusHasBoth}");
            Console.WriteLine("       A direction mismatch is EITHER a regression in the edge OR a corpus change that");
            Console.WriteLine("       moved a customer across the boundary. Both are worth stopping for; the case rows");
            Console.WriteLine("       above say which customer moved and in which direction.");
        }
        Console.ResetColor();

        Console.ForegroundColor = gateC ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(gateC
            ? "  ✅ GATE C — TERMINATION AND THE ANSWER CHANNEL. Every run ended in one of the four frozen"
            : "  ❌ GATE C — TERMINATION AND THE ANSWER CHANNEL.");
        if (gateC)
        {
            Console.WriteLine("       stop reasons, the reason agreed with the approved/PARTIAL flags, BOTH an approved");
            Console.WriteLine("       and a degraded exit were observed, and answer text appeared if and only if items");
            Console.WriteLine("       were presented — checked against an expectation authored per customer.");
        }
        else
        {
            Console.WriteLine($"       stop reasons are in the frozen vocabulary  : {reasonsKnown}");
            Console.WriteLine($"       reason agrees with approved/PARTIAL        : {statesAgree}");
            Console.WriteLine($"       both an approved and a degraded exit seen  : {degradedVisible}");
            Console.WriteLine($"       text ⟺ items, per the authored expectation : {answerChannelHeld}");
            Console.WriteLine($"       corpus has an answering AND an abstaining customer : {answerCorpusHasBoth}");
            Console.WriteLine("       ⚠ An EMPTY answer on a customer expected to answer is the flattering-direction");
            Console.WriteLine("         failure: silence must never be read here as a run that had nothing to get wrong.");
        }
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("  NOT GATED, on purpose:");
        Console.WriteLine("    · whether every frozen stop reason is reachable on this corpus. Gating on it would be");
        Console.WriteLine("      gating on a fact about the customer histories, which creates an incentive to author a");
        Console.WriteLine("      persona until the number came out right. It is printed as a finding instead.");
        Console.WriteLine("    · cost, tokens and latency. Reported per case, never a pass/fail — and there is nothing");
        Console.WriteLine("      to spend: zero model calls on every case, by construction of the bound arm.");
        Console.WriteLine("    · answer QUALITY. Gate C checks only that text appears if and only if items do —");
        Console.WriteLine("      a consistency claim. Five executors in the right order is not a good recommendation,");
        Console.WriteLine("      and this eval would be green either way. Eval 02 is where quality is measured.");
        Console.ResetColor();
    }

    private static void PrintCost(IReadOnlyList<Observation> observations)
    {
        double loopMs = observations.Sum(o => o.LoopElapsed.TotalMilliseconds);
        double turnMs = observations.Sum(o => o.TurnElapsed.TotalMilliseconds);
        int calls = observations.Sum(o => o.ModelCalls);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ⏱️  {observations.Count} case(s): {turnMs:F0} ms of turn time, of which {loopMs:F0} ms inside");
        Console.WriteLine($"      the loop. {calls} model call(s) and $0.0000 spent — the bound arm is the deterministic");
        Console.WriteLine("      path. Any token figure above is a length-based estimate, not a provider count. This is");
        Console.WriteLine("      a floor on the loop's MECHANICAL cost and not an estimate of what Demo 2 costs live.");
        Console.ResetColor();
    }

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════════

    private static string Flatten(string text) =>
        string.Join(" · ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new System.Text.StringBuilder();

        foreach (string word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>Everything one case produced. A refused case carries only <see cref="Refusal"/>.</summary>
    private sealed record Observation
    {
        public required TopologyCase Case { get; init; }
        public string? Refusal { get; init; }

        public IReadOnlyList<string> Routes { get; init; } = [];
        public IReadOnlyList<string> Steps { get; init; } = [];
        public IReadOnlyList<string> TraversedEdgeIds { get; init; } = [];
        public IReadOnlyList<string> TraversedEdgePairs { get; init; } = [];
        public bool EveryTraversedEdgeIsDeclared { get; init; }
        public IReadOnlyList<string> DeclaredEdgeIds { get; init; } = [];
        public IReadOnlyList<string> GraphNodeIds { get; init; } = [];

        public bool LoopBackTraversed { get; init; }
        public int LoopBacksInTrace { get; init; }
        public int Rounds { get; init; }
        public int MaxRounds { get; init; }
        public int SuperSteps { get; init; }
        public bool RoundsAgree { get; init; }
        public bool SuperStepsAgree { get; init; }
        public bool ImpossibleEdgeRejected { get; init; }

        public string? StructuralFailure { get; init; }
        public string? LoopBackFailure { get; init; }
        public string? AnswerFailure { get; init; }
        public IReadOnlyList<WorkflowAssertionResult> HarnessAssertions { get; init; } = [];
        public bool HarnessPassed { get; init; }

        public string StopReason { get; init; } = "";
        public bool Approved { get; init; }
        public bool PartialAnswer { get; init; }
        public int PresentedCount { get; init; }

        // ── What actually opened the gap the loop-back edge reads (Wave 3, 2026-09-06) ──────
        //
        // Recorded because the eval's own per-case prose used to name a mechanism the run
        // refutes. See the advisory row `what opened the gap the loop-back edge read`.
        public int ProposalsMade { get; init; }
        public int ProposalsAccepted { get; init; }
        public IReadOnlyList<string> ProposalRefusals { get; init; } = [];
        public int MapperGapsAtAnyRound { get; init; }

        public int ModelCalls { get; init; }
        public TimeSpan LoopElapsed { get; init; }
        public TimeSpan TurnElapsed { get; init; }
        public TimeSpan ReplayElapsed { get; init; }
        public decimal? EstimatedCost { get; init; }
        public int? TotalTokens { get; init; }
        public bool TokensAreEstimated { get; init; }
        public int PresentToolCalls { get; init; }
        public int FinalAnswerLength { get; init; }

        public static Observation Refused(TopologyCase topologyCase, string why) =>
            new() { Case = topologyCase, Refusal = why };
    }
}
