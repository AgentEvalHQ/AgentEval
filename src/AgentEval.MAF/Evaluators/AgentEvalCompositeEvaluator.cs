// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Adapts an AgentEval <see cref="IEval"/> — including a full <c>CompositeEval</c> such as an
/// <c>AgenticBenchmark</c> preset — into a Microsoft.Extensions.AI.Evaluation
/// <see cref="MEAIIEvaluator"/>, so an entire weighted composite tree can be run as a single
/// evaluator through MAF's <c>agent.EvaluateAsync(...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="AgentEvalEvaluator"/> bundles flat metrics, this runs AgentEval's composite engine
/// (weighted aggregation + thresholds + a real verdict tree) behind the MEAI interface MAF consumes.
/// </para>
/// <para>
/// The composite's sub-evaluators (e.g. the AgenticBenchmark tool sub-evals) are LLM-judged: they
/// grade the query + response text, so they work over MAF's evaluation feature even when only the
/// final response is forwarded. (To also let <i>code-based</i> tool metrics see the calls, run this
/// through <see cref="AgentEvalAgentEvaluator"/>, which forwards the full conversation.)
/// </para>
/// <para>
/// The rich <see cref="EvalResult"/> tree the composite produces is captured in
/// <see cref="CapturedResults"/> so callers can render it (HTML/PDF) with the full hierarchy intact —
/// the flat MEAI <see cref="MEAIEvaluationResult"/> returned to MAF is only for MAF's pass/fail rollup.
/// </para>
/// </remarks>
public sealed class AgentEvalCompositeEvaluator : MEAIIEvaluator
{
    private readonly IEval _composite;
    private readonly List<EvalResult> _captured = [];

    /// <summary>Creates an MEAI evaluator that runs <paramref name="composite"/> per evaluated item.</summary>
    /// <param name="composite">The eval to run per item.</param>
    public AgentEvalCompositeEvaluator(IEval composite)
        : this(composite, null) { }

    /// <summary>
    /// Creates an MEAI evaluator that runs <paramref name="composite"/> and DECLARES a root-level
    /// chance floor beside its verdict.
    /// </summary>
    /// <param name="composite">The eval to run per item.</param>
    /// <param name="declaredRootFloor">
    /// What an arm that understood nothing would score on this composite as a whole, or
    /// <see cref="ChanceFloor.NotDerivable(string)"/> with the reason no such number exists.
    /// <see langword="null"/> means nobody declared one — a THIRD state, distinct from both.
    /// </param>
    /// <exception cref="ArgumentException">A floor was supplied with no derivation.</exception>
    /// <remarks>
    /// <para>
    /// 🔴 <b>RECORDED, NEVER APPLIED — and that is ADR-030 Q6's answer, not an oversight.</b> Q6 —
    /// <i>does a chance floor bind a verdict?</i> — was answered <b>yes on the principle, staged in
    /// execution</b>: the binding test lands in Slice 2.6 under its stated conditions, not at this
    /// door. So this floor changes no score, flips no verdict and gates nothing. It makes the bar
    /// VISIBLE beside a number that previously had none.
    /// </para>
    /// <para>
    /// ⚠ <b>It is not stamped on the composite's root result.</b> <see cref="FloorAdmittedEval"/>
    /// refuses a result carrying sub-results, because one floor on a root would certify every
    /// floorless leaf beneath it. The declaration travels on the MEAI metric this evaluator returns,
    /// and the per-leaf truth is reported separately by <see cref="FlooredLeafCount"/> /
    /// <see cref="LeafCount"/> — read off the tree that actually ran, never from what a caller claimed.
    /// </para>
    /// </remarks>
    public AgentEvalCompositeEvaluator(IEval composite, ChanceFloor? declaredRootFloor)
    {
        _composite = composite ?? throw new ArgumentNullException(nameof(composite));

        if (declaredRootFloor is not null && string.IsNullOrWhiteSpace(declaredRootFloor.Derivation))
        {
            throw new ArgumentException(
                "A chance floor was declared for this composite with NO derivation, so it was refused — "
                + "the same rule FloorAdmittedEval.Admit applies. A floor's number without its derivation "
                + "is unusable, and a floor's ABSENCE without its reason is worse: 'nobody could derive "
                + "one' and 'nobody tried' are different facts, and only the stated reason separates them.",
                nameof(declaredRootFloor));
        }

        DeclaredRootFloor = declaredRootFloor;
    }

    /// <summary>The metric name carrying the floor declaration. Stable, so a renderer can find it.</summary>
    public const string FloorDeclarationMetricName = "AgentEval chance-floor declaration";

    /// <summary>
    /// The root-level floor this door was constructed with, or <see langword="null"/> when nobody
    /// declared one. Recorded beside every verdict; applied to none.
    /// </summary>
    public ChanceFloor? DeclaredRootFloor { get; }

    /// <summary>Atomic leaves in the most recently captured tree.</summary>
    public int LeafCount { get; private set; }

    /// <summary>
    /// How many of those leaves carry a floor of their own — i.e. went through
    /// <see cref="FloorAdmittedEval"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Read off the tree that ran, not off a declaration.</b> A composite whose leaves were
    /// never admitted reports <c>0</c> here however confidently its root is described, and that
    /// number is the honest answer to "is anything in this tree comparable to chance?". Before this
    /// existed, a floorless MAF composite and a fully floored one rendered identically.
    /// </remarks>
    public int FlooredLeafCount { get; private set; }

