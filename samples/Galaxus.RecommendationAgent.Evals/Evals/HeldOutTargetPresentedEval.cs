// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Evals.Graders;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 02c's deterministic verdict: did the arm present the held-out purchase?
/// </summary>
/// <remarks>
/// <para>
/// One order line is hidden from the agent and the agent is asked to recommend. Either the hidden
/// SKU comes back among what it presented, or it does not — no judge required.
/// </para>
/// <para>
/// 🔴 <b>The floor is supplied at admission and derived from the catalogue alone.</b> It never reads
/// this arm's output: k is the suite's DECLARED presentation budget
/// (<see cref="ChanceFloors.DegenerateDrawSize"/>, the same constant <c>Eval02c.K</c> cuts every arm
/// to), never the number of items this arm happened to present. Deriving k from the observed count
/// would let the artifact under test size the null it is judged against — the recorded defect in
/// <see cref="ArmProfile"/>'s remarks.
/// </para>
/// </remarks>
/// <param name="targetSku">The held-out purchase the arm is being asked to rediscover.</param>
public sealed class HeldOutTargetPresentedEval(string targetSku)
    : AtomicCodeEval(EvalKey, "Held-out target was presented", "recommendation.prediction", "1.0.0")
{
    /// <summary>This eval's key, frozen in one place because the admission door refuses duplicates.</summary>
    public const string EvalKey = "held_out_target_presented";

    private readonly string _targetSku = !string.IsNullOrWhiteSpace(targetSku)
        ? targetSku.Trim()
        : throw new ArgumentException("A target SKU is required.", nameof(targetSku));

    /// <summary>
    /// How often a uniform draw of <see cref="ChanceFloors.DegenerateDrawSize"/> products from the
    /// whole catalogue contains the one target by luck alone.
    /// </summary>
    public static ChanceFloor DeclaredFloor => ChanceFloor.AtLeastOneHit(
        poolSize: Catalogue.Default.All.Count,
        favourable: 1,
        draws: ChanceFloors.DegenerateDrawSize);

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ToolCalls is null)
        {
            var blind = PresentedSkuReader.BlindRecorderReason("what this arm presented");
            return NotApplicable(blind, new EvalEvidence("tool-calls", PresentedCall.ToolName, blind));
        }

        var presented = PresentedSkuReader.From(input.ToolCalls);

        // An arm that presented nothing is a MEASURED miss, not an undecidable one: the recorder saw
        // the whole run and the target is demonstrably absent from what reached the customer. This is
        // the opposite direction from NamedSkuNotPresentedEval, where presenting nothing makes an
        // avoidance check unfailable — here it makes a hit check unpassable, which is a real result.
        var hit = presented.Any(sku => string.Equals(sku, _targetSku, StringComparison.OrdinalIgnoreCase));

        var summary = hit
            ? $"the held-out purchase '{_targetSku}' is among the {presented.Count} product(s) presented."
            : presented.Count == 0
                ? $"the arm presented nothing, so the held-out purchase '{_targetSku}' was not recovered."
                : $"the held-out purchase '{_targetSku}' is absent from the {presented.Count} product(s) "
                  + $"presented ({string.Join(", ", presented)}).";

        var scored = Build(
            value: hit ? 1.0 : 0.0,
            passed: hit,
            severity: hit ? "none" : "medium",
            dimensions: null,
            evidence: [new EvalEvidence("tool-calls", PresentedCall.ToolName, summary)]);

        return scored with { Details = scored.Details with { Summary = summary } };
    }
}
