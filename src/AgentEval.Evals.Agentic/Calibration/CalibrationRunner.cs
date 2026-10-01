// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Calibration;

/// <summary>
/// Orchestrates the agentic calibration run: for each <see cref="CalibrationDataset"/>,
/// evaluates every entry via the configured evaluator, then computes per-category accuracy
/// and Cohen's kappa.
/// </summary>
/// <remarks>
/// <para>
/// The runner is intentionally decoupled from any specific evaluator registry.
/// Callers supply a resolver delegate (<c>Func&lt;string, IEval?&gt;</c>) that maps
/// an evaluator key (e.g. <c>task_completion</c>) to a concrete <see cref="IEval"/>
/// instance, enabling full test isolation.
/// </para>
/// <para>
/// <b>Namespace note.</b> The <c>AgentEval.Evals.Agentic.Calibration</c> namespace serves
/// two distinct purposes — keep them straight when adding new types:
/// <list type="bullet">
///   <item>
///     <b>Golden-runner harness</b> (this class, <see cref="CalibrationDataset"/>,
///     <c>CalibrationMetrics</c>): tools that quantify <i>judge-vs-golden</i> agreement
///     across labelled scenarios, producing accuracy + Cohen's kappa for evaluator
///     calibration audits. They do not implement <see cref="IEval"/>.
///   </item>
///   <item>
///     <b>Confidence-calibration evaluators</b> (<see cref="ConfidenceCalibrationEval"/>,
///     <see cref="SelfCorrectionQualityEval"/>, <see cref="UncertaintyAcknowledgmentEval"/>):
///     <see cref="IEval"/> implementations that assess whether an <i>agent's stated
///     confidence</i> matches its actual correctness in the response under test.
///   </item>
/// </list>
/// Both senses use the word "calibration" but answer different questions: the harness
/// asks "is our judge calibrated against the golden labels?", whereas the evaluators
/// ask "is the agent under test calibrated about its own answers?".
/// </para>
/// </remarks>
public sealed class CalibrationRunner
{
    private readonly Func<string, IEval?> _evaluatorResolver;

    /// <summary>
    /// Initialises a new <see cref="CalibrationRunner"/>.
    /// </summary>
    /// <param name="evaluatorResolver">
    /// Delegate that resolves an <see cref="IEval"/> by its string key
    /// (e.g. <c>"task_completion"</c>). Returns <c>null</c> when the key is unknown.
    /// </param>
    public CalibrationRunner(Func<string, IEval?> evaluatorResolver)
    {
        _evaluatorResolver = evaluatorResolver ?? throw new ArgumentNullException(nameof(evaluatorResolver));
    }

    /// <summary>
    /// Runs calibration across all supplied datasets and returns a
    /// <see cref="CalibrationReport"/> with per-category metrics.
    /// </summary>
    /// <param name="datasets">One or more datasets (typically one per category) to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A report containing per-category accuracy, kappa, and score delta statistics.</returns>
    public Task<CalibrationReport> RunAsync(
        IReadOnlyList<CalibrationDataset> datasets, CancellationToken ct = default)
        => RunAsync(datasets, caseSink: null, limitPerCategory: null, ct);

    /// <summary>
    /// Runs calibration and hands every evaluated case to <paramref name="caseSink"/>, so the run can be kept as
    /// per-case records rather than only as category aggregates.
    /// </summary>
    /// <param name="datasets">One or more datasets (typically one per category) to evaluate.</param>
    /// <param name="caseSink">
    /// Receives one <see cref="CalibrationCaseRecord"/> per evaluated entry, including entries that errored. The record
    /// carries the leaf's verdict AND each criterion's verdict, so a later analysis can recompute a verdict from the
    /// criteria (or compare the judge's holistic number with them) without paying for the calls again.
    /// </param>
    /// <param name="limitPerCategory">When set, evaluates at most this many entries per category (the one-item stage of a
    /// paid run). <see langword="null"/> evaluates everything.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CalibrationReport> RunAsync(
        IReadOnlyList<CalibrationDataset> datasets,
        Func<CalibrationCaseRecord, CancellationToken, Task>? caseSink,
        int? limitPerCategory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(datasets);

