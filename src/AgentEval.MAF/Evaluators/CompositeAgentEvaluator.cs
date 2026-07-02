// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using Microsoft.Agents.AI;
using static AgentEval.MAF.Evaluators.HybridEvalInterop;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Runs several inner <see cref="IAgentEvaluator"/>s (e.g. AgentEval-local ⊕ Foundry) over the same
/// items <b>concurrently</b>, each isolated and individually time-boxed, and merges them into one
/// <see cref="AgentEvaluationResults"/>. Pass ONE instance to <c>agent.EvaluateAsync(queries, composite)</c>
/// — the agent still runs once, and this composite fans out to the inner evaluators over its items.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just pass an array to <c>agent.EvaluateAsync(queries, IAgentEvaluator[])</c>?</b> MAF's
/// multi-evaluator overload runs the inners <i>sequentially</i> and does not isolate failures — a slow or
/// failing cloud evaluator (Foundry) blocks the others and can lose their results. This composer runs them
/// with <c>Task.WhenAll</c> (wall-clock ≈ the slowest inner, not the sum) and wraps each in a
/// per-inner <c>try/catch</c> + linked-CTS timeout, so a Foundry timeout becomes a visible "skipped" branch
/// while the local results survive.
/// </para>
/// <para>
/// A genuine <i>caller</i> cancellation still propagates (see the <c>when</c> guard); only an inner's own
/// failure/timeout is isolated. An optional <see cref="CircuitBreaker"/> skips a persistently-failing
/// source fast instead of paying its timeout every call.
/// </para>
/// </remarks>
public sealed class CompositeAgentEvaluator : IAgentEvaluator
{
    private readonly IReadOnlyList<(string Source, IAgentEvaluator Evaluator, TimeSpan? Timeout)> _inners;
    private readonly CircuitBreaker? _breaker;

    /// <summary>Creates a composite over the given inner evaluators.</summary>
    /// <param name="inners">
    /// The inner evaluators, each tagged with a stable <c>Source</c> label (used to prefix its metrics and
    /// to tag its report branch) and an optional per-inner hard <c>Timeout</c> ceiling.
    /// </param>
    /// <param name="name">Display name for this evaluator.</param>
    /// <param name="breaker">Optional shared circuit breaker; when open for a source, that source is skipped.</param>
    public CompositeAgentEvaluator(
        IReadOnlyList<(string Source, IAgentEvaluator Evaluator, TimeSpan? Timeout)> inners,
        string name = "Foundry ⊕ AgentEval",
        CircuitBreaker? breaker = null)
    {
        _inners = inners ?? throw new ArgumentNullException(nameof(inners));
        Name = name;
        _breaker = breaker;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>The untouched per-source results from the last call — feed to <c>UnifiedEvalReport.Build</c>.</summary>
    public IReadOnlyList<(string Source, AgentEvaluationResults Result)> CapturedPerSource { get; private set; } = [];

    /// <inheritdoc/>
    public async Task<AgentEvaluationResults> EvaluateAsync(
        IReadOnlyList<EvalItem> items, string evalName = "Composite", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var perSource = await Task.WhenAll(_inners.Select(async p =>
        {
            if (_breaker?.IsOpen(p.Source) == true)                                          // breaker: skip fast, no call
                return (p.Source, SkippedResults(items, p.Source, "circuit breaker open", label: "skipped"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (p.Timeout is { } t) cts.CancelAfter(t);                                       // per-inner hard ceiling
            try
            {
                var r = await p.Evaluator.EvaluateAsync(items, evalName, cts.Token).ConfigureAwait(false);
                _breaker?.OnSuccess(p.Source);
                return (p.Source, r);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested is false)    // isolate inner failure/timeout;
            {                                                                                 // genuine CALLER cancel still bubbles
                _breaker?.OnFailure(p.Source);
                return (p.Source, SkippedResults(items, p.Source, ex));
            }
        })).ConfigureAwait(false);

        CapturedPerSource = perSource;
        return Merge(perSource, items, evalName);
    }
}