    /// <summary>
    /// The <see cref="EvalResult"/> tree(s) produced — one per evaluated item, in call order.
    /// <b>Accumulates across every <c>EvaluateAsync</c> call on this instance</b> and is never cleared,
    /// so construct a fresh evaluator per evaluation run if you don't want a prior run's results to
    /// persist (an evaluator is cheap — it just wraps the composite).
    /// </summary>
    public IReadOnlyList<EvalResult> CapturedResults => _captured;

    /// <inheritdoc/>
    /// <remarks>
    /// Advertises the stable root metric name <c>EvaluateAsync</c> always emits (<c>"{composite} (overall)"</c>).
    /// The per-leaf metric names are added dynamically per evaluated item (they depend on the composite
    /// tree), so they can't be enumerated statically here.
    /// </remarks>
    public IReadOnlyCollection<string> EvaluationMetricNames => [$"{_composite.Name} (overall)"];

    /// <inheritdoc/>
    public async ValueTask<MEAIEvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<MEAIEvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var query = ConversationExtractor.ExtractLastUserMessage(messages);
        var output = response.Text ?? string.Empty;

        var input = new EvalInput(Query: query, Response: output);
        EvalResult tree = await _composite.EvaluateAsync(input, cancellationToken).ConfigureAwait(false);
        _captured.Add(tree);

        // Flatten to MEAI metrics so MAF's AgentEvaluationResults gets a pass/fail rollup: the
        // composite root carries the overall verdict, and each atomic leaf is surfaced too.
        var leaves = EnumerateAtomicLeaves(tree).ToList();
        LeafCount = leaves.Count;
        FlooredLeafCount = leaves.Count(CarriesItsOwnFloor);

        var result = new MEAIEvaluationResult();
        AddMetric(result, tree, isRoot: true);
        foreach (var leaf in leaves)
            if (!ReferenceEquals(leaf, tree))   // an atomic IEval is its own only "leaf" — don't add it twice
                AddMetric(result, leaf, isRoot: false);

        AddFloorDeclaration(result);

        return result;
    }

    /// <summary>True when this leaf's own result carries a chance-floor record.</summary>
    /// <remarks>
    /// Reads the ADR-030 §3.2 convention <see cref="FloorAdmittedEval"/> writes — the
    /// <c>chance-floor</c> EVIDENCE entry, not the dimension. The dimension is absent for a
    /// not-derivable floor by design (an absent floor is not a zero floor), so counting dimensions
    /// would report an eval that was ASKED and could not answer as one nobody asked.
    /// </remarks>
    private static bool CarriesItsOwnFloor(EvalResult leaf) =>
        leaf.Details.Evidence?.Any(e => string.Equals(
            e.Source, ComparabilityFacts.ChanceFloorEvidenceSource, StringComparison.Ordinal)) == true;

    /// <summary>Puts the floor situation on the result as text a reader cannot miss — never as a gate.</summary>
    private void AddFloorDeclaration(MEAIEvaluationResult result)
    {
        var leafPart = LeafCount == 0
            ? "no atomic leaf was produced"
            : $"{FlooredLeafCount} of {LeafCount} leaf/leaves carry a floor of their own";

        var rootPart = DeclaredRootFloor is null
            ? "no root-level floor was declared for this composite (nobody asked — which is not the same "
              + "as asked-and-not-derivable)"
            : DeclaredRootFloor.State is FloorState.Derived
                ? string.Create(CultureInfo.InvariantCulture,
                    $"declared root floor {DeclaredRootFloor.ComparisonBar:0.0000} ({DeclaredRootFloor.Kind}): {DeclaredRootFloor.Derivation}")
                : $"root floor NOT DERIVABLE ({DeclaredRootFloor.Kind}): {DeclaredRootFloor.Derivation}";

        var reason = rootPart + ". " + leafPart + ". "
            + "This floor is RECORDED and NOT APPLIED: it changes no score and gates nothing "
            + "(ADR-030 Q6 — yes on the principle, staged in execution).";

        result.Metrics[FloorDeclarationMetricName] =
            new BooleanMetric(FloorDeclarationMetricName, LeafCount > 0 && FlooredLeafCount == LeafCount, reason)
            {
                Interpretation = new EvaluationMetricInterpretation(reason: reason),
            };
    }

    private static void AddMetric(MEAIEvaluationResult result, EvalResult node, bool isRoot)
    {
        // AgentEval EvalScore.Value is 0..1; MEAI NumericMetric convention is 1..5.
        var meaiValue = 1.0 + Math.Clamp(node.Score.Value, 0, 1) * 4.0;
        var pct = node.Score.Value * 100.0;
        var reason = $"AgentEval score: {pct:F0}/100 ({node.Score.Label}, severity {node.Score.Severity})";

        var metric = new NumericMetric(isRoot ? $"{node.Metric.Name} (overall)" : node.Metric.Name, meaiValue, reason)
        {
            Interpretation = new EvaluationMetricInterpretation(
                rating: pct switch
                {
                    >= 90 => EvaluationRating.Exceptional,
                    >= 75 => EvaluationRating.Good,
                    >= 50 => EvaluationRating.Average,
                    _ => EvaluationRating.Poor,
                },
                failed: !node.Score.Passed,
                reason: reason),
        };

        // Disambiguate when two leaves share a metric name.
        var key = metric.Name;
        var i = 1;
        while (result.Metrics.ContainsKey(key))
            key = $"{metric.Name} #{++i}";
        result.Metrics[key] = metric;
    }

    private static IEnumerable<EvalResult> EnumerateAtomicLeaves(EvalResult node)
    {
        var subs = node.Details.SubResults;
        if (subs is null || subs.Count == 0)
        {
            yield return node;
            yield break;
        }

        foreach (var child in subs)
            foreach (var leaf in EnumerateAtomicLeaves(child))
                yield return leaf;
    }
}
