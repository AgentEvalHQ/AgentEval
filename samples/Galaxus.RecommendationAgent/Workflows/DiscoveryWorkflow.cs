// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Diagnostics;
using Azure.AI.OpenAI;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Retrieval;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// How one run of the discovery loop is configured.
/// </summary>
/// <param name="Offline">
/// True runs the loop with NO model at all: the deterministic arm. It is a BASELINE, not a
/// simulation of the agent — every claim about what the model adds needs both arms.
/// </param>
/// <param name="PersonalizationDisabled">
/// The FDPIC one-click opt-out (§F.6). The history is not filtered or summarised; it is not read,
/// so it never reaches the state and therefore never reaches a prompt.
/// </param>
/// <param name="SessionRequest">
/// What the customer typed. Null uses the persona's canonical prompt, so Demo 1 and Demo 2 are
/// compared on byte-identical input and any score difference is architecture, not prompt.
/// </param>
/// <param name="MaxRounds">
/// The round cap for this run. Lower it to make the round-cap termination REACHABLE in a test —
/// a guard nobody can trigger is a guard nobody has checked.
/// </param>
/// <param name="ChatClient">
/// The chat client for the live arm. Null with <paramref name="Offline"/> false builds one from
/// <see cref="Config"/>.
/// </param>
/// <param name="Retriever">The retrieval seam. Null builds the same hybrid retriever Demo 1 uses.</param>
/// <param name="Progress">Where domain events go. Null discards them.</param>
/// <param name="Nodes">
/// Per-stage overrides. Null everywhere is the shipped composition; a non-null entry replaces one
/// stage. This is the seam the termination probes and the eval lane drive — see
/// <see cref="DiscoveryNodeOverrides"/>.
/// </param>
/// <param name="ModelCallTimeout">
/// Wall-clock ceiling on ONE model call. Null uses
/// <see cref="DiscoveryModelCall.DefaultModelCallTimeout"/>. A stalled deployment must degrade,
/// not queue — see the remarks on that property for the measurement that forced it.
/// </param>
public sealed record DiscoveryLoopOptions(
    bool Offline = false,
    bool PersonalizationDisabled = false,
    string? SessionRequest = null,
    int MaxRounds = DiscoveryState.DefaultMaxDiscoveryRounds,
    IChatClient? ChatClient = null,
    IProductRetriever? Retriever = null,
    IDiscoveryProgressSink? Progress = null,
    DiscoveryNodeOverrides? Nodes = null,
    TimeSpan? ModelCallTimeout = null);

/// <summary>
/// Replaces individual stages of the loop.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the three termination conditions can be <b>proved</b> rather than asserted. A
/// guard that cannot be triggered on demand has never been checked — and this repository has a
/// standing rule that extreme values are wiring faults until shown otherwise, in BOTH directions:
/// a reviewer that never approves and one that never rejects are both faults, and both look fine
/// on a single happy-path run.
/// </para>
/// <para>
/// Overriding a stage does NOT bypass the graph, the routing predicates,
/// <c>CoverageVerdictProjection</c>, the post-checks or the guardrail pipeline. Those are what the
/// probes are testing; only the stage that has to behave adversarially is substituted.
/// </para>
/// </remarks>
/// <param name="Mapper">Stage 1.</param>
/// <param name="Search">Stage 2.</param>
/// <param name="Reviewer">Stage 3.</param>
/// <param name="Ranker">Stage 4.</param>
/// <param name="Presenter">Stage 5.</param>
public sealed record DiscoveryNodeOverrides(
    IInterestMapperNode? Mapper = null,
    IDiscoverySearchNode? Search = null,
    ICoverageReviewerNode? Reviewer = null,
    IRankerNode? Ranker = null,
    IPresenterNode? Presenter = null);

/// <summary>What one run produced.</summary>
/// <param name="State">The final state: the map, the ledger, the selection, the stop reason.</param>
/// <param name="Workflow">
/// The graph that ran. Handed back so the eval lane can build a
/// <c>MAFWorkflowAdapter.FromMAFWorkflow(workflow, name, executorIds, …)</c> over it and assert
/// that the loop actually looped.
/// </param>
/// <param name="ExecutorIds">The five executor ids, in graph order.</param>
/// <param name="RoutesTaken">Route ids in order, immediate repeats collapsed. A TRACE, not a count.</param>
/// <param name="SuperSteps">MAF super-steps completed.</param>
/// <param name="Elapsed">Wall time.</param>
public sealed record DiscoveryRunResult(
    DiscoveryState State,
    Workflow Workflow,
    IReadOnlyList<string> ExecutorIds,
    IReadOnlyList<string> RoutesTaken,
    int SuperSteps,
    TimeSpan Elapsed)
{
    /// <summary>True when the loop-back edge fired at least once.</summary>
    public bool Looped => RoutesTaken.Contains(DiscoveryRouteIds.ReviewToMoreDiscovery, StringComparer.Ordinal);
}

