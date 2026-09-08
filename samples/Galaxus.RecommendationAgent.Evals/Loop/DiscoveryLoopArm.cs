// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Controls;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Evals.Loop;

/// <summary>
/// The shared body of every loop CONTROL in this project: read the customer from the prompt, run
/// <see cref="EvalDiscoveryLoop"/>, keep the telemetry.
/// </summary>
/// <remarks>
/// <para>
/// The customer id is read from the PROMPT, like the live agent and every scripted control does,
/// rather than handed in by the harness. A control configured out of band would be running a
/// different experiment from the one the live arm runs, and the comparison would not be paired.
/// </para>
/// <para>
/// Subclasses differ in exactly one thing each — which reviewer they build — so a difference in
/// score between two of them is a difference in the reviewer and nothing else.
/// </para>
/// </remarks>
public abstract class DiscoveryLoopArm : IDiscoveryLoopArm
{
    private readonly IProductRetriever _retriever;
    private readonly IReviewTextSource _reviews;
    private readonly DiscoveryLoopOptions _options;

    /// <summary>Creates the arm over a bound retriever.</summary>
    /// <param name="retriever">The same retriever every other arm searches with.</param>
    /// <param name="options">Bounds and presentation size. Defaults to <see cref="DiscoveryLoopOptions.Default"/>.</param>
    /// <param name="reviews">Untrusted review text source. Defaults to the catalogue's own reviews.</param>
    protected DiscoveryLoopArm(
        IProductRetriever retriever,
        DiscoveryLoopOptions? options = null,
        IReviewTextSource? reviews = null)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
        _options = options ?? DiscoveryLoopOptions.Default;
        _reviews = reviews ?? CatalogueReviewSource.Instance;
    }

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public int MaxRounds => _options.MaxRounds;

    /// <inheritdoc/>
    public bool AppliesQueryVocabularyConstraint => _options.ApplyQueryVocabularyConstraint;

    /// <inheritdoc/>
    public DiscoveryLoopTelemetry? LastRun { get; private set; }

    /// <summary>Builds this arm's coverage gate for one customer.</summary>
    /// <param name="customerId">The customer the turn is for.</param>
    protected abstract ICoverageReviewer CreateReviewer(string customerId);

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

        var loop = new EvalDiscoveryLoop(_retriever, CreateReviewer(userId), _reviews, _options);
        var (response, telemetry) = await loop.RunAsync(Name, userId, cancellationToken).ConfigureAwait(false);

        LastRun = telemetry;
        return response;
    }
}
