// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// SNAPSHOT-POLICY: writes            eval05_quality — the 25-point judge spread is what makes a stored baseline worth having (8.20)

using System.Globalization;
using System.Text;
using AgentEval.MAF;
using Galaxus.RecommendationAgent.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Eval 05 — Recommendation Quality. The first eval in this project whose verdict a
/// <b>criterion-based LLM judge</b> participates in, and the only one that asks the question the
/// other four deliberately do not: <i>are the recommendations any good?</i>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this eval exists, stated plainly.</b> Evals 01-04 all construct
/// <c>new MAFEvaluationHarness(verbose: false)</c> — the overload with no evaluator — so the
/// judge path is unreachable by construction. That was the right call for catalogue integrity,
/// where a dictionary lookup is a better instrument than a model. Generalised into a project-wide
/// rule it threw away most of what AgentEval is for: nothing anywhere measured whether a
/// recommendation was <i>relevant</i>, whether its "why this" was <i>useful</i>, or whether the
/// agent surfaced anything a shopper would not have found alone. This eval measures those, and it
/// does <b>not</b> replace the deterministic ones — Eval 01 still owns every claim about
/// fabrication, suppression and citation resolution.
/// </para>
/// <para>
/// <b>What the judge is given, and what it is never given.</b> The judged text is a rendered
/// ANSWER PACKET (<see cref="BuildAnswerPacket"/>) in three labelled sections: what the agent
/// wrote, what the agent presented (its own <c>PresentRecommendation</c> calls, verbatim), and the
/// reference records. The reference records come from <c>Catalogue.Default</c> and the order
/// history — never from the agent, which contributes only its own prose, its own SKUs, its own
/// reasons and its own citations. The agent supplies no input to its own bar. Prices and stock
/// levels are deliberately <b>omitted</b> from the reference records: a price the model states is
/// wrong by construction (§F.4 re-verifies both at render), so giving the judge a price to check
/// against would let a correct-looking number pass a criterion that forbids stating one at all.
/// Gift-ness is shown as the four OBSERVABLE signals, never as a derived label, for the same
/// reason the corpus has no <c>IsGift</c> field (design §0.5 / A-3): the judge must reach the
/// conclusion the way the agent had to.
/// </para>
/// <para>
/// <b>Why the recommendations are rendered into the judged text at all.</b> In this design a
/// recommendation IS a tool call and never prose (design §0.5 / D-1). <c>MAFEvaluationHarness</c>
/// hands the evaluator <c>AgentResponse.Text</c>, so judging the raw response would grade the
/// covering note and report the number as a verdict on the recommendations.
/// <see cref="PresentationPacketAgent"/> therefore reads the agent's own tool trace with the SAME
/// <c>ToolUsageExtractor</c> the harness uses, so the transcript the judge sees and the trace the
/// deterministic cross-checks read cannot disagree.
/// </para>
/// <para>
/// <b>The gate does not rest on the judge's opinion alone.</b> Three conditions, described at
/// <see cref="PrintGate"/>: a deterministic abstention discrimination (no model in that verdict at
/// all), an instrument-health check, and a SEPARATION requirement — the judge must score the live
/// agent above a degenerate popularity baseline that runs the identical path. An uncalibrated
/// judge whose number cannot separate a real agent from a bestseller list is not measuring
/// quality, and this eval says so instead of quoting the number.
/// </para>
/// <para>
/// ⏱️ Runtime: roughly 3-6 minutes. Live model calls per run: five agent turns and ten judge calls
/// (five for the agent lane, five for the control lane, which spends nothing on the arm itself).
/// </para>
/// </remarks>
public static class Eval05_RecommendationQuality
{
    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Criteria
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One judged axis. <b>Not</b> <c>AgentEval.Models.TestCase.EvaluationCriteria</c>'s element
    /// type, which is a bare <see cref="string"/>.
    /// </summary>
    /// <remarks>
    /// AgentEval's judge path has no weighted-criterion type and no per-criterion weights: criteria
    /// are plain strings, each carries equal implicit weight, and the harness reports one holistic
    /// <c>overallScore</c> plus an independent met / not-met verdict per criterion. Weighting is
    /// therefore done HERE, over <c>TestResult.CriteriaResults</c>, and the harness's own holistic
    /// number is printed beside it for contrast rather than used.
    /// </remarks>
    /// <param name="Key">Short stable name for the console table and the console only.</param>
    /// <param name="Text">
    /// The exact sentence sent to the judge. Single-line by contract — <c>ChatClientEvaluator</c>
    /// renders criteria as a numbered list, so an embedded newline would split one criterion into
    /// two and the returned verdicts would no longer line up with the declared set.
    /// </param>
    public sealed record Criterion(string Key, string Text);

    /// <summary>One criterion and the share of the case's score it carries.</summary>
    /// <param name="Criterion">The axis.</param>
    /// <param name="Weight">Share of 1.0. Every rubric's weights sum to 1.0, asserted in <see cref="Validate"/>.</param>
    public readonly record struct WeightedCriterion(Criterion Criterion, double Weight);

    /// <summary>
    /// The declared axes, and the chance floor of each — <i>what does an agent that understands
    /// nothing score here?</i>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three of these five have a HIGH floor and saying so is the point. A rubric whose degenerate
    /// agent scores near the top is a decoration, and the honest response is to name the free
    /// points, weight them accordingly, and guard them somewhere the judge cannot reach:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b><see cref="Restraint"/> — floor 1.0000 for an agent that presents
    ///   NOTHING</b>, and near 1.0 for any agent that simply never states a figure. Silence passes
    ///   it perfectly. Guarded three ways: the deterministic abstention check runs BEFORE the judge
    ///   and a required-recommendation persona that presented nothing is a hard fail whatever the
    ///   judged number says; the weight is low; and the popularity control's score on this axis is
    ///   printed, so a reader can see the points are free.</description></item>
    ///   <item><description><b><see cref="Proactivity"/> — floor near 1.0 for any agent that
    ///   presents anything at all</b> on an open-ended prompt, since anything unsolicited satisfies
    ///   "something the customer did not name". It carries the LOWEST weight for exactly that
    ///   reason. It is reported because it is the product thesis, not because it discriminates.</description></item>
    ///   <item><description><b><see cref="LatentDiscovery"/> — the mechanical half has a high floor
    ///   too.</b> A uniform draw from outside the customer's owned leaf categories lands in a new
    ///   category with probability ~1.0, which is why the sentence also requires that the pick
    ///   FOLLOW from the history. The corpus-derived expectation for a random-5 agent
    ///   (<c>ChanceFloors.RandomDrawFloor</c>) is printed per persona beside the judged score, and
    ///   Eval 02 owns the mechanical measurement.</description></item>
    ///   <item><description><b><see cref="Relevance"/> and <see cref="ExplanationQuality"/> are the
    ///   two axes that can actually separate.</b> Their floors are not derivable in closed form —
    ///   they depend on how lenient the judge is about template prose — so they are <b>MEASURED</b>
    ///   by the popularity control, whose reason string is literally "One of our most popular
    ///   products — {name}" carrying a citation that resolves. If the judge marks that MET on
    ///   <see cref="ExplanationQuality"/>, then this axis is measuring citation resolution rather
    ///   than explanation quality, and the separation gate fails rather than the number being
    ///   quoted.</description></item>
    /// </list>
    /// <para>
    /// The thin-signal rubric's floors run the other way: <see cref="AbstentionHonesty"/> and
    /// <see cref="NoInvention"/> are free for a constant refuser. They are safe because a constant
    /// refuser is deterministically failed on the four personas where recommendations are required,
    /// before any judge is consulted. <b>No constant policy passes this case set.</b>
    /// </para>
    /// </remarks>
    public static class Criteria
    {
        /// <summary>Does each pick tie to something this specific customer actually did?</summary>
        public static Criterion Relevance { get; } = new("relevance",
            "Every recommendation is tied to something this specific customer actually did — a purchase named in "
          + "the CUSTOMER RECORD, or a need the customer stated in this turn. A recommendation whose stated "
          + "reasoning would read identically for any other customer does NOT meet this criterion, even if the "
          + "product is a sensible one.");

        /// <summary>Does the "why this" cite a real signal AND a real product attribute, usefully?</summary>
        public static Criterion ExplanationQuality { get; } = new("explanation",
            "Each 'why this' names BOTH a real signal from the CUSTOMER RECORD and a real attribute of the product "
          + "as listed in its PRODUCT RECORD, and reads as something a shopper could act on. Generic marketing "
          + "filler — 'one of our most popular products', 'a great choice', 'highly rated' — does NOT meet this "
          + "criterion even when a citation is attached to it.");

        /// <summary>Did it surface something non-obvious rather than a same-category restock?</summary>
        public static Criterion LatentDiscovery { get; } = new("discovery",
            "At least one recommendation is in a product area the customer has NOT already bought from, AND the "
          + "reasoning shows why it follows from what they own. A set that is entirely more of what they already "
          + "have, or that reaches a new area with no stated connection to the history, does NOT meet this "
          + "criterion.");

        /// <summary>No overclaiming, no invented benefits, no price or stock assertions.</summary>
        public static Criterion Restraint { get; } = new("restraint",
            "No sentence states a price, a discount, a stock level or a delivery date, and no sentence attributes "
          + "a property or benefit to a product that its PRODUCT RECORD does not carry. The interface prints "
          + "verified figures separately; a figure or an unlisted benefit written by the agent is wrong by "
          + "construction.");

        /// <summary>Did it surface something the customer did not ask for? The product thesis.</summary>
        public static Criterion Proactivity { get; } = new("proactivity",
            "At least one recommendation addresses something the customer did not name in this turn — a gap in "
          + "what they own, an upcoming need, or an adjacent use — rather than only answering the literal "
          + "question asked.");

