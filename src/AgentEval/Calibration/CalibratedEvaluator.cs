// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.Calibration;

/// <summary>
/// Multi-model evaluator that runs criteria-based evaluation through multiple LLM judges
/// and aggregates results using configurable voting strategies.
/// Implements <see cref="IEvaluator"/> for drop-in use with <c>MAFEvaluationHarness</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>CalibratedEvaluator</c> brings multi-model consensus to harness-level criteria evaluation.
/// Each judge independently evaluates the same input/output against the same criteria, and results
/// are aggregated:
/// </para>
/// <list type="bullet">
///   <item><description>Per-criterion <c>Met</c> determined by majority vote across judges</description></item>
///   <item><description><c>OverallScore</c> aggregated via the configured <see cref="VotingStrategy"/></description></item>
///   <item><description><c>Summary</c> enriched with agreement percentage</description></item>
///   <item><description><c>Improvements</c> merged (union) from all judges</description></item>
/// </list>
/// <para>Usage:</para>
/// <code>
/// var evaluator = new CalibratedEvaluator(
///     new[] { ("GPT-4o", gpt4oClient), ("Claude", claudeClient) });
/// var harness = new MAFEvaluationHarness(evaluator);
/// // Everything else unchanged — RunEvaluationAsync, TestCase, etc.
/// </code>
/// </remarks>
public class CalibratedEvaluator : IEvaluator
{
    private readonly IReadOnlyList<(string Name, IEvaluator Evaluator)> _evaluators;
    private readonly CalibratedJudgeOptions _options;

    /// <summary>
    /// Creates a calibrated evaluator from named chat clients.
    /// </summary>
    /// <param name="judges">Named chat clients to use as judges. At least 1 required (2+ recommended).</param>
    /// <param name="options">Optional calibration options. Uses defaults if null.</param>
    /// <exception cref="ArgumentException">Thrown when no judges are provided.</exception>
    public CalibratedEvaluator(
        IEnumerable<(string Name, IChatClient Client)> judges,
        CalibratedJudgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(judges);

        _evaluators = judges
            .Select(j => (j.Name, (IEvaluator)new ChatClientEvaluator(j.Client)))
            .ToList();

        if (_evaluators.Count == 0)
            throw new ArgumentException("At least 1 judge is required.", nameof(judges));

        _options = options ?? CalibratedJudgeOptions.Default;
        _options.Validate();
    }

    /// <summary>
    /// Creates a calibrated evaluator from named evaluators (for testability).
    /// </summary>
    /// <param name="evaluators">Named evaluator instances.</param>
    /// <param name="options">Optional calibration options.</param>
    internal CalibratedEvaluator(
        IEnumerable<(string Name, IEvaluator Evaluator)> evaluators,
        CalibratedJudgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evaluators);

        _evaluators = evaluators.ToList();

        if (_evaluators.Count == 0)
            throw new ArgumentException("At least 1 evaluator is required.", nameof(evaluators));