        var perCategory = new Dictionary<string, CalibrationCategoryReport>();

        foreach (var ds in datasets)
        {
            ct.ThrowIfCancellationRequested();

            var pairs = new List<(string Expected, string Actual)>();
            var scoreDeltas = new List<double>();
            int withinScoreRange = 0;
            int evaluationFailures = 0;
            int skippedUnknownKey = 0;

            var entries = limitPerCategory is int limit ? ds.Entries.Take(limit) : ds.Entries;
            foreach (var entry in entries)
            {
                var eval = _evaluatorResolver(entry.EvaluatorKey);
                if (eval is null)
                {
                    Console.Error.WriteLine(
                        $"[calibration] {ds.CategoryKey} entry {entry.ScenarioId}: " +
                        $"unknown evaluator key '{entry.EvaluatorKey}' — skipping.");
                    skippedUnknownKey++;
                    continue;
                }

                EvalResult result;
                try
                {
                    result = await eval.EvaluateAsync(
                        new EvalInput(Query: entry.Input, Response: entry.AgentResponse), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Surface evaluation failures to stderr so they're visible in CI logs and
                    // count them in the report so the metrics don't silently look fine when the
                    // judge endpoint is broken.
                    Console.Error.WriteLine(
                        $"[calibration] {ds.CategoryKey} entry {entry.ScenarioId}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    evaluationFailures++;
                    if (caseSink is not null)
                        await caseSink(CalibrationCaseRecord.ForError(ds.CategoryKey, entry, eval, ex), ct).ConfigureAwait(false);
                    continue;
                }

                if (caseSink is not null)
                    await caseSink(CalibrationCaseRecord.From(ds.CategoryKey, entry, result), ct).ConfigureAwait(false);

                pairs.Add((entry.ExpectedVerdict, result.Score.Label));

                if (result.Score.Value >= entry.ExpectedScoreMin &&
                    result.Score.Value <= entry.ExpectedScoreMax)
                    withinScoreRange++;

                var midpoint = (entry.ExpectedScoreMin + entry.ExpectedScoreMax) / 2.0;
                scoreDeltas.Add(result.Score.Value - midpoint);
            }

            perCategory[ds.CategoryKey] = new CalibrationCategoryReport(
                Category: ds.CategoryKey,
                EntryCount: pairs.Count,
                Accuracy: CalibrationMetrics.Accuracy(pairs),
                CohensKappa: CalibrationMetrics.CohensKappa(pairs),
                WithinScoreRange: withinScoreRange,
                MeanScoreDelta: scoreDeltas.Count > 0 ? scoreDeltas.Average() : 0.0,
                EvaluationFailures: evaluationFailures,
                SkippedUnknownKey: skippedUnknownKey);
        }

        return new CalibrationReport(DateTimeOffset.UtcNow, perCategory);
    }
}

/// <summary>Full calibration report covering all evaluated agentic categories.</summary>
public sealed record CalibrationReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, CalibrationCategoryReport> PerCategory);

/// <summary>Per-category calibration metrics for agentic evaluators.</summary>
public sealed record CalibrationCategoryReport(
    string Category,
    int EntryCount,
    double Accuracy,
    double CohensKappa,
    int WithinScoreRange,
    double MeanScoreDelta,
    int EvaluationFailures = 0,
    int SkippedUnknownKey = 0);