/// <summary>
/// Builds the graph: five executors, five named routes, ONE conditional loop-back edge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every edge carries <see cref="DiscoveryState"/> and nothing else.</b> That is what makes
/// the loop-back a one-line <c>AddEdge</c> rather than a join plus a message-identity scheme, and
/// it is why fan-out lives inside <c>DiscoveryExecutor</c> instead of in the graph.
/// </para>
/// <para>
/// <b>The reviewer's two outgoing edges provably partition the space.</b>
/// <c>DiscoveryLimitReached</c> is DEFINED as <c>!CoverageApproved &amp;&amp; !NeedsMoreDiscovery</c>,
/// so for any state exactly one of the two conditions below holds: approved ⇒ Ranker; unapproved
/// with budget, progress and a runnable query ⇒ Discovery; otherwise ⇒ Ranker. There is no third
/// outcome and no state in which the reviewer has no outgoing edge — the loop can neither hang
/// nor fall off the graph.
/// </para>
/// <para>
/// <b>API note (MAF 1.17.0).</b> A bare <c>AddEdge&lt;T&gt;(src, tgt)</c> is <c>CS0121</c>-ambiguous
/// — three generic overloads exist and none has an exact two-parameter arity. Naming
/// <c>condition:</c> and <c>label:</c> selects one unambiguously, which is why every edge below
/// is written with named arguments.
/// </para>
/// </remarks>
public static class DiscoveryWorkflowFactory
{
    /// <summary>The workflow's name, used by the eval lane's adapter.</summary>
    public const string WorkflowName = "GalaxusDiscoveryLoop";

    /// <summary>Builds the graph over already-constructed nodes.</summary>
    /// <param name="mapper">Stage 1.</param>
    /// <param name="search">Stage 2.</param>
    /// <param name="reviewer">Stage 3.</param>
    /// <param name="ranker">Stage 4.</param>
    /// <param name="presenter">Stage 5.</param>
    /// <param name="progress">The sink the edge predicates publish routes to.</param>
    public static (Workflow Workflow, IReadOnlyList<string> ExecutorIds) Create(
        IInterestMapperNode mapper,
        IDiscoverySearchNode search,
        ICoverageReviewerNode reviewer,
        IRankerNode ranker,
        IPresenterNode presenter,
        IDiscoveryProgressSink progress)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(ranker);
        ArgumentNullException.ThrowIfNull(presenter);
        progress ??= NullDiscoveryProgressSink.Instance;

        var mapperExecutor = new InterestMapperExecutor(mapper, progress);
        var discoveryExecutor = new DiscoveryExecutor(search, progress);
        var reviewerExecutor = new CoverageReviewerExecutor(reviewer, progress);
        var rankerExecutor = new RankerExecutor(ranker, progress);
        var presenterExecutor = new PresenterExecutor(presenter, progress);

        var builder = new WorkflowBuilder(mapperExecutor)
            .AddEdge<DiscoveryState>(
                mapperExecutor, discoveryExecutor,
                condition: s => s is not null && ObserveRoute(
                    DiscoveryRouteIds.MapToDiscovery,
                    "➡ ROUTE  InterestMapper → Discovery   [interests mapped]",
                    progress),
                label: "interests mapped")

            .AddEdge<DiscoveryState>(
                discoveryExecutor, reviewerExecutor,
                condition: s => s is not null && ObserveRoute(
                    DiscoveryRouteIds.DiscoveryToReview,
                    "➡ ROUTE  Discovery → CoverageReviewer   [candidates ready]",
                    progress),
                label: "candidates ready")

            // ── THE LOOP-BACK ────────────────────────────────────────────────────────
            .AddEdge<DiscoveryState>(
                reviewerExecutor, discoveryExecutor,
                condition: s => s?.NeedsMoreDiscovery == true && ObserveRoute(
                    DiscoveryRouteIds.ReviewToMoreDiscovery,
                    $"↩ ROUTE  CoverageReviewer → Discovery   [gaps remain]   → round {s.DiscoveryRound + 1} of {s.MaxRounds}",
                    progress),
                label: "gaps remain")

