// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Evals.Calibration;

/// <summary>One customer's turn state, built exactly the way <c>Demo01</c> builds it.</summary>
/// <param name="PersonaId">The customer.</param>
/// <param name="Profile">The seeded profile.</param>
/// <param name="Map">The derived interest map.</param>
/// <param name="Context">The guardrail context — the source of the owned-SKU exclusion.</param>
/// <param name="Abstains">True when the §F.8 gate fires before retrieval, so this customer produces no rows.</param>
/// <param name="AbstainReason">Why, when it does.</param>
public sealed record CalibrationPersona(
    string PersonaId,
    CustomerProfile Profile,
    InterestMap Map,
    GuardrailContext Context,
    bool Abstains,
    string AbstainReason);

/// <summary>
/// One score distribution, with the slice it came from and a plain-English statement of what a
/// single row IS.
/// </summary>
/// <param name="Threshold">Which threshold cuts it.</param>
/// <param name="Slice">"fit" or "held-out".</param>
/// <param name="RowMeaning">What one value in <paramref name="Values"/> is a score OF.</param>
/// <param name="Values">The raw scores, unsorted, one per row.</param>
public sealed record ScorePopulation(string Threshold, string Slice, string RowMeaning, IReadOnlyList<double> Values)
{
    /// <summary>Row count.</summary>
    public int Count => Values.Count;

    /// <summary>The values, ascending. Computed once.</summary>
    public IReadOnlyList<double> Sorted { get; } = [.. Values.Order()];

    /// <summary>
    /// The fraction of rows a cut at <paramref name="value"/> ADMITS — the right tail, inclusive,
    /// because every one of the four shipped cuts admits on <c>&gt;=</c> and rejects on <c>&lt;</c>.
    /// </summary>
    /// <param name="value">The cut.</param>
    public double AdmitRate(double value) =>
        Count == 0 ? double.NaN : Values.Count(v => v >= value) / (double)Count;

    /// <summary>
    /// The empirical value whose right-tail mass is at most <paramref name="alpha"/>: the smallest
    /// row value <c>v</c> in this population with <c>AdmitRate(v) &lt;= alpha</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated as an ORDER STATISTIC rather than an interpolated quantile on purpose. Interpolating
    /// invents a score no row ever took, and in a distribution with an atom at exactly 0 — which
    /// the concept space has, in bulk — interpolation lands the cut inside the atom, where the
    /// realised admit rate is nothing like the requested one. Taking a value the population
    /// actually produced keeps the realised rate reportable.
    /// </para>
    /// <para>
    /// Ties therefore make the realised rate LOWER than <paramref name="alpha"/>, never higher, and
    /// the realised rate is printed beside every derived number rather than assumed equal to the
    /// target.
    /// </para>
    /// </remarks>
    /// <param name="alpha">Target admit rate, 0..1.</param>
    public double CutAtAdmitRate(double alpha)
    {
        if (Count == 0) return double.NaN;
        if (alpha >= 1.0) return Sorted[0];

        // Walk down from the top: the first DISTINCT value whose inclusive right tail is still
        // within budget is the cut.
        double cut = Sorted[^1];
        for (int i = Sorted.Count - 1; i >= 0; i--)
        {
            var candidate = Sorted[i];
            if (AdmitRate(candidate) > alpha) break;
            cut = candidate;
        }

        return cut;
    }

    /// <summary>An ascending percentile, by nearest-rank. Reporting only.</summary>
    /// <param name="p">Percentile in 0..100.</param>
    public double Percentile(double p)
    {
        if (Count == 0) return double.NaN;
        var rank = (int)Math.Ceiling(p / 100.0 * Count);
        return Sorted[Math.Clamp(rank - 1, 0, Count - 1)];
    }

    /// <summary>The arithmetic mean. Reporting only.</summary>
    public double Mean => Count == 0 ? double.NaN : Values.Average();
}

/// <summary>
/// Collects, in ONE pass over the authored cohort, the four score distributions the three
/// space-dependent thresholds cut — in whichever space <see cref="EmbeddingSpace"/> resolved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every row is produced by the SHIPPED arithmetic.</b> The dense scores come out of the same
/// <see cref="ProductVectorIndex.Search"/> the retriever calls; the attribution matches come out of
/// <see cref="Demo01_RecommendationAgent.AttributionMatch"/>; the confidences out of
/// <see cref="Demo01_RecommendationAgent.ConfidenceFrom"/> with the signal
/// <see cref="Demo01_RecommendationAgent.AttributeSignalAsync"/> credited. Nothing here
/// re-implements a formula it is calibrating.
/// </para>
/// <para>
/// <b>Everything is collected against the SHIPPED configuration.</b> The confidence distribution
/// depends on which products retrieval returned, which depends on the dense floor — so the three
/// derivations are not independent. Collecting all four populations under the build as it stands
/// today is what makes "the distribution this threshold cuts" a statement about the shipped system
/// rather than about a hypothetical one. The consequence of applying all three at once is then
/// measured directly, by re-running the demos, rather than predicted.
/// </para>
/// </remarks>
public static class CalibrationPopulations
{
    /// <summary>Names used for the four cut points, in reports and in the stored record.</summary>
    public const string DenseFloorName = "HybridRetriever.DenseScoreFloor";

