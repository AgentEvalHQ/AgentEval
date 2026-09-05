// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;

namespace Galaxus.RecommendationAgent.Rendering;

/// <summary>
/// Renders one customer turn: the code-derived interest map, the recommendation cards with
/// two-sided evidence, the VERIFIED price and stock line, the guardrail ledger, and the
/// "AI assists — you verify and decide" footer (design §E.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The renderer is the price authority's mouthpiece, and the model never is.</b> Every figure
/// on a card comes from a <see cref="PriceStockSnapshot"/> produced by
/// <see cref="PriceStockRefresher"/> at render time (§F.4). If a snapshot is missing for a
/// surviving item this prints an explicit red "not verified" line and no number at all. It never
/// falls back to a figure from the model's text, and never to one from a search result: an
/// unverified price printed as if it were verified is the most expensive lie this interface
/// could tell.
/// </para>
/// <para>
/// <b>The guardrail ledger is the punchline.</b> It makes every mechanism in §F visible and
/// countable on screen — the drops, the demotions, the gift exclusions, the tool-call spend. On
/// Marco it prints the two gift exclusions by name, which is the moment the demo stops being a
/// chat window. Inapplicable-arm notes are printed LOUDEST, because an arm that could not run is
/// the single most misleading thing a clean-looking ledger can hide.
/// </para>
/// </remarks>
public static class RecommendationPrinter
{
    /// <summary>Printable width after the two-space base indent. 78 columns total, TravelDemo's box width.</summary>
    private const int Width = 76;

    /// <summary>Base indent for every line this class prints.</summary>
    private const string Indent = "  ";

    /// <summary>Indent for the detail rows under a card.</summary>
    private const string Detail = "     ";

    /// <summary>Passed for <c>toolCallsUsed</c> / <c>toolCallCap</c> to omit the tool-spend line.</summary>
    public const int OmitToolCalls = -1;

    // ── The whole turn, in one call ───────────────────────────────────────────

