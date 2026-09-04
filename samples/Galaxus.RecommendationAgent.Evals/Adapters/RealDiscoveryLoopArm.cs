// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Loop;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Workflows;

// ── Two type names exist TWICE across the two projects, and both duplications are on purpose ──
//
//   · QueryVocabulary      — Evals.Loop's copy computes the BAR that Eval 04 grades against, from
//     the corpus, with the retriever's own tokeniser. Workflows' copy is the CONTROL under test.
//     The standing rule is that the artifact under test must never supply any input to its own
//     pass/fail, so merging them would hand the bar to the thing being barred. Eval 04 coming out
//     green IS the evidence that the two agree — that is what it is for. Neither is referenced by
//     name here, so neither needs an alias; this note is here because the next person to open this
//     file will wonder.
//   · DiscoveryLoopOptions — Workflows' configures a MAF workflow (offline, consent, timeouts);
//     Evals.Loop's configures the deterministic loop SUBSTRATE the controls are built on. Two
//     different machines that happened to pick the same obvious name. This file is the only place
//     both namespaces are in scope, so it is the only place that has to disambiguate — and it
//     aliases rather than renaming either, so each definition stays where its remarks explain it.
using WorkflowLoopOptions = Galaxus.RecommendationAgent.Workflows.DiscoveryLoopOptions;

namespace Galaxus.RecommendationAgent.Evals.Adapters;

/// <summary>
/// Demo 2's real MAF discovery loop, as an eval arm. <b>The only type in this project that names
/// a workflow type.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real here.</b> The graph, the five executors, the conditional loop-back edge, the
/// message-borne round counter, the identity-level dedup, the deterministic pre-gate, the two
/// structural approval vetoes, <c>CoverageVerdictProjection</c>, the shipped
/// <c>QueryVocabulary</c>, the shipped query planner, the shipped post-checks and the shipped
/// <c>GuardrailPipeline</c> — all of it runs, unmodified, through the same
/// <c>GalaxusDiscoveryLoop.RunAsync</c> the demo calls.
/// </para>
/// <para>
/// <b>What is substituted, and it is exactly two things.</b> (1) The loop runs on its
/// DETERMINISTIC path, so this arm makes no model call and needs no credentials — Evals 03 and 04
/// are stated to run without them and <c>-- 2 --dry-run</c> is stated to spend nothing. Read every
/// number this arm produces as a fact about the loop's MECHANICS, never as a fact about the agent.
/// (2) On a D-3 turn the reviewer's PROPOSAL is replaced by the case payload — see
/// <see cref="DiscoveryLoopAdapter.CreateForCase"/> — because no code predicts what a structured
/// model call would propose, and an arm that waited for the model to volunteer a specific payload
/// would be measuring the model's mood rather than the structure's containment.
/// </para>
/// <para>
/// <b>The answer reaches the grader through the same channel every other arm uses.</b>
/// <c>PresentedCall.FromToolUsage</c> over a real tool trace is the only thing any grader in this
/// suite reads, so the screened answer — <c>DiscoveryState.Presented</c>, i.e. what survived the
/// guardrail pipeline, not what the Ranker chose — is replayed as <c>PresentRecommendation</c>
/// calls with the four frozen argument names. Replaying the RANKER's selection instead would
/// report items the customer was never shown, and would flatter the arm by exactly the number of
/// things the guardrails removed.
/// </para>
/// <para>
/// ⚠ <b>Telemetry is a claim by the arm, never a verdict.</b> Eval 04 computes the required drop
/// set independently and compares; an arm that reports nothing fails rather than passes.
/// </para>
/// </remarks>
public sealed class RealDiscoveryLoopArm : IDiscoveryLoopArm
{
    private readonly IProductRetriever _retriever;
    private readonly InjectionCase? _steering;

    /// <summary>Builds the arm for one request.</summary>
    /// <param name="request">The eval's context, plus the D-3 case when there is one.</param>
    public RealDiscoveryLoopArm(DiscoveryArmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);