/// <summary>
/// One evaluated calibration case, as a record that can be written to JSONL and analysed offline.
/// </summary>
/// <remarks>
/// <see cref="Leaves"/> keeps every atomic result under the evaluator, each with its criterion verdicts
/// (<see cref="CalibrationLeafRecord.Criteria"/>), prompt identity and holistic score. Before this, a calibration run
/// kept only category aggregates, so whether a leaf's verdict agreed with its own criteria could not be measured from
/// history.
/// </remarks>
public sealed record CalibrationCaseRecord(
    string Category,
    string ScenarioId,
    string EvaluatorKey,
    string? EvaluatorVersion,
    string ExpectedVerdict,
    double ExpectedScoreMin,
    double ExpectedScoreMax,
    string? Label,
    double? Value,
    bool? Passed,
    string? Error,
    IReadOnlyList<CalibrationLeafRecord> Leaves)
{
    internal static CalibrationCaseRecord From(string category, CalibrationEntry entry, EvalResult result) => new(
        category, entry.ScenarioId, entry.EvaluatorKey, result.Metric.Version,
        entry.ExpectedVerdict, entry.ExpectedScoreMin, entry.ExpectedScoreMax,
        result.Score.Label, result.Score.Value, result.Score.Passed, Error: null,
        Leaves: Flatten(result).Select(CalibrationLeafRecord.From).ToList());

    internal static CalibrationCaseRecord ForError(string category, CalibrationEntry entry, IEval eval, Exception ex) => new(
        category, entry.ScenarioId, entry.EvaluatorKey, eval.Version,
        entry.ExpectedVerdict, entry.ExpectedScoreMin, entry.ExpectedScoreMax,
        Label: null, Value: null, Passed: null, Error: $"{ex.GetType().Name}: {ex.Message}", Leaves: []);

    private static IEnumerable<EvalResult> Flatten(EvalResult node) =>
        node.Details.SubResults is { Count: > 0 } subs ? subs.SelectMany(Flatten) : [node];
}

/// <summary>One leaf of a calibration case: its verdict, its dimension scores and its prompt identity.</summary>
/// <param name="Key">The leaf evaluator's key.</param>
/// <param name="Label">The leaf's verdict label.</param>
/// <param name="Value">The leaf's score.</param>
/// <param name="Passed">Whether the leaf passed.</param>
/// <param name="Criteria">
/// The leaf's dimension scores. For an atomic judge leaf (<paramref name="AggregationStrategy"/> null) these are the
/// per-criterion verdicts. For an evaluator that aggregates without keeping its sub-results, they are the
/// aggregate's own dimensions, and the underlying criterion verdicts were not preserved. One example is
/// <c>JailbreakResistanceEval</c>, which keeps one score per matched pattern.
/// </param>
/// <param name="ProvenanceType">The leaf's provenance type.</param>
/// <param name="JudgeModel">The judge model, when one ran.</param>
/// <param name="PromptId">The prompt the judge was sent.</param>
/// <param name="PromptHash">The fingerprint of that prompt.</param>
/// <param name="AggregationStrategy">
/// Null for an atomic leaf. Otherwise the evaluator's aggregation (for example <c>mean-of-3-pattern-scores</c>),
/// which says that <paramref name="Criteria"/> holds aggregate dimensions rather than criterion verdicts.
/// </param>
public sealed record CalibrationLeafRecord(
    string Key,
    string? Label,
    double Value,
    bool Passed,
    IReadOnlyDictionary<string, double>? Criteria,
    string? ProvenanceType,
    string? JudgeModel,
    string? PromptId,
    string? PromptHash,
    string? AggregationStrategy = null)
{
    internal static CalibrationLeafRecord From(EvalResult leaf) => new(
        leaf.Metric.Key, leaf.Score.Label, leaf.Score.Value, leaf.Score.Passed,
        leaf.Details.Dimensions is { Count: > 0 } d ? new Dictionary<string, double>(d) : null,
        leaf.Provenance.Type, leaf.Provenance.JudgeModel, leaf.Provenance.PromptId, leaf.Provenance.PromptHash,
        leaf.Details.AggregationStrategy);
}
