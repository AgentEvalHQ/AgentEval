// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// Projects a TypedMemEval result into AgentEval's recursive report shape.
/// </summary>
/// <remarks>
/// <para>
/// Safe for default reports: typed outcomes, coverage, and attribution only. Question text, gold
/// answers, agent responses and judge explanations are never copied — keep the native result as a
/// separate, access-controlled artefact when full diagnostic detail is needed.
/// </para>
/// <para>
/// The score value on each node is the correct-share, because the report pipeline needs a number.
/// It is <b>not</b> a TypedMemEval score, and every node says so in its label and carries the full
/// typed vector in its dimensions. This is the same registered compatibility exception that keeps
/// <see cref="ExternalBenchmarkResult.OverallAccuracy"/> populated.
/// </para>
/// </remarks>
public static class TypedMemEvalEvalResultAdapter
{
    /// <summary>The citation rule, attached to the root node of every projected result.</summary>
    public const string CitationRule =
        "Cite as \"TypedMemEval-<Vertical> v2 (AgentEval)\". TypedMemEval results are not " +
        "LongMemEval results and must never be presented as, summed with, or averaged with " +
        "LongMemEval numbers. The typed outcome vector is the citable form; the score value on " +
        "this node exists for tooling compatibility.";

    /// <summary>Converts a family result into an <see cref="EvalResult"/> tree.</summary>
    /// <param name="result">A result carrying <see cref="ExternalBenchmarkResult.TypedOutcomes"/>.</param>
    /// <param name="judgeModel">The declared judge model identity, when known.</param>
    /// <param name="passThresholdPercent">
    /// Correct-share threshold used only to populate the report shape's required pass flag.
    /// </param>
    /// <remarks>
    /// TypedMemEval does not define a pass mark. The threshold exists because
    /// <see cref="EvalScore.Passed"/> is not nullable, it is stated rather than hidden so a reader
    /// can see it was chosen by the caller, and the typed vector in the dimensions is the result.
    /// </remarks>
    /// <exception cref="ArgumentException">When the result is not a TypedMemEval result.</exception>
    public static EvalResult ToEvalResult(
        ExternalBenchmarkResult result,
        string? judgeModel = null,
        double passThresholdPercent = 50.0)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!double.IsFinite(passThresholdPercent) || passThresholdPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(passThresholdPercent));
        var typed = result.TypedOutcomes
            ?? throw new ArgumentException(
                "Result carries no TypedOutcomes, so it is not a TypedMemEval result.",
                nameof(result));

        var now = DateTimeOffset.UtcNow;
        var passThreshold = passThresholdPercent / 100.0;
        var shapeNodes = typed.ByShape
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Node(
                key: $"typedmemeval.{typed.Vertical}.{kv.Key}",
                name: $"TypedMemEval — {typed.Vertical} / {kv.Key}",
                counts: kv.Value,
                type: "composite",
                judgeModel: judgeModel,
                extra: null,
                recommendations: null,
                subResults: null,
                passThreshold: passThreshold,
                now: now))
            .ToList();

        var dimensions = Vector(typed.Outcomes);
        dimensions["coverage.observed"] = typed.Coverage.Observed;
        if (typed.Coverage.Mean is { } mean)
            dimensions["coverage.mean"] = mean;
        dimensions["coverage.observedWithGold"] = typed.Coverage.ObservedWithGold;
        if (typed.Coverage.MeanOverGoldBearing is { } goldMean)
            dimensions["coverage.meanOverGoldBearing"] = goldMean;
        if (typed.Coverage.CalibratedFloorMean is { } floor)
            dimensions["coverage.calibratedFloorMean"] = floor;
        dimensions["attribution.evidencePresent"] = typed.Attribution.EvidencePresent;
        dimensions["attribution.evidenceAbsent"] = typed.Attribution.EvidenceAbsent;
        dimensions["attribution.unobserved"] = typed.Attribution.Unobserved;
        dimensions["attribution.observedShare"] = typed.Attribution.ObservedShare;

        if (typed.StaleRecall is { } stale)
            dimensions["forgetting.staleRecall"] = stale;
        if (typed.OverForgetting is { } over)
            dimensions["forgetting.overForgetting"] = over;
        if (typed.MisattributedForgetting is { } misattributed)
            dimensions["forgetting.misattributedForgetting"] = misattributed;

        if (typed.PairConsistency is { } pairs)
        {
            dimensions["pairs.total"] = pairs.Pairs;
            dimensions["pairs.bothArmsCorrect"] = pairs.BothArmsCorrect;
            dimensions["pairs.prematureBefore"] = pairs.PrematureBefore;
            dimensions["pairs.missedAfter"] = pairs.MissedAfter;
            dimensions["pairs.timeBlindPattern"] = pairs.TimeBlindPattern;
        }

        if (typed.ByDistance is { } byDistance)
        {
            foreach (var (distance, counts) in byDistance.OrderBy(kv => kv.Key))
            {
                dimensions[$"distance.{distance}.n"] = counts.N;
                dimensions[$"distance.{distance}.correct"] = counts.Correct;
            }
        }

        return Node(
            key: $"typedmemeval.{typed.Vertical}",
            name: $"{result.BenchmarkName}",
            counts: typed.Outcomes,
            type: "composite",
            judgeModel: judgeModel,
            extra: dimensions,
            recommendations: [CitationRule],
            subResults: shapeNodes,
            passThreshold: passThreshold,
            now: now);
    }

    private static Dictionary<string, double> Vector(TypedMemEvalOutcomeCounts counts) => new()
    {
        ["n"] = counts.N,
        ["outcome.correct"] = counts.Correct,
        ["outcome.wrong"] = counts.Wrong,
        ["outcome.abstained"] = counts.Abstained,
        ["outcome.missed"] = counts.Missed,
        ["outcome.premature"] = counts.Premature,
        ["outcome.inconclusive"] = counts.Inconclusive,
        ["outcome.unrun"] = counts.Unrun
    };

    private static EvalResult Node(
        string key,
        string name,
        TypedMemEvalOutcomeCounts counts,
        string type,
        string? judgeModel,
        Dictionary<string, double>? extra,
        IReadOnlyList<string>? recommendations,
        IReadOnlyList<EvalResult>? subResults,
        double passThreshold,
        DateTimeOffset now)
    {
        // Denominator is measured questions only. Counting the harness's own failures against the
        // system under test would report a judge outage as a memory regression.
        var measured = counts.N - counts.Inconclusive - counts.Unrun;

        // A run that measured nothing gets the report shape's own indeterminate vocabulary, not a
        // zero. `Value` is not nullable here, so the only honest signal available is the label and
        // the pass flag: emitting 0.0 as "fail" would make a judge outage indistinguishable from a
        // system that got every question wrong, which is the exact confusion the Inconclusive and
        // Unrun members exist to prevent.
        var share = measured > 0 ? (double)counts.Correct / measured : 0;
        var (label, passed, severity) = measured == 0
            ? ("inconclusive", false, "low")
            : share < passThreshold
                ? ("fail", false, "medium")
                : share >= 0.7
                    ? ("pass", true, "none")
                    : ("warn", true, "low");

        // The vector reads as prose here rather than in Label, which the shipped result contract
        // restricts to that fixed vocabulary. It belongs somewhere a human sees, because a single
        // share cannot distinguish a system that abstained from one that confidently denied.
        var vector = measured == 0
            ? $"not measured — {counts.Inconclusive} inconclusive, {counts.Unrun} unrun of {counts.N}"
            : $"correct {counts.Correct}/{measured} · wrong {counts.Wrong} · " +
              $"abstained {counts.Abstained} · missed {counts.Missed}" +
              (counts.Premature > 0 ? $" · premature {counts.Premature}" : "");

        var notes = recommendations is null
            ? new[] { vector }
            : new[] { vector }.Concat(recommendations).ToArray();

        return new EvalResult(
            Metric: new EvalMetadata(
                Key: key,
                Name: name,
                Category: "memory",
                Version: "1.0.0"),
            Score: new EvalScore(
                Value: share,
                Ordinal: null,
                Label: label,
                Passed: passed,
                Threshold: passThreshold,
                Severity: severity,
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: extra ?? Vector(counts),
                Evidence: null,
                Recommendations: notes,
                SubResults: subResults,
                AggregationStrategy: "typed-outcome-vector"),
            Provenance: new EvalProvenance(
                Type: type,
                JudgeModel: judgeModel,
                PromptId: null,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: 0,
                CacheHit: false),
            EvaluatedAt: now);
    }
}
