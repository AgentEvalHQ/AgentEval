// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;
using Galaxus.RecommendationAgent.Tools;

namespace Galaxus.RecommendationAgent.Demos;

/// <summary>
/// The scripted controls for Demo 1's guardrails — nine rows, each one an assertion that CAN
/// FAIL, run at the end of every Demo 1 turn and printed as a table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> There is no test project for this sample, and every fix in
/// design §8.1 carries a named test. A guardrail with no control is a claim, not a mechanism:
/// nothing on screen changes when it stops working, so nobody notices. Each row below therefore
/// scripts an input the guardrail must reject AND, where the failure mode is over-rejection, the
/// twin input it must accept. A row that could only ever be green would prove nothing and is not
/// worth the line it prints on.
/// </para>
/// <para>
/// <b>Every row was RED before the §8.1 fix it guards.</b> That is the property that makes them
/// controls rather than decoration, and it is stated per row in <c>WasRedBefore</c>. A control
/// added after the fact that has never been observed to fail is a control nobody has calibrated.
/// </para>
/// <para>
/// <b>The bar never comes from the artifact.</b> Candidate sets, purchase ids and compatibility
/// values are supplied by this file or read from the seed; nothing under test is allowed to
/// supply the input its own pass depends on.
/// </para>
/// <para>
/// A failing row prints in red and sets <see cref="Environment.ExitCode"/> to 1, so a broken
/// guardrail fails the run rather than quietly producing a nice-looking demo.
/// </para>
/// </remarks>
public static class GuardrailControls
{
    /// <summary>The exit code a failing control leaves behind.</summary>
    public const int FailureExitCode = 1;

    /// <summary>
    /// Re-entrancy guard. Control C-1 runs a whole Demo 1 turn, and that turn ends by calling
    /// this suite again; without the guard the nesting would not terminate.
    /// </summary>
    private static bool _running;

    /// <summary>One control row: what it asserts, and what actually happened.</summary>
    /// <param name="Id">Stable row id, printed.</param>
    /// <param name="Row">The design §8.1 row this control is the test for.</param>
    /// <param name="What">One line naming the assertion, in the language of the failure it catches.</param>
    /// <param name="Passed">Whether the assertion held on this run.</param>
    /// <param name="Observed">What was actually seen — printed on both outcomes, so a green row is checkable too.</param>
    /// <param name="WasRedBefore">What made this row fail before the §8.1 fix landed.</param>
    public sealed record Control(
        string Id,
        string Row,
        string What,
        bool Passed,
        string Observed,
        string WasRedBefore);

    /// <summary>Runs every control and prints the table. Never throws; a thrown control is a failed control.</summary>
    public static async Task RunAsync()
    {
        if (_running) return;
        _running = true;

        // The demo's own retriever binding is restored afterwards: C-1 deliberately unbinds, and a
        // control must not leave the process in a state the next run would misread.
        var boundRetriever = GalaxusTools.Retriever;
        var boundMarket = GalaxusTools.Market;

        try
        {
            var results = await CollectAsync().ConfigureAwait(false);
            Print(results);

            if (results.Any(r => !r.Passed)) Environment.ExitCode = FailureExitCode;
        }
        finally
        {
            if (boundRetriever is not null) GalaxusTools.Bind(boundRetriever, boundMarket);
            _running = false;
        }
    }

