// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;

namespace Galaxus.RecommendationAgent.Tools;

/// <summary>
/// The single JSON writer for every tool return value (design §C).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every tool returns <see cref="string"/> and not a DTO.</b> MEAI's
/// <c>AIFunctionFactory</c> will happily JSON-serialize a returned record, but that puts the
/// demo's compile-time reliability at the mercy of the serializer's default resolver — and
/// whether <c>AIFunctionFactory.Create</c> needs an explicit serializer-options object for
/// record return types in MEAI 10.7.0 is UNVERIFIED. Serialising here, through one pinned
/// <see cref="JsonSerializerOptions"/>, gives the model identical JSON, keeps TravelDemo's
/// "tools return string" convention, and removes an entire failure class.
/// </para>
/// <para>
/// The options are pinned (camelCase via <see cref="JsonSerializerDefaults.Web"/>, not
/// indented) so the wire shape is a property of THIS file rather than of whatever ambient
/// defaults happen to be in force. Tool payloads are asserted on by the eval lane; a
/// silently-renamed property is exactly the drift that produced design §0.5 / D-1.
/// </para>
/// </remarks>
public static class ToolJson
{
    /// <summary>
    /// The pinned serializer options every tool payload goes through. Web defaults give
    /// camelCase property names and case-insensitive reads; indentation is off so a tool
    /// result costs the fewest possible context tokens.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>Serialises a successful tool payload.</summary>
    /// <typeparam name="T">Payload shape — normally an anonymous object authored at the call site.</typeparam>
    /// <param name="payload">The payload. Author it with a <c>status = "ok"</c> member so the
    /// model (and the eval) can branch on one field for every tool alike.</param>
    public static string Ok<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// A typed refusal — never an empty result.
    /// </summary>
    /// <remarks>
    /// An empty array would let "no data" masquerade as "no interests", and the agent would
    /// silently produce a worse answer with no signal that anything had been withheld. A
    /// refusal is a fact the model can read, print and reason about (§F.6).
    /// </remarks>
    /// <param name="code">A frozen machine code from <see cref="ToolRefusalCodes"/>.</param>
    /// <param name="reason">One sentence, addressed to the model, saying what to do instead.</param>
    public static string Refused(string code, string reason) =>
        JsonSerializer.Serialize(new { status = "refused", code, reason }, Options);

    /// <summary>
    /// The per-run tool-call budget is spent (§F.9).
    /// </summary>
    /// <remarks>
    /// The instruction deliberately names the one tool that still works. <c>PresentRecommendation</c>
    /// is the ANSWER channel, not a spend, so it is counted but never refused — see
    /// <see cref="ToolCallBudget"/>. Refusing it too would turn "out of budget" into "presented
    /// nothing", which reads on a report as a clean abstention: an instrument that scores silence
    /// as a pass is broken, not cautious.
    /// </remarks>
    /// <param name="used">Calls consumed so far in this run.</param>
    /// <param name="cap">The cap for this run.</param>
    public static string BudgetExhausted(int used, int cap) =>
        JsonSerializer.Serialize(new
        {
            status = "budget_exhausted",
            code = ToolRefusalCodes.BudgetExhausted,
            used,
            cap,
            reason = "Tool-call budget for this turn is spent. Answer with what you already have, or abstain. "
                   + "PresentRecommendation still works: present only products you have already verified with "
                   + "GetProductDetails, or say you cannot recommend anything yet and ask a clarifying question."
        }, Options);

    /// <summary>
    /// The per-turn DISTINCT-search cap is spent (§F.9, <see cref="ToolCallBudget.DistinctSearchCap"/>).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BudgetExhausted"/> on purpose: the model is told which cap it hit,
    /// and that re-running a search it already ran costs nothing — so the right move is to read
    /// what it already has, not to rephrase the same need a ninth way.
    /// </remarks>
    /// <param name="distinctSearches">Distinct searches run this turn.</param>
    /// <param name="cap">The distinct-search cap.</param>
    public static string SearchCapExhausted(int distinctSearches, int cap) =>
        JsonSerializer.Serialize(new
        {
            status = "budget_exhausted",
            code = ToolRefusalCodes.SearchCapExhausted,
            distinctSearches,
            cap,
            reason = "The distinct-search cap for this turn is spent. Lookups (GetProductDetails, GetReviewDigest, "
                   + "CheckStockAndPrice, BrowseCategory) still work, and repeating a search you already ran this turn "
                   + "is answered from memory at no cost. Verify and present what your searches already returned, or "
                   + "say plainly what you could not find."
        }, Options);

