// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// The five executor ids, frozen.
/// </summary>
/// <remarks>
/// These are a CONTRACT, not decoration. <c>MAFWorkflowAdapter.FromMAFWorkflow(workflow, name,
/// executorIds, …)</c> requires the id array explicitly because <c>Workflow.ExecutorBindings</c>
/// is internal to MAF, and the eval lane's structural assertions
/// (<c>HaveTraversedEdge("CoverageReviewer", "Discovery")</c> — i.e. <i>the loop actually
/// looped</i>) address the nodes by these strings. Renaming one without changing the eval is
/// exactly the lane drift §0.5 / D-1 is about.
/// </remarks>
public static class DiscoveryExecutorIds
{
    /// <summary>Stage 1 — one structured model call, or zero offline.</summary>
    public const string InterestMapper = "InterestMapper";

    /// <summary>Stage 2 — zero model calls, always. The loop-back edge's target.</summary>
    public const string Discovery = "Discovery";

    /// <summary>Stage 3 — a deterministic pre-gate, then at most one model call.</summary>
    public const string CoverageReviewer = "CoverageReviewer";

    /// <summary>Stage 4 — one model call, then three deterministic post-checks.</summary>
    public const string Ranker = "Ranker";

    /// <summary>Stage 5 — one model call for prose, plus the live price and stock read.</summary>
    public const string Presenter = "Presenter";

    /// <summary>All five, in graph order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [InterestMapper, Discovery, CoverageReviewer, Ranker, Presenter];
}

/// <summary>
/// The route ids the edge predicates publish.
/// </summary>
/// <remarks>
/// ⚠ MAF may evaluate an edge predicate more than once per super-step. Route events are a TRACE
/// of which edges were selected, not an authoritative count — never derive a round number from
/// them. The round number lives on the message.
/// </remarks>
public static class DiscoveryRouteIds
{
    /// <summary>InterestMapper → Discovery.</summary>
    public const string MapToDiscovery = "map-to-discovery";

    /// <summary>Discovery → CoverageReviewer.</summary>
    public const string DiscoveryToReview = "discovery-to-review";

    /// <summary>THE LOOP-BACK: CoverageReviewer → Discovery.</summary>
    public const string ReviewToMoreDiscovery = "review-to-more-discovery";

    /// <summary>THE EXIT: CoverageReviewer → Ranker, on approval OR exhaustion.</summary>
    public const string ReviewToRanker = "review-to-ranker";

    /// <summary>Ranker → Presenter.</summary>
    public const string RankerToPresenter = "ranker-to-presenter";
}

/// <summary>
/// Base for the loop's executors: publishes the started/completed pair around one node call and
/// measures its model spend.
/// </summary>
/// <remarks>
/// Executors are STATELESS. Everything that survives a round lives on the message, which is why
/// any node can be a resume root and why the round counter cannot drift between an executor
/// instance and the run it is executing.
/// </remarks>
internal static class DiscoveryNodeRunner
{
    /// <summary>Runs one node with the standard trace around it.</summary>
    /// <param name="nodeId">Executor id.</param>
    /// <param name="note">Optional parenthetical printed on the started line.</param>
    /// <param name="state">The run state.</param>
    /// <param name="progress">The sink.</param>
    /// <param name="body">The node call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async ValueTask<DiscoveryState> RunAsync(
        string nodeId,
        string? note,
        DiscoveryState state,
        IDiscoveryProgressSink progress,
        Func<DiscoveryState, CancellationToken, ValueTask<DiscoveryState>> body,
        CancellationToken cancellationToken)
    {
        progress.Publish(DiscoveryEvent.NodeStarted(nodeId, note));

        int callsBefore = state.ModelCalls;
        var clock = Stopwatch.StartNew();

        var result = await body(state, cancellationToken).ConfigureAwait(false);

        clock.Stop();
        progress.Publish(DiscoveryEvent.NodeCompleted(nodeId, result.ModelCalls - callsBefore, clock.Elapsed));

        return result;
    }
}

