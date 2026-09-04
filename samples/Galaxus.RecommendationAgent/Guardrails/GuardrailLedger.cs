// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>Which stage of <see cref="GuardrailPipeline"/> produced a ledger entry.</summary>
public enum GuardrailStage
{
    /// <summary>The construction-time read-only allow-list (§F.1, <see cref="ToolSurfaceInvariant"/>).</summary>
    ToolSurface,

    /// <summary>The pre-search abstention gate (§F.8).</summary>
    AbstentionGate,

    /// <summary>The interest-map builder's inbound sensitive-label screen (§F.5, §0.5 / D-6).</summary>
    InterestMap,

    /// <summary>Catalogue and ownership grounding (§F.2).</summary>
    CatalogueGrounding,

    /// <summary>Two-sided evidence verification (§F.3).</summary>
    EvidenceRequired,

    /// <summary>Outbound special-category screening (§F.5, §0.5 / D-6).</summary>
    SensitiveInference,

    /// <summary>Confidence banding (§F.7).</summary>
    ConfidenceBands,

    /// <summary>Render-time price and stock re-verification (§F.4).</summary>
    PriceStock
}

/// <summary>What the pipeline did to an item.</summary>
public enum GuardrailAction
{
    /// <summary>Removed from the answer entirely. Never down-ranked — removed.</summary>
    Dropped,

    /// <summary>Moved from the primary tray to <c>also_consider</c>.</summary>
    Demoted,

    /// <summary>An observation with no effect on the answer — including "this arm did not run".</summary>
    Noted
}

/// <summary>
/// The frozen vocabulary of guardrail reasons. Constants rather than an enum because the
/// reasons are printed in the ledger panel, serialised into the <c>--log</c> transcript, and
/// read by the eval lane; a silently renamed enum member is exactly the drift that produced
/// design §0.5 / D-1.
/// </summary>
public static class GuardrailReasons
{
    /// <summary>§F.2 — the product id does not resolve in the catalogue. A hallucinated SKU.</summary>
    public const string Ungrounded = "ungrounded";

    /// <summary>§B.3 trap 1 — the customer already owns this exact SKU. Not a recommendation; an insult with a checkout button.</summary>
    public const string AlreadyOwned = "already_owned";

    /// <summary>§B.3 trap 2 — a durable the customer already owns, still inside its typical horizon. The upgrade lane is suppressed.</summary>
    public const string DurableStillInHorizon = "durable_still_in_horizon";

    /// <summary>§F.3 — the evidence block is absent or empty on a behaviour-derived recommendation.</summary>
    public const string MissingEvidence = "missing_evidence";

    /// <summary>§F.3 — the cited interest label is not present in the CODE-derived interest map.</summary>
    public const string UnknownSignalLabel = "unknown_signal_label";

    /// <summary>§F.3 — a cited purchase id does not belong to this customer.</summary>
    public const string ForeignPurchaseId = "foreign_purchase_id";

    /// <summary>§B.3 — a cited purchase id was classified as a gift, so it is evidence about a different person.</summary>
    public const string GiftPurchaseCited = "gift_purchase_cited";

    /// <summary>§F.6 — a stated-in-session signal was cited alongside purchase ids the agent was never given.</summary>
    public const string StatedNeedCitesHistory = "stated_need_cites_history";

    /// <summary>§F.3 — the cited attribute key exists in neither <c>Specs</c> nor <c>Tags</c>.</summary>
    public const string AttributeNotFound = "attribute_not_found";

    /// <summary>§F.3 — the cited attribute value does not equal the catalogue value.</summary>
    public const string AttributeValueMismatch = "attribute_value_mismatch";

    /// <summary>§F.3 — the cited review id does not exist, or belongs to a different product.</summary>
    public const string ReviewNotFound = "review_not_found";

    /// <summary>Defect class D5 — the compact <c>attr:</c> / <c>review:</c> citation does not parse or does not resolve.</summary>
    public const string UnresolvableEvidence = "unresolvable_evidence";

    /// <summary>§F.5 — the product sits under a category flagged <c>SensitiveInference</c>, and the customer did not ask for it.</summary>
    public const string SensitiveCategory = "sensitive_category";

    /// <summary>§0.5 / D-6 — an emitted interest LABEL hit the special-category term set.</summary>
    public const string SensitiveLabel = "sensitive_label";

    /// <summary>§0.5 / D-6 — a customer-facing reason string hit the special-category term set.</summary>
    public const string SensitiveProse = "sensitive_prose";

    /// <summary>§F.7 — confidence below <see cref="ConfidenceBands.SecondaryThreshold"/>.</summary>
    public const string LowConfidence = "low_confidence";

