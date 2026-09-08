// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Cases;

/// <summary>
/// Eval 02b's twelve stated-need cases — one per scored persona, each a natural-language need
/// with at least three hard constraints that a tag join cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> Every one of Eval 02's twelve personas speaks ONE history-driven
/// sentence ("tell me what I should be looking at next"), and that is exactly the task a
/// <c>WHERE tags &amp;&amp; shared_tags</c> wins by construction — it did, at 1.000. The task the
/// pitch says the language model is FOR — a multi-constraint intent in a shopper's own words, a
/// budget, a thing they already own that the answer has to fit, an exclusion, a deadline — had
/// zero cases. These are them.
/// </para>
/// <para>
/// <b>Authoring rules, stated so the difference between authoring and tuning is visible.</b>
/// </para>
/// <list type="number">
///   <item><description>Every constraint is a STRUCTURED catalogue fact (price, stock, seller,
///   category path, spec value, market, ownership, <c>compat:</c>). None is a use-context tag, so
///   nothing in the gold derives from the field the retrieval index embeds.</description></item>
///   <item><description>Every deadline is codified as "in stock now", because that is the only
///   thing the catalogue can say about arrival — and each case prints that beside the
///   constraint rather than pretending to check a delivery date.</description></item>
///   <item><description>The need is written as a real shopper writes it — francs and CHF, a flat,
///   mates, Ciao — not as a template with slots. Twelve different registers, twelve different
///   shapes of need. Prompt variance is a cost here and it is paid knowingly: this eval measures
///   whether an arm can turn a shopper's sentence into the right filter, and a uniform template
///   would measure only one sentence.</description></item>
///   <item><description>The satisfying set is DERIVED at run time by
///   <c>ConstraintSatisfactionGrader.SatisfyingSet</c> and printed per case with its SKUs. Nothing
///   in this file names an answer. A case whose set comes out empty is NOT APPLICABLE and is
///   never scored as a fail — it is a fact about the corpus, reported.</description></item>
/// </list>
/// <para>
/// ⚠ The persona whose Eval 02 turn is silent by instruction — Jonas, whose interest map has one
/// department after gift exclusion, so the shipped prompt's "fewer than two signals" rule fires
/// — is here given a STATED need. The rule's own wording ("and the customer has not described a
/// need in this conversation") does not apply, so on this case k = 0 is a fail and not a harness
/// artefact.
/// </para>
/// </remarks>
public static class StatedNeedCases
{
    /// <summary>The twelve cases, in <see cref="CoveragePersonas.All"/> order.</summary>
    public static IReadOnlyList<StatedNeedCase> All { get; } =
    [
        new("SN-01", Personas.NadiaUserId, "Nadia Brunner",
            "I've got the hut-to-hut trips coming up in three weeks and I'm still shooting everything on " +
            "the kit lens. I want a proper lens for my Alpha 7 IV — up to about CHF 1400, it has to be in " +
            "stock now so it arrives before I leave, and please only what Galaxus sells itself, I've been " +
            "burned by a marketplace return before.",
            [
                new ConstraintSlot("a lens that fits the body she owns",
                [
                    new CategoryUnderAny("Photography > Lenses"),
                    new CompatibleWithOwned("GLX-1001"),
                    new MaxPriceChf(1400m),
                    new InStockNow("arrives before I leave in three weeks"),
                    new NotMarketplace(),
                ]),
            ],
            "Compatibility with an OWNED body, a budget, a deadline and a seller exclusion. Her latent " +
            "tokens are hut-to-hut / first-light / off-grid-power; not one of them is on a lens."),

        new("SN-02", Personas.MarcoUserId, "Marco Iten",
            "Ciao — the Barista Express is dialled in now and I want to tidy up the workflow around it. " +
            "Something for the 58 mm group in the espresso section, under 80 francs, no tablets or " +
            "descaler (I have those), and nothing I already own.",
            [
                new ConstraintSlot("an espresso accessory that fits his 58 mm machine",
                [
                    new CategoryUnderAny("Home Espresso"),
                    new CompatibleWithOwned("GLX-3001"),
                    new MaxPriceChf(80m),
                    new NotConsumable(),
                    new NotAlreadyOwned(),
                ]),
            ],
            "The gift trap runs the other way here: his two most recent lines are a console and a game, " +
            "and the stated need is espresso. Compat is 58 mm; a 54 mm portafilter fails in code."),

        new("SN-03", Personas.SofiaUserId, "Sofia Keller",
            "I keep buying whole beans and grinding them at my neighbour's, which is getting embarrassing. " +
            "I want my own electric grinder — no hand grinders, I'm not cranking every morning — up to 450 " +
            "francs, in stock, and it has to ship to Germany, that's where I live now.",
            [
                new ConstraintSlot("an electric burr grinder she can receive in Germany",
                [
                    new LeafIn("Electric burr grinders"),
                    new MaxPriceChf(450m),
                    new AvailableInMarket("DE"),
                    new InStockNow("I want it this week"),
                ]),
            ],
            "The capability gap stated out loud, plus the one persona whose MARKET is not CH — a " +
            "marketplace grinder listed for CH/DE/AT only would still pass; a CH-only one would not."),

        new("SN-04", Personas.AndreaUserId, "Andrea Riva",
            "Winter commuting again, 20 km each way, and I arrive soaked every time it rains. What would " +
            "actually help? Budget CHF 70 per item. I've already got lights and good tyres, so not those, " +
            "and I don't need tools. Only things Galaxus ships itself, please.",
            [
                new ConstraintSlot("wet-commute kit outside what he owns",
                [
                    new CategoryUnderAny("Cycling"),
                    new MaxPriceChf(70m),
                    new ExcludeCategorySegment("Lighting", "Tyres", "Tools"),
                    new NotMarketplace(),
                    new NotAlreadyOwned(),
                ]),
            ],
            "Three EXCLUSIONS in one breath. The naive move — another light, because his history is " +
            "lights and tyres — is exactly what the exclusion forbids."),

        new("SN-05", Personas.TheoUserId, "Théo Salamin",
            "Late-night listening at the desk without waking the flat: I'd like a pair of over-ear " +
            "headphones to run off my FiiO — wired only, no Bluetooth, I don't want another thing to " +
            "charge — up to CHF 400, and in stock.",
            [
                new ConstraintSlot("wired over-ear headphones",
                [
                    new CategoryUnderAny("Home Audio > Headphones"),
                    new ExcludeCategorySegment("In-ear monitors"),
                    new SpecExcludes("Connection", "Bluetooth"),
                    new MaxPriceChf(400m),
                    new InStockNow("in stock"),
                ]),
            ],
            "A spec-level exclusion. The bestseller in the department is a Bluetooth headphone, and " +
            "the one wired over-ear in the catalogue is a 2024 listing nobody has reviewed much."),

        new("SN-06", Personas.JonasUserId, "Jonas Vogt",
            "Mates are round on Saturday for the Switch 2. I need games we can play four-up on one " +
            "console, under 80 francs each, physical game card so I can lend it on, and actually in " +
            "stock — this weekend, not next month.",
            [
                new ConstraintSlot("four-player Switch 2 games on a physical card",
                [
                    new CategoryUnderAny("Gaming > Games"),
                    new CompatibleWithOwned("GLX-4001"),
                    new MaxPriceChf(80m),
                    new SpecContains("Players", "4"),
                    new SpecContains("Media", "Game card"),
                    new InStockNow("this weekend, not next month"),
                ]),
            ],
            "The persona Eval 02 records as k = 0 by instruction. A STATED need voids the abstention " +
            "rule's own precondition, so silence here is a fail. Two spec constraints (players, media)."),

        new("SN-07", Personas.LeaUserId, "Lea Moser",
            "Carry-on only city trips and I edit on the go. I need a way to get photos off my SD cards " +
            "onto the laptop over USB-C — under CHF 100, under 100 grams, in stock, and please not a " +
            "marketplace seller.",
            [
                new ConstraintSlot("a card reader that takes her cards",
                [
                    new CategoryUnderAny("Photography > Memory"),
                    new CompatibleWithOwned("GLX-1008"),
                    new MaxPriceChf(100m),
                    new MaxWeightGrams(100),
                    new InStockNow("in stock"),
                    new NotMarketplace(),
                ]),
            ],
            "A weight ceiling parsed from a spec, compat with an owned CARD rather than a body, and " +
            "the gift line (a controller for a nephew) has nothing to do with any of it."),

        new("SN-08", Personas.RenzoUserId, "Renzo Bianchi",
            "I'm moving from day hikes to long mountain running days. I need a running vest or pack " +
            "that takes soft flasks — under 300 g, under CHF 200, in stock, and sold by Galaxus itself, " +
            "not a third-party seller.",
            [
                new ConstraintSlot("a light running vest with flask carry",
                [
                    new CategoryUnderAny("Outdoor & Hiking > Backpacks"),
                    new SpecContains("Flask compatibility", "flask"),
                    new MaxWeightGrams(300),
                    new MaxPriceChf(200m),
                    new InStockNow("in stock"),
                    new NotMarketplace(),
                ]),
            ],
            "Weight AND price AND seller: the marketplace chest pack is lighter and the same price, " +
            "and fails on the seller clause alone."),

        new("SN-09", Personas.PierreUserId, "Pierre Bonvin",
            "Small kitchen, 54 mm machine, and I've started doing flat whites for two. What do I need " +
            "for the milk side? Under 50 francs per item, nothing that's a consumable, nothing I " +
            "already have, in stock — and it must actually work with a 54 mm setup, no 58 mm stuff.",
            [
                new ConstraintSlot("milk-side kit that does not assume a 58 mm machine",
                [
                    new CategoryUnderAny("Home Espresso", "Kitchen & Small Appliances > Coffee & tea"),
                    new CompatibleWithOwned("GLX-3006"),
                    new MaxPriceChf(50m),
                    new NotConsumable(),
                    new NotAlreadyOwned(),
                    new InStockNow("in stock"),
                ]),
            ],
            "The 54 mm / 58 mm distinction the design's compatibility gate exists for, stated as a " +
            "need. Every 58 mm accessory under CHF 50 is a wrong answer in code."),

        new("SN-10", Personas.NoemiUserId, "Noemi Kunz",
            "I've been borrowing a body for two years and I'm done with that. I want a full-frame body " +
            "for my FE 16-35 — up to CHF 2300, weather-sealed, in stock, and from Galaxus directly " +
            "rather than a marketplace seller.",
            [
                new ConstraintSlot("a sealed full-frame body for the lens she owns",
                [
                    new CategoryUnderAny("Photography > Cameras"),
                    new CompatibleWithOwned("GLX-1002"),
                    new MaxPriceChf(2300m),
                    new SpecContains("Weather sealing", "resistant"),
                    new InStockNow("in stock"),
                    new NotMarketplace(),
                ]),
            ],
            "The reachable-answer persona: no body on file, a lens that fixes the mount. Compat runs " +
            "from an owned LENS to a body."),

        new("SN-11", Personas.MirjamUserId, "Mirjam Bosshard",
            "I want proper stereo out of the streamer in the living room. Two things: active speakers I " +
            "can feed from it, under CHF 1100, and stands for them so they sit at ear height, under " +
            "CHF 300. Both in stock and both sold by Galaxus itself.",
            [
                new ConstraintSlot("active speakers",
                [
                    new LeafIn("Active bookshelf"),
                    new MaxPriceChf(1100m),
                    new InStockNow("in stock"),
                    new NotMarketplace(),
                    new NotAlreadyOwned(),
                ]),
                new ConstraintSlot("stands for them",
                [
                    new LeafIn("Speaker stands"),
                    new MaxPriceChf(300m),
                    new InStockNow("in stock"),
                    new NotMarketplace(),
                ]),
            ],
            "Cross-category ASSEMBLY: two slots, two leaves, two budgets. Slot coverage is reported " +
            "beside precision so an answer that nails the speakers and forgets the stands is visible."),

        new("SN-12", Personas.DarioUserId, "Dario Fischer",
            "Ten days self-supported bikepacking in Norway in July, leaving in twelve days. I need to " +
            "keep the phone and the GPS charged off-grid — a bigger bank or a solar panel — under " +
            "CHF 180, under 700 grams, USB-C, in stock now, and not the small bank I already have.",
            [
                new ConstraintSlot("off-grid charging that beats the bank he owns",
                [
                    new LeafIn("High-output power banks", "Ultralight power banks", "Solar chargers"),
                    new CompatibleWithOwned("GLX-8005"),
                    new MaxPriceChf(180m),
                    new MaxWeightGrams(700),
                    new InStockNow("leaving in twelve days"),
                    new NotAlreadyOwned(),
                ]),
            ],
            "Two product classes in one slot (bank OR panel), a weight ceiling, and an owned-anchor " +
            "compat clause that here runs on the USB-C PD value. Marketplace listings are allowed."),
    ];

