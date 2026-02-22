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
        var semaphore = new SemaphoreSlim(_options.MaxParallelJudges);
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
        var finalScore = CalculateFinalScore(scores);
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
    
    private double CalculateFinalScore(List<double> scores)
    {
        if (scores.Count == 0) return 0;
        
        return _options.Strategy switch
        {
            VotingStrategy.Median => CalculateMedian(scores),
            VotingStrategy.Mean => scores.Average(),
            VotingStrategy.Unanimous => CheckConsensus(scores) ? scores.Average() : throw new InvalidOperationException("Judges did not reach consensus."),
            VotingStrategy.Weighted => CalculateWeightedScore(scores),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    
    private double CalculateWeightedScore(List<double> scores)
    {
        if (_options.JudgeWeights == null || _options.JudgeWeights.Count == 0)
            return scores.Average();
        
        var totalWeight = 0.0;
        var weightedSum = 0.0;
        
        for (int i = 0; i < _judges.Count && i < scores.Count; i++)
        {
            var weight = _options.JudgeWeights.GetValueOrDefault(_judges[i].Name, 1.0);
            weightedSum += scores[i] * weight;
            totalWeight += weight;
        }
        
        return totalWeight > 0 ? weightedSum / totalWeight : scores.Average();
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
        
        // Use t-distribution approximation for small samples
        // For 95% CI and n >= 2, use approximate t-value
        var tValue = _options.ConfidenceLevel switch
        {
            >= 0.99 => 3.5,  // Approximate for small n
            >= 0.95 => 2.5,
            >= 0.90 => 1.8,
            _ => 1.5
        };
        
        var marginOfError = tValue * (stdDev / Math.Sqrt(n));
        var lower = Math.Max(0, mean - marginOfError);
        var upper = Math.Min(100, mean + marginOfError);
        
        return (lower, upper);
    }
    
    private bool CheckConsensus(List<double> scores)
    {
        if (scores.Count < 2) return true;
        
        var min = scores.Min();
        var max = scores.Max();
        return (max - min) <= _options.ConsensusTolerance;
    }
}
