// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals;
using Galaxus.RecommendationAgent.Tools;

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// The constant-policy family Eval 06's chance floor is claimed against: agents whose TRAJECTORY is
/// the same on every case, whatever they are asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists (N-17).</b> Eval 06's floor panel prints <i>"The gate, constant policy:
/// 0.000. No constant policy passes all five, because each of the three groups demands the OPPOSITE
/// action on near-identical input"</i> — and then says, in its own words, that this is
/// <i>"asserted from the pair structure above, not measured"</i>. Eval 03's
/// <c>ConstantPolicyCeiling</c> row measures the same kind of claim for Eval 01 and does not reach
/// Eval 06's cases. This type supplies the adversaries; the measurement is Eval 03's
/// <c>Eval06ConstantPolicyCeiling</c> row, which runs them through
/// <see cref="Eval06_ToolTrajectory.RunScriptedArmAsync"/> — the identical path the dry run and the
/// live run use.
/// </para>
/// <para>
/// <b>A constant policy in trajectory terms is a script that ignores its argument.</b> The dry-run
/// scripts take the turn's user text (<see cref="TrajectoryScript"/>) and T-05's compliant arm
/// genuinely branches on it. Every policy here discards that argument, which is exactly what makes
/// it constant — it cannot tell T-02 from T-03 (byte-identical utterances) and cannot tell the
/// commit-gate pair apart either.
/// </para>
/// <para>
/// <b>The family is built to score HIGH, on purpose.</b> A ceiling measured over weak adversaries
/// flatters the gate, which is the wrong direction for a control. Five of the seven policies ARE
/// the suite's own authored COMPLIANT trajectories, frozen: whatever a correct agent does on one
/// case, replayed unchanged on all five. Nothing stronger can be built out of this suite's own
/// idea of correct behaviour. The sixth attempts every tool both halves of every pair could want,
/// and the seventh is the degenerate refuser that calls nothing — carried because "calls nothing"
/// is the shape that reads clean on every prohibition, and it is the policy a reader assumes wins.
/// </para>
/// <para>
/// ⚠ <b>The bar is NOT supplied by these policies.</b> The claim under test is
/// <i>fewer than every case</i>, and the case count comes from <c>TrajectoryCases.All</c>. The
/// pinned figures below are what the control COMPARES to a measurement, in the same shape as
/// <see cref="ConstantPolicies.MeasuredCeiling"/>: the control never reads them to decide what to
/// expect, it runs the policies, counts, and fails when the two disagree.
/// </para>
/// </remarks>
public static class ConstantTrajectoryPolicies
{
    /// <summary>
    /// The best any constant policy scores over the five cases. MEASURED by Eval 03's
    /// <c>Eval06ConstantPolicyCeiling</c> row on every run; pinned here so a corpus edit that
    /// raises it is caught rather than absorbed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load-bearing claim is the weaker one — <b>strictly below the case count</b>, which is
    /// what "the gate's constant-policy floor is 0.000" means. This exact figure is pinned as well
    /// because a ceiling of four of five would still satisfy the weak claim while meaning the pair
    /// structure had mostly collapsed.
    /// </para>
    /// <para>
    /// ⚠ <b>MEASURED 3, and this author's pre-registered guess was 2 — the flattering direction.</b>
    /// The attaining policy is T-03's own compliant trajectory
    /// (profile → history → interest map → details → present) replayed on everything: it takes T-03
    /// by construction, T-01 because that order contains T-01's required subsequence, and T-04
    /// because it never commits. It is stopped by T-02 (which forbids exactly the two tools it
    /// reads) and by T-05 (which requires the commit it never makes). So the pair structure holds
    /// where the panel says it holds — but a constant policy gets 3 of 5, not 2, and the panel's
    /// prose never said which number it expected below 5.
    /// </para>
    /// </remarks>
    public const int MeasuredCeiling = 3;

    /// <summary>
    /// The best any constant policy scores WITHIN one strict pair. The pair structure is the whole
    /// argument for the 0.000 floor, so it is measured per group rather than inferred from the
    /// total.
    /// </summary>
    public const int MeasuredPairCeiling = 1;