    /// <summary>
    /// Prints the complete answer in the order §E.3 specifies: customer, interest map,
    /// recommendations (or the abstention), replenishment, ledger, footer.
    /// </summary>
    /// <param name="user">The customer this turn is for.</param>
    /// <param name="map">The code-derived interest map.</param>
    /// <param name="classified">The classified purchases, used to name the gift exclusions.</param>
    /// <param name="outcome">The guardrail pipeline's result: cleaned answer, ledger, verified figures.</param>
    /// <param name="toolCallsUsed">Tool calls spent this turn, or <see cref="OmitToolCalls"/>.</param>
    /// <param name="toolCallCap">The tool-call cap, or <see cref="OmitToolCalls"/>.</param>
    /// <param name="gateRanBeforeSpend">
    /// True only when the caller ran <c>GuardrailPipeline.ShouldAbstain</c> BEFORE constructing the
    /// agent (§8.1 B-1). The abstention panel prints the "no tokens were spent" sentence only under
    /// this flag; see <see cref="PrintAbstention"/>.
    /// </param>
    public static void PrintAnswer(
        User user,
        InterestMap map,
        IReadOnlyList<ClassifiedPurchase> classified,
        GuardrailOutcome outcome,
        int toolCallsUsed = OmitToolCalls,
        int toolCallCap = OmitToolCalls,
        bool gateRanBeforeSpend = false)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        PrintAnswer(user, map, classified, outcome.Cleaned, outcome.VerifiedPrices, outcome.Ledger,
                    toolCallsUsed, toolCallCap, gateRanBeforeSpend);
    }

    /// <summary>
    /// Prints the complete answer from its parts, for callers that assembled the pieces
    /// themselves.
    /// </summary>
    /// <param name="user">The customer this turn is for.</param>
    /// <param name="map">The code-derived interest map.</param>
    /// <param name="classified">The classified purchases, used to name the gift exclusions.</param>
    /// <param name="set">The recommendation set AFTER the guardrail pipeline.</param>
    /// <param name="verified">Price and stock snapshots keyed by product id. Missing entries print as unverified.</param>
    /// <param name="ledger">The guardrail ledger accumulated by the pipeline.</param>
    /// <param name="toolCallsUsed">Tool calls spent this turn, or <see cref="OmitToolCalls"/>.</param>
    /// <param name="toolCallCap">The tool-call cap, or <see cref="OmitToolCalls"/>.</param>
    /// <param name="gateRanBeforeSpend">
    /// True only when the caller ran <c>GuardrailPipeline.ShouldAbstain</c> BEFORE constructing the
    /// agent (§8.1 B-1). Gates the abstention panel's "no tokens were spent" sentence.
    /// </param>
    public static void PrintAnswer(
        User user,
        InterestMap map,
        IReadOnlyList<ClassifiedPurchase> classified,
        RecommendationSet set,
        IReadOnlyDictionary<string, PriceStockSnapshot> verified,
        GuardrailLedger ledger,
        int toolCallsUsed = OmitToolCalls,
        int toolCallCap = OmitToolCalls,
        bool gateRanBeforeSpend = false)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(classified);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(ledger);

        PrintCustomerHeader(user);
        PrintInterestMap(map, classified);

        if (set.Abstained) PrintAbstention(set, gateRanBeforeSpend);
        else PrintRecommendations(set, verified);

        PrintReplenishment(set);
        PrintGuardrailLedger(ledger, toolCallsUsed, toolCallCap);
        PrintFooter();
    }

    // ── Panels ────────────────────────────────────────────────────────────────

    /// <summary>Prints the customer strip: who they are, and whether personalization is on.</summary>
    /// <param name="user">The customer.</param>
    public static void PrintCustomerHeader(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        Rule("Customer");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"{Indent}{user.DisplayName} · {user.Market} · {user.Language} · customer since {user.CustomerSince:yyyy-MM}  ·  personalization: ");
        Console.ForegroundColor = user.PersonalizationEnabled ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(user.PersonalizationEnabled ? "ON" : "OFF");
        Console.ResetColor();

        if (user.PersonalizationOptOut)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{Indent}   Behavioural history is REFUSED by the tool layer, not merely omitted from the prompt.");
            Console.WriteLine($"{Indent}   The agent runs on what the customer says in this conversation (§F.6).");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Prints the interest map — the panel that says out loud that the reasoning about WHO the
    /// customer is was done by code, not by the model.
    /// </summary>
    /// <param name="map">The derived map.</param>
    /// <param name="classified">Classified purchases, so an excluded id can be printed as a product name.</param>
    public static void PrintInterestMap(InterestMap map, IReadOnlyList<ClassifiedPurchase> classified)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(classified);

        Rule("Interest map (derived by code, not by the model)");

        var byPurchaseId = new Dictionary<string, ClassifiedPurchase>(StringComparer.Ordinal);
        foreach (var c in classified) byPurchaseId[c.Purchase.Id] = c;

        if (map.Signals.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{Indent}(no signals — {(map.PersonalizationEnabled ? "the history carries nothing strong enough to act on" : "personalization is off, so there is no history to derive from")})");
            Console.ResetColor();
        }

        foreach (var signal in map.Signals)
        {
            var marker = signal.IsIndependent ? "●" : "○";
            Console.ForegroundColor = signal.IsIndependent ? ConsoleColor.White : ConsoleColor.DarkGray;
            Console.WriteLine($"{Indent}{marker} {Fit(signal.Label, 46)}  {signal.Strength:0.00}  ← {CompressIds(signal.EvidencePurchaseIds)}");
            Console.ResetColor();
        }

        // The guardrail the audience gets to WATCH fire.
        if (map.HasGiftExclusions)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            var names = map.ExcludedBecauseGift
                .Select(id => byPurchaseId.TryGetValue(id, out var c) ? c.Product.Name : id)
                .ToArray();
            Console.WriteLine($"{Indent}⛔ excluded from your interests: {string.Join(", ", names)}");

            var because = map.ExcludedBecauseGift
                .Select(id => byPurchaseId.TryGetValue(id, out var c) ? c.Because : null)
                .FirstOrDefault(b => !string.IsNullOrWhiteSpace(b));
            if (because is not null) Console.WriteLine($"{Indent}   {because}");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{Indent}⛔ excluded as gift: —");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (map.RoutedToReplenishment.Count > 0)
        {
            var names = map.RoutedToReplenishment
                .Select(id => byPurchaseId.TryGetValue(id, out var c) ? c.Product.Name : id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Console.WriteLine($"{Indent}🔁 replenishment lane: {string.Join(", ", names)}");
        }
        else
        {
            Console.WriteLine($"{Indent}🔁 replenishment lane: —");
        }

        Console.WriteLine($"{Indent}   independent signals {map.IndependentSignalCount} of {InterestMap.MinimumSignalsToProceed} needed "
                        + $"(threshold {InterestMap.IndependentSignalThreshold:0.00})");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>Opens the live tool-trace section. The tools print their own three-space-indented lines into it.</summary>
    public static void PrintTraceHeader() => Rule("Live trace");

    /// <summary>Closes the live tool-trace section.</summary>
    public static void PrintTraceFooter()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"{Indent}{new string('─', Width)}");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the degraded-retrieval banner (§D.4). Degraded mode does not substitute a hash
    /// embedder and carry on — it disables the dense leg and says so.
    /// </summary>
    /// <param name="reason">Why dense retrieval is unavailable; null prints the generic reason.</param>
    public static void PrintDegradedRetrievalNotice(string? reason)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"{Indent}⚠️  Degraded retrieval — {reason ?? "no embedding source is available for this query"}.");
        Console.WriteLine($"{Indent}    Running LEXICAL-ONLY. Cross-category matches will be missed, which is exactly the");
        Console.WriteLine($"{Indent}    capability this demo exists to show — so treat any result below as a lower bound.");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the recommendation cards: primary tray, then the secondary tray, numbered
    /// continuously so the customer sees one ranked list.
    /// </summary>
    /// <param name="set">The cleaned recommendation set.</param>
    /// <param name="verified">Price and stock snapshots keyed by product id.</param>
    public static void PrintRecommendations(
        RecommendationSet set,
        IReadOnlyDictionary<string, PriceStockSnapshot> verified)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(verified);

        var index = 0;

        Rule("Recommended");
        if (set.Recommendations.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{Indent}Nothing survived the guardrail pipeline for the primary tray. The ledger below says why —");
            Console.WriteLine($"{Indent}an empty tray is a RESULT, not an abstention, and the two are counted differently.");
            Console.ResetColor();
        }
        foreach (var item in set.Recommendations) PrintCard(++index, item, verified, secondary: false);

        Console.WriteLine();

        if (set.AlsoConsider.Count > 0)
        {
            Rule("You might also consider");
            foreach (var item in set.AlsoConsider) PrintCard(++index, item, verified, secondary: true);
            Console.WriteLine();
        }
    }

    /// <summary>Prints the replenishment lane. Never a discovery, always its own tray with a due date.</summary>
    /// <param name="set">The cleaned recommendation set.</param>
    public static void PrintReplenishment(RecommendationSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Replenishment.Count == 0) return;

        Rule("Due for a repeat buy");
        foreach (var item in set.Replenishment)
        {
            Console.ForegroundColor = item.IsOverdue ? ConsoleColor.Yellow : ConsoleColor.White;
            var due = item.IsOverdue ? $"overdue by {-item.DaysUntilDue} d" : $"due in {item.DaysUntilDue} d";
            Console.WriteLine($"{Indent}🔁 {Fit(NameOf(item.ProductId), 46)}  {due}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{Detail}{item.Because} · last bought {item.DaysSinceLastPurchase} d ago, typical cadence {item.TypicalReplenishDays} d");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the abstention panel (§F.8): no recommendations, a stated reason, and the two
    /// questions asked instead of a guess.
    /// </summary>
    /// <param name="set">The abstaining set.</param>
    /// <param name="gateRanBeforeSpend">
    /// True only when the caller decided to abstain BEFORE constructing the agent.
    /// </param>
    /// <remarks>
    /// ⚠ <b>The claim on the last line is gated on a flag, and it has to be (§8.1 B-1).</b> This
    /// panel printed "The gate is structural and ran BEFORE any model spend" unconditionally, on
    /// every abstention, while the only caller in the codebase ran the gate AFTER the model had
    /// answered. The sentence was false on every live thin-signal run and the customer read it
    /// anyway. The flag is now supplied by the caller that actually did the short-circuiting, so
    /// the interface is structurally unable to make the claim on a turn where it is not true — the
    /// same discipline as the price line, which prints a figure only from a snapshot.
    /// </remarks>
    public static void PrintAbstention(RecommendationSet set, bool gateRanBeforeSpend = false)
    {
        ArgumentNullException.ThrowIfNull(set);

        Rule("No recommendation — not enough signal");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"{Indent}⏸  {Wrap(set.AbstainReason ?? "The abstention gate fired before the first search.")}");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.White;
        foreach (var question in set.ClarifyingQuestions)
            Console.WriteLine($"{Indent}?  {Wrap(question)}");
        Console.ResetColor();

        Console.ForegroundColor = gateRanBeforeSpend ? ConsoleColor.DarkGray : ConsoleColor.Red;
        Console.WriteLine(gateRanBeforeSpend
            ? $"{Indent}   The gate is structural and ran BEFORE any model spend: no search was made and no prompt"
            : $"{Indent}   ⚠ This gate ran AFTER the answer was assembled, so the model HAD already run and the spend");
        Console.WriteLine(gateRanBeforeSpend
            ? $"{Indent}   token was sent on this turn."
            : $"{Indent}   is already gone. Do not read this panel as evidence that the pre-spend gate works.");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"{Indent}   An abstention is not automatically a pass: on a case that HAD a right answer it must be");
        Console.WriteLine($"{Indent}   scored as a miss, or saying nothing becomes a way to score well.");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the guardrail ledger — every §F mechanism, counted and named. The panel body comes
    /// from <see cref="GuardrailLedger.ToPanelLines"/> so the ledger owns its own wording and the
    /// renderer owns only the frame.
    /// </summary>
    /// <param name="ledger">The accumulated ledger.</param>
    /// <param name="toolCallsUsed">Tool calls spent this turn, or <see cref="OmitToolCalls"/>.</param>
    /// <param name="toolCallCap">The tool-call cap, or <see cref="OmitToolCalls"/>.</param>
    public static void PrintGuardrailLedger(
        GuardrailLedger ledger,
        int toolCallsUsed = OmitToolCalls,
        int toolCallCap = OmitToolCalls)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        Rule("Guardrail ledger");

        var lines = ledger.ToPanelLines();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isSummary = i == lines.Count - 1;

            Console.ForegroundColor = isSummary ? ConsoleColor.DarkCyan
                : line.StartsWith('⛔') ? ConsoleColor.Yellow
                : line.StartsWith('↘') ? ConsoleColor.DarkYellow
                : line.StartsWith('⚠') ? ConsoleColor.Red
                : ConsoleColor.DarkGray;

            Console.WriteLine($"{Indent}{Wrap(line)}");
            Console.ResetColor();
        }

        if (ledger.HasInapplicableArm)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{Indent}⚠  At least one guardrail arm could not run on this turn. A clean ledger with an");
            Console.WriteLine($"{Indent}   inapplicable arm is not evidence that the arm works — it is evidence that it was");
            Console.WriteLine($"{Indent}   never tested. Read the ⚠ line above before quoting any number here.");
            Console.ResetColor();
        }

        if (toolCallsUsed >= 0 && toolCallCap > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"{Indent}tool calls {toolCallsUsed} of {toolCallCap}");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    /// <summary>The closing line. AI assists; the customer verifies and decides.</summary>
    public static void PrintFooter()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"{Indent}⚖  These are suggestions. Prices and availability are verified above; the");
        Console.WriteLine($"{Indent}   decision is yours. Nothing has been added to a basket or ordered — this");
        Console.WriteLine($"{Indent}   agent has no tool that can.");
        Console.ResetColor();
        Console.WriteLine();
    }

    // ── Card rendering ────────────────────────────────────────────────────────

    private static void PrintCard(
        int index,
        RecommendationDto item,
        IReadOnlyDictionary<string, PriceStockSnapshot> verified,
        bool secondary)
    {
        var product = Resolve(item.ProductId);
        var name = product?.Name ?? item.ProductId;
        var tail = product is { IsMarketplaceOffer: true, IsColdStart: true } ? "  (marketplace · no reviews yet)"
                 : product is { IsColdStart: true } ? "  (no reviews yet)"
                 : string.Empty;

        Console.ForegroundColor = secondary ? ConsoleColor.Gray : ConsoleColor.White;
        Console.WriteLine($"{Indent}{index,2}  {Fit(name + tail, 50)}  conf {item.Confidence:0.00} {Bar(item.Confidence)}");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Gray;

        // USER side of the evidence — which code-derived signal, and the purchases behind it.
        Console.WriteLine($"{Detail}▸ Because {Wrap(item.WhyThis, Detail.Length + 10)}");
        Console.WriteLine($"{Detail}▸ Your signal · {Fit(item.Evidence.UserSignalLabel, 40)}"
                        + (item.Evidence.UserPurchaseIds.Count > 0 ? $"  ← {CompressIds(item.Evidence.UserPurchaseIds)}" : ""));

        // PRODUCT side — printed as the CATALOGUE holds it, not as the model wrote it.
        var evidenceText = item.Evidence.ReviewId is { Length: > 0 } reviewId
            ? $"customer review {reviewId}"
            : $"{item.Evidence.ProductAttributeKey}: {item.Evidence.ProductAttributeValue}";
        Console.WriteLine($"{Detail}▸ Catalogue · {Wrap(evidenceText, Detail.Length + 13)}   [{item.Evidence.Citation}]");
        Console.ResetColor();

        PrintVerifiedLine(item.ProductId, verified);
        Console.WriteLine();
    }

    /// <summary>
    /// The verified price/stock line. Prints figures ONLY from a snapshot; a missing snapshot
    /// prints a red warning and no number.
    /// </summary>
    private static void PrintVerifiedLine(string productId, IReadOnlyDictionary<string, PriceStockSnapshot> verified)
    {
        if (!verified.TryGetValue(productId, out var snapshot))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{Detail}▸ ⚠ price and stock NOT verified for {productId} — no figure is shown, by design.");
            Console.ResetColor();
            return;
        }

        var stamp = $"(verified {snapshot.AsOfUtc:HH:mm:ss} UTC)";

        if (!snapshot.AvailableInMarket)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{Detail}▸ ✗ {snapshot.ToPriceLine()} · does not ship to this market   {stamp}");
        }
        else if (!snapshot.InStock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{Detail}▸ ✗ {snapshot.ToPriceLine()}   {stamp}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{Detail}▸ ✓ {snapshot.ToPriceLine()}   {stamp}");
        }

        Console.ResetColor();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void Rule(string title)
    {
        var head = $"─── {title} ";
        var fill = Math.Max(3, Width - head.Length);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"{Indent}{head}{new string('─', fill)}");
        Console.ResetColor();
    }

    /// <summary>
    /// Ten-cell confidence bar. A routing heuristic made visible — NOT a calibrated probability
    /// (§F.7). Nobody has measured whether a 0.88 is right 88% of the time.
    /// </summary>
    private static string Bar(double confidence)
    {
        var filled = Math.Clamp((int)Math.Round(confidence * 10, MidpointRounding.AwayFromZero), 0, 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }

    /// <summary>Pads or clips <paramref name="text"/> to exactly <paramref name="width"/> characters.</summary>
    private static string Fit(string? text, int width)
    {
        if (width < 4) width = 4;
        var flat = Flatten(text);
        if (flat.Length == width) return flat;
        return flat.Length < width ? flat.PadRight(width) : flat[..(width - 1)] + "…";
    }

    /// <summary>
    /// Clips to what fits on one line at the given left offset. Long prose is cut, never wrapped
    /// into a ragged second line that breaks the panel's alignment.
    /// </summary>
    private static string Wrap(string? text, int leftOffset = 0)
    {
        var room = Math.Max(20, Width - leftOffset);
        var flat = Flatten(text);
        return flat.Length <= room ? flat : flat[..(room - 1)] + "…";
    }

    private static string Flatten(string? text) =>
        (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// Compresses a run of ids sharing one prefix: PUR-NB-01, PUR-NB-02, PUR-NB-03 becomes
    /// "PUR-NB-01,02,03". Falls back to a plain comma list when the prefixes differ.
    /// </summary>
    private static string CompressIds(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return "—";
        if (ids.Count == 1) return ids[0];

        var first = ids[0];
        var cut = first.LastIndexOf('-');
        if (cut <= 0) return string.Join(", ", ids);

        var prefix = first[..(cut + 1)];
        foreach (var id in ids)
            if (!id.StartsWith(prefix, StringComparison.Ordinal))
                return string.Join(", ", ids);

        var sb = new StringBuilder(first);
        for (var i = 1; i < ids.Count; i++) sb.Append(',').Append(ids[i][(cut + 1)..]);
        return sb.ToString();
    }

    private static Product? Resolve(string productId) =>
        Catalogue.Default.BySku.TryGetValue(productId, out var product) ? product : null;

    private static string NameOf(string productId) => Resolve(productId)?.Name ?? productId;
}
