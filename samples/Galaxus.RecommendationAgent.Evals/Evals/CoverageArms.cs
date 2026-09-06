// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Adapters;
using Galaxus.RecommendationAgent.Evals.Loop;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>What kind of thing an arm is. Drives how the report reads it, never how it is graded.</summary>
/// <remarks>
/// The grader is arm-blind by construction — <c>InterestCoverageGrader</c> takes a list of
/// <c>PresentedCall</c> and knows nothing else — so this enum can grow without any grader changing.
/// That is the whole point of the registry.
/// </remarks>
public enum CoverageArmKind
{
    /// <summary>A live model-backed agent. Repeated, because it is stochastic.</summary>
    Live,

    /// <summary>A deterministic arm built to take the win away if it can.</summary>
    Control,

    /// <summary>A deterministic floor: what an agent that understands nothing scores.</summary>
    Baseline,

    /// <summary>An arm that reads the gold. A ceiling on what the metric can discriminate, never an entrant.</summary>
    Oracle,

    /// <summary>A bounded discovery loop.</summary>
    Loop,
}

/// <summary>
/// Everything an arm factory needs. One object so a new arm can be registered without changing any
/// signature.
/// </summary>
/// <param name="Retriever">The bound retriever every searching arm shares.</param>
/// <param name="LiveAgentFactory">
/// Builds a FRESH evaluable wrapper around the live agent — a fresh session per repetition, so one
/// rep's context cannot leak into the next.
/// </param>
/// <param name="DryRun">True when the live arm is a stub. Deterministic arms ignore it; they are already free.</param>
/// <param name="DeclaredK">
/// The presentation budget EVERY arm is given — the number the canonical utterance declares. No
/// arm sizes itself: the scripted controls read it as a constant, the live agent is told it in
/// the prompt, Demo 2's arm is cut to it by the grader. Carried here so an arm factory that
/// needs the budget reads the one declaration rather than a local literal.
/// </param>
public sealed record CoverageArmContext(
    IProductRetriever Retriever,
    Func<IEvaluableAgent> LiveAgentFactory,
    bool DryRun,
    int DeclaredK = GalaxusDemoPrompts.CoverageCohortDeclaredK);

/// <summary>
/// One registered arm of Eval 02 — its label, what kind of thing it is, how to build it, how many
/// times to run it, and whether it enters the paired sign test.
/// </summary>
/// <remarks>
/// <para>
/// <b>An arm with a null <see cref="Factory"/> is DECLARED ABSENT, not omitted.</b> That is the
/// point of carrying it in the registry at all: a comparison the design pre-registered and the
/// repository cannot run has to appear in the report as a stated absence with a stated reason. A row
/// that is simply missing reads as an oversight, and a row quietly filled with something else is the
/// substitution this suite exists to prevent.
/// </para>
/// </remarks>
/// <param name="Label">The arm's full name, as printed. Used as the key in every report.</param>
/// <param name="Kind">What kind of arm this is.</param>
/// <param name="Factory">Builds one instance, or null when the arm cannot be run here.</param>
/// <param name="AbsenceReason">Why it cannot be run. Required when <paramref name="Factory"/> is null.</param>
/// <param name="EntersSignTest">
/// True when this arm is paired against the live arm in the sign test. False for an oracle: it reads
/// the gold, so "the oracle led" would be painted as a positive result by a printer that colours the
/// leader green.
/// </param>
/// <param name="IsPrimaryControl">
/// True for the ONE arm whose leading over the live agent would mean the architecture is not
/// load-bearing. Eval 02's second gate reads this rather than a positional index.
/// </param>
/// <param name="Note">One sentence explaining what this arm is for, printed in the legend.</param>
/// <param name="ReachesAModel">
/// True when this arm issues chat-model calls at all — under a dry run that is the stub, which is
/// still a model call as far as the meter is concerned.
/// <para>
/// ⚠ <b>DECLARED, not inferred (plan item 8.3).</b> The cost panel has to tell an arm that genuinely
/// spent nothing from an arm whose usage never arrived, and both look like <c>0 tokens · $0.0000</c>.
/// The only place that distinction is KNOWN is here, where the arm is registered; deriving it from a
/// zero total downstream is reading applicability out of the result, which is the shape §61.8 names.
/// Every arm but the live one runs deterministically — the notes above each entry say so — so the
/// default is <see langword="false"/> and the one exception is spelled out.
/// </para>
/// </param>
public sealed record CoverageArm(
    string Label,
    CoverageArmKind Kind,
    Func<CoverageArmContext, IEvaluableAgent>? Factory,
    string AbsenceReason = "",
    bool EntersSignTest = false,
    bool IsPrimaryControl = false,
    string Note = "",
    bool ReachesAModel = false)
{
    /// <summary>True when this arm can actually be run in this repository.</summary>
    public bool IsRunnable => Factory is not null;

    /// <summary>True when the arm is stochastic and therefore repeated.</summary>
    public bool IsRepeated => Kind == CoverageArmKind.Live;

    /// <summary>Builds one instance. Throws when the arm is declared absent.</summary>
    /// <param name="context">The shared context.</param>
    /// <exception cref="InvalidOperationException">The arm has no factory.</exception>
    public IEvaluableAgent Create(CoverageArmContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Factory is null
            ? throw new InvalidOperationException(
                $"Arm '{Label}' is declared ABSENT and must not be constructed. {AbsenceReason}")
            : Factory(context);
    }
}