            // ── THE EXIT — approval OR exhaustion, same target ────────────────────────
            .AddEdge<DiscoveryState>(
                reviewerExecutor, rankerExecutor,
                condition: s => s is not null
                             && (s.CoverageApproved || s.DiscoveryLimitReached)
                             && ObserveRoute(
                                    DiscoveryRouteIds.ReviewToRanker,
                                    s.CoverageApproved
                                        ? "➡ ROUTE  CoverageReviewer → Ranker   [coverage sufficient]"
                                        : $"➡ ROUTE  CoverageReviewer → Ranker   [{s.ResolveStopReason()}]   (degraded — PARTIAL answer)",
                                    progress),
                label: "coverage sufficient")

            .AddEdge<DiscoveryState>(
                rankerExecutor, presenterExecutor,
                condition: s => s is not null && ObserveRoute(
                    DiscoveryRouteIds.RankerToPresenter,
                    "➡ ROUTE  Ranker → Presenter   [ranked]",
                    progress),
                label: "ranked")

            .WithOutputFrom(presenterExecutor);

        return (builder.Build(validateOrphans: true), DiscoveryExecutorIds.All);
    }

    /// <summary>
    /// Instruments the PREDICATE, not the node. Always returns true so it can be <c>&amp;&amp;</c>-ed
    /// onto a real condition without changing it.
    /// </summary>
    /// <remarks>
    /// ⚠ MAF may evaluate a predicate more than once per super-step, so these events are a TRACE.
    /// Never derive a round number from them — the round number lives on the message. The console
    /// sink collapses immediate repeats for exactly this reason.
    /// </remarks>
    /// <param name="routeId">The route id.</param>
    /// <param name="description">The line to print.</param>
    /// <param name="progress">The sink.</param>
    private static bool ObserveRoute(string routeId, string description, IDiscoveryProgressSink progress)
    {
        progress.Publish(DiscoveryEvent.Route(routeId, description));
        return true;
    }
}