        _retriever = request.Context.Retriever;
        _steering = request.Steering;
    }

    /// <inheritdoc/>
    public string Name => DiscoveryLoopAdapter.ArmLabel;

    /// <inheritdoc/>
    public int MaxRounds => DiscoveryState.DefaultMaxDiscoveryRounds;

    /// <inheritdoc/>
    /// <remarks>
    /// True unconditionally, and it is not a claim taken on trust: the constraint is
    /// <c>CoverageVerdictProjection</c> calling the shipped <c>QueryVocabulary</c> on every gap
    /// query, every attribute pair and every proposed term, on a path this arm cannot bypass.
    /// Eval 04 checks the consequence, not the flag.
    /// </remarks>
    public bool AppliesQueryVocabularyConstraint => true;

    /// <inheritdoc/>
    public DiscoveryLoopTelemetry? LastRun { get; private set; }

    /// <summary>
    /// The final state of the most recent turn, or null before the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exposed so an assertion can reach the graph and the routes — <c>DiscoveryRunResult</c>
    /// carries the <c>Workflow</c> and the executor ids precisely so that
    /// <c>MAFWorkflowAdapter.FromMAFWorkflow(...)</c> can assert
    /// <c>HaveTraversedEdge("CoverageReviewer", "Discovery")</c>.
    /// </para>
    /// <para>
    /// <b><see cref="Evals.Eval07_WorkflowTopology"/> now reads it</b> — the graph off
    /// <c>Workflow</c>, the traversal off <c>RoutesTaken</c>, and the cross-check off
    /// <c>SuperSteps</c> and the state's round counter. Keeping this handle is what made that eval
    /// possible; dropping it on the floor is how the assertion becomes impossible later.
    /// </para>
    /// </remarks>
    public DiscoveryRunResult? LastResult { get; private set; }

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // The customer is read from the PROMPT, exactly as the live agent and every scripted
        // control read it. An arm configured out of band would be running a different experiment
        // from the one it is being paired against.
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

        var catalogue = Catalogue.Default;
        var recorder = new RecordingDiscoveryProgressSink();

        // ⚠ The UTTERANCE, not the framed prompt. `DiscoveryState.SessionRequest` is a typed slot
        // for what the customer said, and the eval's "[session] You are speaking with customer …"
        // header is harness scaffolding, not speech. Passing the frame was measured turning that
        // header into a stated-need interest, searching the catalogue for it, retrieving nothing,
        // and tripping the pre-gate on a DIRECT interest the harness had invented — the arm looked
        // broken and the harness was. See GalaxusEvalPrompt.UtteranceFrom.
        //
        // The shipped QueryVocabulary admits the customer's own sentence, so this widens the
        // allow-list by whatever the utterance contains; InjectionCases.Validate check 6 is what
        // guarantees the payload is still outside it.
        var options = new WorkflowLoopOptions(
            Offline: true,
            PersonalizationDisabled: false,
            SessionRequest: GalaxusEvalPrompt.UtteranceFrom(prompt),
            MaxRounds: DiscoveryState.DefaultMaxDiscoveryRounds,
            Retriever: _retriever,
            Progress: recorder,
            Nodes: BuildOverrides(catalogue, recorder));

        DiscoveryRunResult result = await GalaxusDiscoveryLoop
            .RunAsync(userId, options, cancellationToken)
            .ConfigureAwait(false);

        LastResult = result;
        LastRun = ProjectTelemetry(result.State, catalogue);

        return Replay(result.State);
    }

    /// <summary>
    /// The node substitutions. Presenter always; reviewer only on a D-3 turn.
    /// </summary>
    /// <remarks>
    /// The Presenter override is <c>print: false</c> and nothing else — the same
    /// <c>DeterministicPresenter</c> the offline demo uses, screening and composing exactly as
    /// usual but writing no customer-facing tray. Three personas × several arms × three reps of a
    /// full recommendation panel would bury the eval's own report, and the state it produces is
    /// identical either way.
    /// </remarks>
    private DiscoveryNodeOverrides BuildOverrides(Catalogue catalogue, IDiscoveryProgressSink progress)
    {
        var presenter = new DeterministicPresenter(catalogue, progress, print: false);

        return _steering is null
            ? new DiscoveryNodeOverrides(Presenter: presenter)
            : new DiscoveryNodeOverrides(
                Reviewer: new SteeredCoverageReviewerNode(catalogue, progress, _steering),
                Presenter: presenter);
    }

    /// <summary>
    /// Replays the SCREENED answer as <c>PresentRecommendation</c> tool calls.
    /// </summary>
    /// <param name="state">The final run state.</param>
    private static AgentResponse Replay(DiscoveryState state)
    {
        var trace = new ScriptedTrace();

        foreach (PresentedItem item in state.Presented)
            trace.Present(item.ProductId, item.WhyThis, item.Evidence, item.OutOfStock);

        // ModelId stays null: no model ran on this arm, and stamping a deployment name on a
        // deterministic turn is the one line of this file a reader would quote as evidence that
        // the agent produced these numbers.
        return trace.Say(state.FinalAnswer).ToResponse(modelId: null);
    }

    /// <summary>
    /// Projects the loop's own state onto the eval's telemetry shape.
    /// </summary>
    /// <remarks>
    /// Every field is read from the record the PRODUCER wrote — the query log from the search
    /// node, the drops from the vocabulary constraint, the proposals from the projection, the
    /// presented set from the Presenter. Nothing here recomputes a fact the run already
    /// established.
    /// </remarks>
    /// <param name="state">The final run state.</param>
    /// <param name="catalogue">The catalogue façade, for the marketplace-seller flag on a snippet.</param>
    private DiscoveryLoopTelemetry ProjectTelemetry(DiscoveryState state, Catalogue catalogue)
    {
        var drops = new List<QueryTermDrop>(state.DroppedQueryTerms.Count);
        foreach (DroppedQueryTerm dropped in state.DroppedQueryTerms)
        {
            // The source product is the proposal the term belonged to, when it belonged to one.
            // A gap's refused next_query has no proposing product, and inventing one would be a
            // fabricated provenance in a ledger whose whole job is provenance.
            ProposalOutcome? owner = state.Proposals
                .FirstOrDefault(p => p.ProposedTerms.Contains(dropped.Term, StringComparer.Ordinal));

            drops.Add(new QueryTermDrop(
                dropped.Term,
                owner?.Label ?? dropped.ProposedFor,
                owner?.EvidenceProductId ?? "—",
                QueryTermDrop.OutsideVocabulary));
        }

        var snippets = new List<ReviewSnippet>();
        foreach (ProductCandidate candidate in state.Candidates)
        {
            bool marketplace = catalogue.TryGet(candidate.ProductId, out var product)
                            && product is not null
                            && product.IsMarketplaceOffer;

            for (int i = 0; i < candidate.ReviewSnippets.Count; i++)
            {
                snippets.Add(new ReviewSnippet(
                    candidate.ProductId,
                    i < candidate.ReviewIds.Count ? candidate.ReviewIds[i] : "(unattributed)",
                    candidate.ReviewSnippets[i],
                    marketplace));
            }
        }

        // On a steered turn the payload was proposed by construction, so it is reported as
        // proposed even if the shipped structure refused it before it reached the map. That is the
        // applicability fact Eval 04 needs: "contained" and "never tempted" are different
        // outcomes, and only one of them is a pass.
        var proposedLabels = state.Proposals.Select(p => p.Label).ToList();
        var proposedTerms = state.Proposals.SelectMany(p => p.ProposedTerms).ToList();

        return new DiscoveryLoopTelemetry
        {
            ArmName = Name,
            CustomerId = state.CustomerId,
            RoundsTaken = state.DiscoveryRound,
            MaxRounds = state.MaxRounds,
            ApprovedByReviewer = state.CoverageApproved,
            StopReason = MapStopReason(state.StopReason),
            QueriesRun = [.. state.QueryLog.Select(q => q.Query)],
            CandidateProductIds = [.. state.Candidates.Select(c => c.ProductId)],
            LastRoundNewProductCount = state.LastRoundNewProductCount,
            ProposedInterestLabels = proposedLabels,
            ProposedQueryTerms = proposedTerms,
            AcceptedInterestLabels = [.. state.Interests.Select(i => i.Label)],
            DroppedQueryTerms = drops,
            VocabularyConstraintApplied = true,
            PresentedProductIds = [.. state.Presented.Select(p => p.ProductId)],
            SnippetsSeen = snippets,
        };
    }

    /// <summary>
    /// Maps the workflow's terminal <see cref="DiscoveryStopReason"/> onto the eval's frozen
    /// stop-reason vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two lanes, two vocabularies — an enum in the demo, printable constants here — and this is
    /// the ONE place they are joined. That is deliberate: a shared enum would make a rename in one
    /// lane a silent semantic change in the other, which is the drift §0.5 / D-1 is about.
    /// </para>
    /// <para>
    /// <b>Non-terminal values throw rather than being coerced.</b> <c>GapsRemain</c> and
    /// <c>None</c> are states the loop passes THROUGH; reaching the end of a run in one of them
    /// means the routing predicates and the resolved reason disagree, which is a wiring fault.
    /// Mapping it onto some plausible terminal string would file that fault as a measurement.
    /// </para>
    /// </remarks>
    /// <param name="reason">The workflow's resolved stop reason.</param>
    /// <exception cref="InvalidOperationException">The run ended in a non-terminal stop reason.</exception>
    private static string MapStopReason(DiscoveryStopReason reason) => reason switch
    {
        DiscoveryStopReason.CoverageSufficient => DiscoveryStopReasons.CoverageSufficient,
        DiscoveryStopReason.RoundLimitReached => DiscoveryStopReasons.RoundLimitReached,
        DiscoveryStopReason.NoProgress => DiscoveryStopReasons.NoProgress,
        DiscoveryStopReason.GapsUnresolvable => DiscoveryStopReasons.GapsUnresolvable,
        _ => throw new InvalidOperationException(
            $"The discovery loop ended a run in the non-terminal stop reason '{reason}'. That is a wiring "
          + "fault in the routing predicates, not a result, and it is refused here rather than coerced onto "
          + "a terminal reason the run did not actually reach."),
    };
}

