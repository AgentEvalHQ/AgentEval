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
        
        // Agreement is inverse of coefficient of variation, scaled to 0-100
        var cv = stdDev / mean;
        var agreement = Math.Max(0, 100 - (cv * 100));
        return Math.Min(100, agreement);
    }
    
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
    
    /// <summary>
    /// Returns the two-tailed t-distribution critical value for the given 
    /// degrees of freedom and confidence level.
    /// </summary>
    private static double GetTValue(int degreesOfFreedom, double confidenceLevel)
    {
        if (confidenceLevel >= 0.99)
        {
            return degreesOfFreedom switch
            {
                1 => 63.657, 2 => 9.925, 3 => 5.841, 4 => 4.604,
                5 => 4.032, 6 => 3.707, 7 => 3.499, 8 => 3.355,
                9 => 3.250, 10 => 3.169,
                <= 20 => 2.845, <= 30 => 2.756, _ => 2.576
            };
        }
        if (confidenceLevel >= 0.95)
        {
            return degreesOfFreedom switch
            {
                1 => 12.706, 2 => 4.303, 3 => 3.182, 4 => 2.776,
                5 => 2.571, 6 => 2.447, 7 => 2.365, 8 => 2.306,
                9 => 2.262, 10 => 2.228,
                <= 20 => 2.086, <= 30 => 2.045, _ => 1.960
            };
        }
        if (confidenceLevel >= 0.90)
        {
            return degreesOfFreedom switch
            {
                1 => 6.314, 2 => 2.920, 3 => 2.353, 4 => 2.132,
                5 => 2.015, 6 => 1.943, 7 => 1.895, 8 => 1.860,
                9 => 1.833, 10 => 1.812,
                <= 20 => 1.725, <= 30 => 1.699, _ => 1.645
            };
        }
        return 1.5;
    }
    
    private bool CheckConsensus(List<double> scores)
    {
        if (scores.Count < 2) return true;
        
        var min = scores.Min();
        var max = scores.Max();
        return (max - min) <= _options.ConsensusTolerance;
    }
}