    /// <summary>The case for a persona, or null when the persona has none.</summary>
    /// <param name="personaId">A customer id.</param>
    public static StatedNeedCase? ForPersona(string? personaId) =>
        personaId is null
            ? null
            : All.FirstOrDefault(c => string.Equals(c.PersonaId, personaId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Structural checks on the fixture. Throws with the case named, so a bad fixture fails the eval
    /// loudly instead of scoring a case that cannot be scored.
    /// </summary>
    /// <exception cref="InvalidOperationException">A structural rule is broken.</exception>
    public static void Validate()
    {
        var catalogue = Catalogue.Default;
        var scored = CoveragePersonas.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (All.Count != scored.Count)
            throw Broken($"{All.Count} cases for {scored.Count} scored personas; the fixture is one case per persona.");

        var seenPersonas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenUtterances = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in All)
        {
            if (!seenIds.Add(c.Id)) throw Broken($"duplicate case id '{c.Id}'.");
            if (!scored.Contains(c.PersonaId)) throw Broken($"{c.Id} names '{c.PersonaId}', which is not a scored Eval 02 persona.");
            if (!seenPersonas.Add(c.PersonaId)) throw Broken($"{c.Id} is a second case for '{c.PersonaId}'.");
            if (string.IsNullOrWhiteSpace(c.Utterance)) throw Broken($"{c.Id} has an empty utterance.");
            if (!seenUtterances.Add(c.Utterance)) throw Broken($"{c.Id} repeats another case's utterance verbatim.");
            if (c.Utterance.Contains("GLX-", StringComparison.OrdinalIgnoreCase))
                throw Broken($"{c.Id}'s utterance names a SKU. A shopper does not type SKUs, and a SKU in the prompt hands the arm its answer.");
            if (c.Utterance.Contains("USR-", StringComparison.OrdinalIgnoreCase))
                throw Broken($"{c.Id}'s utterance names a customer id; the frame carries it, the utterance must not.");
            if (c.Slots.Count == 0) throw Broken($"{c.Id} has no slot.");
            if (c.DistinctConstraintCount < 3)
                throw Broken($"{c.Id} carries {c.DistinctConstraintCount} distinct constraint(s); the fixture requires at least three.");

            var profile = UserProfiles.Require(c.PersonaId);
            foreach (var slot in c.Slots)
            {
                if (slot.Constraints.Count == 0) throw Broken($"{c.Id} slot '{slot.Label}' has no constraint.");
                foreach (var constraint in slot.Constraints)
                {
                    if (constraint is CompatibleWithOwned compat)
                    {
                        if (!catalogue.TryGet(compat.OwnedSku, out _))
                            throw Broken($"{c.Id} anchors compatibility on unknown SKU '{compat.OwnedSku}'.");
                        if (!profile.Owns(compat.OwnedSku))
                            throw Broken($"{c.Id} anchors compatibility on '{compat.OwnedSku}', which {c.PersonaId} does not own.");
                    }
                }
            }
        }

        static InvalidOperationException Broken(string message) => new($"Stated-need fixture: {message}");
    }
}
