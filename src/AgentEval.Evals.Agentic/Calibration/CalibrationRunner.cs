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
    public async Task<CalibrationReport> RunAsync(
        IReadOnlyList<CalibrationDataset> datasets, CancellationToken ct = default)
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

            foreach (var entry in ds.Entries)
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
                    continue;
                }

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