/// <summary>
/// Eval 02's arm registry. Adding an arm is adding a row here; no grader, no report and no gate
/// changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is data and not a switch statement.</b> Before this file the arms lived in three
/// places at once: a set of <c>const string</c> labels on the eval, a hard-coded
/// <c>DeterministicArms</c> iterator, and a hand-built list of sign-test pairs indexed by position
/// (<c>signTests[0]</c> was the control gate). Three copies of one fact drift, and the positional
/// index in particular meant that inserting an arm silently re-pointed a GATE at a different
/// comparison. Now the gate reads <see cref="CoverageArm.IsPrimaryControl"/> and the pairs are
/// generated from <see cref="CoverageArm.EntersSignTest"/>.
/// </para>
/// <para>
/// <b>Order is the report's column order</b> and is chosen so the reader meets the live arm, then
/// the control that can take its win away, then the floors, then the loop arms.
/// </para>
/// </remarks>
public static class CoverageArms
{
    /// <summary>
    /// The presentation budget every arm is given and cut to before any pairing — declared by the
    /// canonical utterance (<see cref="GalaxusDemoPrompts.CoverageCohortDeclaredK"/>), never a
    /// literal of this eval's own.
    /// </summary>
    public const int DeclaredK = GalaxusDemoPrompts.CoverageCohortDeclaredK;

    /// <summary>The live agent's label.</summary>
    public const string Live = "Single Agent (Robin)";

    /// <summary>The one-pass control's label.</summary>
    public const string SingleShot = "Control — single shot";

    /// <summary>The popularity floor's label.</summary>
    public const string Popularity = "Baseline — popularity";

    /// <summary>The tag-join oracle's label.</summary>
    public const string TagJoin = "Baseline — tag join";

    /// <summary>The rubber-stamp loop control's label.</summary>
    public const string RubberStampLoop = "Loop control — rubber stamp";

    /// <summary>The real Demo 2 arm's label, bound or not.</summary>
    public const string DiscoveryWorkflow = DiscoveryLoopAdapter.ArmLabel;

