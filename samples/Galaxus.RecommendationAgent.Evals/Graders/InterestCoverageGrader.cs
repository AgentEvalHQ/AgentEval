// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// Scores one arm's recommendations against a derived gold interest map.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Served" requires reaching a NEW category.</b> A latent token counts as served only when the
/// product carrying it sits in a leaf category the customer has not already bought from.
/// Otherwise "discovery" is satisfied by recommending another item from a category they already
/// shop, which is not discovery — it is the thing the metric was built to distinguish itself from.
/// </para>
/// <para>
/// <b>Latent coverage is RECALL, and recall is monotone in k.</b> An arm that presents more items
/// covers more tokens by luck alone. That is why every arm is cut to one declared budget before it
/// is paired (<see cref="GradeAtDeclaredK"/>), why a precision channel sits beside it
/// (<see cref="CoverageScore.PrecisionAtK"/>, floor R/N, indifferent to k), and why the paired
/// comparison refuses any pair whose two sides presented different counts. A coverage number
/// without its k is not a number.
/// </para>
/// <para>
/// <b>Manifest coverage is a regression channel, not a headline.</b> Its chance floor is high (a
/// category-frequency baseline scores around 0.7), so it can only tell you that an agent has
/// stopped recommending anything sensible at all. Averaging it with latent coverage would hide the
/// number that matters, which is why the two are never combined.
/// </para>
/// <para>
/// <b>Empty denominators produce NaN, never 0 and never 1.</b> A persona with no latent gold must
/// be EXCLUDED from the mean, not silently scored — an empty-denominator case scored as a pass is
/// one of the flattering shapes this repository has been bitten by.
/// </para>
/// <para>
/// ⚠ <b>Why <see cref="ForcedChoice"/> exists, and what it caught.</b> On the three-persona corpus
/// this suite used to have, latent coverage was close to saturated by chance and carried no
/// information about customers at all: MEASURED, a one-pass retriever that never saw the gold and
/// the tag-join ORACLE that derives from it scored identically, cell for cell, and an answer built
/// for Marco scored as well against Sofia's gold as against his own — because their gold sets were
/// the same single token. The forced choice asks the question coverage cannot, its chance floor is
/// exactly 1/N by construction, and it cannot be saturated. After the corpus extension
/// (<c>Docs/MEASUREMENT_STATUS.md</c> §4) the oracle scores 1.000 (12 of 12) on it against a chance
/// of 0.083, and no arm is identical to the oracle cell for cell any more. The arm stays because
/// nothing about the metric changed — only the corpus did, and a corpus can regress.
/// </para>
/// </remarks>
public static class InterestCoverageGrader
{
    /// <summary>
    /// Grades one arm's presentations for one persona.
    /// </summary>
    /// <param name="gold">The derived gold map.</param>
    /// <param name="presented">What the arm actually presented, from the tool trace.</param>
    public static CoverageScore Grade(GoldInterestMap gold, IReadOnlyList<PresentedCall> presented)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(presented);

        var catalogue = Catalogue.Default;
        var servedLatent = new HashSet<string>(StringComparer.Ordinal);
        var servedManifest = new HashSet<string>(StringComparer.Ordinal);
        var relevantSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int phantom = 0;
        int newCategory = 0;

        foreach (PresentedCall r in presented)
        {
            if (!catalogue.TryGet(r.Sku, out var product) || product is null)
            {
                phantom++;      // D1 is Eval 01's business; here it just cannot serve anything
                continue;
            }

            bool isNewCategory = !gold.OwnedCategories.Contains(product.LeafCategory);
            if (isNewCategory) newCategory++;

            if (isNewCategory)
            {
                // The SAME vocabulary the gold is drawn from (InterestMapGold.EligibleTokens),
                // never the wider Product.Attributes set. Matching on Attributes would let a spec
                // key or a spec value that happens to spell a use-tag suffix "serve" a latent
                // interest the product does not actually carry.
                bool carriesGold = false;
                foreach (string token in InterestMapGold.EligibleTokens(product))
                {
                    if (!gold.Latent.Contains(token)) continue;
                    servedLatent.Add(token);
                    carriesGold = true;
                }

                // RELEVANT for the precision channel: a new-category item carrying at least one
                // latent gold token. Counted once per SKU — the same product presented twice fills
                // two slots and serves the customer once, and the second slot is a miss.
                if (carriesGold) relevantSkus.Add(product.Id);
            }

            if (gold.Manifest.Contains(product.LeafCategory))
                servedManifest.Add(product.LeafCategory);
        }