        /// <summary>Thin signal: did it say plainly that it does not know enough?</summary>
        public static Criterion AbstentionHonesty { get; } = new("abstention",
            "The answer says plainly that there is not enough on file about this customer to make a personal "
          + "recommendation, instead of presenting a confident personalised set. Hedged prose that still "
          + "recommends products does NOT meet this criterion.");

        /// <summary>Thin signal: did it ask something answerable?</summary>
        public static Criterion Clarification { get; } = new("clarification",
            "It asks at least one specific, answerable question whose answer would actually let it help — about a "
          + "use, an occasion, a constraint or a budget. A generic 'how can I help you?' does NOT meet this "
          + "criterion.");

        /// <summary>Thin signal: did it refrain from inventing a customer?</summary>
        public static Criterion NoInvention { get; } = new("no-invention",
            "It does not attribute an interest, a hobby, a household, a profession or a need to the customer that "
          + "the CUSTOMER RECORD cannot support. Inferring a lifestyle from a single low-information purchase "
          + "does NOT meet this criterion.");

        /// <summary>
        /// The rubric for a persona where recommendations are REQUIRED. Weights sum to 1.0.
        /// </summary>
        /// <remarks>
        /// The two axes that can separate carry half the weight between them; the two near-free
        /// axes carry 0.30 together, of which proactivity — the freest — carries 0.10. The weights
        /// are CHOSEN on that argument and are printed on every run, not tuned against a result.
        /// </remarks>
        public static IReadOnlyList<WeightedCriterion> Discovery { get; } =
        [
            new(Relevance, 0.25),
            new(ExplanationQuality, 0.25),
            new(LatentDiscovery, 0.20),
            new(Restraint, 0.20),
            new(Proactivity, 0.10),
        ];

        /// <summary>
        /// The rubric for the thin-signal persona, where abstention is the right answer. Weights
        /// sum to 1.0.
        /// </summary>
        /// <remarks>
        /// ⚠ <b>Which rubric applies is decided by the PERSONA, never by what the agent produced.</b>
        /// Choosing a rubric from the result — "it presented nothing, so grade it as an abstention" —
        /// is the silent-<c>{}</c> shape: applicability taken from the output instead of the input,
        /// which lets an agent pick its own easier exam by failing. The expectation is declared per
        /// case and cross-checked against the corpus in <see cref="Validate"/>.
        /// </remarks>
        public static IReadOnlyList<WeightedCriterion> ThinSignal { get; } =
        [
            new(AbstentionHonesty, 0.40),
            new(Clarification, 0.30),
            new(NoInvention, 0.20),
            new(Restraint, 0.10),
        ];
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Cases
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a correct answer looks like for a persona. Declared from the corpus, never from the run.</summary>
    public enum AnswerExpectation
    {
        /// <summary>The customer has enough on file that recommendations are owed. Silence is a MISS.</summary>
        RecommendationsRequired = 0,

        /// <summary>The signal is genuinely too thin to personalise. Presenting a confident set is a MISS.</summary>
        AbstentionCorrect = 1,
    }

    /// <summary>One judged persona.</summary>
    /// <param name="PersonaId">A customer id from <c>Personas.AllPersonaIds</c>.</param>
    /// <param name="DisplayName">For the console.</param>
    /// <param name="Expectation">Declared before the run and validated against the corpus.</param>
    /// <param name="Rubric">Which weighted axes apply.</param>
    /// <param name="Why">Why this persona is in the set — printed, so the choice is auditable.</param>
    public sealed record Case(
        string PersonaId,
        string DisplayName,
        AnswerExpectation Expectation,
        IReadOnlyList<WeightedCriterion> Rubric,
        string Why);

    /// <summary>
    /// Minimum <c>PresentRecommendation</c> calls on a persona where recommendations are required.
    /// </summary>
    /// <remarks>
    /// One, not five. The claim this arm carries is the structural one — <b>silence is never a pass
    /// on a case that had a right answer</b> — and a higher bar would start grading answer LENGTH,
    /// which is not a quality axis. The actual count is printed per case.
    /// </remarks>
    public const int MinimumPresentations = 1;

    /// <summary>Maximum presentations on the thin-signal persona.</summary>
    /// <remarks>
    /// Zero, because that is the authored gold: Luca's <c>IndependentSignalCount</c> is 0, so §F.8's
    /// abstention gate fires before any search runs and the correct turn is clarifying questions and
    /// nothing else. This is the half of the pair that stops "abstain always" from being a strategy.
    /// </remarks>
    public const int AbstentionMaximumPresentations = 0;

    /// <summary>The five judged personas.</summary>
    /// <remarks>
    /// <para>
    /// Each persona speaks its own authored utterance via <c>Personas.CanonicalPromptFor</c> rather
    /// than one shared line. Eval 02 shares an utterance because it compares ARCHITECTURES and any
    /// wording spread would be measured as architecture; this eval compares an agent against a
    /// degenerate baseline <i>on the same input</i>, so the pairing is preserved while each persona
    /// keeps the trap it was authored to carry.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Case> Cases { get; } =
    [
        new(Personas.NadiaUserId, "Nadia Brunner", AnswerExpectation.RecommendationsRequired,
            Criteria.Discovery,
            "The latent-interest case. Five purchases across three departments whose only shared signal is use "
          + "context; nothing lexical joins a power bank, a headlamp and a merino layer."),

        new(Personas.MarcoUserId, "Marco Iten", AnswerExpectation.RecommendationsRequired,
            Criteria.Discovery,
            "THE GIFT TRAP. His two most recent and most valuable purchases are gifts. Every naive strategy "
          + "answers 'Pro Controller'; he does not own a console. Relevance and explanation quality are where "
          + "that shows up as a judged number rather than as a suppression count."),

        new(Personas.SofiaUserId, "Sofia Keller", AnswerExpectation.RecommendationsRequired,
            Criteria.Discovery,
            "Replenishment plus the capability gap. Recommending the cartridges she has bought five times is "
          + "not a recommendation; the non-obvious answer is the grinder she does not own."),

        new(Personas.JonasUserId, "Jonas Vogt", AnswerExpectation.RecommendationsRequired,
            Criteria.Discovery,
            "The SECOND gift trap, run the other way round: he OWNS the console Marco was given, and his own two "
          + "gift lines are camera gear. A blanket 'ignore gaming' policy passes Marco and fails here."),

        new(Personas.LucaUserId, "Luca Ferrari", AnswerExpectation.AbstentionCorrect,
            Criteria.ThinSignal,
            "THE THIN-SIGNAL CASE. One purchase, a USB-C cable. The right answer is partly abstention — and an "
          + "eval that scores silence as a pass everywhere would be broken, which is why the other four "
          + "personas fail on silence and this one fails on confidence."),
    ];

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Results
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A criterion the judge returned that could not be joined to any declared axis — with the
    /// evidence a reader needs to tell WHOSE fault that is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two very different things used to print as one line.</b> A judge that graded an entirely
    /// different rubric is a grading fault on the judge's side. A judge that echoed OUR criterion
    /// back in a surface form our matcher did not recognise is a join fault on OUR side, and the
    /// grades it produced are perfectly good ones we threw away. MEASURED on 2026-09-05: all 24
    /// "criteria nobody declared" across 3 cells were the second kind, and the eval reported them
    /// as the first.
    /// </para>
    /// <para>
    /// <see cref="OverlapChars"/> is how many leading characters the returned text shares with the
    /// nearest declared criterion after normalisation. Long overlap and no match means the join
    /// broke; near-zero overlap means the judge really did answer something else.
    /// </para>
    /// </remarks>
    /// <param name="Returned">The criterion string the judge sent back, verbatim.</param>
    /// <param name="NearestKey">The declared criterion it most resembles, or null when it resembles none.</param>
    /// <param name="OverlapChars">Leading characters shared with that criterion, after normalisation.</param>
    public sealed record UnjoinedCriterion(string Returned, string? NearestKey, int OverlapChars)
    {
        /// <summary>
        /// True when the returned text substantially IS a declared criterion that the matcher
        /// failed to join — our defect, not the judge's.
        /// </summary>
        public bool LooksLikeAJoinFailure => OverlapChars >= JoinFailureOverlapChars;

        /// <summary>How this row should be read, in one phrase.</summary>
        public string Diagnosis =>
            LooksLikeAJoinFailure
                ? $"JOIN FAILURE on our side — {OverlapChars} leading chars are shared with the declared "
                + $"'{NearestKey}' criterion, so the judge answered OUR rubric and the matcher did not recognise it"
                : NearestKey is null
                    ? "INVENTED — it shares no leading text with any declared criterion"
                    : $"INVENTED — its nearest declared criterion ('{NearestKey}') shares only {OverlapChars} leading char(s)";
    }

    /// <summary>
    /// How many shared leading characters make an unjoined criterion a JOIN failure rather than an
    /// invention. Well under <see cref="PrefixMatchLength"/>, so anything the matcher itself would
    /// have accepted is far above it.
    /// </summary>
    public const int JoinFailureOverlapChars = 16;

    /// <summary>One judged criterion after the declared set has been reconciled with what came back.</summary>
    /// <param name="Criterion">The declared axis.</param>
    /// <param name="Weight">Its weight in this case's rubric.</param>
    /// <param name="Met">The judge's verdict, or null when the judge returned no verdict for it.</param>
    /// <param name="Explanation">The judge's reasoning, verbatim.</param>
    public sealed record JudgedCriterion(Criterion Criterion, double Weight, bool? Met, string Explanation);

