// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Loop;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Evals.Adapters;

/// <summary>
/// ══ THE ADAPTER POINT ══ The one place Demo 2's real discovery loop enters the eval suite.
/// </summary>
/// <remarks>
/// <para>
/// Demo 2 — the MAF workflow with five executors and a conditional loop-back edge — lives in
/// <c>Galaxus.RecommendationAgent</c>. Everything the eval needs from it is expressed as
/// <see cref="IDiscoveryLoopArm"/>, and exactly one type in this project —
/// <see cref="RealDiscoveryLoopArm"/> — names a workflow type. Every other file in the suite,
/// grader and gate included, sees an <see cref="IEvaluableAgent"/> and nothing more.
/// </para>
///
/// <para><b>═══ HOW IT IS WIRED — as of this integration pass ═══</b></para>
///
/// <para>
/// <b>It is bound.</b> <see cref="RealDiscoveryLoopArm"/> owns a
/// <c>Galaxus.RecommendationAgent.Workflows.GalaxusDiscoveryLoop</c>, runs it for one customer,
/// projects <c>DiscoveryState</c> onto <see cref="DiscoveryLoopTelemetry"/> and replays the
/// screened answer as <c>PresentRecommendation</c> tool calls — the only channel any grader in
/// this suite reads. <c>Program.cs</c> calls <see cref="Bind"/> once, before any eval runs.
/// </para>
///
/// <para>
/// <b>Two things about that binding are declared here rather than left to be discovered.</b>
/// </para>
/// <list type="number">
///   <item><description>
///   <b>The bound arm runs the loop on its DETERMINISTIC path — no model call.</b> Evals 03 and 04
///   are stated to need no credentials and <c>-- 2 --dry-run</c> is stated to spend nothing, and a
///   model-backed arm would break both. It also means the arm is <b>not</b> entered in the sign
///   test against the live single agent: that comparison would vary architecture and model
///   presence at the same time, which is the co-moving-operands hazard, not a measurement. It is a
///   reference row and the report says so.
///   </description></item>
///   <item><description>
///   <b>For Eval 04 the arm's reviewer PROPOSAL is substituted with the case payload</b>, exactly
///   as <see cref="Controls.InjectionProbeLoop"/> does — see <see cref="CreateForCase"/>. Nothing
///   else is substituted: the shipped verdict builder, the shipped
///   <c>CoverageVerdictProjection</c>, the shipped <c>QueryVocabulary</c>, the shipped query
///   planner and the shipped retriever all run. What is measured is therefore the property D-3
///   actually asserts — <i>given a hostile proposal, does the shipped structure contain it?</i> —
///   and NOT "would a model emit this proposal", which no arm in this repository can answer.
///   </description></item>
/// </list>
///
/// <para>
/// <b>The D-3 obligation the bound loop has to meet.</b> A proposed query term is runnable only
/// when EVERY one of its tokens is already present in (the customer's interest map) ∪ (the
/// catalogue's category names, tag tokens and attribute keys and values) — product names and
/// brands deliberately EXCLUDED. Terms that fail are dropped, the drop is recorded, and a
/// proposal with nothing left is refused entirely, label included. Eval 04 does not take the
/// arm's word for what it dropped: it computes the required drop set itself, in
/// <see cref="Loop.QueryVocabulary"/>, from the case fixture and the corpus, and an arm that
/// reports fewer drops FAILS. <b>Those are deliberately two implementations of one rule</b> —
/// the bar must not be supplied by the artifact under test — and Eval 04 passing is the evidence
/// that they agree.
/// </para>
///
/// <para>
/// <b>What NOT to do.</b> Do not point this at
/// <see cref="Controls.Broken05_RubberStampReviewer"/> or at either injection probe. Those are
/// controls: one has a reviewer that never says no, and the other two exist to be steered. Binding a
/// control here would print a control's numbers under the real arm's label, which is the single
/// substitution this whole suite is built to prevent.
/// </para>
///
/// <para>
/// <b>If it is ever unbound</b> every consumer sees <see cref="IsBound"/> false, Eval 02 prints the
/// arm as declared-absent with <see cref="AbsenceReason"/> beside it, and Eval 04 reports the real
/// arm as NOT RUN. Nothing is substituted and no number is invented.
/// </para>
/// </remarks>
public static class DiscoveryLoopAdapter
{
    private static Func<DiscoveryArmRequest, IDiscoveryLoopArm>? _factory;

