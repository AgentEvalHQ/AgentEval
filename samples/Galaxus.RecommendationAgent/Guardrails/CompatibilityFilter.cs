// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Workflows;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// Compatibility against the hardware the customer already owns (§8.1 B-7). Demo 1's third
/// mechanical check, and the one it was missing while Demo 2 had it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pipeline stage and not just the tool.</b> <c>FindComplements</c> already enforces
/// compatibility as a PRE-filter, so nothing incompatible can come back through it. But that is
/// one of five routes into a recommendation: <c>SearchProductsByMeaning</c>,
/// <c>FindSimilarProducts</c>, <c>BrowseCategory</c> and <c>GetProductDetails</c> all reach the
/// same catalogue without passing the gate, and the model may present anything it saw. A rule
/// enforced on one route out of five is a rule the demo cannot claim.
/// </para>
/// <para>
/// <b>The rule is Demo 2's, not the one §8.1 B-7 spells out, and the difference is measured.</b>
/// The row's wording is "drop any presented accessory whose <c>compat:</c> tags are disjoint from
/// every <c>compat:</c> tag the customer owns". That naive rule was tried in Demo 2 and was
/// recorded to fire on a lens hood and a camera strap (<c>compat:camera-body</c>) for a customer
/// who owns a camera body tagged <c>compat:sony-e-mount</c> — two different SIDES of one
/// relationship, not two mismatched standards. So this stage reuses
/// <see cref="CompatibilityChecker"/>'s family rule verbatim: a conflict is a candidate declaring
/// a value in a family the customer's own hardware CONSTRAINS, with a different value.
/// <c>54mm-portafilter</c> against an owned <c>58mm-portafilter</c> conflicts;
/// <c>compat:camera-body</c> against <c>compat:sony-e-mount</c> does not, because
/// <c>body</c> and <c>mount</c> are different families. Calling into that class rather than
/// copying it is deliberate — two implementations of one rule eventually disagree, and the
/// disagreement shows up as a demo where the loop and the single agent drop different things.
/// </para>
/// <para>
/// ⚠ <b>Inapplicable when there is nothing to constrain.</b> Nadia owns nothing that constrains
/// an accessory family the way an espresso group does; on her turn this arm has a chance floor of
/// 1.0 and says so instead of banking a clean sheet it did not earn.
/// </para>
/// </remarks>
public static class CompatibilityFilter
{
    /// <summary>
    /// Tests one product against the customer's owned compatibility values.
    /// </summary>
    /// <param name="product">The presented product.</param>
    /// <param name="ownedByFamily">Family → the values the customer's own hardware declares in it.</param>
    /// <param name="conflictValue">The candidate's offending <c>compat:</c> value, on a conflict.</param>
    /// <param name="conflictFamily">The family the conflict is in, on a conflict.</param>
    /// <returns>True when nothing conflicts — including when nothing could.</returns>
    public static bool IsCompatible(
        Product product,
        IReadOnlyDictionary<string, IReadOnlySet<string>> ownedByFamily,
        out string? conflictValue,
        out string? conflictFamily)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(ownedByFamily);

        conflictValue = null;
        conflictFamily = null;

        var declared = CompatibilityChecker.CompatValues(product);
        if (declared.Count == 0 || ownedByFamily.Count == 0) return true;

        foreach (var value in declared.Order(StringComparer.Ordinal))
        {
            var family = CompatibilityChecker.FamilyOf(value);
            if (family.Length == 0) continue;
            if (!ownedByFamily.TryGetValue(family, out var ownedValues)) continue;   // family unconstrained

            if (ownedValues.Contains(value))
            {
                // It fits. An owned match anywhere clears the whole item, and the out-parameters
                // are reset so a caller cannot read a stale conflict off a compatible verdict.
                conflictValue = null;
                conflictFamily = null;
                return true;
            }

            conflictValue = value;
            conflictFamily = family;
        }

        return conflictFamily is null;
    }

    /// <summary>Drops presented accessories that cannot pair with the customer's own hardware.</summary>
    /// <param name="set">The answer so far.</param>
    /// <param name="context">The catalogue-derived bar, carrying the owned compatibility values.</param>
    /// <param name="ledger">The ledger every drop and note is written to.</param>
    public static RecommendationSet Apply(RecommendationSet set, GuardrailContext context, GuardrailLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        var ownedByFamily = context.OwnedCompatValuesByFamily;

        if (ownedByFamily.Count == 0)
        {
            ledger.Note(GuardrailStage.Compatibility, GuardrailReasons.ArmInapplicable, "compatibility",
                "this customer owns nothing carrying a compat: tag, so there is no constraint for an accessory to "
              + "violate (chance floor 1.0 — not a pass). A 54 mm portafilter would have passed this turn");
            return set;
        }

        return set with
        {
            Recommendations = Filter(set.Recommendations, context, ownedByFamily, ledger),
            AlsoConsider    = Filter(set.AlsoConsider,    context, ownedByFamily, ledger)
        };
    }

    private static IReadOnlyList<RecommendationDto> Filter(
        IReadOnlyList<RecommendationDto> items,
        GuardrailContext context,
        IReadOnlyDictionary<string, IReadOnlySet<string>> ownedByFamily,
        GuardrailLedger ledger)
    {
        var kept = new List<RecommendationDto>(items.Count);

        foreach (var item in items)
        {
            if (!context.ProductsBySku.TryGetValue(item.ProductId, out var product)) { kept.Add(item); continue; }

            if (IsCompatible(product, ownedByFamily, out var conflictValue, out var conflictFamily))
            {
                kept.Add(item);
                continue;
            }

            ledger.Drop(GuardrailStage.Compatibility, GuardrailReasons.IncompatibleWithOwned, item.ProductId,
                Explain(conflictValue!, conflictFamily!, ownedByFamily));
        }

        return kept;
    }

    /// <summary>The verbatim justification written to the ledger, shared with the tool-time screen.</summary>
    /// <param name="conflictValue">The candidate's offending <c>compat:</c> value.</param>
    /// <param name="conflictFamily">The family the conflict is in.</param>
    /// <param name="ownedByFamily">Family → the values the customer's own hardware declares in it.</param>
    public static string Explain(
        string conflictValue,
        string conflictFamily,
        IReadOnlyDictionary<string, IReadOnlySet<string>> ownedByFamily)
    {
        ArgumentNullException.ThrowIfNull(ownedByFamily);

        var owned = ownedByFamily.TryGetValue(conflictFamily, out var values)
            ? string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => "compat:" + v))
            : "(none recorded)";

        return $"declares compat:{conflictValue}, and this customer's own hardware is {owned}. Same "
             + $"\"{conflictFamily}\" family, different standard — a code check against their own hardware, not a "
             + "hope expressed in a prompt";
    }
}
