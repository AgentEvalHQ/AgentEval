// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Configurable retry policy for LLM and embedding operations.
/// Inspired by kbeaugrand/KernelMemory.Evaluation retry patterns.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Maximum number of retry attempts.</summary>
    public int MaxRetries { get; init; } = 3;
    
    /// <summary>Initial delay between retries in milliseconds.</summary>
    public int InitialDelayMs { get; init; } = 100;
    
    /// <summary>Multiplier for exponential backoff.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;
    
    /// <summary>Maximum delay between retries in milliseconds.</summary>
    public int MaxDelayMs { get; init; } = 5000;
    
    /// <summary>Default retry policy (3 retries with exponential backoff).</summary>
    public static RetryPolicy Default { get; } = new();
    
    /// <summary>No retries policy.</summary>
    public static RetryPolicy None { get; } = new() { MaxRetries = 0 };
    
    /// <summary>
    /// Execute an async function with retry logic.
    /// </summary>
    /// <typeparam name="T">Return type.</typeparam>
    /// <param name="action">The async action to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the action.</returns>
    /// <exception cref="RetryExhaustedException">Thrown when all retries are exhausted.</exception>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action, 
        CancellationToken cancellationToken = default)
    {
        var exceptions = new List<Exception>();
        var currentDelay = InitialDelayMs;
        
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Don't retry on explicit cancellation
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
                
                if (attempt == MaxRetries)
                {
                    break; // No more retries
                }
                
                // Wait before retry with exponential backoff
                await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                currentDelay = Math.Min((int)(currentDelay * BackoffMultiplier), MaxDelayMs);
            }
        }
        
        throw new RetryExhaustedException(
            $"Operation failed after {MaxRetries + 1} attempt(s).",
            exceptions);
    }
    
    /// <summary>
    /// Execute an async function with retry logic (simple overload).
    /// Pattern inspired by kbeaugrand: Try(3, async (remainingTry) => {...})
    /// </summary>
    public Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
        => ExecuteAsync(_ => action(), cancellationToken);
}

/// <summary>
/// Exception thrown when all retry attempts have been exhausted.
/// </summary>
public class RetryExhaustedException : Exception
{
    /// <summary>All exceptions that occurred during retry attempts.</summary>
    public IReadOnlyList<Exception> Attempts { get; }
    
    /// <summary>Creates a new retry exhausted exception.</summary>
    public RetryExhaustedException(string message, IEnumerable<Exception> attempts) 
        : base(message, attempts.LastOrDefault())
    {
        Attempts = attempts.ToList().AsReadOnly();
    }
}

/// <summary>
/// Extension methods for using RetryPolicy with metrics.
/// </summary>
public static class RetryPolicyExtensions
{
    /// <summary>
    /// Execute a metric evaluation with retry.
    /// </summary>
    public static async Task<MetricResult> EvaluateWithRetryAsync(
        this IMetric metric,
        EvaluationContext context,
        RetryPolicy? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        var policy = retryPolicy ?? RetryPolicy.Default;
        
        try
        {
            return await policy.ExecuteAsync(
                ct => metric.EvaluateAsync(context, ct),
                cancellationToken);
        }
        catch (RetryExhaustedException ex)
        {
            return MetricResult.Fail(
                metric.Name,
                $"Evaluation failed after {policy.MaxRetries + 1} attempts: {ex.InnerException?.Message}",
                details: new Dictionary<string, object>
                {
                    ["attemptCount"] = policy.MaxRetries + 1,
                    ["lastError"] = ex.InnerException?.Message ?? "Unknown error"
                });
        }
    }
}