    /// <summary>§F.7 — confidence was NaN, negative, or above 1. A number that is not a confidence is not a pass.</summary>
    public const string ConfidenceOutOfRange = "confidence_out_of_range";

    /// <summary>§F.4 — the reason text states a price. The model is structurally forbidden to.</summary>
    public const string StatedPrice = "stated_price";

    /// <summary>§F.4 — zero stock. A demotion to <c>also_consider</c> with an explicit note, not a drop.</summary>
    public const string OutOfStock = "out_of_stock";

    /// <summary>§F.4 — the SKU cannot ship to the customer's market. A hard fact, so a drop.</summary>
    public const string MarketUnavailable = "market_unavailable";

    /// <summary>The same SKU was presented more than once in one turn.</summary>
    public const string DuplicatePresentation = "duplicate_presentation";

    /// <summary>§F.8 — the pre-search gate fired and nothing was searched for.</summary>
    public const string Abstained = "abstained";

    /// <summary>
    /// An arm of a guardrail could not run because its input set was empty — for example no
    /// category in the tree carries <c>SensitiveInference</c>. Recorded LOUDLY: an arm that
    /// cannot fire has a chance floor of 1.0, and reading its silence as a pass is the exact
    /// failure shape design §0.5 / D-5 condemns for <c>PlaceOrder</c>.
    /// </summary>
    public const string ArmInapplicable = "arm_inapplicable";

    /// <summary>Every reason, in declaration order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Ungrounded, AlreadyOwned, DurableStillInHorizon,
        MissingEvidence, UnknownSignalLabel, ForeignPurchaseId, GiftPurchaseCited, StatedNeedCitesHistory,
        AttributeNotFound, AttributeValueMismatch, ReviewNotFound, UnresolvableEvidence,
        SensitiveCategory, SensitiveLabel, SensitiveProse,
        LowConfidence, ConfidenceOutOfRange,
        StatedPrice, OutOfStock, MarketUnavailable,
        DuplicatePresentation, Abstained, ArmInapplicable
    ];

    /// <summary>True when <paramref name="reason"/> is one of <see cref="All"/> (ordinal).</summary>
    /// <param name="reason">A candidate reason token.</param>
    public static bool IsKnown(string? reason) =>
        reason is not null && All.Contains(reason, StringComparer.Ordinal);
}

/// <summary>One line of the ledger: what happened, to what, at which stage, and why.</summary>
/// <param name="Stage">The pipeline stage.</param>
/// <param name="Action">Dropped, demoted, or merely noted.</param>
/// <param name="Reason">One of <see cref="GuardrailReasons"/>.</param>
/// <param name="Subject">The product id, interest label, or tool name the entry is about. <c>"—"</c> when it is about the run.</param>
/// <param name="Detail">The human-readable justification, printed verbatim in the ledger panel.</param>
public sealed record GuardrailEntry(
    GuardrailStage Stage,
    GuardrailAction Action,
    string Reason,
    string Subject,
    string Detail);

/// <summary>
/// The counted record of everything the guardrail pipeline did in one turn, printed as the
/// ledger panel and serialised into the <c>--log</c> transcript.
/// </summary>
/// <remarks>
/// <para>
/// This is the artefact that makes the guardrails WATCHABLE. "Marco was not recommended a Pro
/// Controller" is unfalsifiable on its own — the model might simply not have thought of one.
/// "Two purchases were excluded as gifts, and here are their ids and the four signals that
/// fired" is a claim you can check.
/// </para>
/// <para>
/// Mutable by design: the stages write into one instance as the answer flows through them.
/// It is not thread-safe, and it does not need to be — one ledger belongs to one turn.
/// </para>
/// </remarks>
public sealed class GuardrailLedger
{
    private readonly List<GuardrailEntry> _entries = [];

    /// <summary>How many recommendations entered the pipeline, across both trays.</summary>
    public int InputCount { get; private set; }

    /// <summary>How many survived to be rendered, across both trays.</summary>
    public int OutputCount { get; private set; }

    /// <summary>Every entry, in the order the stages wrote them.</summary>
    public IReadOnlyList<GuardrailEntry> Entries => _entries;

    /// <summary>Records the size of the answer entering the pipeline.</summary>
    /// <param name="count">Number of presented recommendations before any filter ran.</param>
    public void RecordInput(int count) => InputCount = count;

    /// <summary>Records the size of the answer leaving the pipeline.</summary>
    /// <param name="count">Number of presented recommendations after every filter ran.</param>
    public void RecordOutput(int count) => OutputCount = count;