/// <summary>Stage 1. Builds the interest map before a single catalogue record has been seen.</summary>
/// <param name="mapper">The mapper node — deterministic or model-backed.</param>
/// <param name="progress">The sink.</param>
internal sealed partial class InterestMapperExecutor(IInterestMapperNode mapper, IDiscoveryProgressSink progress)
    : Executor(DiscoveryExecutorIds.InterestMapper)
{
    [MessageHandler]
    private ValueTask<DiscoveryState> HandleAsync(
        DiscoveryState state,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        DiscoveryNodeRunner.RunAsync(
            DiscoveryExecutorIds.InterestMapper, null, state, progress,
            (s, ct) => mapper.MapAsync(s, ct), cancellationToken);
}

/// <summary>
/// Stage 2, and the target of the loop-back edge. Zero model calls: round 1's queries come from
/// the map, round 2+'s from the reviewer's gaps.
/// </summary>
/// <param name="search">The search node.</param>
/// <param name="progress">The sink.</param>
internal sealed partial class DiscoveryExecutor(IDiscoverySearchNode search, IDiscoveryProgressSink progress)
    : Executor(DiscoveryExecutorIds.Discovery)
{
    [MessageHandler]
    private ValueTask<DiscoveryState> HandleAsync(
        DiscoveryState state,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        DiscoveryNodeRunner.RunAsync(
            DiscoveryExecutorIds.Discovery,
            "no model call — the queries come from the map, then from the gaps",
            state, progress,
            (s, ct) => search.RunRoundAsync(s, ct), cancellationToken);
}

/// <summary>
/// Stage 3. The only node with two outgoing edges, and the reason the loop is a loop.
/// </summary>
/// <remarks>
/// It also resolves the FINAL stop reason, in one place, from the same predicates the edges read
/// — so the printed reason and the taken edge cannot disagree.
/// </remarks>
/// <param name="reviewer">The reviewer node.</param>
/// <param name="progress">The sink.</param>
internal sealed partial class CoverageReviewerExecutor(ICoverageReviewerNode reviewer, IDiscoveryProgressSink progress)
    : Executor(DiscoveryExecutorIds.CoverageReviewer)
{
    [MessageHandler]
    private ValueTask<DiscoveryState> HandleAsync(
        DiscoveryState state,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        DiscoveryNodeRunner.RunAsync(
            DiscoveryExecutorIds.CoverageReviewer, null, state, progress,
            async (s, ct) =>
            {
                var reviewed = await reviewer.ReviewAsync(s, ct).ConfigureAwait(false);
                reviewed.StopReason = reviewed.ResolveStopReason();
                return reviewed;
            },
            cancellationToken);
}

/// <summary>
/// Stage 4. One model call, then three deterministic post-checks that REMOVE rather than
/// down-rank.
/// </summary>
/// <param name="ranker">The ranker node.</param>
/// <param name="progress">The sink.</param>
internal sealed partial class RankerExecutor(IRankerNode ranker, IDiscoveryProgressSink progress)
    : Executor(DiscoveryExecutorIds.Ranker)
{
    [MessageHandler]
    private ValueTask<DiscoveryState> HandleAsync(
        DiscoveryState state,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        DiscoveryNodeRunner.RunAsync(
            DiscoveryExecutorIds.Ranker, null, state, progress,
            (s, ct) => ranker.RankAsync(s, ct), cancellationToken);
}

/// <summary>
/// Stage 5. Renders the answer, reads price and stock live, and yields the workflow's output.
/// </summary>
/// <param name="presenter">The presenter node.</param>
/// <param name="progress">The sink.</param>
internal sealed partial class PresenterExecutor(IPresenterNode presenter, IDiscoveryProgressSink progress)
    : Executor(DiscoveryExecutorIds.Presenter)
{
    [MessageHandler]
    private async ValueTask<DiscoveryState> HandleAsync(
        DiscoveryState state,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var result = await DiscoveryNodeRunner.RunAsync(
            DiscoveryExecutorIds.Presenter, null, state, progress,
            (s, ct) => presenter.PresentAsync(s, ct), cancellationToken).ConfigureAwait(false);

        await context.YieldOutputAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
