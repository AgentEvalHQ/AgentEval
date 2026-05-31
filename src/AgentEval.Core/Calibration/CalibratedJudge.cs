// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.Calibration;

/// <summary>
/// Wraps multiple LLM judges to provide calibrated, high-confidence evaluations.
/// </summary>
/// <remarks>
/// <para>
/// CalibratedJudge improves the reliability of LLM-as-judge evaluations by:
/// </para>
/// <list type="bullet">
///   <item><description>Running the same evaluation with multiple LLM judges in parallel</description></item>
///   <item><description>Aggregating scores using configurable voting strategies</description></item>
///   <item><description>Calculating agreement percentages and confidence intervals</description></item>
///   <item><description>Providing graceful degradation if individual judges fail</description></item>
/// </list>
/// <para>
/// Usage example:
/// </para>
/// <code>
/// var judge = CalibratedJudge.Create(
///     ("GPT-4o", gpt4oClient),
///     ("Claude", claudeClient),
///     ("Gemini", geminiClient));
/// 
/// var result = await judge.EvaluateAsync(context, 
///     judgeName => new FaithfulnessMetric(judges[judgeName]));
/// 
/// Console.WriteLine($"Score: {result.Score:F1}, Agreement: {result.Agreement:F0}%");
/// </code>
/// </remarks>
public class CalibratedJudge : ICalibratedJudge
{
    private readonly IReadOnlyList<(string Name, IChatClient Client)> _judges;
    private readonly CalibratedJudgeOptions _options;
    
    /// <summary>
    /// Creates a new calibrated judge with the specified judges and options.
    /// </summary>
    /// <param name="judges">Collection of named judges. At least 1 judge required (2+ recommended for calibration).</param>
    /// <param name="options">Optional calibration options. Uses defaults if null.</param>
    /// <exception cref="ArgumentException">Thrown when no judges are provided.</exception>
    public CalibratedJudge(
        IEnumerable<(string Name, IChatClient Client)> judges,
        CalibratedJudgeOptions? options = null)
    {
        _judges = judges?.ToList() ?? throw new ArgumentNullException(nameof(judges));
        _options = options ?? CalibratedJudgeOptions.Default;
        
        if (_judges.Count == 0)
            throw new ArgumentException("At least 1 judge is required.", nameof(judges));
        
        _options.Validate();
    }
    
    /// <summary>
    /// Creates a new calibrated judge with auto-named judges.
    /// </summary>
    /// <param name="clients">Collection of chat clients. At least 1 judge required.</param>
    /// <param name="options">Optional calibration options. Uses defaults if null.</param>
    /// <exception cref="ArgumentException">Thrown when no clients are provided.</exception>
    public CalibratedJudge(
        IEnumerable<IChatClient> clients,
        CalibratedJudgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var clientList = clients.ToList();
        
        if (clientList.Count == 0)
            throw new ArgumentException("At least 1 judge is required.", nameof(clients));
        
        _judges = clientList.Select((c, i) => ($"Judge{i + 1}", c)).ToList();
        _options = options ?? CalibratedJudgeOptions.Default;
        
        _options.Validate();
    }
    
    /// <summary>
    /// Creates a calibrated judge using the factory convenience method.
    /// </summary>
    /// <param name="judges">Named judges to use for calibration.</param>
    /// <returns>A new CalibratedJudge instance.</returns>
    public static CalibratedJudge Create(params (string Name, IChatClient Client)[] judges)
        => new(judges);
    
    /// <summary>
    /// Creates a calibrated judge with custom options.
    /// </summary>
    /// <param name="options">Calibration options.</param>
    /// <param name="judges">Named judges to use for calibration.</param>
    /// <returns>A new CalibratedJudge instance.</returns>
    public static CalibratedJudge Create(CalibratedJudgeOptions options, params (string Name, IChatClient Client)[] judges)
        => new(judges, options);
    
    /// <inheritdoc />
    public IReadOnlyList<string> JudgeNames => _judges.Select(j => j.Name).ToList();
    
    /// <inheritdoc />
    public CalibratedJudgeOptions Options => _options;
    
    /// <summary>
    /// Gets the configured judges with their clients.
    /// </summary>
    public IReadOnlyList<(string Name, IChatClient Client)> Judges => _judges;
    
    /// <inheritdoc />
    public async Task<CalibratedResult> EvaluateAsync<TMetric>(
        TMetric metric,
        EvaluationContext context,
        CancellationToken cancellationToken = default) where TMetric : IMetric
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(context);
        