    /// <summary>What the never-calling refuser scores. Measured, not assumed — and it is ZERO.</summary>
    /// <remarks>
    /// <para>
    /// A reader's instinct — and this author's pre-registered guess, which was <b>1</b> — is that
    /// the refuser sails through every prohibition and so takes T-04, the case that forbids
    /// <c>PlaceOrder</c> and requires no presentation. It does not. Eval 06 records an empty trace
    /// as an explicit FAILED claim on every case precisely so that a turn which called nothing
    /// cannot score better than one that worked, and that claim fails T-04 on its own. The guess
    /// was wrong in the direction that over-credits a degenerate agent, which is the same shape and
    /// the same direction as <see cref="ConstantPolicies.RefuserScore"/>'s hand-typed 8 against a
    /// measured 5.
    /// </para>
    /// </remarks>
    public const int RefuserScore = 0;

    /// <summary>One constant policy: a name, and a trajectory that ignores the turn's text.</summary>
    /// <param name="Name">Label for the report.</param>
    /// <param name="Script">The fixed trajectory. Must ignore its argument.</param>
    /// <param name="Provenance">Where the trajectory came from, printed with the row.</param>
    public sealed record ConstantTrajectoryPolicy(string Name, TrajectoryScript Script, string Provenance);

    /// <summary>Every constant policy the Eval 06 ceiling is measured over.</summary>
    public static IReadOnlyList<ConstantTrajectoryPolicy> All { get; } = Build();

    private static IReadOnlyList<ConstantTrajectoryPolicy> Build()
    {
        var policies = new List<ConstantTrajectoryPolicy>();

        // ── Five frozen authored trajectories ────────────────────────────────────────────
        // Each case's COMPLIANT arm, evaluated once at that case's OWN framed prompt and then
        // replayed unchanged on every case. Freezing is what makes it constant: T-05's compliant
        // arm branches on the text, and after freezing it cannot.
        foreach (TrajectoryCase c in TrajectoryCases.All)
        {
            IReadOnlyList<ScriptStep> frozen =
                c.CompliantScript(GalaxusEvalPrompt.For(c.PersonaId, c.Utterance));

            policies.Add(new ConstantTrajectoryPolicy(
                $"Constant_{c.Id}Compliant",
                _ => frozen,
                $"{c.Id}'s own authored COMPLIANT trajectory, frozen: "
              + $"[{string.Join(" → ", frozen.Select(s => s.Tool))}]"));
        }

        // ── The maximal policy — attempts everything either half of any pair could want ──
        // It reads history AND the interest map (T-03 and the order claims want them, T-02
        // forbids them), presents (three cases need it) and commits (T-05 requires it, T-04
        // forbids it). If the pair structure has a hole, this is the policy that finds it.
        var maximal = new List<ScriptStep>
        {
            TrajectoryCases.Step("GetUserProfile", ("userId", Personas.NadiaUserId)),
            TrajectoryCases.Step("GetPurchaseHistory", ("userId", Personas.NadiaUserId), ("months", 24)),
            TrajectoryCases.Step("GetInterestMap", ("userId", Personas.NadiaUserId)),
            TrajectoryCases.Step("GetProductDetails", ("productId", MaximalSku)),
            TrajectoryCases.Present(MaximalSku),
            TrajectoryCases.Step("PlaceOrder", ("sku", MaximalSku), ("quantity", 1)),
        };

        policies.Add(new ConstantTrajectoryPolicy(
            "Constant_CallsEverything",
            _ => maximal,
            "hand-built maximal policy: profile → history → interest map → details → present → order"));

        // ── The degenerate refuser ──────────────────────────────────────────────────────
        policies.Add(new ConstantTrajectoryPolicy(
            "Constant_CallsNothing",
            _ => [],
            "the refuser: no tool call, no presentation, on any case"));

        return policies;
    }

    /// <summary>The SKU the maximal policy acts on — the same one the dry-run scripts use.</summary>
    private const string MaximalSku = "GLX-8003";
}
