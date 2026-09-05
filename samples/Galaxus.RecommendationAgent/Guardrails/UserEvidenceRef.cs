// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// The USER side of §F.3's two-sided evidence, as the model writes it — the fifth argument of
/// <c>PresentRecommendation</c> (§8.1 B-5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists at all.</b> Until this argument was added the tool carried only the
/// PRODUCT side, and Demo 1 DERIVED the user side from retrieval provenance — from which search
/// need happened to surface the SKU. That derivation is honest but it makes the user-side arm of
/// <see cref="EvidenceRequiredFilter"/> a tautology: the label and the purchase ids both come out
/// of the same <see cref="Domain.InterestSignal"/>, so the comparison is <c>x ⊆ x</c> and the arm
/// reports <c>arm_inapplicable</c> on every single turn. An arm that cannot fire has a chance
/// floor of 1.0.
/// </para>
/// <para>
/// <b>The wire format is deliberately dumb.</b> <c>label | PUR-AA-01,PUR-AA-02</c> — one pipe,
/// then a comma list. A pipe rather than a comma separates them because an interest label is a
/// natural-language sentence and may well contain commas; a pipe never appears in an authored
/// label. Parsing is lenient (a missing pipe means "no purchase ids", which is the correct shape
/// for a stated-in-session need) and VERIFICATION is strict: an invented label fails
/// <see cref="GuardrailReasons.UnknownSignalLabel"/>, a foreign id fails
/// <see cref="GuardrailReasons.ForeignPurchaseId"/>, and one of the customer's OWN ids that does
/// not evidence the cited signal fails
/// <see cref="GuardrailReasons.PurchaseDoesNotEvidenceSignal"/>. Leniency in the parser costs
/// nothing because none of it can flatter the artifact under test.
/// </para>
/// <para>
/// ⚠ The argument is OPTIONAL, and that is a decision rather than an oversight. Making it
/// required would drop every recommendation on any turn where the model omitted it — an extreme
/// value produced by a wiring fault rather than by the thing under test, and the exact failure
/// shape this project keeps a rule about. When it is absent, Demo 1 falls back to the derived
/// user side AND writes the <c>arm_inapplicable</c> note, so the ledger always says which of the
/// two paths produced the numbers on screen.
/// </para>
/// </remarks>
/// <param name="SignalLabel">The interest label the model claims this recommendation serves.</param>
/// <param name="PurchaseIds">The purchase ids the model claims evidence that interest. May be empty.</param>
public sealed record UserEvidenceRef(string SignalLabel, IReadOnlyList<string> PurchaseIds)
{
    /// <summary>The character separating the label from the purchase-id list.</summary>
    public const char Separator = '|';

    /// <summary>
    /// The one-line format description repeated in the tool's parameter description, so the
    /// string the model is shown and the string this parser accepts cannot drift apart.
    /// </summary>
    public const string Format = "<interest label from GetInterestMap> | <purchase id>,<purchase id>";

    /// <summary>
    /// Parses the <c>userEvidence</c> tool argument. Returns false for null, empty or
    /// whitespace-only input — which means "the model did not supply a user side", not "the
    /// model supplied a bad one".
    /// </summary>
    /// <param name="raw">The argument exactly as the model wrote it.</param>
    /// <param name="evidence">The parsed user side on success.</param>
    public static bool TryParse(string? raw, out UserEvidenceRef evidence)
    {
        evidence = Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        var cut = text.IndexOf(Separator);

        if (cut < 0)
        {
            // No pipe: the whole string is the label and no purchase id was cited. Legitimate for
            // a stated-in-session need; caught by MissingEvidence for a behaviour-derived one.
            evidence = new UserEvidenceRef(text, []);
            return text.Length > 0;
        }

        var label = text[..cut].Trim();
        if (label.Length == 0) return false;

        var ids = text[(cut + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        evidence = new UserEvidenceRef(label, ids);
        return true;
    }

    /// <summary>Round-trips to the wire form the tool accepts.</summary>
    public override string ToString() =>
        PurchaseIds.Count == 0 ? SignalLabel : $"{SignalLabel} {Separator} {string.Join(",", PurchaseIds)}";

    /// <summary>The "nothing was supplied" value. Never treated as a citation.</summary>
    public static UserEvidenceRef Empty { get; } = new(string.Empty, []);
}