        double latent = gold.Latent.Count == 0
            ? double.NaN
            : servedLatent.Count / (double)gold.Latent.Count;

        double manifest = gold.Manifest.Count == 0
            ? double.NaN
            : servedManifest.Count / (double)gold.Manifest.Count;

        // Precision over what was SHOWN. Undefined for a silent answer and undefined when there is
        // no gold to be relevant to — both are NaN here, and neither is a pass. The slot-based
        // precision@k needs a declared budget and is computed by GradeAtDeclaredK.
        double precisionOfPresented = gold.Latent.Count == 0 || presented.Count == 0
            ? double.NaN
            : relevantSkus.Count / (double)presented.Count;

        return new CoverageScore(
            Latent: latent,
            Manifest: manifest,
            LatentServed: servedLatent.Count,
            LatentTotal: gold.Latent.Count,
            ManifestServed: servedManifest.Count,
            ManifestTotal: gold.Manifest.Count,
            PresentedCount: presented.Count,
            NewCategoryCount: newCategory,
            PhantomCount: phantom,
            PresentedBeforeCut: presented.Count,
            RelevantCount: relevantSkus.Count,
            PrecisionOfPresented: precisionOfPresented);
    }

    /// <summary>
    /// The arm's top <paramref name="k"/>, in the arm's OWN stated order — the cut every arm gets
    /// before it is scored at a declared budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the trace order of the arm's <c>PresentRecommendation</c> calls</b>
    /// (<see cref="PresentedCall.Order"/>), because that IS the arm's stated ranking: the live agent
    /// is instructed to present "in the order you want them shown" and "your strongest first"; the
    /// scripted controls present in their ranked order; Demo 2's arm replays
    /// <c>DiscoveryState.Presented</c>, the screened answer in Ranker order. Nothing here re-sorts
    /// by any score of the grader's own — the grader must not supply the ranking it grades.
    /// </para>
    /// <para>
    /// <b>Ties.</b> <see cref="PresentedCall.Order"/> is a strict position in one tool timeline, so
    /// two calls cannot share it and no tie can arise in practice. Should a trace ever carry two
    /// calls with one position, the tie is broken by SKU (ordinal) so the cut is deterministic and
    /// reproducible rather than dependent on list construction order; the rule is stated so a
    /// reader is not left to guess. Duplicates and phantom ids are NOT removed before the cut —
    /// they occupied slots the customer saw, and removing them would hand the arm a free slot it
    /// did not earn.
    /// </para>
    /// </remarks>
    /// <param name="presented">What the arm presented, from the tool trace.</param>
    /// <param name="k">The declared budget. Zero or negative returns nothing.</param>
    public static IReadOnlyList<PresentedCall> TopK(IReadOnlyList<PresentedCall> presented, int k)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (k <= 0) return [];

        return
        [
            .. presented
                .OrderBy(p => p.Order)
                .ThenBy(p => p.Sku, StringComparer.Ordinal)
                .Take(k)
        ];
    }

    /// <summary>
    /// Grades one arm's answer AT A DECLARED BUDGET: the top <paramref name="declaredK"/> in the
    /// arm's own order, recall (latent coverage) and precision@k over those slots, each against its
    /// own floor, plus the cross-persona forced choice on the same cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a declared k and not each arm's own.</b> MEASURED on the 2026-09-04 live run: the
    /// live agent presented 0–4 items, every control exactly 5, Demo 2's loop 7–12, and the sign
    /// test paired their raw coverage. Coverage is recall and monotone in k, so that comparison
    /// measured presentation count as much as architecture. Every arm now receives the same
    /// budget (the canonical utterance declares it), every arm is cut to it here, and only cells
    /// cut to the same budget are ever paired.
    /// </para>
    /// <para>
    /// <b>The recall floor is derived at min(k, presented)</b> — an arm that under-filled its
    /// budget is compared against a random draw of the size it actually made, which is the
    /// non-flattering direction (a random-5 floor beside a 3-item answer would be a higher bar
    /// than the answer faced). <b>The precision floor is R/N and does not depend on k</b>
    /// (<see cref="ChanceFloors.RandomPrecisionFloor"/>). Precision@k divides by the DECLARED k,
    /// so the under-filled slots count as misses and a silent answer scores 0.000, not NaN:
    /// silence is a fact about what the customer received, and it is never a pass.
    /// </para>
    /// </remarks>
    /// <param name="personaId">The customer this answer was produced for.</param>
    /// <param name="goldByPersona">Every scored persona's derived gold, keyed by customer id.</param>
    /// <param name="presented">What the arm presented, from the tool trace, BEFORE any cut.</param>
    /// <param name="declaredK">The budget every arm was given. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="declaredK"/> is not positive.</exception>
    public static CoverageScore GradeAtDeclaredK(
        string personaId,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona,
        IReadOnlyList<PresentedCall> presented,
        int declaredK)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaId);
        ArgumentNullException.ThrowIfNull(goldByPersona);
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentOutOfRangeException.ThrowIfLessThan(declaredK, 1);

        if (!goldByPersona.TryGetValue(personaId, out var gold))
            throw new ArgumentException($"No derived gold for '{personaId}'.", nameof(personaId));

        IReadOnlyList<PresentedCall> cut = TopK(presented, declaredK);
        CoverageScore score = Grade(gold, cut);

        var (_, recallFloor, _) = ChanceFloors.RandomDrawFloor(gold, score.PresentedCount);
        var (_, _, precisionFloor) = ChanceFloors.RandomPrecisionFloor(gold);

        double precisionAtK = gold.Latent.Count == 0
            ? double.NaN
            : score.RelevantCount / (double)declaredK;

        return score with
        {
            DeclaredK = declaredK,
            PresentedBeforeCut = presented.Count,
            LatentFloor = recallFloor,
            PrecisionAtK = precisionAtK,
            PrecisionFloor = precisionFloor,
            ForcedChoice = ForcedChoice(personaId, goldByPersona, cut),
        };
    }

    /// <summary>
    /// Grades one arm's answer AND the two controls a bare coverage number cannot carry: the
    /// random-draw floor at the arm's own k, and the cross-persona forced choice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The floor is computed at k = the arm's OWN presentation count.</b> A fixed k = 5 floor
    /// compared against an arm that presented eight is wrong in the FLATTERING direction exactly
    /// when the agent is most verbose: MEASURED on this corpus, Nadia's floor is 0.129 at k = 1,
    /// 0.491 at k = 5 and 0.655 at k = 8. Whatever the arm actually presented is the k its floor
    /// is derived at.
    /// </para>
    /// <para>
    /// <b>The forced choice is the arm that cannot saturate.</b> See <see cref="ForcedChoice"/>.
    /// </para>
    /// </remarks>
    /// <param name="personaId">The customer this answer was produced for.</param>
    /// <param name="goldByPersona">Every scored persona's derived gold, keyed by customer id.</param>
    /// <param name="presented">What the arm presented, from the tool trace.</param>
    public static CoverageScore GradeWithControls(
        string personaId,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona,
        IReadOnlyList<PresentedCall> presented)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaId);
        ArgumentNullException.ThrowIfNull(goldByPersona);
        ArgumentNullException.ThrowIfNull(presented);

        if (!goldByPersona.TryGetValue(personaId, out var gold))
            throw new ArgumentException($"No derived gold for '{personaId}'.", nameof(personaId));

        var score = Grade(gold, presented);

        // k is what this arm actually presented, never a constant.
        var (_, floor, _) = ChanceFloors.RandomDrawFloor(gold, score.PresentedCount);
        var (_, _, precisionFloor) = ChanceFloors.RandomPrecisionFloor(gold);

        // ⚠ This is the OWN-k grading. Two scores from this method are comparable only when the
        // two arms presented the same number of items — see PairedCoverageReport.SignTestAtEqualK.
        return score with
        {
            LatentFloor = floor,
            PrecisionFloor = precisionFloor,
            ForcedChoice = ForcedChoice(personaId, goldByPersona, presented),
        };
    }

    /// <summary>
    /// The cross-persona forced choice: 1 when this persona's own gold scores STRICTLY highest of
    /// every scorable persona's gold on this same answer, 0 otherwise, NaN when fewer than two
    /// personas are scorable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this arm exists.</b> Latent coverage answers "did the answer contain a product
    /// carrying a planted tag?". It does not answer "was this answer FOR this customer?" — and
    /// personalisation is the only thing Eval 02 exists to support. MEASURED on the corpus this
    /// suite used to have, an answer built for Marco scored 1.000 against Sofia's gold and vice
    /// versa, so coverage carried no evidence about personalisation at all. It does now — but only
    /// because the corpus was changed, and this arm is the thing that would say so again if it
    /// stopped.
    /// </para>
    /// <para>
    /// ⚠ <b>Disjoint gold sets are not enough.</b> MEASURED while the corpus was being extended:
    /// Marco and Pierre had fully disjoint gold, and BOTH still lost the forced choice, because the
    /// same products carried both customers' tokens and one five-item answer covered both sets
    /// completely. Two customers whose interests are served by the same objects cannot be told
    /// apart by an answer, whatever their token sets say — that is a property of the corpus this
    /// arm measures and a coverage number cannot.
    /// </para>
    /// <para>
    /// <b>Its chance floor is exactly 1/N and it cannot be saturated by construction.</b> A
    /// degenerate arm that presents the same products to everyone wins at most one persona, which
    /// is precisely the 1/N a coin gets. No corpus edit can raise that floor — unlike a
    /// coverage floor, which rises with the pool and with k. Cost: N² deterministic gradings and
    /// no model calls.
    /// </para>
    /// <para>
    /// STRICTLY highest, so a tie is a loss. An arm that scores equally for two customers has not
    /// distinguished them, and scoring a tie as a win is how a metric with no discrimination
    /// reports a perfect result.
    /// </para>
    /// </remarks>
    /// <param name="personaId">The customer this answer was produced for.</param>
    /// <param name="goldByPersona">Every scored persona's derived gold, keyed by customer id.</param>
    /// <param name="presented">What the arm presented.</param>
    public static double ForcedChoice(
        string personaId,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona,
        IReadOnlyList<PresentedCall> presented)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaId);
        ArgumentNullException.ThrowIfNull(goldByPersona);
        ArgumentNullException.ThrowIfNull(presented);

        var scorable = goldByPersona.Where(kv => !kv.Value.LatentIsEmpty).ToList();
        if (scorable.Count < 2) return double.NaN;
        if (!goldByPersona.TryGetValue(personaId, out var own) || own.LatentIsEmpty) return double.NaN;

        double mine = Grade(own, presented).Latent;
        if (double.IsNaN(mine)) return double.NaN;

        foreach (var (otherId, otherGold) in scorable)
        {
            if (string.Equals(otherId, personaId, StringComparison.Ordinal)) continue;
            double theirs = Grade(otherGold, presented).Latent;
            if (!double.IsNaN(theirs) && theirs >= mine) return 0.0;   // a tie is a loss
        }

        return 1.0;
    }

    /// <summary>
    /// The chance floor of the forced choice: exactly 1/N over the scorable personas. Derived, not
    /// quoted, and unsaturable — no corpus edit can move it.
    /// </summary>
    /// <param name="scorablePersonaCount">How many personas have a non-empty latent gold set.</param>
    public static double ForcedChoiceFloor(int scorablePersonaCount) =>
        scorablePersonaCount <= 0 ? double.NaN : 1.0 / scorablePersonaCount;

    /// <summary>
    /// The latent tokens a gold set contains that NO product outside the customer's owned
    /// categories can serve. A token in this set is unreachable by construction, so leaving it in
    /// the denominator would cap every arm below 1.0 for a reason that has nothing to do with the
    /// agent.
    /// </summary>
    /// <remarks>
    /// Reported rather than removed. Silently dropping unreachable tokens would raise every
    /// score; leaving them in and not saying so would depress every score. Printing the count is
    /// the only option that does not move a number without telling anyone.
    /// </remarks>
    /// <param name="gold">The derived gold map.</param>
    public static IReadOnlyList<string> UnreachableLatentTokens(GoldInterestMap gold)
    {
        ArgumentNullException.ThrowIfNull(gold);
        var catalogue = Catalogue.Default;

        var unreachable = new List<string>();
        foreach (string token in gold.Latent.OrderBy(t => t, StringComparer.Ordinal))
        {
            bool reachable = catalogue.All.Any(p =>
                !gold.OwnedCategories.Contains(p.LeafCategory)
                && InterestMapGold.EligibleTokens(p).Contains(token));

            if (!reachable) unreachable.Add(token);
        }

        return unreachable;
    }
}
