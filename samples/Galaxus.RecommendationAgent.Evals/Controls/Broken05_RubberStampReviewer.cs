// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Loop;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Negative control #5 — a real discovery loop whose coverage reviewer approves on round 1, every
/// time. Design §D.3's "⚠️ Reviewer rubber-stamps round 1".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this control and not another broken agent.</b> The other four controls break the ANSWER —
/// invented SKUs, missing citations, no personalisation. This one breaks the LOOP while leaving the
/// answer well formed, which is the failure the design singles out as most dangerous because it
/// fails in the flattering direction: you pay for a loop you never take, the eval reads
/// "loop ≈ one-shot", and the conclusion drawn is that the ARCHITECTURE does not pay for itself when
/// in fact the REVIEWER is broken.
/// </para>
/// <para>
/// <b>The bar it sets, and why the bar is the point.</b> This arm has the loop's topology, the
/// loop's per-interest fan-out, the loop's dedup and the loop's presentation rule. The only thing it
/// lacks is a reviewer that ever says no. So the difference between it and the real Demo 2 loop is
/// <i>exactly</i> the reviewer's judgement — nothing else varies. <b>If the real loop cannot beat
/// this, the loop is not doing the work</b>, and the honest report is that the second round is
/// costing tokens for nothing. That is a result, not a failure of the eval.
/// </para>
/// <para>
/// ⚠ It is registered in Eval 02 as a loop arm, and it is NOT a stand-in for Arm B. The real arm's
/// row stays declared-absent until <see cref="Adapters.DiscoveryLoopAdapter"/> is wired; substituting
/// a broken loop for a missing one and printing the comparison would be exactly the substitution
/// Eval 02's remarks refuse to make.
/// </para>
/// <para>
/// It applies the §0.5 / D-3 vocabulary constraint like any correctly built loop would. That costs
/// it nothing, because a reviewer that never withholds approval also never proposes an interest —
/// which is itself worth seeing in Eval 04, where this arm comes out INAPPLICABLE rather than clean.
/// </para>
/// </remarks>
public sealed class Broken05_RubberStampReviewer : DiscoveryLoopArm
{
    /// <summary>Creates the control over an already-built retriever.</summary>
    /// <param name="retriever">The same retriever the live agent's tools are bound to.</param>
    public Broken05_RubberStampReviewer(IProductRetriever retriever)
        : base(retriever)
    {
    }

    /// <inheritdoc/>
    public override string Name => nameof(Broken05_RubberStampReviewer);

    /// <inheritdoc/>
    protected override ICoverageReviewer CreateReviewer(string customerId) => new RubberStampReviewer();
}