/// <summary>
/// The shipped deterministic coverage reviewer, with ONE change: on the first round it gets to
/// speak, it proposes the D-3 case's payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything downstream of the proposal is the shipped code.</b> The verdict is built by
/// <c>DeterministicCoverageReviewer.BuildVerdict</c>, the pre-gate is the shipped
/// <c>CoverageReviewGate</c>, and the payload is handed to the shipped
/// <c>CoverageVerdictProjection.Project</c> in the <c>new_interest</c> slot — the same slot a
/// model's structured response fills. So the acceptance rules, the confidence clamp, the
/// cited-candidate requirement, the special-category label check and the vocabulary constraint all
/// run exactly as they ship, and the drop ledger they produce is the shipped ledger.
/// </para>
/// <para>
/// <b>Why it waits for a round in which the pre-gate did not fire.</b> The shipped pre-gate
/// short-circuits before any reviewer speaks and emits a Conservative verdict with no proposal —
/// so a payload spliced into a pre-gated round would be a proposal the shipped loop structurally
/// cannot make. Waiting is the faithful behaviour, and with a three-round cap there is room for it.
/// </para>
/// <para>
/// ⚠ It replaces the proposal, not the judgement: whether the loop then approves, loops or
/// exhausts is decided by the shipped predicates on the shipped state.
/// </para>
/// </remarks>
internal sealed class SteeredCoverageReviewerNode : ICoverageReviewerNode
{
    private readonly Catalogue _catalogue;
    private readonly IDiscoveryProgressSink _progress;
    private readonly InjectionCase _case;
    private bool _spliced;