    /// <summary>One persona, one arm.</summary>
    /// <param name="Case">The persona.</param>
    /// <param name="Arm">"agent" or "popularity".</param>
    /// <param name="Presentations">How many <c>PresentRecommendation</c> calls the turn made.</param>
    /// <param name="EvidenceResolved">How many of those carried a citation that resolves against the catalogue.</param>
    /// <param name="Judged">Per-criterion verdicts, in declared order.</param>
    /// <param name="ExtraCriteria">Criteria the judge returned that could not be joined, each with its diagnosis.</param>
    /// <param name="HolisticScore">The harness's own <c>overallScore</c>. Reported for contrast, never used.</param>
    /// <param name="Summary">The judge's summary sentence.</param>
    /// <param name="DurationMs">Wall time of the turn.</param>
    /// <param name="PromptTokens">Agent-turn prompt tokens, when the provider reported them.</param>
    /// <param name="CompletionTokens">Agent-turn completion tokens, when the provider reported them.</param>
    /// <param name="CostUsd">Agent-turn estimated cost. The judge call is NOT in this figure.</param>
    /// <param name="Error">A harness-level exception, when the turn threw.</param>
    public sealed record Row(
        Case Case,
        string Arm,
        int Presentations,
        int EvidenceResolved,
        IReadOnlyList<JudgedCriterion> Judged,
        IReadOnlyList<UnjoinedCriterion> ExtraCriteria,
        int HolisticScore,
        string Summary,
        double DurationMs,
        int? PromptTokens,
        int? CompletionTokens,
        decimal? CostUsd,
        string? Error)
    {
        /// <summary>
        /// The weighted quality score, 0-100, over the DECLARED rubric.
        /// </summary>
        /// <remarks>
        /// The denominator is always the declared weight total, never "the criteria that came back".
        /// A judge that silently drops a criterion must not thereby shrink the exam — that is the
        /// diluted-denominator shape, and it fails in the flattering direction. A missing verdict
        /// scores zero here AND trips <see cref="InstrumentFailed"/>, so the number is never quoted
        /// on its own.
        /// </remarks>
        public double WeightedScore =>
            Judged.Sum(j => j.Met == true ? j.Weight : 0.0) * 100.0;

        /// <summary>True when the judge did not return a verdict for every declared criterion, or invented one.</summary>
        public bool InstrumentFailed => Error is not null || Judged.Any(j => j.Met is null) || ExtraCriteria.Count > 0;

        /// <summary>
        /// The deterministic half of the verdict: did the turn do the right STRUCTURAL thing?
        /// </summary>
        /// <remarks>
        /// No model participates in this property. It is the arm that makes "silence is never a pass"
        /// and "abstain always is not a strategy" both true at once.
        /// </remarks>
        public bool AbstentionCorrect => Case.Expectation switch
        {
            AnswerExpectation.RecommendationsRequired => Presentations >= MinimumPresentations,
            AnswerExpectation.AbstentionCorrect => Presentations <= AbstentionMaximumPresentations,
            _ => false,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Entry point
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Runs the eval.</summary>
    /// <param name="dryRun">
    /// Run every case against a stub agent AND a stub judge. Spends nothing, writes nothing, and
    /// exercises the real harness, the real judge parser and the real weighting. The first of this
    /// repository's three run stages.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 0 when the gate passed, 1 when it failed, 3 when credentials are missing and nothing was
    /// measured. ⚠ The <c>ci</c> parameter is GONE — see <see cref="CredentialGuard"/>.
    /// </returns>
    public static async Task<int> RunAsync(bool dryRun = false, CancellationToken ct = default)
    {
        PrintHeader();

        try
        {
            Validate();
        }
        catch (InvalidOperationException ex)
        {
            EvalPrinter.PrintRefusal("Eval 05 refused to run.", ex.Message);
            return 1;
        }

        PrintRubric();
        PrintDerivedFloors();

        // ⚠️ HONESTY GATE. This eval needs a model on BOTH sides — the agent turn and the judge — so
        // with no credentials there is nothing to report. It must not fall back to a deterministic
        // arm and print that number as if it were the agent: that is the exact confusion this eval
        // was added to fix. Routed through CredentialGuard so the rule lives in ONE file.
        if (CredentialGuard.Blocks(
                "Eval 05", "Recommendation quality", dryRun,
                "Every number here needs a live agent turn AND a live judge. The popularity control",
                "would run without a key, and its score under a heading reading \"recommendation",
                "quality\" would be the degenerate baseline reported as the agent.")
            is { } noCredentials)
        {
            return noCredentials;
        }

        if (dryRun) PrintDryRunBanner();
        else { Config.PrintAzureTarget(); Console.WriteLine(); }

        await EvalRuntime.EnsureBoundAsync(ct).ConfigureAwait(false);

        // ── The judge. A REAL evaluator client, wired through the harness overload that builds a
        //    ChatClientEvaluator internally — the path Evals 01-04 leave unreachable.
        StubJudgeClient? stubJudge = dryRun ? new StubJudgeClient() : null;
        IChatClient evaluatorClient = dryRun
            ? stubJudge!
            : new Azure.AI.OpenAI.AzureOpenAIClient(Config.Endpoint, Config.KeyCredential)
                .GetChatClient(Config.Model).AsIChatClient();

        var harness = new MAFEvaluationHarness(evaluatorClient, verbose: false);

        var options = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            EvaluateResponse = true,     // ⭐ the judge branch. Unreachable in Evals 01-04 by construction.
            Verbose = false,
            ModelName = Config.Model,    // required, or PerformanceMetrics.EstimatedCost stays null
        };

        // The live read-only surface, built once; the SESSION is what must be fresh per case.
        ChatClientAgent? liveAgent = dryRun ? null : RecommendationAgentFactory.Create();

        var agentRows = new List<Row>();
        var controlRows = new List<Row>();

        foreach (Case testCase in Cases)
        {
            PrintCaseHeader(testCase);

            IEvaluableAgent agentArm = dryRun
                ? new MAFAgentAdapter(RecommendationAgentFactory.Create(StubAgentFor(testCase)))
                : new MAFAgentAdapter(liveAgent!);

            Row agentRow = await RunArmAsync(testCase, agentArm, "agent", harness, options, ct)
                .ConfigureAwait(false);
            agentRows.Add(agentRow);
            PrintRow(agentRow);

            // ── The degenerate arm, down the IDENTICAL path: same packet renderer, same harness,
            //    same criteria, same judge. A control that took a different route would prove
            //    nothing about this one. It makes no model call of its own; only its judge call costs.
            Row controlRow = await RunArmAsync(
                testCase, new Broken04_PopularityAgent(), "popularity", harness, options, ct)
                .ConfigureAwait(false);
            controlRows.Add(controlRow);
            PrintRow(controlRow);
        }

        PrintComparison(agentRows, controlRows);
        PrintCost(agentRows, controlRows, dryRun);

        if (dryRun)
        {
            bool held = PrintDryRunVerdict(agentRows, controlRows, stubJudge!);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  📁 Nothing written — a dry run must not leave a result behind.");
            Console.ResetColor();
            return held ? 0 : 1;
        }

        bool gatePassed = PrintGate(agentRows, controlRows);
        PersistRun(agentRows, controlRows, gatePassed);
        return gatePassed ? 0 : 1;
    }

    /// <summary>
    /// The measured re-grade spread of this eval's own judge on ONE fixed input, in points.
    /// </summary>
    /// <remarks>
    /// 45/30/35/55/35 on five re-grades of one unchanged answer (<c>SUITE_SUMMARY</c> §18.1). It is
    /// stored beside every score this eval persists, because it is the bound on all of them: a
    /// reader holding the numbers without it will over-read a difference smaller than the noise —
    /// and this eval's own headline margin is +20.
    /// </remarks>
    public const int MeasuredJudgeSpreadPoints = 25;

    /// <summary>Writes the run's record. Model-backed, so it never runs on the dry-run path.</summary>
    /// <remarks>
    /// <para>
    /// <b>Plan item 8.20.</b> This eval persisted nothing and said nothing about it. Eval 08 also
    /// persists nothing and states its reason in code — nothing consumes a stability snapshot, and
    /// a number in a shared store that no gate reads is a hazard. That argument does NOT transfer
    /// here: this is the eval with a 25-point judge spread, so a single run of it cannot be told
    /// apart from noise without a stored baseline to put beside it.
    /// </para>
    /// <para>
    /// ⚠ It is a RECORD, not a gate, and nothing reads it. The flag that says a judge left a
    /// declared criterion unanswered travels in the same row as the score, because correction ⑫
    /// was three cells scoring 0.0 as an artefact of the instrument and nothing on the number
    /// saying so.
    /// </para>
    /// </remarks>
    /// <param name="agentRows">The agent arm.</param>
    /// <param name="controlRows">The popularity control arm.</param>
    /// <param name="gatePassed">The gate's verdict.</param>
    private static void PersistRun(
        IReadOnlyList<Row> agentRows, IReadOnlyList<Row> controlRows, bool gatePassed)
    {
        static QualityCellSnapshot Cell(Row row) => new(
            row.Case.PersonaId,
            row.Arm,
            row.WeightedScore,
            row.HolisticScore,
            row.InstrumentFailed,
            row.Presentations,
            row.EvidenceResolved,
            row.ExtraCriteria.Count,
            row.CostUsd,
            row.Error);

        var all = agentRows.Concat(controlRows).ToList();

        EvalResultStore.SaveQuality(EvalResultStore.QualityKey, new QualitySnapshot
        {
            Label = "Eval 05 — Judged Recommendation Quality",
            Cells = [.. all.Select(Cell)],
            GatePassed = gatePassed,
            InstrumentFailures = all.Count(r => r.InstrumentFailed),
            JudgeModel = Config.Model,
            JudgeSpreadPoints = MeasuredJudgeSpreadPoints,
        });

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 Snapshot saved → {EvalResultStore.StorageLocation}");
        Console.WriteLine($"     ⚠ a RECORD, not a baseline: this judge's re-grade spread on one fixed input is "
                        + $"{MeasuredJudgeSpreadPoints} points, which bounds every score in the file.");
        Console.ResetColor();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  One arm, one persona
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs one persona against one arm and reconciles the judge's verdicts with the declared rubric.
    /// </summary>
    /// <remarks>
    /// Public so a future negative-control eval can drive the identical path with a scripted agent.
    /// </remarks>
    /// <param name="testCase">The persona.</param>
    /// <param name="arm">The agent under test, or a control.</param>
    /// <param name="armName">Label for the report.</param>
    /// <param name="harness">A harness constructed WITH an evaluator client.</param>
    /// <param name="options">Evaluation options with <c>EvaluateResponse = true</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Row> RunArmAsync(
        Case testCase,
        IEvaluableAgent arm,
        string armName,
        MAFEvaluationHarness harness,
        EvaluationOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(harness);

        var packetAgent = new PresentationPacketAgent(arm, testCase.PersonaId);

        var harnessCase = new TestCase
        {
            Name = $"Eval05 {testCase.PersonaId} [{armName}]",
            Input = GalaxusEvalPrompt.For(testCase.PersonaId, Personas.CanonicalPromptFor(testCase.PersonaId)),
            EvaluationCriteria = [.. testCase.Rubric.Select(w => w.Criterion.Text)],

            // PassingScore is set to the floor and TestResult.Passed is IGNORED. With criteria
            // supplied the harness sets Passed from the judge's holistic overallScore; this eval's
            // verdict is the weighted per-criterion score plus two checks the judge does not touch.
            PassingScore = 0,
        };

        TestResult result;
        using (EvalRuntime.BeginTurn())
        {
            result = await harness.RunEvaluationAsync(packetAgent, harnessCase, options, ct).ConfigureAwait(false);
        }

        var presented = packetAgent.Presented;
        int resolved = presented.Count(p =>
            Catalogue.Default.TryGet(p.Sku, out var product) && product is not null
            && CatalogueIntegrityGrader.ResolvesEvidence(p.Evidence, product, out _));

        var (judged, extra) = Reconcile(testCase.Rubric, result.CriteriaResults);

        return new Row(
            testCase,
            armName,
            presented.Count,
            resolved,
            judged,
            extra,
            result.Score,
            result.Details ?? string.Empty,
            result.Performance?.TotalDuration.TotalMilliseconds ?? 0,
            result.Performance?.PromptTokens,
            result.Performance?.CompletionTokens,
            result.Performance?.EstimatedCost,
            result.Error?.Message);
    }

    /// <summary>
    /// Joins what the judge returned back onto the DECLARED rubric.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not a zip.</b> <c>CriterionResult.Criterion</c> is a string the judge echoed,
    /// and a model re-wraps, re-cases or truncates it freely. Matching is exact first, then on
    /// whitespace-normalised text, then on a normalised prefix — and anything still unmatched is
    /// reported rather than positionally guessed.
    /// </para>
    /// <para>
    /// <b>An unmatched criterion is never a free pass and never a silent drop.</b> A declared
    /// criterion with no verdict scores zero AND sets <c>InstrumentFailed</c>; a returned criterion
    /// nobody declared is recorded in <c>ExtraCriteria</c> and sets it too.
    /// <c>MAFEvaluationHarness</c> discards <c>EvaluationResult.EvaluationFailed</c>, so a judge
    /// parse failure reaches this eval as <c>Score = 50</c> with an EMPTY criteria list and no flag —
    /// which this reconciliation detects as "no verdict for any declared criterion". That is the
    /// guard that keeps a 50 from being read as a grade.
    /// </para>
    /// </remarks>
    /// <param name="rubric">The declared axes and weights.</param>
    /// <param name="returned">What the judge sent back. Null is treated as empty.</param>
    public static (IReadOnlyList<JudgedCriterion> Judged, IReadOnlyList<UnjoinedCriterion> Extra) Reconcile(
        IReadOnlyList<WeightedCriterion> rubric,
        IReadOnlyList<CriterionResult>? returned)
    {
        ArgumentNullException.ThrowIfNull(rubric);

        var pool = new List<CriterionResult>(returned ?? []);
        var judged = new List<JudgedCriterion>(rubric.Count);

        foreach (WeightedCriterion declared in rubric)
        {
            string want = Normalise(declared.Criterion.Text);

            int index = pool.FindIndex(c => string.Equals(c.Criterion, declared.Criterion.Text, StringComparison.Ordinal));
            if (index < 0) index = pool.FindIndex(c => Normalise(c.Criterion) == want);
            if (index < 0) index = pool.FindIndex(c => PrefixMatches(Normalise(c.Criterion), want));

            if (index < 0)
            {
                judged.Add(new JudgedCriterion(declared.Criterion, declared.Weight, null,
                    "The judge returned no verdict for this criterion. Recorded as an INSTRUMENT FAILURE, "
                  + "not as a fail and never as a pass."));
                continue;
            }

            CriterionResult match = pool[index];
            pool.RemoveAt(index);
            judged.Add(new JudgedCriterion(declared.Criterion, declared.Weight, match.Met, match.Explanation));
        }

        return (judged, [.. pool.Select(c => Diagnose(c.Criterion, rubric))]);
    }

    /// <summary>
    /// Says which declared criterion an unjoined string most resembles, and by how much — so a
    /// broken join is never printed as a judge that invented a rubric.
    /// </summary>
    /// <param name="returned">The criterion string the judge sent back.</param>
    /// <param name="rubric">The declared axes.</param>
    public static UnjoinedCriterion Diagnose(string? returned, IReadOnlyList<WeightedCriterion> rubric)
    {
        ArgumentNullException.ThrowIfNull(rubric);

        // Normalise already un-renders the ordinal; stripping twice would eat real text.
        string text = returned ?? "";
        string mine = Normalise(text);

        string? nearest = null;
        int best = 0;

        foreach (WeightedCriterion declared in rubric)
        {
            string theirs = Normalise(declared.Criterion.Text);
            int shared = 0;
            int limit = Math.Min(mine.Length, theirs.Length);
            while (shared < limit && mine[shared] == theirs[shared]) shared++;

            if (shared > best) { best = shared; nearest = declared.Criterion.Key; }
        }

        return new UnjoinedCriterion(text, nearest, best);
    }

    private const int PrefixMatchLength = 48;

    private static bool PrefixMatches(string a, string b)
    {
        int length = Math.Min(PrefixMatchLength, Math.Min(a.Length, b.Length));
        return length >= PrefixMatchLength
            && string.Equals(a[..length], b[..length], StringComparison.Ordinal);
    }

    /// <summary>
    /// Whitespace-normalises and lower-cases, then removes ONE leading enumeration marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>THIS IS UN-RENDERING OUR OWN PROMPT, NOT GUESSING.</b>
    /// <c>src/AgentEval.Core/Core/ChatClientEvaluator.cs:46</c> builds the criteria block as
    /// <c>string.Join("\n", criteria.Select((c, i) =&gt; $"{i + 1}. {c}"))</c> — it prepends the
    /// ordinal itself. A judge that echoes back exactly what it was shown returns
    /// <c>"1. Every recommendation is tied to…"</c> where the rubric holds
    /// <c>"Every recommendation is tied to…"</c>, and a three-character offset defeats the exact
    /// match, the normalised match and the 48-character prefix match alike.
    /// </para>
    /// <para>
    /// <b>MEASURED, 2026-09-05 run, <c>34-eval05-quality-judged.log</c>:</b> 24 lines reading "the
    /// judge returned a criterion nobody declared", on 3 of 10 judged cells — every one of them one
    /// of this eval's own five Discovery criteria carrying the ordinal the evaluator printed. The
    /// judge did not invent a rubric. It answered ours and we failed to recognise our own text.
    /// USR-NB-01's SEPARATION failure is downstream of exactly this.
    /// </para>
    /// <para>
    /// <b>Deliberately narrow.</b> ONE marker, only at the start, only the forms a list renderer
    /// produces: <c>1.</c> <c>1)</c> <c>(1)</c> <c>a.</c> <c>-</c> <c>*</c> <c>•</c> <c>#1</c>.
    /// Everything after it is matched as TEXT, exactly as before. Nothing here matches by position,
    /// and a criterion that is genuinely different stays unjoined and is reported as such — see
    /// <see cref="Diagnose"/>.
    /// </para>
    /// </remarks>
    /// <param name="normalised">Text that has already been through <c>Normalise</c>.</param>
    public static string StripEnumeration(string normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);

        int i = 0;
        if (i < normalised.Length && (normalised[i] == '(' || normalised[i] == '#')) i++;

        int labelStart = i;
        while (i < normalised.Length && (char.IsAsciiDigit(normalised[i]) || char.IsAsciiLetterLower(normalised[i]))) i++;
        int labelLength = i - labelStart;

        // A bullet: no label at all, just the mark.
        if (labelLength == 0 && labelStart == 0 && normalised.Length > 1
            && (normalised[0] == '-' || normalised[0] == '*' || normalised[0] == '•')
            && normalised[1] == ' ')
        {
            return normalised[2..];
        }

        // A label has to be SHORT — "1", "12", "a", "iv". Anything longer is a word, and stripping
        // a word is how a normaliser starts inventing matches.
        if (labelLength is < 1 or > 3) return normalised;

        while (i < normalised.Length && (normalised[i] == '.' || normalised[i] == ')' || normalised[i] == ':' || normalised[i] == '-')) i++;
        if (i == labelStart + labelLength) return normalised;      // no separator: not an enumeration
        while (i < normalised.Length && normalised[i] == ' ') i++;

        return i >= normalised.Length ? normalised : normalised[i..];
    }

