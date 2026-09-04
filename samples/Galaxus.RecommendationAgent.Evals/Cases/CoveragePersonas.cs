// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Cases;

/// <summary>
/// Eval 02's analysis set — and, just as importantly, the personas that are NOT in it and why.
/// </summary>
/// <remarks>
/// <para>
/// <b>The design pre-registers twelve personas. This corpus now supports twelve, and how it got
/// there is the part that has to stay auditable.</b> The previous revision of this file said the
/// corpus supported three, and it was right: §C.2's P-01..P-12 cast never existed in this
/// repository (design §0.5 / D-1 records that the eval lane and the agent lane were written in
/// parallel with different casts), and at n = 3 the pre-registered rule could not be evaluated at
/// all. The response was NOT to relabel three personas as twelve. It was to author nine more
/// customer histories and the 23 catalogue SKUs their latent interests need in order to be
/// reachable, to a stated structure, and then to re-measure whatever came out.
/// </para>
/// <para>
/// <b>The structure each cohort persona was authored to</b> — stated here because it is the thing
/// that makes the difference between extending a corpus and tuning one:
/// </para>
/// <list type="number">
///   <item><description>at least THREE latent tokens, so a persona's coverage is not a single
///   Bernoulli trial printed as a fraction;</description></item>
///   <item><description>DISJOINT from every other scored persona's set, so a strict win in the
///   forced choice is arithmetically possible for both sides of every pair;</description></item>
///   <item><description>each token carried by two of that customer's OWN purchases spanning two
///   leaf categories — which is what rule R2 asks for — and by two or three products in leaves the
///   customer does not own, so the token is REACHABLE rather than capping every arm below
///   1.0;</description></item>
///   <item><description>at most <c>InterestMapGold.LatentMaximumCarriers</c> carriers in total, so
///   the derived random-draw floor stays far below the advisory 0.50 ceiling.</description></item>
/// </list>
/// <para>
/// Gold is still DERIVED from those histories by rules R3 / R1 / R2. Not one token in this file is
/// hand-picked, and the numbers the run prints are whatever the rule produces from the corpus.
/// </para>
/// <list type="bullet">
///   <item><description><b>Excluded — Luca Ferrari.</b> One purchase, a USB-C cable. R2 yields an
///   EMPTY latent-gold set, and a coverage metric with an empty denominator is a silent
///   divide-by-zero that flatters the mean. He is DELIBERATELY left thin: he is Eval 01's
///   abstention persona, and an agent with no refusal path is the actual danger.</description></item>
///   <item><description><b>Excluded — Elena Weber.</b> Her corpus exists to test SUPPRESSION
///   (design §0.5 / D-6). Latent coverage rewards reaching a category the customer has not bought
///   from, and the most reachable new category from her history is the sensitive one — so scoring
///   her here would reward exactly what Eval 01 case C-07 forbids. Two metrics pulling in opposite
///   directions on one persona is not a measurement, and excluding her is stated here rather than
///   left to be noticed.</description></item>
/// </list>
/// </remarks>
public static class CoveragePersonas
{
    /// <summary>The personas actually scored. Twelve.</summary>
    public static IReadOnlyList<CoveragePersona> All { get; } =
    [
        new(Personas.NadiaUserId, "Nadia Brunner",
            "Five purchases across three departments whose only shared signal is use context. "
          + "Latent: hut-to-hut, first-light, off-grid-power. (Her gold used to be carried / "
          + "dawn-start / golden-hour — MEASURED, those sit on 10, 17 and 10 of the catalogue and "
          + "are stopwords under the tightened R2 specificity rule, which is why the narrow tags "
          + "were authored rather than the cap simply lowered.)"),
        new(Personas.MarcoUserId, "Marco Iten",
            "Four real Home Espresso purchases plus two gift lines that R3 must exclude before any "
          + "gold is derived. Latent: dialling-in, latte-art, machine-care — disjoint from Pierre's, "
          + "who lives in the same department."),
        new(Personas.SofiaUserId, "Sofia Keller",
            "Two consumable cadences and a capability gap. Latent: whole-bean, soft-water-brewing, "
          + "prep-and-store. Her manifest categories are the ones a frequency counter already sees, "
          + "which is what makes the manifest/latent split visible on her."),
        new(Personas.AndreaUserId, "Andrea Riva",
            "The all-weather bike commuter. Latent: dark-commute, wet-road, winter-base-miles. "
          + "Carries a REPLACEMENT cadence — the same road tyres 524 days apart."),
        new(Personas.TheoUserId, "Théo Salamin",
            "The desk listener with a two-channel room. Latent: desk-listening, two-channel-room, "
          + "travel-listening. Three interests inside one department that a category counter reads "
          + "as one."),
        new(Personas.JonasUserId, "Jonas Vogt",
            "The SECOND gift trap, run the other way round: he OWNS the console Marco was given, and "
          + "his own two gift lines are camera gear. Latent: couch-co-op, handheld-away, "
          + "late-night-session — none of them photographic, which is the point."),
        new(Personas.LeaUserId, "Lea Moser",
            "The city and travel photographer. Latent: street-walkaround, card-to-edit, "
          + "carry-on-only, city. One gift line (a controller for a nephew) that R3 removes."),
        new(Personas.RenzoUserId, "Renzo Bianchi",
            "The mountain trail runner — deliberately not Nadia. Latent: mountain-running, "
          + "steep-ascents, effort-tracking. Shares one SKU with Andrea (the shell jacket) and "
          + "still shares no gold token with him."),
        new(Personas.PierreUserId, "Pierre Bonvin",
            "The 54 mm compact-machine espresso drinker. Latent: hand-ground, weigh-every-shot, "
          + "small-kitchen-espresso. He and Marco are the disjointness stress case: same department, "
          + "no shared token. CONSUMABLE cadence on cleaning tablets at ~63-day intervals."),
        new(Personas.NoemiUserId, "Noemi Kunz",
            "Long exposures, and no camera body on file. Latent: long-exposure-water, blue-hour, "
          + "wide-vistas, landscape. The full-frame body is a REACHABLE answer rather than a lookup, "
          + "the same shape as Elena's monitor without the sensitive category."),
        new(Personas.MirjamUserId, "Mirjam Bosshard",
            "The living-room music and film household. Latent: multi-room-music, "
          + "late-evening-volume, dock-and-play. Three of her four purchases are cold-start "
          + "marketplace listings, so a system that ranks by review volume has little of hers to use."),
        new(Personas.DarioUserId, "Dario Fischer",
            "The bikepacker. Latent: bikepacking, self-supported, all-day-riding. Spans Cycling, "
          + "Outdoor and Power, and his REPLACEMENT cadence is a squeeze filter 519 days apart."),
    ];