/// <summary>
/// The public entry point: run the bounded discovery loop for one customer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three terminations, and every one of them is reachable from here.</b> The round cap via
/// <see cref="DiscoveryLoopOptions.MaxRounds"/>; no-progress whenever a round adds zero NEW
/// product ids (which the dedup exclusion makes possible rather than hypothetical); and
/// gaps-unresolvable whenever the reviewer has no materially different query left. All three
/// exit through the SAME edge to the same downstream node, so exhaustion degrades to a PARTIAL
/// answer with a printed shortfall — it never throws and it never hangs.
/// </para>
/// </remarks>
public static class GalaxusDiscoveryLoop
{
    /// <summary>
    /// Runs the loop for one customer, end to end.
    /// </summary>
    /// <param name="userId">One of <see cref="Personas.AllPersonaIds"/>.</param>
    /// <param name="options">Run configuration. Null uses every default (live, consented, 3 rounds).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The final state, the graph, the routes taken and the timings.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="userId"/> is not an authored customer. There is deliberately no fallback to
    /// a default persona: running the wrong persona's history produces a plausible, wrong demo.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The live arm was requested and Azure credentials are not configured. Run with
    /// <see cref="DiscoveryLoopOptions.Offline"/> to use the deterministic arm instead.
    /// </exception>
    public static async ValueTask<DiscoveryRunResult> RunAsync(
        string userId,
        DiscoveryLoopOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        options ??= new DiscoveryLoopOptions();

        var catalogue = Catalogue.Default;

        // Canonical casing matters: GuardrailContext.Validate compares the interest map's owner
        // id to the customer id ORDINALLY, and a lookup that succeeded case-insensitively would
        // otherwise fail that check later, far from the cause.
        var profile = UserProfiles.Require(userId.Trim());

        var recorder = new RecordingDiscoveryProgressSink();
        IDiscoveryProgressSink progress = options.Progress is null
            ? recorder
            : new CompositeDiscoveryProgressSink(options.Progress, recorder);

        var retriever = options.Retriever
            ?? await HybridRetriever
                .BuildAsync(catalogue.All, ConceptEmbeddingSource.Instance, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        var chatClient = options.Offline ? null : options.ChatClient ?? CreateChatClient();

        var state = new DiscoveryState
        {
            CustomerId = profile.Id,
            Market = profile.Market,
            Language = profile.Language,
            PersonalizationConsent = !options.PersonalizationDisabled && profile.PersonalizationEnabled,
            MaxRounds = Math.Max(1, options.MaxRounds),
            SessionRequest = options.SessionRequest ?? Personas.CanonicalPromptFor(profile.Id)
        };

        var (mapper, search, reviewer, ranker, presenter) =
            BuildNodes(catalogue, retriever, chatClient, progress, options.ModelCallTimeout);

        if (options.Nodes is { } overrides)
        {
            mapper = overrides.Mapper ?? mapper;
            search = overrides.Search ?? search;
            reviewer = overrides.Reviewer ?? reviewer;
            ranker = overrides.Ranker ?? ranker;
            presenter = overrides.Presenter ?? presenter;
        }

        var (workflow, executorIds) = DiscoveryWorkflowFactory.Create(mapper, search, reviewer, ranker, presenter, progress);

        progress.Publish(DiscoveryEvent.RunStarted(state, chatClient is null ? "offline (deterministic arm)" : "live"));

        var clock = Stopwatch.StartNew();
        DiscoveryState? output = null;
        int superSteps = 0;

        var run = await InProcessExecution
            .RunStreamingAsync(workflow, state, sessionId: state.RunId.ToString("N"), cancellationToken)
            .ConfigureAwait(false);

        await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (workflowEvent)
            {
                case WorkflowOutputEvent { Data: DiscoveryState final }:
                    output = final;
                    break;

                case ExecutorFailedEvent failed:
                    // A node that throws must not take the run down silently. The message-borne
                    // counter means the round it was in did not consume budget either.
                    progress.Publish(DiscoveryEvent.Degraded(failed.ExecutorId, $"executor FAILED: {failed.Data}"));
                    break;

                case WorkflowErrorEvent error:
                    progress.Publish(DiscoveryEvent.Degraded("workflow", $"error: {error}"));
                    break;

                case WorkflowWarningEvent warning:
                    progress.Publish(DiscoveryEvent.Degraded("workflow", $"warning: {warning}"));
                    break;

                case SuperStepCompletedEvent:
                    superSteps++;
                    break;
            }
        }

        clock.Stop();

        var finalState = output ?? state;
        if (finalState.StopReason == DiscoveryStopReason.None)
            finalState.StopReason = finalState.ResolveStopReason();

        progress.Publish(DiscoveryEvent.RunComplete(finalState, clock.Elapsed));

        return new DiscoveryRunResult(
            finalState, workflow, executorIds, recorder.RoutesTaken(), superSteps, clock.Elapsed);
    }

    /// <summary>
    /// Chooses the node implementations. The SEARCH node is the same object on both arms — it
    /// makes zero model calls by design, so there is nothing to substitute.
    /// </summary>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="retriever">The retrieval seam.</param>
    /// <param name="chatClient">The chat client, or null for the deterministic arm.</param>
    /// <param name="progress">The sink.</param>
    /// <param name="modelCallTimeout">Per-call ceiling, or null for the default.</param>
    public static (IInterestMapperNode Mapper,
                   IDiscoverySearchNode Search,
                   ICoverageReviewerNode Reviewer,
                   IRankerNode Ranker,
                   IPresenterNode Presenter) BuildNodes(
        Catalogue catalogue,
        IProductRetriever retriever,
        IChatClient? chatClient,
        IDiscoveryProgressSink progress,
        TimeSpan? modelCallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(retriever);
        progress ??= NullDiscoveryProgressSink.Instance;

        var search = new CatalogueDiscoverySearch(catalogue, retriever, progress);

        if (chatClient is null)
        {
            return (new DeterministicInterestMapper(catalogue, progress),
                    search,
                    new DeterministicCoverageReviewer(catalogue, progress),
                    new DeterministicRanker(catalogue, progress),
                    new DeterministicPresenter(catalogue, progress));
        }

        var model = new DiscoveryModelCall(chatClient, progress, modelCallTimeout);

        return (new ModelInterestMapper(catalogue, model, progress),
                search,
                new ModelCoverageReviewer(catalogue, model, progress),
                new ModelRanker(catalogue, model, progress),
                new ModelPresenter(catalogue, model, progress));
    }

    private static IChatClient CreateChatClient()
    {
        if (!Config.IsConfigured)
        {
            throw new InvalidOperationException(
                "Azure OpenAI credentials are not configured. Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY, " +
                "or run the loop with DiscoveryLoopOptions { Offline = true } — the deterministic arm needs no key.");
        }

        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        return azureClient.GetChatClient(Config.Model).AsIChatClient();
    }
}