    private static string Normalise(string? text) =>
        text is null
            ? string.Empty
            : StripEnumeration(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant());

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  The answer packet
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Renders what the judge reads: the agent's words, the agent's recommendations, and the
    /// reference records the claims are checkable against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sections 1 and 2 are the agent's own work and are the only things under judgement. Section 3
    /// is corpus fact — the agent influences it only by choosing which SKUs to present.
    /// </para>
    /// <para>
    /// <b>Prices and stock levels are deliberately absent</b> from section 3. Both are live facts
    /// the model is structurally barred from stating (§F.4 re-verifies them at render), so a stated
    /// figure is wrong whether or not it happens to match. Supplying one would let the judge mark a
    /// lucky guess as acceptable on the restraint axis.
    /// </para>
    /// <para>
    /// <b>Gift-ness is shown as the four observable signals, never as a label</b>, for the reason
    /// the corpus itself has no <c>IsGift</c> field: a judge handed the conclusion is not checking
    /// whether the agent reached it.
    /// </para>
    /// </remarks>
    /// <param name="prose">The agent's own response text.</param>
    /// <param name="presented">The agent's own <c>PresentRecommendation</c> calls.</param>
    /// <param name="customerId">Whose turn it was.</param>
    public static string BuildAnswerPacket(
        string? prose, IReadOnlyList<PresentedCall> presented, string customerId)
    {
        ArgumentNullException.ThrowIfNull(presented);

        var catalogue = Catalogue.Default;
        var sb = new StringBuilder();

        sb.AppendLine("=== SECTION 1 — WHAT THE AGENT WROTE (the agent's own words; UNDER JUDGEMENT) ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(prose) ? "(the agent wrote nothing)" : prose.Trim());
        sb.AppendLine();

        sb.AppendLine("=== SECTION 2 — WHAT THE AGENT RECOMMENDED (its own tool calls, verbatim; UNDER JUDGEMENT) ===");
        if (presented.Count == 0)
        {
            sb.AppendLine("(the agent recommended no products in this turn)");
        }
        else
        {
            int index = 1;
            foreach (PresentedCall call in presented)
            {
                string name = catalogue.TryGet(call.Sku, out var found) && found is not null
                    ? found.Name
                    : "NOT IN THE CATALOGUE — no record exists for this id";
                sb.AppendLine(CultureInfo.InvariantCulture, $"[{index++}] sku {call.Sku} — {name}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    why this: \"{call.Reason}\"");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    citation: {call.Evidence}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("=== SECTION 3 — REFERENCE RECORDS (from the catalogue and the order history; NOT written by ");
        sb.AppendLine("    the agent and NOT under judgement. Prices, discounts and stock levels are deliberately ");
        sb.AppendLine("    omitted: the agent may not state them at all, so there is nothing here to check one against.) ===");

        var profile = UserProfiles.Find(customerId);
        sb.AppendLine(CultureInfo.InvariantCulture, $"CUSTOMER RECORD {customerId}");
        if (profile is null)
        {
            sb.AppendLine("  (no history on file)");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  market {profile.Market}, language {profile.Language}, "
              + $"personalization {(profile.PersonalizationEnabled ? "on" : "OFF")}");
            foreach (var purchase in profile.Purchases)
            {
                var bought = catalogue.Find(purchase.ProductId);
                string leaf = bought?.LeafCategory ?? "unknown category";
                var signals = new List<string>();
                if (purchase.WasGiftWrapped) signals.Add("gift-wrapped");
                if (purchase.ShippedToAlternateAddress) signals.Add("shipped to another address");
                if (purchase.HasGiftMessage) signals.Add("gift message attached");
                if (!purchase.HasOwnReview) signals.Add("never reviewed by this customer");

                string line = string.Create(CultureInfo.InvariantCulture,
                    $"  {purchase.Id}: {bought?.Name ?? purchase.ProductId} [{leaf}] "
                  + $"x{purchase.Quantity} on {purchase.PurchasedOn:yyyy-MM-dd}");
                if (signals.Count > 0) line += $" — observed: {string.Join("; ", signals)}";
                sb.AppendLine(line);
            }
            sb.AppendLine("  (the four observations above are raw signals, not a conclusion. Deciding what they mean "
                        + "is part of what is being judged.)");
        }
        sb.AppendLine();

        foreach (string sku in presented.Select(p => p.Sku).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!catalogue.TryGet(sku, out var product) || product is null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"PRODUCT RECORD {sku}: NOT IN THE CATALOGUE. No record exists, so no claim about it is supported.");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"PRODUCT RECORD {product.Id} — {product.Name} by {product.Brand}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  category: {string.Join(" > ", product.CategoryPath)}");
            foreach (var (key, value) in product.Specs)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  spec — {key}: {value}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  tags: {string.Join(", ", product.Tags)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  description: {product.Description}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Wraps any evaluable agent so the judge reads the RECOMMENDATIONS, not just the covering note.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recommendation channel in this design is the <c>PresentRecommendation</c> tool, never
    /// prose (design §0.5 / D-1). <c>MAFEvaluationHarness</c> passes <c>AgentResponse.Text</c> to
    /// the evaluator, so an unwrapped run would grade the covering note and print the number under a
    /// heading about recommendation quality.
    /// </para>
    /// <para>
    /// It reads the trace with <c>AgentEval.Core.ToolUsageExtractor</c> — the same extractor the
    /// harness itself uses a moment later — so the transcript the judge sees and the trace the
    /// deterministic cross-checks read are built from one function and cannot drift apart.
    /// <c>RawMessages</c> is passed through untouched, so the harness's own tool extraction,
    /// performance metrics and any downstream assertion still see exactly what the agent produced.
    /// </para>
    /// </remarks>
    public sealed class PresentationPacketAgent : IEvaluableAgent
    {
        private readonly IEvaluableAgent _inner;
        private readonly string _customerId;

        /// <summary>Wraps an agent.</summary>
        /// <param name="inner">The agent or control under test.</param>
        /// <param name="customerId">Whose records the packet carries.</param>
        public PresentationPacketAgent(IEvaluableAgent inner, string customerId)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
            _inner = inner;
            _customerId = customerId;
        }

        /// <inheritdoc/>
        public string Name => _inner.Name;

        /// <summary>The recommendations the wrapped agent made on its last turn.</summary>
        public IReadOnlyList<PresentedCall> Presented { get; private set; } = [];

        /// <summary>The packet handed to the judge on the last turn. Kept so a run can be audited.</summary>
        public string LastPacket { get; private set; } = string.Empty;

        /// <inheritdoc/>
        public async Task<AgentEval.Core.AgentResponse> InvokeAsync(
            string prompt, CancellationToken cancellationToken = default)
        {
            // Fully qualified: this file also uses Microsoft.Agents.AI, which owns a DIFFERENT
            // AgentResponse. The two are unrelated types with the same name.
            AgentEval.Core.AgentResponse response =
                await _inner.InvokeAsync(prompt, cancellationToken).ConfigureAwait(false);

            Presented = PresentedCall.FromToolUsage(ToolUsageExtractor.Extract(response.RawMessages));
            LastPacket = BuildAnswerPacket(response.Text, Presented, _customerId);

            return new AgentEval.Core.AgentResponse
            {
                Text = LastPacket,
                RawMessages = response.RawMessages,
                TokenUsage = response.TokenUsage,
                ModelId = response.ModelId,
                FinishReason = response.FinishReason,
                AdditionalProperties = response.AdditionalProperties,
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Validation — the case set must still agree with the corpus
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Refuses the run when the declared cases no longer match the corpus they were authored against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load-bearing check is the third one. <see cref="AnswerExpectation.AbstentionCorrect"/> is
    /// a claim about the CORPUS — "this customer's history genuinely cannot support a personal
    /// recommendation" — and it is verified by deriving the gold interest map and requiring it to be
    /// empty. If a later edit gives Luca a second purchase, this eval refuses to run rather than
    /// scoring an abstention that has stopped being correct. The same rule in reverse guards the
    /// four required personas: each must have a NON-empty latent gold set, so "recommendations were
    /// owed" is a corpus fact and not a typed assertion.
    /// </para>
    /// <para>
    /// The gift trap is checked the same way — by deriving it, from a gift line the intent
    /// classifier actually excluded, rather than by trusting the comment that says Marco is the
    /// gift-trap persona.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The case set and the corpus disagree.</exception>
    public static void Validate()
    {
        foreach (var rubric in new[] { Criteria.Discovery, Criteria.ThinSignal })
        {
            double total = rubric.Sum(w => w.Weight);
            if (Math.Abs(total - 1.0) > 1e-9)
            {
                throw new InvalidOperationException(
                    $"A rubric's weights sum to {total:F4}, not 1.0. A weighted score whose weights do not sum to "
                  + "one is not on the scale it is printed on.");
            }

            foreach (var weighted in rubric)
            {
                if (weighted.Criterion.Text.Contains('\n', StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Criterion '{weighted.Criterion.Key}' contains a newline. ChatClientEvaluator renders "
                      + "criteria as a numbered list, so an embedded newline splits one criterion into two and the "
                      + "returned verdicts stop lining up with the declared set.");
                }
            }

            if (rubric.Select(w => w.Criterion.Text).Distinct(StringComparer.Ordinal).Count() != rubric.Count)
            {
                throw new InvalidOperationException(
                    "A rubric declares the same criterion text twice. Reconciliation matches on text, so duplicates "
                  + "would let one verdict satisfy two weights.");
            }
        }

        int abstentionCases = 0;
        int giftTrapCases = 0;

        foreach (Case testCase in Cases)
        {
            if (!Personas.AllPersonaIds.Contains(testCase.PersonaId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{testCase.PersonaId}' is not an authored persona. The case set has drifted from the corpus.");
            }

            GoldInterestMap gold = InterestMapGold.Derive(testCase.PersonaId);

            switch (testCase.Expectation)
            {
                case AnswerExpectation.AbstentionCorrect:
                    abstentionCases++;
                    if (!gold.LatentIsEmpty)
                    {
                        throw new InvalidOperationException(
                            $"{testCase.PersonaId} is declared as the thin-signal case, but its derived latent gold "
                          + $"set is NOT empty ({gold.Latent.Count} token(s)). Abstention has stopped being the "
                          + "right answer for this customer, so scoring it as correct would reward a miss.");
                    }
                    break;

                case AnswerExpectation.RecommendationsRequired:
                    if (gold.LatentIsEmpty)
                    {
                        throw new InvalidOperationException(
                            $"{testCase.PersonaId} is declared as requiring recommendations, but its derived latent "
                          + "gold set is EMPTY. There is nothing on file to recommend from, so failing the agent for "
                          + "silence here would be failing it for being right.");
                    }
                    if (gold.ExcludedPurchaseIds.Any(id => id.Contains("(gift)", StringComparison.Ordinal)))
                        giftTrapCases++;
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled expectation on {testCase.PersonaId}.");
            }
        }

        if (abstentionCases != 1)
        {
            throw new InvalidOperationException(
                $"Exactly one thin-signal case is required and {abstentionCases} were declared. Without it, "
              + "'never abstain' is an undetected winning strategy; with more than one, the four-persona "
              + "separation arithmetic printed in the floors block is wrong.");
        }

        if (giftTrapCases == 0)
        {
            throw new InvalidOperationException(
                "No persona in this set has a purchase the intent classifier excluded as a gift, so the gift trap — "
              + "the case where relevance and explanation quality actually bite — is not being exercised.");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Gate
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Prints and returns the gate: three conditions, of which only one is the judge's opinion.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><description><b>Abstention discrimination — deterministic.</b> Every persona owed
    ///   recommendations produced at least one, and the thin-signal persona produced none. No model
    ///   participates. A constant "always present" policy scores 4 of 5; a constant "never present"
    ///   policy scores 1 of 5. <b>No constant policy passes, so the chance floor of this arm is
    ///   0.0000.</b></description></item>
    ///   <item><description><b>Instrument health.</b> Every declared criterion came back with a
    ///   verdict on every judged case, no criterion was invented, and no turn threw. A judge that
    ///   dropped criteria would otherwise shrink the exam silently.</description></item>
    ///   <item><description><b>Separation — the judge's opinion, made falsifiable.</b> On every
    ///   persona owed recommendations the agent must score strictly above the popularity control on
    ///   the same rubric, same packet, same judge. This is the only reason the judged number is
    ///   quotable at all: it is uncalibrated (no gold set, no inter-rater agreement, no calibration
    ///   run), so what makes it informative is that it DISCRIMINATES, not that it is high. <b>Chance
    ///   floor: a judge answering at random and never tying separates four personas in the right
    ///   direction with probability 0.5^4 = 0.0625.</b> That is the weakest link in this gate and it
    ///   is stated rather than buried. Strictly-greater, with no margin: a margin would be a number
    ///   tuned against a result.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="agentRows">The agent lane.</param>
    /// <param name="controlRows">The popularity lane, one row per persona.</param>
    public static bool PrintGate(IReadOnlyList<Row> agentRows, IReadOnlyList<Row> controlRows)
    {
        ArgumentNullException.ThrowIfNull(agentRows);
        ArgumentNullException.ThrowIfNull(controlRows);

        bool abstention = agentRows.All(r => r.AbstentionCorrect);
        bool instrument = agentRows.Concat(controlRows).All(r => !r.InstrumentFailed);

        var required = agentRows.Where(r => r.Case.Expectation == AnswerExpectation.RecommendationsRequired).ToList();
        var separated = required
            .Select(a => (Row: a, Control: controlRows.FirstOrDefault(c =>
                string.Equals(c.Case.PersonaId, a.Case.PersonaId, StringComparison.Ordinal))))
            .ToList();
        bool separation = separated.Count > 0
            && separated.All(p => p.Control is not null && p.Row.WeightedScore > p.Control.WeightedScore);

        bool passed = abstention && instrument && separation;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── GATE — three conditions, of which one is the judge's opinion ──────");
        Console.ResetColor();

        Line(abstention,
            $"ABSTENTION DISCRIMINATION (deterministic, no model in this verdict): "
          + $"{agentRows.Count(r => r.AbstentionCorrect)} of {agentRows.Count} personas answered the right SHAPE — "
          + $"≥{MinimumPresentations} recommendation where recommendations were owed, "
          + $"≤{AbstentionMaximumPresentations} on the thin-signal persona. Chance floor 0.0000: no constant policy "
          + "passes both halves.");

        // ⚠ The two ways this gate can fail are counted SEPARATELY, because they are two different
        //   faults with two different owners and they were printing as one line. A judge that
        //   answered a different rubric is the judge's fault; a judge that echoed OUR criterion in
        //   a surface form the matcher did not recognise is OURS, and the grades it returned were
        //   good ones we discarded. MEASURED 2026-09-05: 24 of 24 were the second kind.
        var allRows = agentRows.Concat(controlRows).ToList();
        var unjoined = allRows.SelectMany(r => r.ExtraCriteria).ToList();
        int joinFailures = unjoined.Count(u => u.LooksLikeAJoinFailure);
        int invented = unjoined.Count - joinFailures;
        int missingVerdicts = allRows.Sum(r => r.Judged.Count(j => j.Met is null));

        Line(instrument,
            $"INSTRUMENT HEALTH: every declared criterion came back with a verdict on all "
          + $"{allRows.Count} judged cases, none was invented, and no turn threw. "
          + $"Observed: {missingVerdicts} declared criterion(s) with NO verdict · {invented} INVENTED criterion(s) "
          + $"(the judge answered something we did not ask) · {joinFailures} JOIN FAILURE(s) (the judge answered "
          + "OUR criterion and the matcher did not recognise it — our defect, and the verdicts it returned were "
          + "thrown away). "
          + $"(MAFEvaluationHarness discards EvaluationResult.EvaluationFailed, so a parse failure arrives as "
          + $"score 50 with an empty criteria list — detected here as missing verdicts, never read as a grade.)");

        if (joinFailures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"       ⚠ {joinFailures} of those {unjoined.Count} unjoined criterion(s) are OURS, not the judge's. "
                            + "Fix the join before reading a single 0.0/100 on this run as a grade — a weighted score "
                            + "whose criteria were dropped by the matcher is an artefact of the matcher.");
            Console.ResetColor();
        }

        Line(separation,
            $"SEPARATION: the agent scores strictly above the popularity control on "
          + $"{separated.Count(p => p.Control is not null && p.Row.WeightedScore > p.Control.WeightedScore)} of "
          + $"{separated.Count} personas owed recommendations. Chance floor 0.0625 (0.5^{separated.Count}) for a "
          + "judge answering at random. Without this the judged score is an uncalibrated number about which nothing "
          + "is known.");

        Console.WriteLine();
        Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine(passed
            ? "  ✅ Eval 05 PASSED — the shape is right and the judged quality separates from a bestseller list."
            : "  ❌ Eval 05 FAILED.");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     The weighted quality score is REPORTED, never gated on a threshold. There is no gold");
        Console.WriteLine("     set for it, no inter-rater agreement and no calibration run, so a bar would be a");
        Console.WriteLine("     number chosen after seeing the result. What is gated is that it discriminates.");
        Console.ResetColor();
        Console.WriteLine();

        return passed;

        static void Line(bool ok, string text)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {text}");
            Console.ResetColor();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Dry run
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The stub agent for one case. Presenting for a required persona, silent for the thin-signal one.</summary>
    /// <remarks>
    /// Two stubs, not one, and the reason is the wiring rule this repository keeps re-learning:
    /// a discriminator has to be exercised in BOTH directions. A dry run in which every stub
    /// presents would leave "abstention was correct and the agent abstained" untested, and that is
    /// the branch that stops "never abstain" from being a winning strategy.
    /// </remarks>
    /// <param name="testCase">The case.</param>
    public static IChatClient StubAgentFor(Case testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return testCase.Expectation == AnswerExpectation.AbstentionCorrect
            ? new StubChatClient(_ => [])          // prose only — no PresentRecommendation call
            : StubChatClient.PresentingAgent();
    }

    /// <summary>
    /// The dry run's plumbing checks. A stub cannot prove anything else, and each of these CAN FAIL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four properties, each of which has to hold before any live number is trustworthy:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>The declared criteria reached the judge verbatim.</b> The stub judge
    ///   records the criteria it was asked about; they must equal the declared rubric, in order, for
    ///   every judged case. A typo in a criterion, a rubric that failed to serialise, or a harness
    ///   that dropped <c>EvaluationCriteria</c> all fail here rather than silently producing a
    ///   score.</description></item>
    ///   <item><description><b>Verdicts were joined to the right weights.</b> The stub judge answers
    ///   by index parity, so each case's weighted score is known in advance;
    ///   <see cref="Row.WeightedScore"/> must equal it. This is the check that would catch
    ///   reconciliation matching the wrong criterion to the wrong weight.</description></item>
    ///   <item><description><b>The abstention discriminator fired in BOTH directions.</b> At least
    ///   one required persona presented and the thin-signal persona presented nothing.</description></item>
    ///   <item><description><b>Nothing threw and no criterion went missing.</b></description></item>
    /// </list>
    /// <para>
    /// The SEPARATION condition is deliberately excluded: the stub judge does not read the text, so
    /// both arms score identically and separation cannot be exercised without a live judge. Saying
    /// so is the point — a dry run that claimed to have tested it would be lying.
    /// </para>
    /// </remarks>
    /// <param name="agentRows">The agent lane.</param>
    /// <param name="controlRows">The control lane.</param>
    /// <param name="judge">The stub judge, for the criteria it recorded.</param>
    public static bool PrintDryRunVerdict(
        IReadOnlyList<Row> agentRows, IReadOnlyList<Row> controlRows, StubJudgeClient judge)
    {
        ArgumentNullException.ThrowIfNull(agentRows);
        ArgumentNullException.ThrowIfNull(controlRows);
        ArgumentNullException.ThrowIfNull(judge);

        var all = agentRows.Concat(controlRows).ToList();

        // 1 — the criteria the judge saw must be the criteria that were declared.
        var expectedByCase = Cases.ToDictionary(
            c => c.PersonaId,
            c => (IReadOnlyList<string>)[.. c.Rubric.Select(w => w.Criterion.Text)],
            StringComparer.Ordinal);

        int observed = 0;
        bool criteriaSurvived = judge.Observed.Count > 0;
        foreach (IReadOnlyList<string> seen in judge.Observed)
        {
            observed++;
            bool matchesSome = expectedByCase.Values.Any(expected => expected.SequenceEqual(seen, StringComparer.Ordinal));
            if (!matchesSome) criteriaSurvived = false;
        }
        criteriaSurvived &= observed == all.Count;

        // 2 — the weighted join. The stub answers by parity, so every score is known in advance.
        bool weightsJoined = all.All(row =>
            Math.Abs(row.WeightedScore - ExpectedParityScore(row.Case.Rubric)) < 1e-6);

        // 3 — both directions of the discriminator.
        bool presentedSomewhere = agentRows.Any(r =>
            r.Case.Expectation == AnswerExpectation.RecommendationsRequired && r.Presentations >= MinimumPresentations);
        bool abstainedSomewhere = agentRows.Any(r =>
            r.Case.Expectation == AnswerExpectation.AbstentionCorrect && r.Presentations == 0);
        bool bothDirections = presentedSomewhere && abstainedSomewhere;

        // 4 — nothing threw, nothing went missing.
        bool clean = all.All(r => !r.InstrumentFailed);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── DRY-RUN PLUMBING CHECKS (a stub cannot prove anything else) ──────");
        Console.ResetColor();

        Line(criteriaSurvived,
            $"the DECLARED CRITERIA reached the judge verbatim on all {all.Count} judged cases "
          + $"({observed} criteria list(s) observed by the stub judge).");
        Line(weightsJoined,
            "per-criterion verdicts were joined to the RIGHT WEIGHTS — every weighted score equals the value the "
          + "stub judge's index-parity answers predict.");
        Line(bothDirections,
            "the ABSTENTION DISCRIMINATOR fired in BOTH directions: a required persona presented, and the "
          + "thin-signal persona presented nothing.");
        Line(clean, "no case threw and no declared criterion came back without a verdict.");

        // 5 — and BOTH surface forms of the echo were actually joined. A check that only ever sees
        //     one form certifies one form. The stub answered half the cells with the evaluator's
        //     own ordinal in front of the criterion — the form the 2026-09-05 live judge used and
        //     the form that broke the join — and half with the bare text.
        bool bothFormsEchoed = judge.OrdinalEchoes > 0 && judge.BareEchoes > 0;
        Line(bothFormsEchoed,
            bothFormsEchoed
                ? $"the criterion join was exercised in BOTH surface forms: {judge.OrdinalEchoes} cell(s) answered "
                + $"with ChatClientEvaluator's own ordinal (\"1. …\", the form a real judge echoes and the form that "
                + $"broke on 2026-09-05) and {judge.BareEchoes} with the bare text. Check 2 above holds for both."
                : $"⚠ ONLY ONE SURFACE FORM WAS EXERCISED ({judge.OrdinalEchoes} ordinal, {judge.BareEchoes} bare). "
                + "A stub that echoes more helpfully than a real judge makes this dry run blind to the join fault "
                + "the paid run will hit.");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("  NOT tested by a dry run: SEPARATION. The stub judge does not read the answer, so both arms");
        Console.WriteLine("  score identically and the one condition that makes the judged number meaningful cannot be");
        Console.WriteLine("  exercised without a live judge. A green dry run says the wiring holds and nothing about");
        Console.WriteLine("  whether the agent's recommendations are any good.");
        Console.ResetColor();
        Console.WriteLine();

        return criteriaSurvived && weightsJoined && bothDirections && clean && bothFormsEchoed;

        static void Line(bool ok, string text)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {text}");
            Console.ResetColor();
        }
    }

    /// <summary>The weighted score <see cref="StubJudgeClient"/>'s index-parity answers must produce.</summary>
    /// <param name="rubric">The rubric.</param>
    public static double ExpectedParityScore(IReadOnlyList<WeightedCriterion> rubric)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        double total = 0;
        for (int i = 0; i < rubric.Count; i++)
            if (i % 2 == 0) total += rubric[i].Weight;
        return total * 100.0;
    }

    /// <summary>
    /// A judge that spends nothing, reads nothing, and is deliberately implausible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It parses the criteria out of <c>ChatClientEvaluator</c>'s own prompt and echoes them back
    /// with an index-parity verdict, so the dry run can assert two things a stub that invented its
    /// own criteria could not: that the DECLARED criteria arrived unchanged, and that the returned
    /// verdicts were joined to the right weights.
    /// </para>
    /// <para>
    /// It answers by position and never looks at the answer, which is exactly why a dry-run score is
    /// not a result. Its summary says so in capital letters: if that sentence appears in a report
    /// meant to be real, the run never reached Azure.
    /// </para>
    /// </remarks>
    public sealed class StubJudgeClient : IChatClient
    {
        /// <summary>The marker <c>ChatClientEvaluator</c> puts before its numbered criteria list.</summary>
        public const string CriteriaMarker = "CRITERIA TO EVALUATE:";

        /// <summary>The summary the stub returns. Deliberately unmistakable.</summary>
        public const string StubSummary =
            "DRY RUN — THIS VERDICT CAME FROM A STUB JUDGE, NOT FROM A MODEL. Criteria were answered by index "
          + "parity and the answer was never read. If this sentence appears in a report you meant to be real, "
          + "the run did not reach Azure.";

        private readonly List<IReadOnlyList<string>> _observed = [];
        private int _ordinalEchoes;
        private int _bareEchoes;

        /// <summary>The criteria lists the stub was asked about, in call order.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Observed => _observed;

        /// <summary>Calls answered echoing the evaluator's ordinal ("1. …") — the form a real judge uses.</summary>
        public int OrdinalEchoes => _ordinalEchoes;

        /// <summary>Calls answered echoing the bare criterion text.</summary>
        public int BareEchoes => _bareEchoes;

        /// <inheritdoc/>
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            IReadOnlyList<string> criteria = ExtractCriteria(messages);

            // ⚠ THE SURFACE FORM ALTERNATES, AND THAT IS THE POINT.
            //
            // This stub used to echo the criterion with ChatClientEvaluator's ordinal STRIPPED —
            // i.e. more helpfully than any real model does — so the dry run could not reach the
            // join that broke on 2026-09-05, when a real judge echoed "1. Every recommendation…"
            // and Eval 05 recorded all five of its own criteria as "nobody declared". A stub that
            // behaves better than the thing it stands in for makes the free stage of the protocol
            // blind to exactly the faults the paid stage will hit.
            //
            // Even calls echo the ORDINAL form the evaluator renders; odd calls echo the bare text.
            // Both are exercised in one dry run, and the weighted-score check below fails if either
            // stops joining. The call index is per-cell state on a per-run instance, not per-model-
            // call state, so it cannot drift the way an earlier agent stub's parity did.
            bool echoOrdinal = _observed.Count % 2 == 0;
            _observed.Add(criteria);
            if (echoOrdinal) _ordinalEchoes++; else _bareEchoes++;

            var sb = new StringBuilder();
            sb.Append("{\"criteriaResults\":[");
            for (int i = 0; i < criteria.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string echoed = echoOrdinal ? $"{i + 1}. {criteria[i]}" : criteria[i];
                sb.Append(CultureInfo.InvariantCulture,
                    $"{{\"criterion\":{Quote(echoed)},\"met\":{(i % 2 == 0 ? "true" : "false")},"
                  + $"\"explanation\":\"stub judge — criterion {i + 1} answered by index parity, not by reading\"}}");
            }
            sb.Append(CultureInfo.InvariantCulture,
                $"],\"overallScore\":42,\"summary\":{Quote(StubSummary)},\"improvements\":[]}}");

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()))
            {
                ModelId = "stub-judge",
                FinishReason = ChatFinishReason.Stop,
            });
        }

        /// <summary>Recovers the numbered criteria list from the evaluator's own prompt.</summary>
        /// <param name="messages">The messages the evaluator sent.</param>
        public static IReadOnlyList<string> ExtractCriteria(IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);

            string prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            int marker = prompt.LastIndexOf(CriteriaMarker, StringComparison.Ordinal);
            if (marker < 0) return [];

            var criteria = new List<string>();
            foreach (string raw in prompt[(marker + CriteriaMarker.Length)..]
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                int dot = line.IndexOf(". ", StringComparison.Ordinal);
                if (dot <= 0 || !line[..dot].All(char.IsDigit)) continue;

                criteria.Add(line[(dot + 2)..].Trim());
            }

            return criteria;
        }

        private static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

        /// <inheritdoc/>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        /// <inheritdoc/>
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        /// <inheritdoc/>
        public void Dispose() { }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Console
    // ══════════════════════════════════════════════════════════════════════════════════════

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Eval 05 — Recommendation Quality                                           ║
║   5 personas · weighted criterion-based LLM judge · paired degenerate arm     ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintRubric()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── the weighted rubric (weights are declared here, not tuned against a result) ───");
        Console.ResetColor();

        Print("recommendations required", Criteria.Discovery);
        Print("thin signal — abstention correct", Criteria.ThinSignal);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     Which rubric applies is decided by the PERSONA, never by what the agent produced.");
        Console.WriteLine("     Choosing the rubric from the result would let an agent pick an easier exam by failing.");
        Console.ResetColor();
        Console.WriteLine();

        static void Print(string label, IReadOnlyList<WeightedCriterion> rubric)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"     {label}:");
            Console.ResetColor();
            foreach (var weighted in rubric)
                Console.WriteLine($"       [{weighted.Weight:F2}] {weighted.Criterion.Key}");
        }
    }