        _options = options ?? CalibratedJudgeOptions.Default;
        _options.Validate();
    }

    /// <summary>
    /// Gets the names of all configured judges.
    /// </summary>
    public IReadOnlyList<string> JudgeNames => _evaluators.Select(e => e.Name).ToList();

    /// <summary>
    /// Gets the calibration options.
    /// </summary>
    public CalibratedJudgeOptions Options => _options;

    /// <inheritdoc />
    public async Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken cancellationToken = default)
    {
        var criteriaList = criteria.ToList();
        var results = new List<(string Name, EvaluationResult Result)>();
        var errors = new List<(string Name, Exception Error)>();

        // Run all evaluators in parallel with concurrency limit
        using var semaphore = new SemaphoreSlim(_options.MaxParallelJudges);
        var tasks = _evaluators.Select(async evaluator =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.Timeout);

                var result = await evaluator.Evaluator.EvaluateAsync(
                    input, output, criteriaList, cts.Token);
                return (evaluator.Name, Result: (EvaluationResult?)result, Error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_options.ContinueOnJudgeFailure)
                    throw;
                return (evaluator.Name, Result: (EvaluationResult?)null, Error: (Exception?)ex);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var taskResults = await Task.WhenAll(tasks);

        foreach (var tr in taskResults)
        {
            if (tr.Result != null)
                results.Add((tr.Name, tr.Result));
            else if (tr.Error != null)
                errors.Add((tr.Name, tr.Error));
        }

        // Check minimum judges threshold
        if (results.Count < _options.MinimumJudgesRequired)
        {
            var errorMessages = string.Join("; ", errors.Select(e => $"{e.Name}: {e.Error.Message}"));
            throw new InvalidOperationException(
                $"Only {results.Count} of {_evaluators.Count} judges succeeded, " +
                $"but {_options.MinimumJudgesRequired} are required. Errors: {errorMessages}");
        }

        // Aggregate results
        return AggregateResults(results, criteriaList);
    }

    private EvaluationResult AggregateResults(
        List<(string Name, EvaluationResult Result)> results,
        List<string> criteriaList)
    {
        var scores = results.Select(r => (double)r.Result.OverallScore).ToList();
        var judgeScoreMap = results.ToDictionary(r => r.Name, r => (double)r.Result.OverallScore);

        // Aggregate overall score via voting strategy
        var finalScore = CalculateFinalScore(scores, judgeScoreMap);

        // Aggregate per-criterion results via majority vote
        var aggregatedCriteria = AggregateCriteriaResults(results, criteriaList);

        // Merge improvements (union, deduplicated)
        var allImprovements = results
            .SelectMany(r => r.Result.Improvements)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Calculate agreement
        var stdDev = CalculateStandardDeviation(scores);
        var agreement = CalculateAgreement(scores, stdDev);

        // Build summary with agreement info
        var judgeDetails = string.Join(", ", results.Select(r => $"{r.Name}={r.Result.OverallScore}"));
        var summary = $"Calibrated evaluation ({results.Count} judges, {agreement:F0}% agreement). " +
                      $"Scores: [{judgeDetails}]. " +
                      string.Join(" ", results.Select(r => r.Result.Summary).Where(s => !string.IsNullOrEmpty(s)).Take(1));

        return new EvaluationResult
        {
            OverallScore = (int)Math.Round(finalScore),
            Summary = summary,
            Improvements = allImprovements,
            CriteriaResults = aggregatedCriteria
        };
    }

    private List<CriterionResult> AggregateCriteriaResults(
        List<(string Name, EvaluationResult Result)> results,
        List<string> criteriaList)
    {
        var aggregated = new List<CriterionResult>();

        foreach (var criterion in criteriaList)
        {
            // Collect all judge verdicts for this criterion, keeping judge name paired
            var judgements = results
                .Select(r => (r.Name, Criterion: r.Result.CriteriaResults
                    .FirstOrDefault(c => string.Equals(c.Criterion, criterion, StringComparison.OrdinalIgnoreCase))))
                .Where(j => j.Criterion != null)
                .ToList();

            if (judgements.Count == 0)
            {
                aggregated.Add(new CriterionResult
                {
                    Criterion = criterion,
                    Met = false,
                    Explanation = "No judges returned a result for this criterion."
                });
                continue;
            }

            // Majority vote for Met
            var metCount = judgements.Count(j => j.Criterion!.Met);
            var met = metCount > judgements.Count / 2.0;

            // Combine explanations with judge names
            var explanations = judgements
                .Select(j => $"[{j.Name}] {j.Criterion!.Explanation}")
                .Where(e => !string.IsNullOrWhiteSpace(e));
            var combinedExplanation = $"Majority vote: {metCount}/{judgements.Count} judges say met. " +
                                      string.Join(" | ", explanations);

            aggregated.Add(new CriterionResult
            {
                Criterion = criterion,
                Met = met,
                Explanation = combinedExplanation
            });
        }

        return aggregated;
    }

    private double CalculateFinalScore(List<double> scores, Dictionary<string, double> judgeScores)
    {
        if (scores.Count == 0) return 0;

        return _options.Strategy switch
        {
            VotingStrategy.Median => CalculateMedian(scores),
            VotingStrategy.Mean => scores.Average(),
            VotingStrategy.Unanimous => CheckConsensus(scores)
                ? scores.Average()
                : throw new InvalidOperationException("Judges did not reach consensus on criteria evaluation."),
            VotingStrategy.Weighted => CalculateWeightedScore(judgeScores),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private double CalculateWeightedScore(Dictionary<string, double> judgeScores)
    {
        if (_options.JudgeWeights == null || _options.JudgeWeights.Count == 0)
            return judgeScores.Values.Average();

        var totalWeight = 0.0;
        var weightedSum = 0.0;

        foreach (var (judgeName, score) in judgeScores)
        {
            var weight = _options.JudgeWeights.GetValueOrDefault(judgeName, 1.0);
            weightedSum += score * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : judgeScores.Values.Average();
    }

    private static double CalculateMedian(List<double> scores)
    {
        var sorted = scores.OrderBy(s => s).ToList();
        var count = sorted.Count;

        if (count == 0) return 0;
        if (count % 2 == 0)
            return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
        return sorted[count / 2];
    }

    private static double CalculateStandardDeviation(List<double> scores)
    {
        if (scores.Count < 2) return 0;

        var mean = scores.Average();
        var sumSquares = scores.Sum(s => (s - mean) * (s - mean));
        return Math.Sqrt(sumSquares / (scores.Count - 1));
    }

    private static double CalculateAgreement(List<double> scores, double stdDev)
    {
        if (scores.Count < 2) return 100;

        var mean = scores.Average();
        if (mean == 0) return scores.All(s => s == 0) ? 100 : 0;

        var cv = stdDev / mean;
        var agreement = Math.Max(0, 100 - (cv * 100));
        return Math.Min(100, agreement);
    }

    private bool CheckConsensus(List<double> scores)
    {
        if (scores.Count < 2) return true;

        var min = scores.Min();
        var max = scores.Max();
        return (max - min) <= _options.ConsensusTolerance;
    }
}