    /// <summary>
    /// A refusable call whose arguments were IDENTICAL to one already answered this turn. The
    /// work is not re-run; the model is pointed back at the answer it already has.
    /// </summary>
    /// <remarks>
    /// MEASURED on the 2026-09-04 live run, case C-09: four byte-identical searches, then four
    /// more, on about three distinct queries — twelve of twenty-four refusable slots and roughly
    /// half of a 148-second turn spent re-running work that had already returned. The replay
    /// carries the product ids the first answer carried so the model can recover without a second
    /// round trip, and it consumes no budget (<see cref="ToolCallBudget"/>).
    /// </remarks>
    /// <param name="toolName">The tool.</param>
    /// <param name="firstReturnedAsCall">1-based position of the call that first answered these arguments.</param>
    /// <param name="productIds">The product ids that first answer carried.</param>
    public static string AlreadyReturned(string toolName, int firstReturnedAsCall, IReadOnlyList<string> productIds) =>
        JsonSerializer.Serialize(new
        {
            status = "already_returned_this_turn",
            code = ToolRefusalCodes.AlreadyReturned,
            tool = toolName,
            firstReturnedAsCall,
            productIds,
            reason = "You already called this tool with exactly these arguments in this turn, and the answer has not "
                   + "changed — the catalogue does not move within a turn. Re-read that result above; the product ids it "
                   + "returned are listed here so you need not run it again. This replay consumed no budget."
        }, Options);

    /// <summary>
    /// A tool call that was accepted but is defective in a way the model can still fix.
    /// </summary>
    /// <remarks>
    /// Used only by <c>PresentRecommendation</c>. The arguments are recorded VERBATIM before
    /// this is returned — the tool never silently repairs them, because the eval reads the
    /// arguments and a repaired argument is a defect that can never fire (design §0.5 / D-1).
    /// </remarks>
    /// <param name="payload">The accepted payload, carrying <c>status = "accepted_with_warning"</c>.</param>
    public static string AcceptedWithWarning<T>(T payload) => JsonSerializer.Serialize(payload, Options);
}

/// <summary>
/// The frozen machine codes a tool refusal can carry. Constants rather than an enum because
/// they are serialised into tool JSON and asserted on by name in the eval lane.
/// </summary>
public static class ToolRefusalCodes
{
    /// <summary>The customer id does not exist. No silent fallback to a default persona.</summary>
    public const string UnknownUser = "unknown_user";

    /// <summary>The product id does not exist in the catalogue — the phantom-SKU signal (defect class D1).</summary>
    public const string UnknownProduct = "unknown_product";

    /// <summary>The category path does not exist in the category tree.</summary>
    public const string UnknownCategory = "unknown_category";

    /// <summary>
    /// FDPIC one-click opt-out is on for this customer (§F.6). Behavioural history is not
    /// available — enforced in the tool, not requested in the prompt.
    /// </summary>
    public const string PersonalizationDisabled = "personalization_disabled";

    /// <summary>The per-run tool-call budget is spent (§F.9).</summary>
    public const string BudgetExhausted = "budget_exhausted";

    /// <summary>The per-turn DISTINCT-search cap is spent (§F.9). Lookups and replays still work.</summary>
    public const string SearchCapExhausted = "search_cap_exhausted";

    /// <summary>
    /// The call repeated arguments already answered this turn and was answered from memory,
    /// consuming nothing. Not a refusal — the model has the answer already.
    /// </summary>
    public const string AlreadyReturned = "already_returned_this_turn";

    /// <summary>
    /// No retriever was bound before the run. The composition root did not call
    /// <see cref="GalaxusTools.Bind"/>; the semantic leg is unavailable and says so loudly
    /// rather than returning zero hits, which would read as "nothing matched" (§D.4).
    /// </summary>
    public const string RetrieverUnbound = "retriever_unbound";

    /// <summary>A required argument was empty or unusable.</summary>
    public const string InvalidArgument = "invalid_argument";
}