    /// <summary>The label the real arm is reported under, bound or not.</summary>
    /// <remarks>
    /// It says <i>deterministic arm</i> because that is what runs — see the type remarks. A label
    /// that read simply "Discovery Workflow (Demo 2)" would let a reader take a row produced with
    /// zero model calls for the model-backed loop the design's headline comparison is about.
    /// </remarks>
    public const string ArmLabel = "Discovery Workflow (Demo 2) — deterministic arm";

    /// <summary>Why the arm is absent, printed verbatim wherever the arm would have been.</summary>
    public const string AbsenceReason =
        "Demo 2's MAF workflow is not wired into this project. DiscoveryLoopAdapter.Bind has not been "
      + "called, so there is no loop to run. NOTHING has been substituted for it — the rubber-stamp "
      + "control is a control, not a stand-in.";

    /// <summary>True once <see cref="Bind"/> has supplied a real loop.</summary>
    public static bool IsBound => _factory is not null;

    /// <summary>
    /// Wires the real loop in. Call once, before any eval runs.
    /// </summary>
    /// <param name="factory">Builds the arm from one request.</param>
    /// <exception cref="InvalidOperationException">A loop is already bound.</exception>
    public static void Bind(Func<DiscoveryArmRequest, IDiscoveryLoopArm> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (_factory is not null)
        {
            throw new InvalidOperationException(
                "A discovery loop is already bound. Two bindings in one process would mean two different "
              + "arms reported under one label, and the report could not say which one produced a number.");
        }

        _factory = factory;
    }

    /// <summary>Builds the real arm for an ordinary coverage turn, or null when nothing is bound.</summary>
    /// <param name="context">The eval's shared context.</param>
    public static IDiscoveryLoopArm? Create(CoverageArmContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _factory?.Invoke(new DiscoveryArmRequest(context, Steering: null));
    }

    /// <summary>
    /// Builds the real arm for one D-3 case, with the case's payload substituted for whatever the
    /// loop's reviewer would otherwise have proposed. Null when nothing is bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a substitution is the right instrument here, and what it costs.</b> Demo 2's shipped
    /// reviewer is one structured model call, and no code in this repository predicts what that
    /// call would say — so an arm that waited for the real reviewer to volunteer this payload
    /// would measure the model's mood, and on a run where it volunteered nothing the case would
    /// come out INAPPLICABLE, which Eval 04 correctly refuses to score as a pass. Substituting the
    /// proposal asks the question the design actually asserts: containment must hold for EVERY
    /// proposal, not for the average one.
    /// </para>
    /// <para>
    /// What it does not establish: any rate at which a model would be steered. Nothing in Eval 04
    /// contains a model, and <c>Docs/MEASUREMENT_STATUS.md</c> §7 records that as an open gap
    /// rather than leaving it to be noticed.
    /// </para>
    /// </remarks>
    /// <param name="context">The eval's shared context.</param>
    /// <param name="injectionCase">The case whose payload the reviewer proposes.</param>
    public static IDiscoveryLoopArm? CreateForCase(CoverageArmContext context, InjectionCase injectionCase)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(injectionCase);
        return _factory?.Invoke(new DiscoveryArmRequest(context, injectionCase));
    }

    /// <summary>
    /// Clears the binding. Test seam only — nothing in the shipped run path calls it.
    /// </summary>
    public static void Reset() => _factory = null;
}

/// <summary>
/// Everything the bound factory needs to build one turn of the real loop.
/// </summary>
/// <remarks>
/// A record rather than two overloads so that adding a future dimension — a language toggle, a
/// round cap, a live/offline switch — is a new property with a default and touches neither
/// <see cref="DiscoveryLoopAdapter.Bind"/>'s signature nor any eval.
/// </remarks>
/// <param name="Context">The eval's shared context: the bound retriever and the dry-run flag.</param>
/// <param name="Steering">
/// The D-3 case whose payload replaces whatever the loop's reviewer would have proposed, or null
/// for an ordinary coverage turn. See <see cref="DiscoveryLoopAdapter.CreateForCase"/> for why the
/// substitution is the right instrument and exactly what it does not establish.
/// </param>
public sealed record DiscoveryArmRequest(CoverageArmContext Context, InjectionCase? Steering);