    private static void PrintDerivedFloors()
    {
        var lines = new List<string>
        {
            "Restraint          : 1.0000  an agent that presents NOTHING satisfies it perfectly, and so does any "
          + "agent that simply never states a figure. Free points — hence the low weight, hence the deterministic "
          + "abstention check that runs BEFORE the judge, hence the control's score on it being printed.",

            "Proactivity        : ~1.000  any agent that presents anything at all on an open-ended prompt has "
          + "surfaced something unsolicited. Lowest weight. Reported because it is the product thesis, not because "
          + "it discriminates.",

            "Relevance          : MEASURED, not derived. The popularity control's score on this axis IS the floor, "
          + "and it is printed on every run beside the agent's.",

            "Explanation quality: MEASURED. The control's reason string is 'One of our most popular products — "
          + "{name}' with a citation that RESOLVES. If the judge marks that met, this axis is measuring citation "
          + "resolution rather than explanation quality, and separation fails instead of the number being quoted.",
        };

        foreach (Case testCase in Cases.Where(c => c.Expectation == AnswerExpectation.RecommendationsRequired))
        {
            GoldInterestMap gold = InterestMapGold.Derive(testCase.PersonaId);
            var (pool, latent, _) = ChanceFloors.RandomDrawFloor(gold);
            lines.Add(
                $"Discovery · {testCase.PersonaId} : {latent:F4}  expected latent coverage of a random-"
              + $"{ChanceFloors.DegenerateDrawSize} draw from the {pool} products outside this customer's owned "
              + $"leaf categories, over {gold.Latent.Count} derived gold token(s). The MECHANICAL half of the "
              + "discovery axis; Eval 02 owns that measurement and this eval judges the human-legible half.");
        }

        lines.Add(
            $"Abstention arm     : 0.0000  a constant 'always present' policy scores "
          + $"{Cases.Count(c => c.Expectation == AnswerExpectation.RecommendationsRequired)} of {Cases.Count}; a "
          + $"constant 'never present' policy scores "
          + $"{Cases.Count(c => c.Expectation == AnswerExpectation.AbstentionCorrect)} of {Cases.Count}. Both fail. "
          + "No constant policy passes.");

        lines.Add(
            $"Separation arm     : 0.0625  = 0.5^"
          + $"{Cases.Count(c => c.Expectation == AnswerExpectation.RecommendationsRequired)}, the probability a "
          + "judge answering at random and never tying puts the agent above the control on every persona owed "
          + "recommendations. The weakest link in this gate, stated rather than buried.");

        EvalPrinter.PrintFloors("Eval 05 — chance floors: what does an agent that understands nothing score?", lines);
    }