        // Create a factory that returns the same metric for all judges
        // This is a simplified version - the metric will be called multiple times
        // For true multi-model calibration, use the other overload with a factory
        return await EvaluateAsync(context, _ => metric, cancellationToken);
    }
    
    /// <inheritdoc />
    public async Task<CalibratedResult> EvaluateAsync(
        EvaluationContext context,
        Func<string, IMetric> metricFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metricFactory);
        
        var judgeScores = new Dictionary<string, double>();
        var errors = new List<(string Judge, Exception Error)>();
        
        // Run judges with parallelism limit
        using var semaphore = new SemaphoreSlim(_options.MaxParallelJudges);
        var tasks = _judges.Select(async judge =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_options.Timeout);
                
                var metric = metricFactory(judge.Name);
                var result = await metric.EvaluateAsync(context, cts.Token);

                return (Judge: judge.Name, Score: (double?)result.Score, Error: (Exception?)null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-judge timeout (linked CTS fired via CancelAfter), not caller cancellation.
                // Degrade gracefully instead of aborting the whole evaluation; caller
                // cancellation still propagates via the filter exclusion above (BUG-05).
                var timeout = new TimeoutException(
                    $"Judge '{judge.Name}' exceeded the per-judge timeout of {_options.Timeout}.");
                if (!_options.ContinueOnJudgeFailure)
                    throw timeout;
                return (Judge: judge.Name, Score: (double?)null, Error: (Exception?)timeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_options.ContinueOnJudgeFailure)
                    throw;
                return (Judge: judge.Name, Score: (double?)null, Error: (Exception?)ex);
            }
            finally
            {
                semaphore.Release();
            }
        });
        
        var results = await Task.WhenAll(tasks);
        
        // Collect successful scores and errors
        foreach (var result in results)
        {
            if (result.Score.HasValue)
            {
                judgeScores[result.Judge] = result.Score.Value;
            }
            else if (result.Error != null)
            {
                errors.Add((result.Judge, result.Error));
            }
        }
        
        // Check if we have enough successful judges
        if (judgeScores.Count < _options.MinimumJudgesRequired)
        {
            var errorMessages = string.Join("; ", errors.Select(e => $"{e.Judge}: {e.Error.Message}"));
            throw new InvalidOperationException(
                $"Only {judgeScores.Count} of {_judges.Count} judges succeeded, " +
                $"but {_options.MinimumJudgesRequired} are required. Errors: {errorMessages}");
        }
        
        // Calculate aggregated score
        var scores = judgeScores.Values.ToList();
        var finalScore = CalculateFinalScore(scores, judgeScores);
        var stdDev = CalculateStandardDeviation(scores);
        var agreement = CalculateAgreement(scores, stdDev);
        var (ciLower, ciUpper) = CalculateConfidenceInterval(scores, stdDev);
        var hasConsensus = CheckConsensus(scores);
        
        return new CalibratedResult
        {
            Score = finalScore,
            Agreement = agreement,
            JudgeScores = judgeScores,
            ConfidenceLower = ciLower,
            ConfidenceUpper = ciUpper,
            StandardDeviation = stdDev,
            Strategy = _options.Strategy,
            HasConsensus = hasConsensus
        };
    }
    
    private double CalculateFinalScore(List<double> scores, Dictionary<string, double> judgeScores)
    {
        if (scores.Count == 0) return 0;
        
        return _options.Strategy switch
        {
            VotingStrategy.Median => CalculateMedian(scores),
            VotingStrategy.Mean => scores.Average(),
            VotingStrategy.Unanimous => CheckConsensus(scores) ? scores.Average() : throw new InvalidOperationException("Judges did not reach consensus."),
            VotingStrategy.Weighted => CalculateWeightedScore(judgeScores),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    
    // ARC-04: shared with CalibratedEvaluator via CalibrationMath.
    private double CalculateWeightedScore(Dictionary<string, double> judgeScores)
        => CalibrationMath.WeightedScore(judgeScores, _options.JudgeWeights);

    private static double CalculateMedian(List<double> scores)
        => CalibrationMath.Median(scores);

    private static double CalculateStandardDeviation(List<double> scores)
        => CalibrationMath.StandardDeviation(scores);

    private static double CalculateAgreement(List<double> scores, double stdDev)
        => CalibrationMath.Agreement(scores, stdDev);

    private (double? Lower, double? Upper) CalculateConfidenceInterval(List<double> scores, double stdDev)
    {
        if (!_options.CalculateConfidenceInterval || scores.Count < 2)
            return (null, null);
        
        var mean = scores.Average();
        var n = scores.Count;
        var df = n - 1;
        
        var tValue = GetTValue(df, _options.ConfidenceLevel);
        
        var marginOfError = tValue * (stdDev / Math.Sqrt(n));
        var lower = Math.Max(0, mean - marginOfError);
        var upper = Math.Min(100, mean + marginOfError);
        
        return (lower, upper);
    }
    
    private static double GetTValue(int degreesOfFreedom, double confidenceLevel)
        => CalibrationMath.GetTValue(degreesOfFreedom, confidenceLevel);

    private bool CheckConsensus(List<double> scores)
        => CalibrationMath.WithinConsensus(scores, _options.ConsensusTolerance);
}