    /// <summary>
    /// Every arm, runnable and absent, in report order.
    /// </summary>
    /// <remarks>
    /// Evaluated fresh on each access because <see cref="DiscoveryLoopAdapter.IsBound"/> can change
    /// between construction and use — a cached list built before <c>Bind</c> ran would report the
    /// real arm as absent forever.
    /// </remarks>
    public static IReadOnlyList<CoverageArm> All =>
    [
        new CoverageArm(
            Live, CoverageArmKind.Live,
            Factory: context => context.LiveAgentFactory(),
            Note: "The shipped single agent with its five tools. Repeated, because it is stochastic.",
            ReachesAModel: true),

        new CoverageArm(
            SingleShot, CoverageArmKind.Control,
            Factory: context => new Broken03_SingleShotWorkflow(context.Retriever),
            EntersSignTest: true,
            IsPrimaryControl: true,
            Note: "One retrieval pass, no second look. The control that can take the win away: if it "
                + "matches the agent, the advantage is not architectural."),

        new CoverageArm(
            Popularity, CoverageArmKind.Baseline,
            Factory: _ => new Broken04_PopularityAgent(),
            EntersSignTest: true,
            Note: "The bestseller list, ignoring the customer. An empirical floor, MEASURED rather than "
                + "quoted at the design's 0.00."),

        new CoverageArm(
            TagJoin, CoverageArmKind.Oracle,
            Factory: _ => new Baseline_TagJoin(),
            EntersSignTest: false,
            Note: "Design §0.5 / D-4's missing SQL baseline. It CALLS InterestMapGold.Derive, so it is a "
                + "ceiling on what this metric can discriminate — an upper reference line, never an entrant."),

        new CoverageArm(
            RubberStampLoop, CoverageArmKind.Loop,
            Factory: context => new Broken05_RubberStampReviewer(context.Retriever),
            EntersSignTest: false,
            Note: "A real discovery loop whose reviewer approves on round 1, every time (design §D.3). It is "
                + "the bar the REAL loop has to clear: if the loop cannot beat a rubber stamp, the second "
                + "round is buying nothing. It is NOT a stand-in for the row below."),

        DiscoveryLoopAdapter.IsBound
            ? new CoverageArm(
                DiscoveryWorkflow, CoverageArmKind.Loop,
                Factory: context => DiscoveryLoopAdapter.Create(context)
                    ?? throw new InvalidOperationException("DiscoveryLoopAdapter reported bound but produced nothing."),
                // ⚠ NOT entered in the sign test, and the reason is not modesty. This arm runs the
                // real loop on its DETERMINISTIC path — no model call — so pairing it against the
                // live single agent would vary architecture AND model presence in one comparison,
                // and neither operand could be read alone. That is the co-moving-operands hazard,
                // not a measurement. It is a reference row: read its coverage cells beside the
                // other deterministic arms, and read its ROUNDS distribution beside the
                // rubber-stamp control, which is the comparison it can actually settle.
                EntersSignTest: false,
                Note: "Demo 2's bounded discovery loop, wired in through DiscoveryLoopAdapter and run on its "
                    + "deterministic path (zero model calls). Compare its rounds-taken distribution with the "
                    + "rubber stamp's; do NOT read its coverage number as the design's loop-vs-agent headline.")
            : new CoverageArm(
                DiscoveryWorkflow, CoverageArmKind.Loop,
                Factory: null,
                AbsenceReason: DiscoveryLoopAdapter.AbsenceReason,
                Note: "The design's headline comparison. DECLARED ABSENT rather than omitted or substituted."),
    ];

    /// <summary>The arms that can actually be run here, in report order.</summary>
    public static IReadOnlyList<CoverageArm> Runnable => [.. All.Where(a => a.IsRunnable)];

    /// <summary>The arms that are declared absent, in report order.</summary>
    public static IReadOnlyList<CoverageArm> Absent => [.. All.Where(a => !a.IsRunnable)];

    /// <summary>
    /// The paired comparisons to run: every runnable arm that enters the sign test, against the live
    /// arm.
    /// </summary>
    /// <remarks>
    /// Generated, not hand-listed. The old hand-built list carried the control gate at index 0, so
    /// inserting an arm ahead of it would have re-pointed a gate at a different comparison without
    /// changing a line of gate code.
    /// </remarks>
    public static IReadOnlyList<(string Reference, string Challenger)> SignTestPairs =>
    [
        .. Runnable
            .Where(a => a.EntersSignTest && !string.Equals(a.Label, Live, StringComparison.Ordinal))
            .Select(a => (Reference: Live, Challenger: a.Label))
    ];

    /// <summary>
    /// The arm whose leading over the live agent would void the architecture claim, or null when it
    /// is not runnable.
    /// </summary>
    public static CoverageArm? PrimaryControl =>
        Runnable.FirstOrDefault(a => a.IsPrimaryControl);

    /// <summary>Looks an arm up by label, or null.</summary>
    /// <param name="label">The arm label.</param>
    public static CoverageArm? Find(string label) =>
        All.FirstOrDefault(a => string.Equals(a.Label, label, StringComparison.Ordinal));
}