    /// <summary>Attribution cut name.</summary>
    public const string AttributionName = "Demo01.AttributionFloor";

    /// <summary>Confidence primary-tray cut name.</summary>
    public const string ConfidencePrimaryName = "ConfidenceBands.PrimaryThreshold";

    /// <summary>Confidence drop-line cut name.</summary>
    public const string ConfidenceSecondaryName = "ConfidenceBands.SecondaryThreshold";

    /// <summary>How many signals the offline arm searches — mirrors <c>Demo01</c>'s own constant.</summary>
    private const int SignalsSearched = 3;

    /// <summary>Presentation budget per signal on the offline arm — mirrors <c>Demo01</c>'s own constant.</summary>
    private const int CandidatesPerSignal = 2;

    /// <summary>Builds every authored customer's turn state, the abstaining ones included.</summary>
    public static IReadOnlyList<CalibrationPersona> BuildPersonas()
    {
        var catalogue = Catalogue.Default;
        var built = new List<CalibrationPersona>();

        foreach (var id in Personas.AllPersonaIds)
        {
            var profile = UserProfiles.Require(id);
            var prompt  = Personas.CanonicalPromptFor(id);

            var classified = PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, Personas.DemoToday);

            var map = InterestMapBuilder.Build(
                profile.User,
                profile.Purchases,
                catalogue.BySku,
                statedNeeds: null,
                asOf: Personas.DemoToday,
                sensitiveCategoryNames: catalogue.SensitiveCategories);

            var context = GuardrailContext.Create(
                catalogue.BySku,
                profile.User,
                map,
                classified,
                categories: catalogue.Categories,
                customerUtterance: prompt,
                asOf: Personas.DemoToday);

            var abstains = GuardrailPipeline.ShouldAbstain(context, out var reason);
            built.Add(new CalibrationPersona(id, profile, map, context, abstains, reason));
        }

