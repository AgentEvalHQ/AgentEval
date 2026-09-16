// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
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
    /// <remarks>
    /// The revision is interpolated from <see cref="TypedMemEvalVerticalDescriptor.CorpusRevision"/>
    /// rather than typed here. A citation rule that names a revision by hand goes stale at the next
    /// bump, and this is the copy that reaches a consumer's report — it named v1 while the corpora
    /// said v2, which is precisely the two-question-sets-one-label failure the rule exists to stop.
    /// </remarks>
    public static readonly string CitationRule =
        $"Cite as \"TypedMemEval-<Vertical> {TypedMemEvalVerticalDescriptor.CorpusRevision} " +
        "(AgentEval)\". TypedMemEval results are not " +
        "LongMemEval results and must never be presented as, summed with, or averaged with " +
        "LongMemEval numbers. The typed outcome vector is the citable form; the score value on " +
        "this node exists for tooling compatibility.";

    /// <summary>
    /// Projects a FAMILY sweep -- one result per vertical -- as a single tree:
    /// family → vertical → shape → question.
    /// </summary>
    /// <remarks>
    /// The root score is a micro-average over every question asked, and it is not a result.
    /// Ten verticals measure ten different constructs; `arithmetic` and `forgetting` are not two
    /// samples of one quantity, so a mean over them answers no question anyone actually has.
    /// <see cref="EvalScore.Value"/> is not nullable, so a number has to be there — every note on
    /// the node exists to stop it being read as the finding. Read the vertical nodes.
    /// </remarks>
    /// <param name="results">One result per vertical. Each must carry TypedOutcomes.</param>
    /// <param name="judgeModel">Judge deployment, recorded on every node.</param>
    /// <param name="passThresholdPercent">Pass threshold applied per node.</param>
    public static EvalResult ToEvalResult(
        IReadOnlyList<ExternalBenchmarkResult> results,
        string? judgeModel = null,
        double passThresholdPercent = 50.0)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            throw new ArgumentException("A family projection needs at least one result.", nameof(results));

        // The SAME guard the single-result overload applies. Two public overloads of one method
        // accepting different ranges is a trap: a caller who validated against one is not
        // validated against the other, and an unchecked NaN here divides by 100 and lands in
        // EvalScore looking like a measurement.
        if (!double.IsFinite(passThresholdPercent) || passThresholdPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(passThresholdPercent));

        // A single vertical projects as itself. Wrapping one result in a "family" root would
        // invent a level that says nothing and would put a second, identical score above it.
        if (results.Count == 1)
            return ToEvalResult(results[0], judgeModel, passThresholdPercent);

        var now = DateTimeOffset.UtcNow;
        var passThreshold = passThresholdPercent / 100.0;

        var verticalNodes = results
            .OrderBy(r => r.TypedOutcomes?.Vertical ?? string.Empty, StringComparer.Ordinal)
            .Select(r => ToEvalResult(r, judgeModel, passThresholdPercent))
            .ToList();

        // Summed over questions, NOT averaged over verticals: a mean of ten rates would weight a
        // 50-question vertical the same as an 80-question one. Both readings are poor; the
        // micro-average is at least the one whose denominator is stated.
        var totals = results
            .Select(r => r.TypedOutcomes!.Outcomes)
            .Aggregate(
                (n: 0, correct: 0, wrong: 0, abstained: 0, missed: 0, premature: 0, inconclusive: 0, unrun: 0),
                (acc, c) => (acc.n + c.N, acc.correct + c.Correct, acc.wrong + c.Wrong,
                             acc.abstained + c.Abstained, acc.missed + c.Missed,
                             acc.premature + c.Premature, acc.inconclusive + c.Inconclusive,
                             acc.unrun + c.Unrun));

        var measured = totals.n - totals.inconclusive - totals.unrun;
        var share = measured > 0 ? (double)totals.correct / measured : 0;

        var verticals = results
            .Select(r => r.TypedOutcomes!.Vertical)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        var dimensions = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["n"] = totals.n,
            ["outcome.correct"] = totals.correct,
            ["outcome.wrong"] = totals.wrong,
            ["outcome.abstained"] = totals.abstained,
            ["outcome.missed"] = totals.missed,
            ["outcome.premature"] = totals.premature,
            ["outcome.inconclusive"] = totals.inconclusive,
            ["outcome.unrun"] = totals.unrun,
            ["verticals"] = verticals.Count,
            ["shapes"] = verticalNodes.Sum(v => v.Details.SubResults?.Count ?? 0)
        };

        return new EvalResult(
            Metric: new EvalMetadata(
                Key: "typedmemeval",
                Name: $"TypedMemEval — {verticals.Count} verticals",
                Category: "memory",
                Version: "1.0.0"),
            Score: new EvalScore(
                Value: share,
                Ordinal: null,
                Label: measured == 0 ? "inconclusive" : "aggregate",
                Passed: measured > 0 && share >= passThreshold,
                Threshold: passThreshold,
                Severity: "none",
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: dimensions,
                Evidence: null,
                Recommendations:
                [
                    "THIS NUMBER IS NOT A RESULT. It is a micro-average over "
                    + $"{measured} measured questions spanning {verticals.Count} verticals, and those "
                    + "verticals measure different constructs — arithmetic and forgetting are not "
                    + "two samples of one quantity. It exists because the score field is not "
                    + "nullable. Read the vertical nodes.",
                    $"Verticals swept: {string.Join(", ", verticals)}.",
                    "Per-shape n is the number that decides whether any row below is evidence. A "
                    + "sweep spread thin across 36 shapes buys breadth, not confidence.",
                    CitationRule
                ],
                SubResults: verticalNodes,
                AggregationStrategy: "typed-outcome-vector"),
            Provenance: new EvalProvenance(
                Type: "composite",
                JudgeModel: judgeModel,
                PromptId: null,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: 0,
                CacheHit: false),
            EvaluatedAt: now);
    }

    /// <summary>Converts a single vertical's result into an <see cref="EvalResult"/> tree.</summary>
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

        // THE SCORE IS NOT THE RESULT, AND A REPORT THAT PRINTS ONLY THE SCORE IS WORSE THAN ONE
        // THAT PRINTS NOTHING. A rendered report used to show "recency 15/15, 1.000" with no
        // chance floor, no oracle ceiling, no retrieval arm and no named retriever -- so a reader
        // took a saturated full-haystack figure for a memory result. The shipped sidecar carries
        // all of it, so it travels with the projection.
        var context = SidecarContext.Load(typed.Vertical);

        // EVERY QUESTION BECOMES A LEAF. Without this the tree is root -> shapes -> nothing:
        // a 50-question run renders three rows and no way to see which question did what, while
        // the per-question results sit unused in the result object. `TypedOutcome.Shape` carries
        // the grouping, so the questions attach to their shape without re-reading the corpus.
        var questionsByShape = result.QuestionResults
            .Where(q => q.TypedOutcome is not null)
            .GroupBy(q => q.TypedOutcome!.Shape, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var shapeNodes = typed.ByShape
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Node(
                key: $"typedmemeval.{typed.Vertical}.{kv.Key}",
                name: $"TypedMemEval — {typed.Vertical} / {kv.Key}",
                counts: kv.Value,
                type: "composite",
                judgeModel: judgeModel,
                extra: context.DimensionsFor(kv.Key),
                recommendations: context.NotesFor(kv.Key),
                subResults: QuestionNodes(
                    typed.Vertical, kv.Key, questionsByShape, judgeModel, passThreshold, now),
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
            recommendations: [CitationRule, .. context.RootNotes()],
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

    /// <summary>
    /// The per-shape numbers a score has to be read against, taken from the sidecar that ships
    /// beside the corpus. Fail-soft by construction: an unreadable sidecar, an unparsable
    /// vertical, or a shape the sidecar does not carry yields no dimensions and no notes rather
    /// than an exception. This projection must never be the thing that fails a completed run.
    /// </summary>
    private sealed class SidecarContext
    {
        private readonly JsonElement _byShape;
        private readonly JsonElement _sensitivityByShape;
        private readonly JsonElement _sensitivity;
        private readonly bool _loaded;

        private SidecarContext(JsonElement byShape, JsonElement sensitivity,
                               JsonElement sensitivityByShape, bool loaded)
        {
            _byShape = byShape;
            _sensitivity = sensitivity;
            _sensitivityByShape = sensitivityByShape;
            _loaded = loaded;
        }

        public static SidecarContext Load(string vertical)
        {
            try
            {
                if (!Enum.TryParse<TypedMemEvalVertical>(vertical, ignoreCase: true, out var v))
                    return Empty;

                // Parsed into a detached clone: the JsonDocument is disposed here, and a
                // JsonElement backed by a disposed document throws on access.
                using var document = JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(v));
                var probes = document.RootElement.TryGetProperty("probes", out var p)
                    ? p.Clone()
                    : default;
                if (probes.ValueKind != JsonValueKind.Object)
                    return Empty;

                var byShape = probes.TryGetProperty("by_shape", out var bs) ? bs.Clone() : default;
                var sensitivity = probes.TryGetProperty("retriever_sensitivity", out var rs)
                    ? rs.Clone()
                    : default;
                var sensByShape = sensitivity.ValueKind == JsonValueKind.Object
                                  && sensitivity.TryGetProperty("by_shape", out var sbs)
                    ? sbs.Clone()
                    : default;

                return new SidecarContext(byShape, sensitivity, sensByShape, loaded: true);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
                return Empty;
            }
        }

        private static SidecarContext Empty { get; } =
            new(default, default, default, loaded: false);

        /// <summary>The numbers. Rendered by the HTML renderer as its "Metrics" table.</summary>
        public Dictionary<string, double>? DimensionsFor(string shape)
        {
            if (!_loaded || !TryRow(_byShape, shape, out var row))
                return null;

            var dims = new Dictionary<string, double>(StringComparer.Ordinal);

            // A chance floor that is ABSENT is not a floor of zero, so it is simply not written.
            if (Num(row, "chance_floor") is { } floor)
                dims["floor.chance"] = floor;
            if (Num(row, "headroom_perfect_selector") is { } headroom)
                dims["headroom.perfectSelector"] = headroom;

            if (Rate(row, "v1_passed", "v1_applicable") is { } v1)
                dims["arm.v1GoldOnlyCeiling"] = v1;
            if (Rate(row, "v8_passed", "v8_applicable") is { } v8)
                dims["arm.v8FullHaystack"] = v8;
            if (Rate(row, "v9_passed", "v9_applicable") is { } v9)
                dims["arm.v9ReferenceRetrieval"] = v9;

            return dims.Count > 0 ? dims : null;
        }

        /// <summary>The words. Rendered as the node's bullet list.</summary>
        public IReadOnlyList<string>? NotesFor(string shape)
        {
            if (!_loaded)
                return null;

            var notes = new List<string>();

            if (Str(_sensitivityByShape, shape, "retriever_agreement") is { } agreement)
            {
                notes.Add(agreement switch
                {
                    "robust-ranking" =>
                        "Ranking: ROBUST — this shape separates two systems under BOTH published retrievers.",
                    "retriever-sensitive" =>
                        "Ranking: RETRIEVER-SENSITIVE — the two published retrievers DISAGREE on whether "
                        + "this shape separates two systems. Read any comparison here cautiously.",
                    "non-ranking" =>
                        "Ranking: NON-RANKING — neither published retriever separates two systems on this "
                        + "shape. A good score here is not evidence that a memory system is good.",
                    "not-applicable" =>
                        "Ranking: NOT APPLICABLE — the gold set is empty, so the retrieval operand is "
                        + "undefined rather than perfect. No retrieval figure is published for it.",
                    _ => $"Ranking: {agreement}."
                });
            }

            if (TryRow(_byShape, shape, out var row))
            {
                var v8 = Rate(row, "v8_passed", "v8_applicable");
                var v9 = Rate(row, "v9_passed", "v9_applicable");
                if (v8 is { } full && v9 is { } retrieved && full - retrieved > 0.0005)
                {
                    notes.Add(
                        $"Condition matters here: {full:F3} with the whole haystack in context, "
                        + $"{retrieved:F3} under the reference retriever. A score measured by stuffing "
                        + "the context is the former and must not be read as the latter.");
                }
            }

            return notes.Count > 0 ? notes : null;
        }

        /// <summary>Root-level notes: what every figure above is conditional on.</summary>
        public IReadOnlyList<string> RootNotes()
        {
            var notes = new List<string>
            {
                "Read the per-shape nodes, not this node's score. A mean over shapes hides the "
                + "structure the corpus exists to expose; the score here exists for tooling "
                + "compatibility and is not the citable result."
            };

            if (!_loaded || _sensitivity.ValueKind != JsonValueKind.Object)
                return notes;

            // EACH RETRIEVER CONDITIONS A DIFFERENT THING, and saying "all figures depend on all
            // three" is the kind of loose conditionality statement this family exists to stamp
            // out. `headroom.perfectSelector` is V1 minus V9 and `arm.v9ReferenceRetrieval` IS
            // V9 — both measured on the BM25 reference arm alone. The two dense retrievers do
            // not enter either number; they decide only the ranking class above.
            if (Field("reference_retriever") is { } reference)
            {
                var budget = _sensitivity.TryGetProperty("reference_k", out var k)
                             && k.ValueKind == JsonValueKind.Number
                    ? $", top-K={k.GetInt32()}"
                    : string.Empty;
                notes.Add($"Reference retriever: {reference}{budget}. "
                          + "`arm.v9ReferenceRetrieval` and `headroom.perfectSelector` are "
                          + "conditional on THIS and on nothing else.");
            }

            var dense = new[] { Field("dense_retriever"), Field("second_dense_retriever") }
                .Where(d => d is not null)
                .ToList();
            if (dense.Count > 0)
            {
                notes.Add($"Dense retrievers compared: {string.Join(" and ", dense)}. These decide "
                          + "the ranking class ONLY — they do not enter the V9 or headroom "
                          + "figures above.");
            }

            return notes;
        }

        private string? Field(string name)
            => _sensitivity.ValueKind == JsonValueKind.Object
               && _sensitivity.TryGetProperty(name, out var v)
               && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static bool TryRow(JsonElement byShape, string shape, out JsonElement row)
        {
            if (byShape.ValueKind == JsonValueKind.Object
                && byShape.TryGetProperty(shape, out row)
                && row.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            row = default;
            return false;
        }

        private static double? Num(JsonElement row, string field)
            => row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble()
                : null;

        // 0/0 is not 0.000: an arm that was never applicable has no rate at all.
        private static double? Rate(JsonElement row, string passed, string applicable)
            => Num(row, passed) is { } p && Num(row, applicable) is { } a && a > 0
                ? p / a
                : null;

        private static string? Str(JsonElement byShape, string shape, string field)
            => TryRow(byShape, shape, out var row)
               && row.TryGetProperty(field, out var v)
               && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }

    /// <summary>
    /// One leaf per question, under its own shape. Ordered by question id so two runs of the same
    /// corpus diff line-for-line.
    /// </summary>
    private static IReadOnlyList<EvalResult>? QuestionNodes(
        string vertical,
        string shape,
        IReadOnlyDictionary<string, List<QuestionResult>> byShape,
        string? judgeModel,
        double passThreshold,
        DateTimeOffset now)
    {
        if (!byShape.TryGetValue(shape, out var questions) || questions.Count == 0)
            return null;

        return questions
            .OrderBy(q => q.QuestionId, StringComparer.Ordinal)
            .Select(q => QuestionNode(vertical, shape, q, judgeModel, passThreshold, now))
            .ToList();
    }

    private static EvalResult QuestionNode(
        string vertical,
        string shape,
        QuestionResult question,
        string? judgeModel,
        double passThreshold,
        DateTimeOffset now)
    {
        var outcome = question.TypedOutcome!.Outcome;

        // THE OUTCOME IS THE RESULT, NOT THE BOOLEAN. A question that abstained, was never run,
        // or came back inconclusive is not a question answered wrongly, and collapsing all three
        // into `false` is the exact loss this family's typed vector exists to prevent. `Value`
        // is not nullable, so the distinction is carried by Label and Severity instead.
        var (value, label, passed, severity) = outcome switch
        {
            TypedMemEvalOutcome.Correct => (1.0, "pass", true, "none"),
            TypedMemEvalOutcome.Wrong => (0.0, "fail", false, "medium"),
            TypedMemEvalOutcome.Abstained => (0.0, "abstained", false, "low"),
            TypedMemEvalOutcome.Missed => (0.0, "missed", false, "medium"),
            TypedMemEvalOutcome.Premature => (0.0, "premature", false, "medium"),
            TypedMemEvalOutcome.Inconclusive => (0.0, "inconclusive", false, "low"),
            _ => (0.0, "unrun", false, "low")
        };

        var notes = new List<string> { $"Outcome: {outcome}." };
        if (!string.IsNullOrWhiteSpace(question.JudgeExplanation))
        {
            // Truncated: a report is an index into the run, not a second copy of it.
            var reason = question.JudgeExplanation!.Trim();
            notes.Add("Judge: " + (reason.Length > 400 ? reason[..400] + "…" : reason));
        }

        var dimensions = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["llmCalls.agent"] = question.AgentLlmCallCount,
            ["llmCalls.judge"] = question.JudgeLlmCallCount
        };
        if (question.JudgeRetryLlmCallCount > 0)
            dimensions["llmCalls.judgeRetry"] = question.JudgeRetryLlmCallCount;
        if (question.Duration is { } duration)
            dimensions["durationSeconds"] = Math.Round(duration.TotalSeconds, 3);

        return new EvalResult(
            Metric: new EvalMetadata(
                Key: $"typedmemeval.{vertical}.{shape}.{question.QuestionId}",
                Name: question.QuestionId,
                Category: "memory",
                Version: "1.0.0"),
            Score: new EvalScore(
                Value: value,
                Ordinal: null,
                Label: label,
                Passed: passed,
                Threshold: passThreshold,
                Severity: severity,
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: dimensions,
                Evidence: null,
                Recommendations: notes,
                SubResults: null,
                AggregationStrategy: "typed-outcome-vector"),
            Provenance: new EvalProvenance(
                Type: "question",
                JudgeModel: judgeModel,
                PromptId: question.QuestionId,
                PromptHash: null,
                TokensUsed: question.JudgeTokensUsed,
                EstimatedCost: 0,
                CacheHit: false),
            EvaluatedAt: now);
    }

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