    /// <summary>Personas deliberately left out of the analysis set, with the reason.</summary>
    public static IReadOnlyList<CoveragePersona> Excluded { get; } =
    [
        new(Personas.LucaUserId, "Luca Ferrari",
            "EXCLUDED and DELIBERATELY THIN — one purchase, so R2 yields an empty latent-gold set. "
          + "An empty denominator scored as a pass is a silent divide-by-zero that flatters the "
          + "mean. He is kept thin on purpose: he is the abstention case, and nothing else in this "
          + "corpus exercises the refusal path."),
        new(Personas.ElenaUserId, "Elena Weber",
            "EXCLUDED — her corpus tests sensitive-inference suppression. Latent coverage would "
          + "reward reaching the sensitive department that Eval 01 case C-07 forbids reaching."),
    ];

    /// <summary>The number of paired cases actually analysed.</summary>
    public static int AnalysedCount => All.Count;

    /// <summary>
    /// A CEILING on the power of the sign test at this persona count — 2 x (1/2)^n for a clean
    /// sweep of every paired case. At n = 12 this is 0.00049, so the pre-registered decision rule
    /// (≥ 10 wins of 12, p = 0.0386) is now evaluable — which it was not at n = 3, where the
    /// smallest attainable two-sided p was 0.250.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>It is a ceiling, not the number a run will report.</b> The exact sign test DISCARDS
    /// tied pairs, so the n it actually runs on is the non-tied count and the smallest p it can
    /// attain is correspondingly larger — 1.000 when every pair ties, which is what the previous
    /// corpus measurably produced for the arms that scored identically.
    /// <c>SignTestOutcome.MinimumAttainableP</c> is computed from the attained n and is the honest
    /// figure; this one bounds it from below. Twelve personas make the rule REACHABLE; they do not
    /// make it reached.
    /// </remarks>
    public static double MinimumAttainableTwoSidedP => Math.Min(1.0, 2.0 * Math.Pow(0.5, AnalysedCount));
}