    /// <summary>Runs every control and returns the rows, without printing.</summary>
    public static async Task<IReadOnlyList<Control>> CollectAsync()
    {
        var rows = new List<Control>();

        foreach (var control in Definitions())
        {
            try
            {
                rows.Add(await control().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                rows.Add(new Control("??", "—", "control threw", false,
                    $"{ex.GetType().Name}: {ex.Message}", "n/a"));
            }
        }

        return rows;
    }

    private static IEnumerable<Func<Task<Control>>> Definitions() =>
    [
        GateRunsBeforeTheRetrieverIsBoundAsync,
        GateDoesNotFireOnPersonasWithSignal,
        ConsoleCannotClaimAPreSpendGateItDidNotRun,
        OwnPurchaseCitedForTheWrongInterestIsDropped,
        CandidateSetIsWidenedToEveryRetrievalRouteAsync,
        RealSkuNeverRetrievedIsDropped,
        IncompatiblePortafilterIsDropped,
        ReplenishmentIsNamedBeforeOwnership,
        ToolWarningsDeriveFromScreenAsync,
        SemanticSearchCarriesTheBoundMarketAsync,
        LedgerRendersTheThreeCounters,
        ToolSchemaExposesTheFifthArgument
    ];

    // ══ B-1 — the abstention gate runs before any spend ═══════════════════════════

    /// <summary>
    /// The retriever binding is the WITNESS. Demo 1 binds it between the gate and the first
    /// search, so a run that abstains must leave it unbound.
    /// </summary>
    /// <remarks>
    /// A flag the demo sets about its own ordering would be the artifact supplying its own bar.
    /// <c>GalaxusTools.IsBound</c> is a side effect on a different type, produced by a line that
    /// sits physically between the gate and every search — nothing the gate can fake.
    /// </remarks>
    private static async Task<Control> GateRunsBeforeTheRetrieverIsBoundAsync()
    {
        GalaxusTools.Unbind();

        await Quiet(() => Demo01_RecommendationAgent.RunAsync(
            Personas.LucaUserId, personalizationDisabled: false, offline: true)).ConfigureAwait(false);

        var bound = GalaxusTools.IsBound;

        return new Control("C-1", "B-1",
            "a thin-signal turn (Luca) never binds a retriever — the gate short-circuits before any search",
            !bound,
            bound
                ? "the retriever WAS bound, so the turn reached the retrieval seam despite abstaining"
                : "retriever unbound after the turn: no search ran, and no model was constructed after it",
            "RED: Bind() ran unconditionally, before the gate, on every persona");
    }

    /// <summary>
    /// The twin. A gate that abstains on everybody would pass C-1 and be worthless, so the three
    /// personas with signal must NOT abstain.
    /// </summary>
    private static Task<Control> GateDoesNotFireOnPersonasWithSignal()
    {
        string[] personas = [Personas.NadiaUserId, Personas.MarcoUserId, Personas.SofiaUserId];

        var abstaining = personas
            .Where(id => GuardrailPipeline.ShouldAbstain(ContextFor(id), out _))
            .ToArray();

        return Task.FromResult(new Control("C-2", "B-1",
            "the gate does NOT fire on Nadia, Marco or Sofia — it is a gate, not a refuser",
            abstaining.Length == 0,
            abstaining.Length == 0
                ? "0 of 3 abstained; Luca (0 independent signals) is the only persona the gate holds back"
                : $"{abstaining.Length} of 3 abstained: {string.Join(", ", abstaining)}",
            "GREEN before and after — the discrimination twin for C-1, which a blanket refuser would pass"));
    }

    /// <summary>The console half of B-1: the panel may not claim a pre-spend gate it did not run.</summary>
    private static Task<Control> ConsoleCannotClaimAPreSpendGateItDidNotRun()
    {
        const string Claim = "ran BEFORE any model spend";
        var set = RecommendationSet.Abstain("not enough signal", ["q1?", "q2?"]);

        var afterSpend  = Capture(() => RecommendationPrinter.PrintAbstention(set, gateRanBeforeSpend: false));
        var beforeSpend = Capture(() => RecommendationPrinter.PrintAbstention(set, gateRanBeforeSpend: true));

        bool silentWhenFalse = !afterSpend.Contains(Claim, StringComparison.Ordinal);
        bool statedWhenTrue  = beforeSpend.Contains(Claim, StringComparison.Ordinal);

        return Task.FromResult(new Control("C-3", "B-1",
            $"the abstention panel prints \"{Claim}\" only when the caller actually ran the gate first",
            silentWhenFalse && statedWhenTrue,
            $"gateRanBeforeSpend=false → claim printed: {!silentWhenFalse} · =true → claim printed: {statedWhenTrue}",
            "RED: the sentence was unconditional, and the only caller ran the gate AFTER the model"));
    }

    // ══ B-5 — the user side of the evidence ═══════════════════════════════════════

    /// <summary>
    /// Cites one of the customer's OWN purchase ids for an interest it does not evidence, through
    /// the real tool → assemble → pipeline path, and requires the drop.
    /// </summary>
    /// <remarks>
    /// ⚠ The §8.1 B-5 row words this as "Nadia's coffee purchase for a photography SKU". Nadia has
    /// no coffee purchase — her five lines are a camera, a trekking pack, a power bank, a headlamp
    /// and a base layer — and a purchase belonging to a DIFFERENT customer already failed on
    /// <c>foreign_purchase_id</c> before this fix, so it cannot be the case the row means by "today
    /// it is presented". The discriminating case, and the one built here, is one of Nadia's own ids
    /// cited for an interest the code-derived map does not rest on that id.
    /// </remarks>
    private static Task<Control> OwnPurchaseCitedForTheWrongInterestIsDropped()
    {
        var context = ContextFor(Personas.NadiaUserId);

        // The (signal, id) pair is DERIVED from the seed at run time rather than hard-coded, so a
        // corpus edit can make this control inconclusive — which it reports — but never silently
        // turn it into a tautology. Nadia's strongest signal is a conjunction resting on all five
        // of her lines, so the first signal is deliberately not assumed to work.
        var pair = context.InterestMap.Signals
            .Select(s => (Signal: s, WrongId: context.UserPurchaseIds
                .Where(id => !s.EvidencePurchaseIds.Contains(id, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault()))
            .FirstOrDefault(p => p.WrongId is not null);

        if (pair.Signal is null || pair.WrongId is null)
        {
            return Task.FromResult(new Control("C-4", "B-5",
                "a purchase of the customer's own, cited for an interest it does not evidence, is dropped",
                false,
                "INCONCLUSIVE: every signal in this customer's map rests on every one of their purchase ids, so "
              + "the control has no negative case. Reported as a FAILURE — an unexercisable control is not a pass",
                "n/a"));
        }

        var product = PresentableFor(context);
        var rightIds = string.Join(",", pair.Signal.EvidencePurchaseIds);

        var wrong = Screen(context, product, $"{pair.Signal.Label} | {pair.WrongId}");
        var right = Screen(context, product, $"{pair.Signal.Label} | {rightIds}");

        // The twin asserts the arm STAYS SILENT, not that the whole pipeline keeps the item: a
        // correct citation can still lose the item to the confidence band, and folding that into
        // this row would make it fail for a reason it is not about.
        bool droppedWrong = DroppedFor(wrong, product.Id, GuardrailReasons.PurchaseDoesNotEvidenceSignal);
        bool silentOnRight = !DroppedFor(right, product.Id, GuardrailReasons.PurchaseDoesNotEvidenceSignal);

        return Task.FromResult(new Control("C-4", "B-5",
            "citing an own-but-unrelated purchase drops the item; citing the signal's own ids does not",
            droppedWrong && silentOnRight,
            $"{product.Id} ← \"{Clip(pair.Signal.Label, 30)}\" · {pair.WrongId} (unrelated) dropped: {droppedWrong} · "
          + $"{rightIds} (the signal's own) dropped: {!silentOnRight}",
            "RED: the tool had no user-side argument, so Assemble wrote the signal's own ids back and the "
          + "comparison was x ⊆ x"));
    }

    // ══ B-6a — candidate-set containment ══════════════════════════════════════════

    /// <summary>
    /// The widening, checked first: <c>BrowseCategory</c> and <c>GetProductDetails</c> must land in
    /// the candidate set, or enforcing containment would drop items for the route they arrived by.
    /// </summary>
    private static async Task<Control> CandidateSetIsWidenedToEveryRetrievalRouteAsync()
    {
        var catalogue = Catalogue.Default;
        var browsed = catalogue.All.First(p => p.CategoryPath.Count > 0);
        var detailed = catalogue.All.Last(p => !string.Equals(p.RootCategory, browsed.RootCategory, StringComparison.Ordinal));

        IReadOnlySet<string>? candidates;
        using (GalaxusTools.BeginRunCapture())
        {
            await Quiet(async () =>
            {
                await GalaxusTools.BrowseCategory(browsed.RootCategory).ConfigureAwait(false);
                await GalaxusTools.GetProductDetails(detailed.Id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            candidates = GalaxusTools.CandidateSetInCurrentRun;
        }

        bool hasBrowse = candidates?.Contains(browsed.Id) == true;
        bool hasDetails = candidates?.Contains(detailed.Id) == true;

        return new Control("C-5", "B-6a",
            "BrowseCategory and GetProductDetails results enter the candidate set, not only the semantic tools",
            hasBrowse && hasDetails,
            $"candidate set {(candidates is null ? "NULL" : candidates.Count.ToString())} id(s) · "
          + $"browse hit {browsed.Id} present: {hasBrowse} · details hit {detailed.Id} present: {hasDetails}",
            "RED: only SearchProductsByMeaning, FindSimilarProducts and FindComplements recorded anything");
    }

    /// <summary>Presents a real SKU the turn never retrieved, and requires the drop.</summary>
    private static Task<Control> RealSkuNeverRetrievedIsDropped()
    {
        var baseline = ContextFor(Personas.NadiaUserId);

        // Both SKUs must survive the grounding stage, or the containment stage never runs on them
        // and the row would be testing the wrong arm. PresentableFor picks accordingly.
        var retrieved = PresentableFor(baseline);
        var never = PresentableFor(baseline, exclude: retrieved.Id);

        // ⚠ The candidate set is supplied BY THIS FILE. The artifact under test does not get to
        // say what it was allowed to choose from.
        var context = baseline with
        {
            CandidateProductIds = new HashSet<string>(StringComparer.Ordinal) { retrieved.Id }
        };

        var outside = Screen(context, never, null);
        var inside = Screen(context, retrieved, null);

        bool droppedOutside = DroppedFor(outside, never.Id, GuardrailReasons.OutsideCandidateSet);
        bool silentOnInside = !DroppedFor(inside, retrieved.Id, GuardrailReasons.OutsideCandidateSet);

        return Task.FromResult(new Control("C-6", "B-6a",
            $"{never.Id} (outside the candidate set) is dropped; {retrieved.Id} (inside it) is not",
            droppedOutside && silentOnInside,
            $"candidate set = {{{retrieved.Id}}} · outside dropped: {droppedOutside} · inside dropped: {!silentOnInside}",
            "RED: Demo 1 had no containment stage at all — existence was the only test"));
    }

    // ══ B-7 — compatibility against the customer's own hardware ═══════════════════

    /// <summary>
    /// A 54 mm portafilter for Marco's 58 mm group must drop; a 58 mm accessory must not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ §8.1 B-7 names <c>GLX-3004</c> as "(54 mm)". It is not: GLX-3004 is the Normcore V4 WDT
    /// tool and it declares <c>compat:58mm-portafilter</c>, so on Marco it is COMPATIBLE and must
    /// survive. The 54 mm item in the seed is <c>GLX-3006</c>, the Bezzera bottomless portafilter.
    /// This control therefore uses GLX-3006 as the drop case and GLX-3004 as the twin the rule must
    /// leave alone — which also catches an over-eager rule, the failure Demo 2 measured.
    /// </para>
    /// <para>
    /// ⚠ <b>The durable arm pre-empts this one on the shipped corpus, and the control isolates it
    /// rather than hiding that.</b> GLX-3006 sits in the leaf "Portafilters", and Marco bought a
    /// 58 mm portafilter in 2025 — well inside the 1825-day horizon — so
    /// <see cref="CatalogueGroundingFilter"/> removes it as <c>durable_still_in_horizon</c> before
    /// the compatibility stage is reached. GLX-3006 is the seed's ONLY value that conflicts with
    /// any family Marco owns, so on the shipped corpus this arm contributes no drop to any persona
    /// run. The control therefore switches the durable suppression off — the flag exists for
    /// exactly that, the compatibility values still come from Marco's real purchases, and turning
    /// off an unrelated arm isolates this one rather than flattering it.
    /// </para>
    /// </remarks>
    private static Task<Control> IncompatiblePortafilterIsDropped()
    {
        const string Incompatible = "GLX-3006";   // 54 mm — the seed's actual 54 mm portafilter
        const string Compatible = "GLX-3004";     // 58 mm — the SKU §8.1 B-7 mislabels as 54 mm

        var catalogue = Catalogue.Default;
        var context = ContextFor(Personas.MarcoUserId) with { SuppressDurableUpgrades = false };

        if (!catalogue.TryGet(Incompatible, out var bad) || bad is null ||
            !catalogue.TryGet(Compatible, out var good) || good is null)
        {
            return Task.FromResult(new Control("C-7", "B-7",
                "a 54 mm portafilter is dropped for a 58 mm owner",
                false, $"INCONCLUSIVE: {Incompatible} or {Compatible} is not in the catalogue", "n/a"));
        }

        var dropped = Screen(context, bad, null);
        var kept = Screen(context, good, null);

        bool droppedBad = DroppedFor(dropped, Incompatible, GuardrailReasons.IncompatibleWithOwned);
        bool silentOnGood = !DroppedFor(kept, Compatible, GuardrailReasons.IncompatibleWithOwned);

        var families = string.Join("; ", context.OwnedCompatValuesByFamily
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + string.Join("/", kv.Value.Order(StringComparer.Ordinal))));

        return Task.FromResult(new Control("C-7", "B-7",
            $"{Incompatible} (54 mm) drops for Marco's 58 mm group; {Compatible} (58 mm) is untouched",
            droppedBad && silentOnGood,
            $"owned [{families}] · 54 mm dropped: {droppedBad} · 58 mm dropped: {!silentOnGood} "
          + "(durable suppression off, to isolate this arm from the one that pre-empts it)",
            "RED: compatibility was enforced only inside FindComplements — one of five retrieval routes"));
    }

    // ══ B-16 — the replenishment lane names itself ════════════════════════════════

    /// <summary>Sofia's cartridges must leave the ledger as a replenishment item, not as ownership.</summary>
    private static Task<Control> ReplenishmentIsNamedBeforeOwnership()
    {
        var context = ContextFor(Personas.SofiaUserId);
        var catalogue = Catalogue.Default;

        var sku = context.ReplenishmentProductIds.Order(StringComparer.Ordinal).FirstOrDefault();
        if (sku is null || !catalogue.TryGet(sku, out var product) || product is null)
        {
            return Task.FromResult(new Control("C-8", "B-16",
                "a consumable on a replenishment cadence drops as replenishment_not_discovery",
                false, "INCONCLUSIVE: no purchase of Sofia's is routed to replenishment", "n/a"));
        }

        var outcome = Screen(context, product, null);

        bool named = DroppedFor(outcome, sku, GuardrailReasons.ReplenishmentNotDiscovery);
        bool notOwnership = !DroppedFor(outcome, sku, GuardrailReasons.AlreadyOwned);

        return Task.FromResult(new Control("C-8", "B-16",
            $"{sku} drops as replenishment_not_discovery, and NOT as already_owned",
            named && notOwnership,
            $"replenishment_not_discovery: {named} · already_owned: {!notOwnership} · lane holds "
          + $"{context.ReplenishmentProductIds.Count} sku(s)",
            "RED: the reason did not exist; the cartridges dropped as already_owned and the lane was invisible"));
    }

    // ══ B-13 — the tool's warnings come from Screen ═══════════════════════════════

    /// <summary>
    /// Presents a SKU the customer already owns and requires the tool to say so. Ownership is a
    /// rule <see cref="GuardrailPipeline.Screen"/> has and the tool's hand-rolled list never did,
    /// so a warning naming it can only have come through the advisory screen.
    /// </summary>
    private static async Task<Control> ToolWarningsDeriveFromScreenAsync()
    {
        var context = ContextFor(Personas.MarcoUserId);
        var catalogue = Catalogue.Default;

        var owned = context.OwnedProductIds
            .Where(id => !context.ReplenishmentProductIds.Contains(id))
            .Order(StringComparer.Ordinal)
            .First();

        catalogue.TryGet(owned, out var product);
        var citation = EvidenceRef.AttributePrefix + catalogue.AttributesOf(product!).Order(StringComparer.Ordinal).First();

        string json = string.Empty;
        using (GalaxusTools.BeginRunCapture(context with { CandidateProductIds = null }))
        {
            await Quiet(async () =>
            {
                json = await GalaxusTools.PresentRecommendation(
                    owned, "A perfectly reasonable sentence.", citation, false, null).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        bool carriesScreenRule = json.Contains(GuardrailReasons.AlreadyOwned, StringComparison.Ordinal);

        return new Control("C-9", "B-13",
            "the tool's warnings carry a rule only GuardrailPipeline.Screen knows (already_owned)",
            carriesScreenRule,
            carriesScreenRule
                ? $"PresentRecommendation({owned}) warned with reason '{GuardrailReasons.AlreadyOwned}'"
                : $"PresentRecommendation({owned}) returned no ownership warning — Screen is not wired in",
            "RED: Screen had no call site; the tool hand-rolled four warnings and knew nothing about ownership");
    }

    // ══ B-17 — the customer's market reaches the semantic query ═══════════════════

    /// <summary>
    /// A recording retriever is the witness: the query the tool builds must carry the BOUND market.
    /// </summary>
    /// <remarks>
    /// ⚠ The §8.1 B-17 row's stated test — "no <c>market_unavailable</c> drop on any offline
    /// persona run" — is VACUOUS on the shipped corpus: every one of the 99 seeded products is
    /// available in CH and DE, and all four personas are in one of those two, so no persona could
    /// produce that drop before the fix either. The defect is real but invisible at the output, so
    /// the control asserts the wiring instead of a symptom the corpus cannot express.
    /// </remarks>
    private static async Task<Control> SemanticSearchCarriesTheBoundMarketAsync()
    {
        const string Market = "DE";

        var recorder = new QueryRecordingRetriever();
        GalaxusTools.Bind(recorder, Market);

        await Quiet(() => GalaxusTools.SearchProductsByMeaning("a fully specified need sentence")).ConfigureAwait(false);

        var seen = recorder.LastQuery?.Market;
        bool carried = string.Equals(seen, Market, StringComparison.Ordinal);

        return new Control("C-10", "B-17",
            $"SearchProductsByMeaning issues its query in the BOUND market ({Market}), not the CH default",
            carried,
            $"query.Market = \"{seen ?? "(no query issued)"}\", bound market = \"{Market}\"",
            $"RED: RetrievalQuery.For leaves Market at \"{RetrievalQuery.DefaultMarket}\" and nothing overwrote it");
    }

    // ══ B-15 — the three counters reach the panel ═════════════════════════════════

    /// <summary>The ledger panel must print the values the control put into the ledger.</summary>
    private static Task<Control> LedgerRendersTheThreeCounters()
    {
        var ledger = new GuardrailLedger();
        ledger.GiftExcluded = 2;
        ledger.RecordPriceStock(requested: 6, verified: 5);

        var panel = string.Join(" ⏎ ", ledger.ToPanelLines());

        bool gift = panel.Contains("gift-excluded 2", StringComparison.Ordinal);
        bool price = panel.Contains("price/stock re-verified 5 of 6", StringComparison.Ordinal);

        return Task.FromResult(new Control("C-11", "B-15",
            "GiftExcluded, PriceStockVerified and PriceStockRequested are rendered, with the values set",
            gift && price,
            $"\"gift-excluded 2\": {gift} · \"price/stock re-verified 5 of 6\": {price}",
            "RED: all three were populated and read by nothing — the shape that hides a dead arm"));
    }

    // ══ B-5 — the wire contract the model and the eval lane both read ════════════

    /// <summary>
    /// Builds the shipped read-only tool surface and asserts that the function schema the model is
    /// handed actually carries <c>userEvidence</c> alongside the four frozen argument names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two jobs. It is the only offline exercise of <c>AIFunctionFactory.Create</c> over the new
    /// five-parameter signature, so a schema that fails to build is caught here rather than on a
    /// paid live run. And it pins the WIRE names: the eval lane reads tool-call arguments by name,
    /// so a rename is a contract break even when the code still compiles.
    /// </para>
    /// <para>
    /// All five names now come from <c>PresentRecommendationArguments</c>, so the row checks the
    /// property that actually matters: <b>constant equals parameter</b>. The fifth constant was
    /// owed by the cross-lane contract file and is now in place, closing the one argument whose
    /// name was pinned only by a literal here.
    /// </para>
    /// </remarks>
    private static Task<Control> ToolSchemaExposesTheFifthArgument()
    {
        var tool = Agents.RecommendationAgentFactory.BuildReadOnlyTools()
            .OfType<Microsoft.Extensions.AI.AIFunction>()
            .FirstOrDefault(t => string.Equals(t.Name, nameof(GalaxusTools.PresentRecommendation), StringComparison.Ordinal));

        if (tool is null)
        {
            return Task.FromResult(new Control("C-12", "B-5",
                "the PresentRecommendation schema carries all five argument names",
                false, "INCONCLUSIVE: PresentRecommendation is not on the read-only tool surface", "n/a"));
        }

        var schema = tool.JsonSchema.ToString();
        string[] required = [
            PresentRecommendationArguments.Sku,
            PresentRecommendationArguments.Reason,
            PresentRecommendationArguments.Evidence,
            PresentRecommendationArguments.OutOfStock,
            PresentRecommendationArguments.UserEvidence];

        var missing = required.Where(n => !schema.Contains($"\"{n}\"", StringComparison.Ordinal)).ToArray();

        return Task.FromResult(new Control("C-12", "B-5",
            "the tool schema handed to the model carries userEvidence AND the four frozen names",
            missing.Length == 0,
            missing.Length == 0
                ? $"schema names all five: {string.Join(", ", required)}"
                : $"missing from the schema: {string.Join(", ", missing)}",
            "RED: the signature had four parameters, so no schema could carry a user side"));
    }

    // ══ Rendering ═════════════════════════════════════════════════════════════════

    /// <summary>Prints the control table.</summary>
    /// <param name="rows">The collected rows.</param>
    public static void Print(IReadOnlyList<Control> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var passed = rows.Count(r => r.Passed);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─── Guardrail controls (scripted; each row can fail) ─────────────────────");
        Console.ResetColor();

        foreach (var row in rows)
        {
            Console.ForegroundColor = row.Passed ? ConsoleColor.DarkGray : ConsoleColor.Red;
            Console.WriteLine($"     {(row.Passed ? "✓" : "✗")} {row.Id,-4} {row.Row,-5} {Clip(row.What, 62)}");
            Console.ResetColor();

            Console.ForegroundColor = row.Passed ? ConsoleColor.DarkGray : ConsoleColor.Yellow;
            Console.WriteLine($"            {Clip(row.Observed, 100)}");
            if (!row.Passed) Console.WriteLine($"            before the fix — {Clip(row.WasRedBefore, 100)}");
            Console.ResetColor();
        }

        Console.ForegroundColor = passed == rows.Count ? ConsoleColor.DarkCyan : ConsoleColor.Red;
        Console.WriteLine($"     {passed}/{rows.Count} controls caught what they exist to catch"
                        + (passed == rows.Count ? "" : $"  ⚠ exit {FailureExitCode}"));
        Console.ResetColor();
        Console.WriteLine();
    }

    // ══ Helpers ═══════════════════════════════════════════════════════════════════

    /// <summary>Builds the same context Demo 1 builds, for one seeded persona.</summary>
    private static GuardrailContext ContextFor(string userId)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(userId);

        var classified = PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, Personas.DemoToday);
        var map = InterestMapBuilder.Build(
            profile.User, profile.Purchases, catalogue.BySku,
            statedNeeds: null, asOf: Personas.DemoToday, sensitiveCategoryNames: catalogue.SensitiveCategories);

        return GuardrailContext.Create(
            catalogue.BySku, profile.User, map, classified,
            categories: catalogue.Categories,
            customerUtterance: Personas.CanonicalPromptFor(profile.Id),
            asOf: Personas.DemoToday);
    }

    /// <summary>
    /// Puts one scripted presentation through the REAL path: the frozen tool, then Demo 1's
    /// assembler, then the pipeline. Going through the tool rather than hand-building a
    /// <c>RecommendationDto</c> is what makes these controls test the shipped code.
    /// </summary>
    private static GuardrailOutcome Screen(GuardrailContext context, Product product, string? userEvidence)
    {
        var catalogue = Catalogue.Default;
        var citation = EvidenceRef.AttributePrefix
                     + catalogue.AttributesOf(product).Order(StringComparer.Ordinal).First();

        IReadOnlyList<PresentedRecommendation> presented;
        IReadOnlyList<string?> evidence;

        using (GalaxusTools.BeginRunCapture())
        {
            Quiet(() => GalaxusTools.PresentRecommendation(
                product.Id, "A sentence the price scanner is happy with.", citation, false, userEvidence))
                .GetAwaiter().GetResult();

            presented = GalaxusTools.PresentedInCurrentRun;
            evidence = GalaxusTools.UserEvidenceInCurrentRun;
        }

        // Blocking on the assembler, exactly as the PresentRecommendation call above blocks: these
        // scripted controls are a synchronous harness around an async pipeline. AssembleAsync
        // became async at B-21 because the confidence arithmetic now embeds in whichever space the
        // run resolved, and on the real-vector path that reaches the network.
        var (raw, _, _) = Demo01_RecommendationAgent.AssembleAsync(
            presented, evidence,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            context.InterestMap, catalogue, [])
            .GetAwaiter().GetResult();

        return GuardrailPipeline.Apply(raw, context);
    }

    /// <summary>
    /// A catalogue product this customer can legitimately be shown: not owned, not on their
    /// replenishment cadence, not in a leaf whose durable they already own, in stock, and in their
    /// market.
    /// </summary>
    /// <remarks>
    /// The rows that test a LATE stage need an input that survives the EARLY ones, or they silently
    /// measure the wrong arm — the first cut of C-6 picked Nadia's own camera and its "kept" twin
    /// failed on <c>already_owned</c>, which had nothing to do with containment.
    /// </remarks>
    /// <param name="context">The customer's bar.</param>
    /// <param name="exclude">A product id to skip, so two calls give two different products.</param>
    private static Product PresentableFor(GuardrailContext context, string? exclude = null) =>
        Catalogue.Default.All.First(p =>
            p.Id != exclude &&
            p.Attributes.Count > 0 &&
            p.StockUnits > 0 &&
            p.IsAvailableIn(context.User.Market) &&
            !context.OwnedProductIds.Contains(p.Id) &&
            !context.ReplenishmentProductIds.Contains(p.Id) &&
            !context.OwnedDurableLeafCategories.Contains(p.LeafCategory) &&
            !p.CategoryPath.Any(e => context.SensitiveCategoryNames.Contains(e)
                                  || SensitiveInferenceBlocklist.IsBlockedCategoryName(e)));

    private static bool DroppedFor(GuardrailOutcome outcome, string sku, string reason) =>
        outcome.Ledger.Entries.Any(e =>
            e.Action == GuardrailAction.Dropped &&
            string.Equals(e.Subject, sku, StringComparison.Ordinal) &&
            string.Equals(e.Reason, reason, StringComparison.Ordinal));

    /// <summary>Runs <paramref name="action"/> with the console silenced, restoring it afterwards.</summary>
    private static async Task Quiet(Func<Task> action)
    {
        var saved = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { await action().ConfigureAwait(false); }
        finally { Console.SetOut(saved); }
    }

    /// <summary>Runs <paramref name="action"/> and returns everything it wrote to the console.</summary>
    private static string Capture(Action action)
    {
        var saved = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try { action(); }
        finally { Console.SetOut(saved); }
        return buffer.ToString();
    }

    private static string Clip(string? text, int max)
    {
        var flat = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..Math.Max(0, max - 1)] + "…";
    }

    /// <summary>
    /// A retriever that records the query it was handed and returns nothing. It exists so the
    /// market control can assert on the QUERY rather than on hits, which the shipped corpus cannot
    /// distinguish — every seeded product ships to both CH and DE.
    /// </summary>
    private sealed class QueryRecordingRetriever : IProductRetriever
    {
        public RetrievalQuery? LastQuery { get; private set; }

        public string Name => "control:query-recorder";

        public int ProductCount => 0;

        public bool DenseAvailable => false;

        public ValueTask<RetrievalResult> SearchAsync(RetrievalQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return ValueTask.FromResult(RetrievalResult.Empty(new RetrievalDiagnostics
            {
                Dense = false,
                Lexical = false,
                Degraded = true,
                DegradedReason = "control stub: this retriever records the query and returns nothing"
            }));
        }
    }
}