        return built;
    }

    /// <summary>
    /// The DENSE-FLOOR population: every raw cosine in the per-leg candidate list the floor screens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The floor is applied inside <see cref="HybridRetriever.SearchAsync"/> to the
    /// <c>perLeg</c>-long list <see cref="ProductVectorIndex.Search"/> returns — not to the whole
    /// catalogue — so that list is the population. <c>perLeg</c> is
    /// <c>max(topK, PerLegCandidates)</c>, which on the offline arm's <c>topK = 6</c> is the
    /// default 24.
    /// </para>
    /// <para>
    /// A query whose vector is UNAVAILABLE or all-zero contributes nothing: the dense leg does not
    /// run for it, so the floor never sees it. Those queries are counted and reported rather than
    /// entered as zeros, which would drag the distribution's mass down and pull every derived cut
    /// with it.
    /// </para>
    /// </remarks>
    /// <param name="personas">The slice.</param>
    /// <param name="retriever">A retriever built on the resolved space.</param>
    /// <param name="skippedQueries">Queries whose dense leg could not run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<double>> DenseScoresAsync(
        IReadOnlyList<CalibrationPersona> personas,
        HybridRetriever retriever,
        List<string> skippedQueries,
        CancellationToken cancellationToken = default)
    {
        var products = Catalogue.Default.All;
        var scores   = new List<double>();

        foreach (var persona in personas)
        {
            if (persona.Abstains) continue;

            foreach (var query in OfflineQueriesFor(persona))
            {
                var vector = await EmbeddingSpace.EmbedAsync(products, query.Need, cancellationToken).ConfigureAwait(false);

                if (vector.IsUnavailable() || EmbeddingVectors.IsAllZero(vector.Span))
                {
                    skippedQueries.Add($"{persona.PersonaId}: \"{query.Need}\"");
                    continue;
                }

                var perLeg = Math.Max(query.EffectiveTopK, retriever.Options.PerLegCandidates);
                foreach (var hit in retriever.VectorIndex.Search(vector.Span, perLeg, query.ToPredicate()))
                    scores.Add(hit.Score);
            }
        }

        return scores;
    }

    /// <summary>
    /// The ATTRIBUTION population: every derived signal against every catalogue product's own
    /// embedding document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This is the model-path population, and it is the only one in which this floor
    /// discriminates at all.</b> On the offline arm the probe handed to
    /// <see cref="Demo01_RecommendationAgent.AttributeSignalAsync"/> is the search need that
    /// surfaced the product — which IS the searching signal's own label — so that signal matches
    /// itself at 1.0 and the floor cannot drop anything. Deriving a cut from a population whose
    /// mode is 1.0 by construction would produce a number about the identity of a string with
    /// itself. The population used instead is the one the constant's own remarks measure and the
    /// one the fallback branch screens: <c>label × product document</c> over the whole catalogue.
    /// </para>
    /// <para>
    /// The label × issued-query population is collected too, and reported beside it, so the reader
    /// can see the degeneracy rather than take this paragraph's word for it.
    /// </para>
    /// </remarks>
    /// <param name="personas">The slice.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<double>> AttributionMatchesAsync(
        IReadOnlyList<CalibrationPersona> personas,
        CancellationToken cancellationToken = default)
    {
        var catalogue = Catalogue.Default;
        var products  = catalogue.All;
        var matches   = new List<double>();

        var docs = products.ToDictionary(p => p.Id, EmbeddingDocument.ForProduct, StringComparer.Ordinal);

        foreach (var persona in personas)
        {
            if (persona.Abstains) continue;

            foreach (var signal in persona.Map.Signals)
            {
                var label = await EmbeddingSpace.EmbedAsync(products, signal.Label, cancellationToken).ConfigureAwait(false);

                foreach (var product in products)
                {
                    var doc       = docs[product.Id];
                    var docVector = await EmbeddingSpace.EmbedAsync(products, doc, cancellationToken).ConfigureAwait(false);
                    var cosine    = EmbeddingVectors.DotOfUnitVectors(label.Span, docVector.Span);

                    matches.Add(Demo01_RecommendationAgent.AttributionMatch(signal.Label, doc, cosine));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// The degenerate companion to <see cref="AttributionMatchesAsync"/>: every signal against
    /// every query the same map issues, which is what the offline arm actually screens.
    /// </summary>
    /// <param name="personas">The slice.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<double>> AttributionMatchesOfflineArmAsync(
        IReadOnlyList<CalibrationPersona> personas,
        CancellationToken cancellationToken = default)
    {
        var products = Catalogue.Default.All;
        var matches  = new List<double>();

        foreach (var persona in personas)
        {
            if (persona.Abstains) continue;

            var probes = OfflineQueriesFor(persona).Select(q => q.Need).ToArray();

            foreach (var signal in persona.Map.Signals)
            {
                var label = await EmbeddingSpace.EmbedAsync(products, signal.Label, cancellationToken).ConfigureAwait(false);

                foreach (var probe in probes)
                {
                    var probeVector = await EmbeddingSpace.EmbedAsync(products, probe, cancellationToken).ConfigureAwait(false);
                    var cosine      = EmbeddingVectors.DotOfUnitVectors(label.Span, probeVector.Span);

                    matches.Add(Demo01_RecommendationAgent.AttributionMatch(signal.Label, probe, cosine));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// The CONFIDENCE population: the confidence of every product the offline arm PRESENTS,
    /// credited to the signal the shipped attribution rule picks.
    /// </summary>
    /// <remarks>
    /// This is the exact set <see cref="ConfidenceBands.Apply"/> bands, minus whatever the earlier
    /// guardrail stages remove first — a superset, and stated as one. It is small: at most
    /// <c>3 signals × 2 products</c> per customer. The size is printed with the number, because a
    /// quantile taken on sixty rows has a resolution of one sixtieth and no report of it should
    /// imply otherwise.
    /// </remarks>
    /// <param name="personas">The slice.</param>
    /// <param name="retriever">A retriever built on the resolved space — WITH the floor in force.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<double>> PresentedConfidencesAsync(
        IReadOnlyList<CalibrationPersona> personas,
        HybridRetriever retriever,
        CancellationToken cancellationToken = default)
    {
        var catalogue    = Catalogue.Default;
        var products     = catalogue.All;
        var confidences  = new List<double>();

        foreach (var persona in personas)
        {
            if (persona.Abstains) continue;

            var taken = new HashSet<string>(StringComparer.Ordinal);

            foreach (var query in OfflineQueriesFor(persona))
            {
                var result = await retriever.SearchAsync(query, cancellationToken).ConfigureAwait(false);

                var kept = 0;
                foreach (var hit in result.Hits)
                {
                    if (kept >= CandidatesPerSignal) break;
                    if (!taken.Add(hit.ProductId)) continue;
                    if (!catalogue.TryGet(hit.ProductId, out var product) || product is null) continue;

                    kept++;

                    var signal = await Demo01_RecommendationAgent
                        .AttributeSignalAsync([query.Need], product, persona.Map, cancellationToken)
                        .ConfigureAwait(false);

                    if (signal is null) continue;   // dropped upstream of the bands; never banded.

                    var label     = await EmbeddingSpace.EmbedAsync(products, signal.Label, cancellationToken).ConfigureAwait(false);
                    var docVector = await EmbeddingSpace
                        .EmbedAsync(products, EmbeddingDocument.ForProduct(product), cancellationToken)
                        .ConfigureAwait(false);

                    var fit = EmbeddingVectors.DotOfUnitVectors(label.Span, docVector.Span);
                    confidences.Add(Demo01_RecommendationAgent.ConfidenceFrom(signal.Strength, fit));
                }
            }
        }

        return confidences;
    }

    /// <summary>
    /// The NULL confidence population: every derived signal against every catalogue product,
    /// whether retrieval would ever have surfaced it or not.
    /// </summary>
    /// <remarks>
    /// The chance distribution the second derivation rule reads. A confidence this high is what an
    /// ARBITRARY catalogue product reaches against this customer's interests — so a tray line drawn
    /// inside that mass is admitting products at a rate chance alone would supply.
    /// </remarks>
    /// <param name="personas">The slice.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<double>> NullConfidencesAsync(
        IReadOnlyList<CalibrationPersona> personas,
        CancellationToken cancellationToken = default)
    {
        var products    = Catalogue.Default.All;
        var confidences = new List<double>();

        foreach (var persona in personas)
        {
            if (persona.Abstains) continue;

            foreach (var signal in persona.Map.Signals)
            {
                var label = await EmbeddingSpace.EmbedAsync(products, signal.Label, cancellationToken).ConfigureAwait(false);

                foreach (var product in products)
                {
                    var docVector = await EmbeddingSpace
                        .EmbedAsync(products, EmbeddingDocument.ForProduct(product), cancellationToken)
                        .ConfigureAwait(false);

                    var fit = EmbeddingVectors.DotOfUnitVectors(label.Span, docVector.Span);
                    confidences.Add(Demo01_RecommendationAgent.ConfidenceFrom(signal.Strength, fit));
                }
            }
        }

        return confidences;
    }

    /// <summary>
    /// The NULL dense population: every catalogue cosine for every issued query, not only the
    /// per-leg head. The chance distribution the second rule reads for the retrieval floor.
    /// </summary>
    /// <param name="personas">The slice.</param>
    /// <param name="retriever">A retriever built on the resolved space.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<double>> NullDenseScoresAsync(
        IReadOnlyList<CalibrationPersona> personas,
        HybridRetriever retriever,
        CancellationToken cancellationToken = default)
    {
        var products = Catalogue.Default.All;
        var scores   = new List<double>();

        foreach (var persona in personas)
        {
            if (persona.Abstains) continue;

            foreach (var query in OfflineQueriesFor(persona))
            {
                var vector = await EmbeddingSpace.EmbedAsync(products, query.Need, cancellationToken).ConfigureAwait(false);
                if (vector.IsUnavailable() || EmbeddingVectors.IsAllZero(vector.Span)) continue;

                // topK = the whole catalogue: every eligible product, not the head.
                foreach (var hit in retriever.VectorIndex.Search(vector.Span, products.Count, query.ToPredicate()))
                    scores.Add(hit.Score);
            }
        }

        return scores;
    }

    /// <summary>
    /// The queries one customer's turn issues on the deterministic arm: the top
    /// <see cref="SignalsSearched"/> signals by strength, shaped exactly as <c>Demo01</c> shapes
    /// them — same topK, same market, same owned-SKU exclusion.
    /// </summary>
    /// <param name="persona">The customer.</param>
    public static IReadOnlyList<RetrievalQuery> OfflineQueriesFor(CalibrationPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        var exclude = new HashSet<string>(persona.Context.OwnedProductIds, StringComparer.Ordinal);

        return
        [
            .. persona.Map.Signals
                .OrderByDescending(s => s.Strength)
                .Take(SignalsSearched)
                .Select(s => RetrievalQuery.For(s.Label) with
                {
                    TopK = CandidatesPerSignal + 4,
                    Market = persona.Context.User.Market,
                    ExcludeProductIds = exclude
                })
        ];
    }
}