    /// <summary>Removes an item from the answer and says why.</summary>
    /// <param name="stage">The stage doing the dropping.</param>
    /// <param name="reason">One of <see cref="GuardrailReasons"/>.</param>
    /// <param name="subject">Product id or interest label.</param>
    /// <param name="detail">Human-readable justification.</param>
    public void Drop(GuardrailStage stage, string reason, string subject, string detail) =>
        _entries.Add(new GuardrailEntry(stage, GuardrailAction.Dropped, reason, subject, detail));

    /// <summary>Moves an item from the primary tray to <c>also_consider</c> and says why.</summary>
    /// <param name="stage">The stage doing the demotion.</param>
    /// <param name="reason">One of <see cref="GuardrailReasons"/>.</param>
    /// <param name="subject">Product id.</param>
    /// <param name="detail">Human-readable justification.</param>
    public void Demote(GuardrailStage stage, string reason, string subject, string detail) =>
        _entries.Add(new GuardrailEntry(stage, GuardrailAction.Demoted, reason, subject, detail));

    /// <summary>
    /// Records an observation that did not change the answer — including the important one:
    /// an arm that COULD NOT RUN. Silence and inapplicability must be distinguishable in the
    /// record, or an arm with nothing to test reads as a clean pass.
    /// </summary>
    /// <param name="stage">The stage making the observation.</param>
    /// <param name="reason">One of <see cref="GuardrailReasons"/>.</param>
    /// <param name="subject">Product id, label, or <c>"—"</c> for a run-level note.</param>
    /// <param name="detail">Human-readable justification.</param>
    public void Note(GuardrailStage stage, string reason, string subject, string detail) =>
        _entries.Add(new GuardrailEntry(stage, GuardrailAction.Noted, reason, subject, detail));

    /// <summary>How many items were removed.</summary>
    public int DroppedCount => _entries.Count(e => e.Action == GuardrailAction.Dropped);

    /// <summary>How many items were moved to the secondary tray.</summary>
    public int DemotedCount => _entries.Count(e => e.Action == GuardrailAction.Demoted);

    /// <summary>How many observations were recorded without changing the answer.</summary>
    public int NotedCount => _entries.Count(e => e.Action == GuardrailAction.Noted);

    /// <summary>
    /// True when nothing was dropped. NOT the same as "the guardrails passed" — an empty
    /// answer drops nothing either, which is why <see cref="InputCount"/> is recorded.
    /// </summary>
    public bool IsClean => DroppedCount == 0;

    /// <summary>True when at least one arm reported that it had nothing to run against.</summary>
    public bool HasInapplicableArm =>
        _entries.Any(e => string.Equals(e.Reason, GuardrailReasons.ArmInapplicable, StringComparison.Ordinal));

    /// <summary>Counts entries carrying <paramref name="reason"/>, whatever the action.</summary>
    /// <param name="reason">One of <see cref="GuardrailReasons"/>.</param>
    public int CountOf(string reason) =>
        _entries.Count(e => string.Equals(e.Reason, reason, StringComparison.Ordinal));

    /// <summary>Counts entries carrying <paramref name="reason"/> AND <paramref name="action"/>.</summary>
    /// <param name="reason">One of <see cref="GuardrailReasons"/>.</param>
    /// <param name="action">The action to filter on.</param>
    public int CountOf(string reason, GuardrailAction action) =>
        _entries.Count(e => e.Action == action && string.Equals(e.Reason, reason, StringComparison.Ordinal));

    // ── the ledger PANEL's surface (read by Rendering/RecommendationPrinter) ─────────
    // Named for what the audience sees, computed from the same entries as everything else,
    // so the panel and the detail list can never disagree about what happened.

    /// <summary>How many recommendations the model proposed, across both trays. Alias of <see cref="InputCount"/>.</summary>
    public int Proposed => InputCount;

    /// <summary>Dropped because the product id does not exist in the catalogue (§F.2).</summary>
    public int DroppedUngrounded => CountOf(GuardrailReasons.Ungrounded, GuardrailAction.Dropped);

    /// <summary>
    /// Dropped by the two-sided evidence check (§F.3) — for any of its reasons: an invented
    /// interest label, a foreign or gift purchase id, an attribute that does not exist, a value
    /// that does not match, a review that is not there, or a citation that does not parse.
    /// </summary>
    public int DroppedMissingEvidence =>
        _entries.Count(e => e.Action == GuardrailAction.Dropped && e.Stage == GuardrailStage.EvidenceRequired);

    /// <summary>Dropped because the model stated a price (§F.4).</summary>
    public int DroppedStatedPrice => CountOf(GuardrailReasons.StatedPrice, GuardrailAction.Dropped);

    /// <summary>Demoted to <c>also consider</c> for sitting below the primary confidence band (§F.7).</summary>
    public int DemotedLowConfidence => CountOf(GuardrailReasons.LowConfidence, GuardrailAction.Demoted);

