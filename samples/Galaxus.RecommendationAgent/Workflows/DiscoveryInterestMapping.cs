// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// Derives the interest map, the anti-interests, the compatibility constraints and the
/// ownership set FROM CODE, using the same <see cref="InterestMapBuilder"/> Demo 1 runs.
/// </summary>
/// <remarks>
/// <para>
/// This is both the offline mapper's whole implementation and the live mapper's fallback, which
/// is deliberate: the loop must produce a usable map when the model is absent, when it is
/// unreachable, and when it returns something unparseable — and all three must produce the SAME
/// map, or a "degraded" run would silently be a differently-shaped run.
/// </para>
/// <para>
/// <b>Round 1 deliberately carries no category hint.</b> Constraining the first round to
/// categories derived from the customer's own history is how a recommender stays inside the
/// departments it already knows about — and the cross-category jump is the thing this demo
/// exists to show. Categories enter in round 2, from the reviewer, taken off candidates it
/// actually saw. That is the vocabulary changing hands, and it is visible on the search lines.
/// </para>
/// </remarks>
public static class DiscoveryInterestMapping
{
    /// <summary>The label given to the customer's in-session request when it becomes an interest.</summary>
    public const string SessionRequestLabelPrefix = "stated this session: ";

    /// <summary>
    /// Populates the state's map, anti-interests, constraints, ownership set and coverage rows.
    /// </summary>
    /// <param name="state">The run state, mutated in place.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <returns>The classified purchase lines, which the Presenter needs for its ledger.</returns>
    public static IReadOnlyList<ClassifiedPurchase> PopulateFromCode(DiscoveryState state, Catalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);

        var profile = UserProfiles.Find(state.CustomerId);
        if (profile is null)
        {
            // No silent fallback to a default persona: answering for the wrong customer is a
            // plausible, wrong demo, which is worse than one that stops.
            state.DegradedNotes.Add($"unknown customer '{state.CustomerId}' — no history, no interests derived");
            return [];
        }

        var user = profile.User with { PersonalizationEnabled = state.PersonalizationConsent };

        // §F.6 — when consent is off, history is NOT filtered, minimised or summarised. It is not
        // read. The builder itself refuses to touch it, so the data never reaches the state and
        // therefore never reaches a prompt.
        var classified = state.PersonalizationConsent
            ? PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, Personas.DemoToday)
            : [];

        var statedNeeds = string.IsNullOrWhiteSpace(state.SessionRequest)
            ? null
            : new[] { state.SessionRequest };

        var built = InterestMapBuilder.BuildDetailed(
            user,
            profile.Purchases,
            catalogue.BySku,
            statedNeeds: statedNeeds,
            asOf: Personas.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);

        state.Interests.Clear();
        state.Coverage.Clear();

        int index = 0;
        foreach (var signal in built.Map.Signals
                                       .OrderByDescending(s => s.Strength)
                                       .ThenBy(s => s.Label, StringComparer.Ordinal)
                                       .Take(DiscoveryState.MaxInterests))
        {
            var interest = ToInterest(signal, $"I-{++index}");
            state.Interests.Add(interest);
            state.CoverageFor(interest.Id);
        }

        // ── anti-interests ───────────────────────────────────────────────────────────
        //
        // This catalogue models no RETURN rows, so the strongest available "do not recommend"
        // signal is a gift: gift-wrapped, shipped elsewhere, no review, no follow-on purchase.
        // Stated rather than implied — a demo that claims to honour returns while modelling none
        // is claiming a control it does not have.
        state.AntiInterests.Clear();
        var giftLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in classified)
        {
            if (!line.IsGift) continue;
            if (!giftLeaves.Add(line.Product.LeafCategory)) continue;

            state.AntiInterests.Add(new AntiInterest(
                line.Product.LeafCategory,
                [line.PurchaseId],
                line.Because));
        }

        // ── constraints and ownership ────────────────────────────────────────────────
        state.Constraints.Clear();
        state.Constraints.AddRange(CompatibilityChecker.Derive(classified, state.Market));

        state.OwnedProductIds.Clear();
        foreach (var line in classified)
            if (!line.IsGift)
                state.OwnedProductIds.Add(line.Product.Id);

        return classified;
    }

    /// <summary>Projects one code-derived signal onto a loop interest.</summary>
    /// <param name="signal">The derived signal.</param>
    /// <param name="id">The id to give it, <c>"I-n"</c>.</param>
    public static Interest ToInterest(InterestSignal signal, string id)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        bool latent =
            string.Equals(signal.EvidenceKind, InterestEvidenceKinds.CoPurchaseContext, StringComparison.Ordinal) ||
            string.Equals(signal.EvidenceKind, InterestEvidenceKinds.CapabilityGap, StringComparison.Ordinal);

        bool stated = string.Equals(signal.EvidenceKind, InterestEvidenceKinds.StatedInSession, StringComparison.Ordinal);

        var label = stated ? SessionRequestLabelPrefix + signal.Label : signal.Label;

        return new Interest
        {
            Id = id,
            Label = label,
            Kind = latent ? InterestKind.Latent : InterestKind.Direct,
            Origin = InterestOrigin.Mapper,
            Confidence = Math.Clamp(signal.Strength, 0.0, 1.0),
            EvidenceSignalIds = signal.EvidencePurchaseIds,
            Rationale = Rationale(signal, latent, stated),
            QueryTerms = QueryTermsFor(signal),
            CategoryHints = [],       // see the type remarks: round 1 uses the customer's words
            AttributeHints = new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// The two-to-four search phrases an interest contributes to round 1.
    /// </summary>
    /// <remarks>
    /// Written in the customer's vocabulary on purpose — that is the arm the loop is measured
    /// against. A conjunction label is split on its commas so each half of the conjunction gets
    /// its own query; a capability-gap label contributes the companion class it names, which is
    /// the thing the customer is MISSING and the one a collaborative filter cannot express.
    /// </remarks>
    /// <param name="signal">The derived signal.</param>
    public static IReadOnlyList<string> QueryTermsFor(InterestSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var terms = new List<string> { signal.Label.Trim() };

        if (string.Equals(signal.EvidenceKind, InterestEvidenceKinds.CoPurchaseContext, StringComparison.Ordinal))
        {
            foreach (var phrase in signal.Label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (terms.Count >= DiscoveryQueryPlanner.MaxTermsPerInterest) break;
                if (phrase.Length < 4) continue;
                if (terms.Contains(phrase, StringComparer.OrdinalIgnoreCase)) continue;
                terms.Add(phrase);
            }
        }
        else if (string.Equals(signal.EvidenceKind, InterestEvidenceKinds.CapabilityGap, StringComparison.Ordinal))
        {
            const string marker = "but no ";
            int at = signal.Label.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var companion = signal.Label[(at + marker.Length)..].Trim();
                if (companion.Length > 2) terms.Add(companion);
            }
        }

        return terms;
    }

    private static string Rationale(InterestSignal signal, bool latent, bool stated) => stated
        ? "The customer said it in this session. History explains; the request decides."
        : latent
            ? string.Create(CultureInfo.InvariantCulture,
                $"The CONJUNCTION is the signal — {signal.EvidencePurchaseIds.Count} purchase(s) across the history share it, " +
                $"and dropping any one of them drops the inference ({signal.EvidenceKind}).")
            : string.Create(CultureInfo.InvariantCulture,
                $"A single signal states it: {string.Join(", ", signal.EvidencePurchaseIds)} ({signal.EvidenceKind}).");
}