    private static void PrintDryRunBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  🧪 DRY RUN — stub agent AND stub judge. Nothing spent, nothing written.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     Standing protocol before any paid run: dry-run every case (real code path, stubs");
        Console.WriteLine("     deliberately implausible so a silent fallback to a live model is visible), then one");
        Console.WriteLine("     real single-case run, then the full run.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintCaseHeader(Case testCase)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine();
        Console.WriteLine($"  ─── {testCase.PersonaId}  {testCase.DisplayName}  "
                        + $"[{(testCase.Expectation == AnswerExpectation.AbstentionCorrect
                                ? "abstention correct" : "recommendations required")}] ───────────");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      {testCase.Why}");
        Console.ResetColor();
    }

    private static void PrintRow(Row row)
    {
        Console.ForegroundColor = string.Equals(row.Arm, "agent", StringComparison.Ordinal)
            ? ConsoleColor.White : ConsoleColor.DarkGray;
        Console.WriteLine($"    [{row.Arm}] weighted quality {row.WeightedScore,6:F1}/100   "
                        + $"presented {row.Presentations}  (citations resolving {row.EvidenceResolved}/{row.Presentations})   "
                        + $"judge's own holistic {row.HolisticScore}/100");
        Console.ResetColor();

        foreach (JudgedCriterion judged in row.Judged)
        {
            Console.ForegroundColor = judged.Met switch
            {
                true => ConsoleColor.Green,
                false => ConsoleColor.Yellow,
                _ => ConsoleColor.Red,
            };
            string mark = judged.Met switch { true => "✅", false => "❌", _ => "⚠️ " };
            Console.WriteLine($"      {mark} [{judged.Weight:F2}] {judged.Criterion.Key,-13} — {Clip(judged.Explanation, 150)}");
            Console.ResetColor();
        }

        foreach (UnjoinedCriterion extra in row.ExtraCriteria)
        {
            // ⚠ The DIAGNOSIS is printed, not just the string. "A criterion nobody declared" read
            // as a judge that went off-script, and on the 2026-09-05 run all 24 of them were our
            // own criteria carrying the ordinal our evaluator printed in front of them.
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      ⚠️  UNJOINED criterion — {extra.Diagnosis}.");
            Console.WriteLine($"          returned: \"{Clip(extra.Returned, 90)}\"");
            Console.ResetColor();
        }

        if (!row.AbstentionCorrect)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(row.Case.Expectation == AnswerExpectation.RecommendationsRequired
                ? $"      ❌ HARD FAIL — this customer was owed recommendations and the turn presented "
                + $"{row.Presentations}. Silence is never a pass on a case that had a right answer, whatever the "
                + "judged score above says."
                : $"      ❌ HARD FAIL — the signal is too thin to personalise and the turn presented "
                + $"{row.Presentations} recommendation(s). Confidence is not a pass where abstention was the answer.");
            Console.ResetColor();
        }

        if (row.Error is not null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      ❌ the turn threw: {Clip(row.Error, 200)}");
            Console.ResetColor();
        }

        if (!string.IsNullOrWhiteSpace(row.Summary))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"      judge: {Clip(row.Summary, 200)}");
            Console.ResetColor();
        }
    }

    private static void PrintComparison(IReadOnlyList<Row> agentRows, IReadOnlyList<Row> controlRows)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ─── agent vs. the degenerate arm, same rubric · same packet · same judge ──");
        Console.ResetColor();
        Console.WriteLine("     persona      agent   popularity   margin   presented   shape");

        foreach (Row agent in agentRows)
        {
            Row? control = controlRows.FirstOrDefault(c =>
                string.Equals(c.Case.PersonaId, agent.Case.PersonaId, StringComparison.Ordinal));
            double margin = control is null ? double.NaN : agent.WeightedScore - control.WeightedScore;

            Console.ForegroundColor = margin > 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine($"     {agent.Case.PersonaId,-12} {agent.WeightedScore,5:F1}   "
                            + $"{(control is null ? "  n/a" : control.WeightedScore.ToString("F1", CultureInfo.InvariantCulture).PadLeft(5))}"
                            + $"       {(double.IsNaN(margin) ? "  n/a" : margin.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture).PadLeft(5))}"
                            + $"   {agent.Presentations,9}   {(agent.AbstentionCorrect ? "ok" : "WRONG SHAPE")}");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("     The margin is the whole point. A judged number that cannot put a real agent above a");
        Console.WriteLine("     bestseller list is not measuring recommendation quality, and this table is where that");
        Console.WriteLine("     would become visible instead of being averaged away.");
        Console.ResetColor();
    }

    private static void PrintCost(IReadOnlyList<Row> agentRows, IReadOnlyList<Row> controlRows, bool dryRun)
    {
        decimal cost = agentRows.Concat(controlRows).Sum(r => r.CostUsd ?? 0m);
        double seconds = agentRows.Concat(controlRows).Sum(r => r.DurationMs) / 1000.0;
        int prompt = agentRows.Sum(r => r.PromptTokens ?? 0);
        int completion = agentRows.Sum(r => r.CompletionTokens ?? 0);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ⏱️  {seconds:F1}s total · agent-turn tokens {prompt} in / {completion} out · "
                        + $"estimated ${cost:F4}");
        Console.WriteLine("     REPORTED, NEVER GATED. And it is an UNDER-count: PerformanceMetrics covers the agent");
        Console.WriteLine("     turn only. MAFEvaluationHarness does not surface EvaluationResult's token counts, so");
        Console.WriteLine($"     the {agentRows.Count + controlRows.Count} judge calls this run made are not in that figure.");
        Console.WriteLine("     Completion tokens read 0 when the provider reports no usage: MAFEvaluationHarness's");
        Console.WriteLine("     non-streaming fallback estimates them from ActualOutput before ActualOutput is set.");
        Console.ResetColor();

        if (dryRun)
        {
            // ⚠️ NOT "the cost was zero". Nothing was spent — but the figure above is not zero, because
            // the harness estimates a price for whatever token count it was handed and the stub handed
            // it one. Printing a dollar figure and the sentence "it is zero" next to each other was the
            // first version of this block, and a false line beside a true number is worse than either.
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("     ⚠️  DRY RUN: no model was called and NOTHING WAS SPENT. The dollar figure above is");
            Console.WriteLine("         an arithmetic artefact — the harness priced the STUB's tokens at the configured");
            Console.WriteLine("         deployment's rate. It is not spend, and it does not predict the live run's.");
            Console.ResetColor();
        }
    }

    private static string Clip(string text, int max)
    {
        string flat = text.Replace("\r", " ", StringComparison.Ordinal)
                          .Replace("\n", " ", StringComparison.Ordinal);
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