    /// <summary>Demoted to <c>also consider</c> for being out of stock (§F.4).</summary>
    public int DemotedOutOfStock => CountOf(GuardrailReasons.OutOfStock, GuardrailAction.Demoted);

    /// <summary>Blocked by the special-category screen, in either direction (§F.5, §0.5 / D-6).</summary>
    public int BlockedSensitive =>
        _entries.Count(e => e.Action == GuardrailAction.Dropped && e.Stage == GuardrailStage.SensitiveInference);

    /// <summary>
    /// How many purchases the intent classifier ruled out as gifts. Set by the demo from
    /// <see cref="Domain.InterestMap.ExcludedBecauseGift"/> — the exclusion happens before the
    /// pipeline runs, upstream of anything this ledger observes, and printing a zero here when
    /// two purchases were actually excluded would understate the best guardrail in the demo.
    /// </summary>
    public int GiftExcluded { get; set; }

    /// <summary>How many surviving items had their price and stock re-read from the catalogue (§F.4).</summary>
    public int PriceStockVerified { get; private set; }

    /// <summary>How many items reached the price/stock stage at all. The denominator of the line above.</summary>
    public int PriceStockRequested { get; private set; }

    /// <summary>Tool calls spent this turn. Set by the demo from the tool-call budget scope (§F.9).</summary>
    public int ToolCallsUsed { get; set; }

    /// <summary>The per-turn tool-call cap. Set by the demo from the tool-call budget scope (§F.9).</summary>
    public int ToolCallCap { get; set; }

    /// <summary>
    /// Observations that changed nothing but that the reader needs — above all, an arm that had
    /// nothing to fire against. An inapplicable arm is prefixed so it cannot be skimmed past.
    /// </summary>
    public IReadOnlyList<string> Notes =>
        _entries.Where(e => e.Action == GuardrailAction.Noted)
                .Select(e => string.Equals(e.Reason, GuardrailReasons.ArmInapplicable, StringComparison.Ordinal)
                    ? $"⚠ NOT TESTED — {e.Detail}"
                    : $"{e.Subject}: {e.Detail}")
                .ToList();

    /// <summary>Records how many items the price/stock stage looked at, and how many it verified.</summary>
    /// <param name="requested">Items that reached the stage.</param>
    /// <param name="verified">Items whose figures were re-read from the catalogue.</param>
    public void RecordPriceStock(int requested, int verified)
    {
        PriceStockRequested = requested;
        PriceStockVerified = verified;
    }

    /// <summary>Drops grouped by reason, ordered by count descending then reason ordinal.</summary>
    public IReadOnlyDictionary<string, int> DropsByReason =>
        _entries.Where(e => e.Action == GuardrailAction.Dropped)
                .GroupBy(e => e.Reason, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>Every entry written by one stage, in order.</summary>
    /// <param name="stage">The stage to filter on.</param>
    public IReadOnlyList<GuardrailEntry> EntriesFor(GuardrailStage stage) =>
        _entries.Where(e => e.Stage == stage).ToList();

    /// <summary>
    /// A one-line summary for the console header, e.g.
    /// <c>"9 in → 5 out · 3 dropped · 1 demoted · ungrounded 1, sensitive_category 2"</c>.
    /// </summary>
    public string ToSummaryLine()
    {
        var reasons = DropsByReason.Count == 0
            ? "no drops"
            : string.Join(", ", DropsByReason.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value}")));

        return string.Create(CultureInfo.InvariantCulture,
            $"{InputCount} in → {OutputCount} out · {DroppedCount} dropped · {DemotedCount} demoted · {reasons}");
    }

    /// <summary>
    /// The ledger panel body, one string per line, without box drawing — the renderer owns
    /// the frame. Inapplicable-arm notes are listed LAST and prefixed, because an arm that
    /// could not run is the single most misleading thing a clean-looking ledger can hide.
    /// </summary>
    public IReadOnlyList<string> ToPanelLines()
    {
        var lines = new List<string>();

        foreach (var entry in _entries.Where(e => e.Action != GuardrailAction.Noted))
        {
            var mark = entry.Action == GuardrailAction.Dropped ? "⛔" : "↘";
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{mark} {entry.Subject} — {entry.Reason}: {entry.Detail}"));
        }

        foreach (var entry in _entries.Where(e => e.Action == GuardrailAction.Noted))
        {
            var mark = string.Equals(entry.Reason, GuardrailReasons.ArmInapplicable, StringComparison.Ordinal) ? "⚠" : "·";
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{mark} {entry.Subject} — {entry.Reason}: {entry.Detail}"));
        }

        if (lines.Count == 0) lines.Add("· nothing to report — no item was dropped, demoted or held back");

        lines.Add(ToSummaryLine());
        return lines;
    }
}
