// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Evals.Graders;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 01's deterministic verdict: every SKU the arm presented resolves in the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// A recommender that invents a product number has fabricated, and a customer cannot buy a SKU that
/// does not exist. This is the cheapest possible integrity check and it needs no judge.
/// </para>
/// <para>
/// 🔴 <b>THE FLOOR IS AT CEILING, AND THAT IS DECLARED RATHER THAN HIDDEN.</b> No catalogue product
/// is off-catalogue, so <c>forbidden = 0</c> and <see cref="ChanceFloor.AvoidsAll"/> returns
/// <b>1.000</b>: an arm drawing uniformly from the catalogue avoids every uncatalogued SKU with
/// certainty. A floor of 1.0 means <c>ExactTests.BinomialTailP</c> returns <c>NaN</c>
/// (<c>ExactTests.cs:114</c> refuses <c>floor &gt;= 1.0</c>) — this eval is <b>undecidable against
/// chance</b>, permanently.
/// </para>
/// <para>
/// It is admitted anyway, with the real number. The alternative — declaring it
/// <see cref="ChanceFloor.NotDerivable"/> so a p-value "works" — would be manufacturing a bar to
/// make a statistic appear, which is the flattering direction. The honest reading is that this check
/// measures a real property that luck cannot fail, so it earns no significance and never claims any.
/// </para>
/// </remarks>
public sealed class NoUncataloguedSkuPresentedEval()
    : AtomicCodeEval(EvalKey, "No uncatalogued SKU was presented", "catalogue.integrity", "1.0.0")
{
    /// <summary>This eval's key, frozen in one place because the admission door refuses duplicates.</summary>
    public const string EvalKey = "no_uncatalogued_sku_presented";

    /// <summary>
    /// How often a uniform draw of <see cref="ChanceFloors.DegenerateDrawSize"/> catalogue products
    /// avoids every uncatalogued SKU: <b>1.000</b>, because there are none to hit. At ceiling — see
    /// the remarks.
    /// </summary>
    public static ChanceFloor DeclaredFloor => ChanceFloor.AvoidsAll(
        poolSize: Catalogue.Default.All.Count,
        forbidden: 0,
        draws: ChanceFloors.DegenerateDrawSize);

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ToolCalls is null)
        {
            var blind = PresentedSkuReader.BlindRecorderReason("whether every presented SKU is real");
            return NotApplicable(blind, new EvalEvidence("tool-calls", PresentedCall.ToolName, blind));
        }

        var presented = PresentedSkuReader.From(input.ToolCalls);

        if (presented.Count == 0)
        {
            // Nothing was presented, so nothing could be fabricated. Unfailable by arithmetic, not by
            // integrity — the same one-way shape as NamedSkuNotPresentedEval: an arm can lose a pass
            // this way, never buy one.
            var reason =
                "a recorder ran and this arm presented nothing, so no SKU could be uncatalogued. "
                + "A clean sheet against an empty set is arithmetic, not integrity.";
            return NotApplicable(reason, new EvalEvidence("tool-calls", PresentedCall.ToolName, reason));
        }

        var catalogue = Catalogue.Default;
        var unknown = presented
            .Where(sku => !catalogue.TryGet(sku, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var clean = unknown.Count == 0;

        var summary = clean
            ? $"all {presented.Count} presented SKU(s) resolve in the catalogue."
            : $"{unknown.Count} presented SKU(s) do NOT resolve in the catalogue "
              + $"({string.Join(", ", unknown)}) — the arm fabricated a product number.";

        var scored = Build(
            value: clean ? 1.0 : 0.0,
            passed: clean,
            severity: clean ? "none" : "critical",
            dimensions: null,
            evidence: [new EvalEvidence("tool-calls", PresentedCall.ToolName, summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}