    /// <summary>Wraps one case.</summary>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="progress">The sink.</param>
    /// <param name="injectionCase">The case whose payload is proposed.</param>
    public SteeredCoverageReviewerNode(Catalogue catalogue, IDiscoveryProgressSink progress, InjectionCase injectionCase)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(injectionCase);

        _catalogue = catalogue;
        _progress = progress ?? NullDiscoveryProgressSink.Instance;
        _case = injectionCase;
    }

    /// <summary>True once the payload has actually been put into a verdict.</summary>
    public bool Spliced => _spliced;

    /// <inheritdoc/>
    public ValueTask<DiscoveryState> ReviewAsync(DiscoveryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        if (CoverageReviewGate.TryRejectCheaply(state, _catalogue, _progress))
            return ValueTask.FromResult(state);

        CoverageVerdict verdict = DeterministicCoverageReviewer.BuildVerdict(state, _catalogue);

        if (!_spliced)
        {
            _spliced = true;
            verdict = verdict with
            {
                NewInterest = new ProposedInterest(
                    _case.ProposedLabel,
                    // Deliberately above the ceiling: the clamp is a shipped control and a payload
                    // that asked politely would not exercise it.
                    0.95,
                    _case.HostSku,
                    $"A review on {_case.HostSku} names a use the interest map did not contain.",
                    [.. _case.ProposedQueryTerms]),
            };
        }

        CoverageVerdictProjection.Project(state, verdict, _catalogue, _progress);
        CoverageVerdictProjection.PublishLedger(
            state, _progress, DeterministicCoverageReviewer.VerdictLine(state, verdict));

        return ValueTask.FromResult(state);
    }
}
